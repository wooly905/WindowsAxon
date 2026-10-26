using WindowsAxon.Agent.Settings;

namespace WindowsAxon.Agent.Tests.Settings;

public class ModelSettingsTests
{
    [Fact]
    public void Load_reads_endpoints_and_chat_settings()
    {
        const string json = """
            {
              "Models": {
                "Endpoints": {
                  "local": { "BaseUrl": "http://localhost:11434/v1" },
                  "cloud": { "BaseUrl": "https://api.example.com/v1", "ApiKey": "credential:WindowsAxon/cloud", "Api": "Responses" }
                },
                "Chat": { "Endpoint": "cloud", "Model": "m1", "ContextWindow": 8192, "StepMode": "Native", "ArgsSchema": "Free", "MaxActiveTools": 10 }
              }
            }
            """;

        var settings = ModelSettings.Load(json);

        Assert.Equal("https://api.example.com/v1", settings.ChatEndpoint.BaseUrl);
        Assert.Equal(EndpointApi.Responses, settings.ChatEndpoint.Api);
        Assert.True(settings.UsesResponsesApi);
        Assert.Equal("m1", settings.Chat.Model);
        Assert.Equal(8192, settings.Chat.ContextWindow);
        Assert.Equal(StepMode.Native, settings.Chat.StepMode);
        Assert.Equal(ArgsSchemaMode.Free, settings.Chat.ArgsSchema);
        Assert.Equal(10, settings.Chat.MaxActiveTools);
        Assert.True(settings.IsChatRemote);
    }

    [Fact]
    public void Defaults_use_the_local_ollama_endpoint()
    {
        var settings = ModelSettings.Load("""{ "Models": {} }""");

        Assert.Equal("http://localhost:11434/v1", settings.ChatEndpoint.BaseUrl);
        Assert.Equal(16384, settings.Chat.ContextWindow);
        Assert.Equal(StepMode.Structured, settings.Chat.StepMode);
        Assert.Equal(ArgsSchemaMode.PerTool, settings.Chat.ArgsSchema);
        Assert.Equal(14, settings.Chat.MaxActiveTools);
        Assert.False(settings.Chat.Thinking);
        Assert.False(settings.IsChatRemote);
        Assert.Equal(EndpointApi.Automatic, settings.ChatEndpoint.Api);
        Assert.False(settings.UsesResponsesApi);
    }

    [Fact]
    public void Unknown_endpoint_name_is_a_clear_error()
    {
        var settings = ModelSettings.Load("""{ "Models": { "Chat": { "Endpoint": "nope" } } }""");

        var error = Assert.Throws<InvalidOperationException>(() => settings.ChatEndpoint);
        Assert.Contains("nope", error.Message);
    }

    [Theory]
    [InlineData("http://localhost:11434/v1", false)]
    [InlineData("http://127.0.0.1:8000/v1", false)]
    [InlineData("http://[::1]:1234/v1", false)]
    [InlineData("http://192.168.1.20:8000/v1", true)]
    [InlineData("https://api.openai.com/v1", true)]
    public void IsRemote_is_true_for_non_loopback_addresses(string url, bool remote) =>
        Assert.Equal(remote, EndpointRules.IsRemote(url));

    [Theory]
    [InlineData("https://api.openai.com/v1", true)]
    [InlineData("http://localhost:8000/v1", true)]
    [InlineData("http://192.168.1.20:8000/v1", false)]
    public void Api_key_is_never_sent_over_http_to_a_remote_address(string url, bool allowed) =>
        Assert.Equal(allowed, EndpointRules.MaySendApiKey(url));
}
