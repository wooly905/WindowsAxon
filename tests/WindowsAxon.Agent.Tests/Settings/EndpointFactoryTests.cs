using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using WindowsAxon.Agent.Settings;
using WindowsAxon.Agent.Tests.Fakes;
using NSubstitute;
using ChatCompletionOptions = OpenAI.Chat.ChatCompletionOptions;

namespace WindowsAxon.Agent.Tests.Settings;

public class EndpointFactoryTests
{
    private static ModelSettings WithEndpoint(string url, string? key) => new()
    {
        Endpoints = new()
        {
            ["e"] = new EndpointSettings { BaseUrl = url, ApiKey = key }
        },
        Chat = new ChatSettings { Endpoint = "e" },
    };

    [Fact]
    public void Key_reference_is_resolved_from_the_secret_store()
    {
        var secrets = Substitute.For<ISecretStore>();
        secrets.Read("WindowsAxon/cloud").Returns("k-123");
        var factory = new EndpointFactory(WithEndpoint("https://api.example.com/v1", "credential:WindowsAxon/cloud"),
                                          secrets);

        Assert.Equal("k-123", factory.ResolveKey(factory.Settings.ChatEndpoint));
    }

    [Fact]
    public void Plain_text_key_in_configuration_is_rejected()
    {
        var factory = new EndpointFactory(WithEndpoint("https://api.example.com/v1", "sk-plain"),
                                          Substitute.For<ISecretStore>());

        Assert.Throws<InvalidOperationException>(() => factory.ResolveKey(factory.Settings.ChatEndpoint));
    }

    [Fact]
    public void Key_over_http_to_a_remote_address_is_rejected()
    {
        var factory = new EndpointFactory(WithEndpoint("http://192.168.1.20:8000/v1", "credential:x"),
                                          Substitute.For<ISecretStore>());

        var error = Assert.Throws<InvalidOperationException>(() => factory.ResolveKey(factory.Settings.ChatEndpoint));
        Assert.Contains("http", error.Message);
    }

    [Fact]
    public void Endpoint_without_key_resolves_to_null()
    {
        var factory = new EndpointFactory(WithEndpoint("http://localhost:11434/v1", null),
                                          Substitute.For<ISecretStore>());

        Assert.Null(factory.ResolveKey(factory.Settings.ChatEndpoint));
    }

    [Fact]
    public async Task Reasoning_effort_client_sets_the_raw_option()
    {
        var inner = new ScriptedChatClient().Reply("ok");
        using var client = new ReasoningEffortChatClient(inner, "none");

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")],
                                      cancellationToken: TestContext.Current.CancellationToken);

        var factory = inner.Calls[0].Options!.RawRepresentationFactory!;
        var raw = Assert.IsType<ChatCompletionOptions>(factory(inner));
        Assert.Equal("none", raw.ReasoningEffortLevel?.ToString());
    }

    [Theory]
    [InlineData("https://api.example.com/v1", "auto")]
    [InlineData("https://api.example.com/v1", "gpt-5.4")]
    [InlineData("http://localhost:11434/v1", "auto")]
    [InlineData("http://localhost:11434/v1", "local-model")]
    public void Automatic_requires_detection_before_creating_a_client(string url, string model)
    {
        var settings = WithEndpoint(url, null);
        settings = settings with
        {
            Chat = settings.Chat with
            {
                Model = model,
                ReasoningEffortWhenThinkingOff = null
            }
        };

        var factory = new EndpointFactory(settings, Substitute.For<ISecretStore>());

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateChatClient());
        Assert.Contains("ResolveAsync", error.Message);
    }

    [Fact]
    public async Task Reasoning_effort_client_supports_responses_options()
    {
        var inner = new ScriptedChatClient().Reply("ok");
        using var client = new ReasoningEffortChatClient(inner, "low");
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")],
                                      cancellationToken: TestContext.Current.CancellationToken);

        using var responseClient = new OpenAI.OpenAIClient("test-placeholder").GetResponsesClient()
            .AsIChatClient("auto");
        var factory = inner.Calls[0].Options!.RawRepresentationFactory!;
        var raw = Assert.IsType<OpenAI.Responses.CreateResponseOptions>(factory(responseClient));
        Assert.Equal("low", raw.ReasoningOptions?.ReasoningEffortLevel?.ToString());
    }

    [Theory]
    [InlineData("http://localhost:5168/v1", "gpt-6.1-sol", EndpointApi.Responses, true)]
    [InlineData("http://localhost:5168/v1", "auto", EndpointApi.Responses, true)]
    [InlineData("https://api.example.com/v1", "gpt-6.1-sol", EndpointApi.Responses, true)]
    [InlineData("https://api.example.com/v1", "auto", EndpointApi.ChatCompletions, false)]
    public async Task Explicit_api_selection_bypasses_detection(string url,
                                                                    string model,
                                                                    EndpointApi api,
                                                                    bool responses)
    {
        var settings = new ModelSettings
        {
            Endpoints = new() { ["e"] = new EndpointSettings { BaseUrl = url, Api = api } },
            Chat = new ChatSettings { Endpoint = "e", Model = model },
        };
        var resolved = await new EndpointFactory(settings, Substitute.For<ISecretStore>())
            .ResolveAsync(TestContext.Current.CancellationToken);
        using var client = resolved.CreateChatClient();

        Assert.Equal(responses, client.GetService<OpenAI.Responses.ResponsesClient>() is not null);
        Assert.Equal(!responses, client.GetService<OpenAI.Chat.ChatClient>() is not null);
        Assert.Equal(responses ? StepMode.Native : StepMode.Structured, resolved.Settings.Chat.StepMode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Automatic_probes_the_wire_protocol_and_uses_the_resolved_settings(bool supportsResponses)
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var settings = new ModelSettings
        {
            Endpoints = new() { ["e"] = new EndpointSettings { BaseUrl = $"http://localhost:{port}/v1" } },
            Chat = new ChatSettings { Endpoint = "e", Model = $"named-{Guid.NewGuid():N}" }
        };
        var requests = new List<(string Path, JsonObject Body)>();
        var serving = ServeAsync();
        var resolved = await new EndpointFactory(settings, Substitute.For<ISecretStore>()).ResolveAsync(timeout.Token);
        using var client = resolved.CreateChatClient();
        var answer = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Actual request")],
                                                    new ChatOptions { Temperature = 0.1f },
                                                    timeout.Token);
        await serving;

        Assert.Equal("actual answer", answer.Text);
        Assert.Equal(supportsResponses ? EndpointApi.Responses : EndpointApi.ChatCompletions,
                     resolved.Settings.ChatEndpoint.Api);
        Assert.Equal(StepMode.Native, resolved.Settings.Chat.StepMode);
        Assert.Null(resolved.Settings.Chat.ReasoningEffortWhenThinkingOff);
        Assert.Equal(EndpointApi.Automatic, settings.ChatEndpoint.Api);
        Assert.Equal(StepMode.Structured, settings.Chat.StepMode);
        Assert.Equal("/v1/responses", requests[0].Path);
        Assert.Equal(supportsResponses ? "/v1/responses" : "/v1/chat/completions", requests[^1].Path);
        Assert.All(requests, request =>
        {
            Assert.Equal(settings.Chat.Model, request.Body["model"]!.GetValue<string>());
            Assert.False(request.Body.ContainsKey("temperature"));
            Assert.False(request.Body.ContainsKey("reasoning_effort"));
            Assert.False(request.Body.ContainsKey("reasoning"));

            if (request.Path == "/v1/responses")
            {
                Assert.False(request.Body["store"]!.GetValue<bool>());
                Assert.False(request.Body.ContainsKey("previous_response_id"));
            }
        });
        Assert.All(requests.Take(requests.Count - 1), request => Assert.DoesNotContain("Actual request", request.Body.ToJsonString()));

        async Task ServeAsync()
        {
            var count = supportsResponses ? 3 : 4;

            for (var index = 0; index < count; index++)
            {
                var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                using var reader = new StreamReader(context.Request.InputStream);
                var request = JsonNode.Parse(await reader.ReadToEndAsync(timeout.Token))!.AsObject();
                var responses = context.Request.Url!.AbsolutePath == "/v1/responses";
                requests.Add((context.Request.Url.AbsolutePath, request));
                string json;

                if (responses && !supportsResponses)
                {
                    context.Response.StatusCode = 404;
                    json = """{"error":{"message":"Unknown endpoint","type":"invalid_request_error","code":"unsupported_endpoint"}}""";
                }
                else
                {
                    var history = request[responses ? "input" : "messages"]!.AsArray();
                    var result = history.FirstOrDefault(item => responses
                        ? item?["type"]?.GetValue<string>() == "function_call_output"
                        : item?["role"]?.GetValue<string>() == "tool");
                    var text = index == count - 1 ? "actual answer" : result?[responses ? "output" : "content"]?.GetValue<string>();
                    json = ProbeResponse(responses, settings.Chat.Model, text);
                }

                var bytes = Encoding.UTF8.GetBytes(json);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, timeout.Token);
                context.Response.Close();
            }
        }
    }

    private static string ProbeResponse(bool responses, string model, string? text)
    {
        var textJson = System.Text.Json.JsonSerializer.Serialize(text);

        if (responses)
        {
            var output = text is null
                ? """{"type":"function_call","id":"fc_probe","call_id":"probe_call","name":"windowsaxon_api_probe","arguments":"{}","status":"completed"}"""
                : $$"""{"type":"message","id":"msg_probe","role":"assistant","status":"completed","content":[{"type":"output_text","text":{{textJson}},"annotations":[]}]}""";

            return $$"""{"id":"resp_probe","object":"response","created_at":1,"status":"completed","model":"{{model}}","output":[{{output}}]}""";
        }

        var message = text is null
            ? """{"role":"assistant","tool_calls":[{"id":"probe_call","type":"function","function":{"name":"windowsaxon_api_probe","arguments":"{}"}}]}"""
            : $$"""{"role":"assistant","content":{{textJson}}}""";
        var reason = text is null ? "tool_calls" : "stop";

        return $$"""{"id":"chat_probe","object":"chat.completion","created":1,"model":"{{model}}","choices":[{"index":0,"message":{{message}},"finish_reason":"{{reason}}"}]}""";
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Responses_client_posts_full_history_and_reads_text_or_tool_calls(bool toolCall)
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var settings = new ModelSettings
        {
            Endpoints = new()
            {
                ["e"] = new EndpointSettings { BaseUrl = $"http://localhost:{port}/v1", Api = EndpointApi.Responses },
            },
            Chat = new ChatSettings { Endpoint = "e", Model = "gpt-6.1-sol", ReasoningEffortWhenThinkingOff = "low" },
        };
        using var client = new EndpointFactory(settings, Substitute.For<ISecretStore>()).CreateChatClient();
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "ok", "probe")] };
        var responseTask = client.GetResponseAsync([new ChatMessage(ChatRole.User, "Remember 4821."),
                                                   new ChatMessage(ChatRole.Assistant, "OK"),
                                                   new ChatMessage(ChatRole.User, "Which number?")],
                                                  options,
                                                  timeout.Token);
        var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
        var method = context.Request.HttpMethod;
        var path = context.Request.Url!.AbsolutePath;
        using var reader = new StreamReader(context.Request.InputStream);
        var request = JsonNode.Parse(await reader.ReadToEndAsync(timeout.Token))!.AsObject();
        var output = toolCall
            ? """{"type":"function_call","id":"fc_test","call_id":"call_test","name":"probe","arguments":"{}","status":"completed"}"""
            : """{"type":"message","id":"msg_test","role":"assistant","status":"completed","content":[{"type":"output_text","text":"4821","annotations":[]}]}""";
        var json = $$"""
            {"id":"resp_test","object":"response","created_at":1,"status":"completed","model":"gpt-6.1-sol","output":[{{output}}]}
            """;
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, timeout.Token);
        context.Response.Close();
        var response = await responseTask;

        Assert.Equal("POST", method);
        Assert.Equal("/v1/responses", path);
        Assert.False(request["store"]!.GetValue<bool>());
        Assert.False(request["stream"]?.GetValue<bool>() ?? false);
        Assert.False(request.ContainsKey("previous_response_id"));
        Assert.Equal(3, request["input"]!.AsArray().Count);
        Assert.Equal("gpt-6.1-sol", request["model"]!.GetValue<string>());
        Assert.Equal("low", request["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Equal("probe", request["tools"]![0]!["name"]!.GetValue<string>());

        if (toolCall)
        {
            var call = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
            Assert.Equal("call_test", call.CallId);
            Assert.Equal("probe", call.Name);
        }
        else
        {
            Assert.Equal("4821", response.Text);
        }
    }
}
