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
    [InlineData("https://api.example.com/v1", "auto", true)]
    [InlineData("https://api.example.com/v1", "gpt-5.4", false)]
    [InlineData("http://localhost:11434/v1", "auto", false)]
    [InlineData("http://localhost:11434/v1", "local-model", false)]
    public void Remote_auto_uses_responses_while_other_models_keep_chat_completions(string url,
                                                                                    string model,
                                                                                    bool responses)
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

        using var client = new EndpointFactory(settings, Substitute.For<ISecretStore>()).CreateChatClient();

        Assert.Equal(responses, client.GetService<OpenAI.Responses.ResponsesClient>() is not null);
        Assert.Equal(!responses, client.GetService<OpenAI.Chat.ChatClient>() is not null);
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
    public void Explicit_api_selection_overrides_the_automatic_route(string url,
                                                                    string model,
                                                                    EndpointApi api,
                                                                    bool responses)
    {
        var settings = new ModelSettings
        {
            Endpoints = new() { ["e"] = new EndpointSettings { BaseUrl = url, Api = api } },
            Chat = new ChatSettings { Endpoint = "e", Model = model },
        };
        using var client = new EndpointFactory(settings, Substitute.For<ISecretStore>()).CreateChatClient();

        Assert.Equal(responses, client.GetService<OpenAI.Responses.ResponsesClient>() is not null);
        Assert.Equal(!responses, client.GetService<OpenAI.Chat.ChatClient>() is not null);
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
