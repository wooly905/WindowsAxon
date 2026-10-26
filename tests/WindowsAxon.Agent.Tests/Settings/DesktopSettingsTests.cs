using WindowsAxon.Agent.Selection;
using WindowsAxon.Agent.Settings;
using WindowsAxon.App;
using NSubstitute;

namespace WindowsAxon.Agent.Tests.Settings;

public sealed class DesktopSettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"windowsaxon-settings-{Guid.NewGuid():N}");
    private readonly WindowsSecretStore _secrets = new();
    private readonly HashSet<string> _credentials = [];

    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact]
    public void Endpoint_validation_rejects_null_url_with_its_parameter_name()
    {
        var error = Assert.Throws<ArgumentNullException>(() => DesktopChatService.ValidateEndpoint(null!));

        Assert.Equal("baseUrl", error.ParamName);
    }

    private DesktopChatService CreateService(string? path = null) =>
        new(Substitute.For<IApprovalPrompter>(),
            Substitute.For<IStepLimitPrompter>(),
            path ?? Path.Combine(_folder,
                                 "settings.json"));

    private string TrackKey(DesktopChatService service)
    {
        var name = service.Settings.ChatEndpoint.ApiKey!["credential:".Length..];
        _credentials.Add(name);
        return name;
    }

    [Fact(Skip = "Windows Credential Manager requires Windows.", SkipUnless = nameof(IsWindows))]
    public void Save_persists_a_credential_reference_and_reload_reads_the_key()
    {
        using var service = CreateService();
        const string key = "test-secret-not-for-json";
        service.SaveSettings("https://api.example.com/v1", "remote-model", key);
        var name = TrackKey(service);

        var json = File.ReadAllText(Path.Combine(_folder, "settings.json"));
        Assert.DoesNotContain(key, json);
        Assert.Contains($"credential:{name}", json);
        Assert.Equal(key, _secrets.Read(name));

        using var reloaded = CreateService();
        reloaded.LoadSettings();
        Assert.Equal(service.Settings.ChatEndpoint, reloaded.Settings.ChatEndpoint);
        Assert.Equal("remote-model", reloaded.Settings.Chat.Model);
    }

    [Fact(Skip = "Windows Credential Manager requires Windows.", SkipUnless = nameof(IsWindows))]
    public void Blank_key_preserves_the_saved_key_when_only_the_model_changes()
    {
        using var service = CreateService();
        service.SaveSettings("https://api.example.com/v1", "model-1", "test-preserve");
        var name = TrackKey(service);

        service.SaveSettings("https://API.EXAMPLE.COM:443/v1/", "model-2", api: EndpointApi.Responses);

        Assert.Equal($"credential:{name}", service.Settings.ChatEndpoint.ApiKey);
        Assert.Equal("test-preserve", _secrets.Read(name));
        Assert.Equal("model-2", service.Settings.Chat.Model);
        Assert.Equal(EndpointApi.Responses, service.Settings.ChatEndpoint.Api);
    }

    [Fact(Skip = "Windows Credential Manager requires Windows.", SkipUnless = nameof(IsWindows))]
    public void Replacement_saves_the_new_key_and_deletes_the_old_managed_key()
    {
        using var service = CreateService();
        service.SaveSettings("https://api.example.com/v1", "model", "old-test-key");
        var oldName = TrackKey(service);

        service.SaveSettings("https://api.example.com/v1", "model", "new-test-key");
        var newName = TrackKey(service);

        Assert.NotEqual(oldName, newName);
        Assert.Equal("new-test-key", _secrets.Read(newName));
        Assert.Throws<InvalidOperationException>(() => _secrets.Read(oldName));
    }

    [Fact(Skip = "Windows Credential Manager requires Windows.", SkipUnless = nameof(IsWindows))]
    public void Remove_clears_the_reference_and_deletes_the_managed_key()
    {
        using var service = CreateService();
        service.SaveSettings("https://api.example.com/v1", "model", "test-remove");
        var name = TrackKey(service);

        service.SaveSettings("https://api.example.com/v1", "model", removeApiKey: true);

        Assert.Null(service.Settings.ChatEndpoint.ApiKey);
        Assert.Throws<InvalidOperationException>(() => _secrets.Read(name));
        using var reloaded = CreateService();
        reloaded.LoadSettings();
        Assert.Null(reloaded.Settings.ChatEndpoint.ApiKey);
    }

    [Theory(Skip = "Windows Credential Manager requires Windows.", SkipUnless = nameof(IsWindows))]
    [InlineData("https://other.example.com/v1")]
    [InlineData("https://api.example.com/v2")]
    [InlineData("http://api.example.com/v1")]
    public void Endpoint_changes_do_not_reuse_the_previous_key(string url)
    {
        using var service = CreateService();
        service.SaveSettings("https://api.example.com/v1", "model", "test-bound-key");
        var name = TrackKey(service);

        service.SaveSettings(url, "model");

        Assert.Null(service.Settings.ChatEndpoint.ApiKey);
        Assert.Throws<InvalidOperationException>(() => _secrets.Read(name));
    }

    [Fact]
    public void Remote_http_rejects_a_key_before_writing_settings()
    {
        using var service = CreateService();
        var previous = service.Settings;

        var error = Assert.Throws<ArgumentException>(() => service.SaveSettings("http://192.168.1.20/v1",
                                                                                "model",
                                                                                "test-key"));

        Assert.Contains("HTTPS", error.Message);
        Assert.DoesNotContain("test-key", error.Message);
        Assert.Same(previous, service.Settings);
        Assert.False(Directory.Exists(_folder));
    }

    [Theory]
    [InlineData("https://api.example.com/v1", "none", null, StepMode.Native)]
    [InlineData("https://api.example.com/v1", "low", "low", StepMode.Native)]
    [InlineData("http://localhost:11434/v1", "none", "none", StepMode.Structured)]
    public void Remote_auto_settings_use_native_tools_and_omit_the_local_reasoning_default(string url,
                                                                                           string effort,
                                                                                           string? expected,
                                                                                           StepMode mode)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "settings.json"),
                          $$"""
            { "Models": { "Chat": { "ReasoningEffortWhenThinkingOff": "{{effort}}" } } }
            """);
        using var service = CreateService();
        service.LoadSettings();

        service.SaveSettings(url, "auto");

        Assert.Equal("auto", service.Settings.Chat.Model);
        Assert.Equal(expected, service.Settings.Chat.ReasoningEffortWhenThinkingOff);
        Assert.Equal(mode, service.Settings.Chat.StepMode);
        using var reloaded = CreateService();
        reloaded.LoadSettings();
        Assert.Equal(expected, reloaded.Settings.Chat.ReasoningEffortWhenThinkingOff);
        Assert.Equal(mode, reloaded.Settings.Chat.StepMode);
    }

    [Fact(Skip = "Windows Credential Manager requires Windows.", SkipUnless = nameof(IsWindows))]
    public void Loopback_http_accepts_an_optional_key()
    {
        using var service = CreateService();
        service.SaveSettings("http://127.0.0.1:8000/v1", "model", "test-local-key");
        Assert.Equal("test-local-key", _secrets.Read(TrackKey(service)));
    }

    [Fact]
    public void Responses_selection_persists_for_a_local_codex_endpoint()
    {
        using var service = CreateService();
        service.SaveSettings("http://localhost:5168/v1", "gpt-6.1-sol", api: EndpointApi.Responses);

        using var reloaded = CreateService();
        reloaded.LoadSettings();
        Assert.Equal(EndpointApi.Responses, reloaded.Settings.ChatEndpoint.Api);
        Assert.True(reloaded.Settings.UsesResponsesApi);
        Assert.Equal(StepMode.Native, reloaded.Settings.Chat.StepMode);
        Assert.Null(reloaded.Settings.Chat.ReasoningEffortWhenThinkingOff);

        reloaded.SaveSettings("http://localhost:5168/v1", "gpt-6.1-sol");
        Assert.Equal(EndpointApi.Responses, reloaded.Settings.ChatEndpoint.Api);
    }

    [Fact(Skip = "Windows Credential Manager requires Windows.", SkipUnless = nameof(IsWindows))]
    public void Removing_a_manual_reference_preserves_the_external_credential()
    {
        var name = $"WindowsAxon/test-external/{Guid.NewGuid():N}";
        _credentials.Add(name);
        _secrets.Write(name, "external-test-key");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "settings.json"),
                          $$"""
            { "Models": { "Endpoints": { "cloud": { "BaseUrl": "https://api.example.com/v1", "ApiKey": "credential:{{name}}" } }, "Chat": { "Endpoint": "cloud" } } }
            """);
        using var service = CreateService();
        service.LoadSettings();

        service.SaveSettings("https://api.example.com/v1", "model", removeApiKey: true);

        Assert.Null(service.Settings.ChatEndpoint.ApiKey);
        Assert.Equal("external-test-key", _secrets.Read(name));
    }

    [Fact(Skip = "Windows Credential Manager requires Windows.", SkipUnless = nameof(IsWindows))]
    public void Save_failure_keeps_the_previous_configuration_and_key()
    {
        using var service = CreateService();
        service.SaveSettings("https://api.example.com/v1", "model", "test-original");
        var name = TrackKey(service);
        var previous = service.Settings;
        var path = Path.Combine(_folder, "settings.json");
        using var lockedSettings = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var error = Record.Exception(() => service.SaveSettings("https://api.example.com/v1",
                                                                "replacement",
                                                                "test-replacement"));
        Assert.True(error is IOException or UnauthorizedAccessException);

        Assert.Same(previous, service.Settings);
        Assert.Equal("test-original", _secrets.Read(name));
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact(Skip = "Windows Credential Manager requires Windows.", SkipUnless = nameof(IsWindows))]
    public void Credential_store_roundtrips_unicode_overwrites_and_deletes_idempotently()
    {
        var name = $"WindowsAxon/test-store/{Guid.NewGuid():N}";
        _credentials.Add(name);
        _secrets.Write(name, "test-測試-🔑");
        Assert.Equal("test-測試-🔑", _secrets.Read(name));
        _secrets.Write(name, "replacement");
        Assert.Equal("replacement", _secrets.Read(name));
        _secrets.Delete(name);
        _secrets.Delete(name);
        Assert.Throws<InvalidOperationException>(() => _secrets.Read(name));
    }

    public void Dispose()
    {
        foreach (var name in _credentials)
        {
            _secrets.Delete(name);
        }
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
