using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using WindowsAxon.Agent.Settings;
using WindowsAxon.Agent.Tests.Fakes;
using NSubstitute;

namespace WindowsAxon.Agent.Tests.Settings;

public sealed class EndpointApiDetectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClientResultException Error(int status, string message, string? code = null, string? parameter = null)
    {
        var response = Substitute.For<PipelineResponse>();
        response.Status.Returns(status);
        response.Content.Returns(BinaryData.FromString(JsonSerializer.Serialize(new
        {
            error = new
            {
                message,
                code,
                param = parameter
            }
        })));

        return new ClientResultException(message, response);
    }

    private static ModelSettings Settings(string url = "http://localhost:8000/v1",
                                           string model = "named-model",
                                           string? credential = null) => new()
                                           {
                                               Endpoints = new()
                                               {
                                                   ["e"] = new EndpointSettings { BaseUrl = url, ApiKey = credential }
                                               },
                                               Chat = new ChatSettings { Endpoint = "e", Model = model }
                                           };

    private static ScriptedChatClient SuccessfulProbe() => new ScriptedChatClient()
        .ReplyCall("windowsaxon_api_probe", callId: "probe_call")
        .ReplyWith((messages, options) =>
        {
            Assert.Equal(ChatToolMode.None, options!.ToolMode);
            var result = Assert.Single(messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>());
            Assert.Equal("probe_call", result.CallId);

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, result.Result!.ToString()));
        });

    [Fact]
    public async Task Responses_is_preferred_after_a_complete_tool_round_trip()
    {
        var detector = new EndpointApiDetector();
        var client = SuccessfulProbe();
        var candidates = new List<EndpointApi>();
        var api = await detector.DetectAsync(Settings(), candidate =>
        {
            candidates.Add(candidate);

            return client;
        }, Ct);

        Assert.Equal(EndpointApi.Responses, api);
        Assert.Equal([EndpointApi.Responses], candidates);
        Assert.Equal(2, client.Calls.Count);
        Assert.Equal(ChatToolMode.RequireSpecific("windowsaxon_api_probe"), client.Calls[0].Options!.ToolMode);
        Assert.Equal("windowsaxon_api_probe", Assert.Single(client.Calls[0].Options!.Tools!).Name);
        Assert.Single(client.Calls[0].Messages);
    }

    [Theory]
    [InlineData(404, "Not Found")]
    [InlineData(405, "Method Not Allowed")]
    [InlineData(501, "Not Implemented")]
    [InlineData(400, "This model does not support the Responses endpoint.")]
    [InlineData(404, "This model is not supported with the Responses API.")]
    [InlineData(422, "Function calling is not supported for this model.")]
    public async Task Explicit_incompatibility_falls_back_to_chat_completions(int status, string message)
    {
        var detector = new EndpointApiDetector();
        var candidates = new List<EndpointApi>();
        var api = await detector.DetectAsync(Settings(), candidate =>
        {
            candidates.Add(candidate);

            return candidate == EndpointApi.Responses
                ? new ScriptedChatClient().ReplyWith((_, _) => throw Error(status, message))
                : SuccessfulProbe();
        }, Ct);

        Assert.Equal(EndpointApi.ChatCompletions, api);
        Assert.Equal([EndpointApi.Responses, EndpointApi.ChatCompletions], candidates);
    }

    [Theory]
    [InlineData(401, "Invalid API key")]
    [InlineData(403, "Access denied")]
    [InlineData(429, "Rate limit exceeded")]
    [InlineData(500, "Internal Server Error")]
    [InlineData(404, "The model does not exist or you do not have access to it.")]
    [InlineData(400, "Unsupported parameter: temperature")]
    [InlineData(400, "Invalid tool schema")]
    public async Task Other_errors_are_preserved_without_fallback(int status, string message)
    {
        var detector = new EndpointApiDetector();
        var expected = Error(status, message);
        var candidates = new List<EndpointApi>();

        var error = await Assert.ThrowsAsync<ClientResultException>(() => detector.DetectAsync(Settings(), candidate =>
        {
            candidates.Add(candidate);

            return new ScriptedChatClient().ReplyWith((_, _) => throw expected);
        }, Ct));

        Assert.Same(expected, error);
        Assert.Equal([EndpointApi.Responses], candidates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_tool_calls_or_ignored_tool_results_do_not_count_as_support(bool ignoreResult)
    {
        var detector = new EndpointApiDetector();
        var api = await detector.DetectAsync(Settings(), candidate => candidate == EndpointApi.ChatCompletions
            ? SuccessfulProbe()
            : ignoreResult
                ? new ScriptedChatClient().ReplyCall("windowsaxon_api_probe").Reply("Ignored result")
                : new ScriptedChatClient().Reply("Plain text only"), Ct);

        Assert.Equal(EndpointApi.ChatCompletions, api);
    }

    [Fact]
    public async Task Neither_protocol_supporting_tools_reports_both_failures_and_does_not_cache()
    {
        var detector = new EndpointApiDetector();
        var count = 0;

        IChatClient Create(EndpointApi api)
        {
            count++;

            return new ScriptedChatClient().Reply("No tools");
        }

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => detector.DetectAsync(Settings(), Create, Ct));
        Assert.Equal(2, Assert.IsType<AggregateException>(error.InnerException).InnerExceptions.Count);
        await Assert.ThrowsAsync<NotSupportedException>(() => detector.DetectAsync(Settings(), Create, Ct));
        Assert.Equal(4, count);
    }

    [Fact]
    public async Task Cached_success_expires_after_one_hour_and_can_be_invalidated()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var detector = new EndpointApiDetector(time);
        var count = 0;

        IChatClient Create(EndpointApi api)
        {
            count++;

            return SuccessfulProbe();
        }

        await detector.DetectAsync(Settings(), Create, Ct);
        time.Now = time.Now.AddMinutes(59);
        await detector.DetectAsync(Settings(), Create, Ct);
        Assert.Equal(1, count);
        time.Now = time.Now.AddMinutes(1);
        await detector.DetectAsync(Settings(), Create, Ct);
        Assert.Equal(2, count);
        detector.Invalidate();
        await detector.DetectAsync(Settings(), Create, Ct);
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task Cache_is_separate_for_endpoint_model_and_credential_reference()
    {
        var detector = new EndpointApiDetector();
        var count = 0;

        IChatClient Create(EndpointApi api)
        {
            count++;

            return SuccessfulProbe();
        }

        await detector.DetectAsync(Settings(), Create, Ct);
        await detector.DetectAsync(Settings(url: "http://localhost:8000/v1/"), Create, Ct);
        Assert.Equal(1, count);
        await detector.DetectAsync(Settings(url: "http://localhost:8001/v1"), Create, Ct);
        await detector.DetectAsync(Settings(model: "other-model"), Create, Ct);
        await detector.DetectAsync(Settings(credential: "credential:other"), Create, Ct);
        Assert.Equal(4, count);
    }

    [Fact]
    public async Task Concurrent_detection_reuses_the_successful_probe()
    {
        var detector = new EndpointApiDetector();
        var count = 0;
        var tasks = Enumerable.Range(0, 5).Select(_ => detector.DetectAsync(Settings(), api =>
        {
            Interlocked.Increment(ref count);

            return SuccessfulProbe();
        }, Ct));

        Assert.All(await Task.WhenAll(tasks), api => Assert.Equal(EndpointApi.Responses, api));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Cancellation_does_not_fall_back_or_cache_a_result()
    {
        var detector = new EndpointApiDetector();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var count = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => detector.DetectAsync(Settings(), api =>
        {
            count++;

            return new ScriptedChatClient().ReplyWith((_, _) =>
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();

                throw new InvalidOperationException("Unreachable");
            });
        }, cancellation.Token));

        Assert.Equal(1, count);
        await detector.DetectAsync(Settings(), _ => SuccessfulProbe(), Ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Network_errors_and_timeouts_do_not_switch_protocol(bool timeout)
    {
        var detector = new EndpointApiDetector();
        Exception expected = timeout ? new TimeoutException("Timed out") : new HttpRequestException("Connection failed");
        var candidates = new List<EndpointApi>();

        var actual = await Assert.ThrowsAnyAsync<Exception>(() => detector.DetectAsync(Settings(), api =>
        {
            candidates.Add(api);

            return new ScriptedChatClient().ReplyWith((_, _) => throw expected);
        }, Ct));

        Assert.Same(expected, actual);
        Assert.Equal([EndpointApi.Responses], candidates);
    }

    [Theory]
    [InlineData("model_not_found", null)]
    [InlineData(null, "model")]
    public void Missing_model_metadata_is_preserved_even_without_model_in_the_message(string? code, string? parameter)
    {
        Assert.False(EndpointApiDetector.IsCompatibilityError(Error(404, "Not found", code, parameter)));
    }
}
