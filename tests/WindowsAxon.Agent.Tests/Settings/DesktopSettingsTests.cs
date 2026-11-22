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

    [Fact]
    public void Skill_tool_names_include_the_app_window_and_shared_tools()
    {
        using var service = CreateService();
        var names = service.ToolNames;

        Assert.Contains("app_find", names);
        Assert.Contains("ui_list_windows", names);
        Assert.Contains("net_ping", names);
    }

    [Fact]
    public void Skill_tool_names_include_the_listed_tools_of_each_mcp_server()
    {
        var path = WriteSettings("""{ "Models": { "Chat": { "McpServers": [ { "Name": "pulsar", "Url": "http://data.example:8020/mcp", "Tools": ["get_product"] } ] } } }""");
        using var service = CreateService(path);
        service.LoadSettings();

        Assert.Contains("mcp_pulsar_get_product", service.ToolNames);
        Assert.Contains("app_find", service.ToolNames);
    }

    [Theory]
    [InlineData("""{ "Name": "Bad-Name", "Url": "http://data.example/mcp" }""")]
    [InlineData("""{ "Name": "both", "Url": "http://data.example/mcp", "Command": "server.exe" }""")]
    [InlineData("""{ "Name": "none" }""")]
    [InlineData("""{ "Name": "plain", "Url": "https://data.example/mcp", "Headers": { "Authorization": "Bearer abc" } }""")]
    [InlineData("""{ "Name": "remotehttp", "Url": "http://data.example/mcp", "Headers": { "Authorization": "credential:x" } }""")]
    [InlineData("""{ "Name": "stdio", "Command": "server.exe", "Headers": { "Authorization": "credential:x" } }""")]
    public void Load_rejects_an_invalid_mcp_server(string server)
    {
        var path = WriteSettings($$"""{ "Models": { "Chat": { "McpServers": [ {{server}} ] } } }""");
        using var service = CreateService(path);

        Assert.Throws<ArgumentException>(service.LoadSettings);
    }

    [Fact]
    public void Load_rejects_two_mcp_servers_with_the_same_name()
    {
        var path = WriteSettings("""{ "Models": { "Chat": { "McpServers": [ { "Name": "a", "Url": "http://x/mcp" }, { "Name": "a", "Command": "s.exe" } ] } } }""");
        using var service = CreateService(path);

        Assert.Throws<ArgumentException>(service.LoadSettings);
    }

    [Fact]
    public void Save_keeps_the_mcp_servers()
    {
        var path = WriteSettings("""{ "Models": { "Chat": { "McpServers": [ { "Name": "pulsar", "Url": "http://data.example:8020/mcp" } ] } } }""");
        using var service = CreateService(path);
        service.LoadSettings();

        service.SaveSettings("http://localhost:11434/v1", "local-model");
        using var reloaded = CreateService(path);
        reloaded.LoadSettings();

        Assert.Equal("http://data.example:8020/mcp", Assert.Single(reloaded.Settings.Chat.McpServers).Url);
        Assert.DoesNotContain("\"Group\"", File.ReadAllText(path));
    }

    [Fact]
    public void Reasoning_settings_persist()
    {
        using var service = CreateService();
        service.SaveReasoning(true, "high");

        using var reloaded = CreateService();
        reloaded.LoadSettings();

        Assert.True(reloaded.Settings.Chat.Thinking);
        Assert.Equal("high", reloaded.Settings.Chat.ReasoningEffort);
    }

    [Fact]
    public void Mcp_server_add_rename_and_delete_persist()
    {
        using var service = CreateService();
        service.SaveMcpServer(new McpServerSettings { Name = "pulsar", Url = "http://data.example:8020/mcp", Tools = ["get_product"] }, null);
        service.SaveMcpServer(new McpServerSettings { Name = "files", Command = "npx", Args = ["-y", "server"] }, null);
        service.SaveMcpServer(new McpServerSettings { Name = "pulsar2", Url = "http://data.example:8020/mcp" }, "pulsar");

        using var reloaded = CreateService();
        reloaded.LoadSettings();
        Assert.Equal(["learn", "pulsar2", "files"], reloaded.Settings.Chat.McpServers.Select(s => s.Name));
        Assert.Equal(["-y", "server"], reloaded.Settings.Chat.McpServers[2].Args);

        Assert.True(reloaded.Settings.Chat.McpServers[1].Enabled);
        reloaded.SetMcpServerEnabled("pulsar2", false);
        Assert.DoesNotContain("mcp_pulsar2_", string.Join(",", reloaded.ToolNames));
        using (var again = CreateService())
        {
            again.LoadSettings();
            Assert.False(again.Settings.Chat.McpServers[1].Enabled);
            Assert.True(again.Settings.Chat.McpServers[2].Enabled);
        }

        reloaded.DeleteMcpServer("pulsar2");
        Assert.Equal(["learn", "files"], reloaded.Settings.Chat.McpServers.Select(s => s.Name));
    }

    [Fact]
    public void New_settings_include_the_default_mcp_servers_and_a_deleted_server_stays_deleted()
    {
        using var service = CreateService();

        var learn = Assert.Single(service.Settings.Chat.McpServers);
        Assert.Equal("https://learn.microsoft.com/api/mcp", learn.Url);
        Assert.True(learn.Enabled);
        Assert.False(learn.AutoApprove);
        Assert.Contains("mcp_learn_microsoft_docs_search", service.ToolNames);
        Assert.Contains("mcp_learn_microsoft_docs_fetch", service.ToolNames);
        Assert.Contains("mcp_learn_microsoft_code_sample_search", service.ToolNames);

        var path = WriteSettings("""{ "models": { "chat": { "Model": "m" } } }""");
        using var withoutList = CreateService(path);
        withoutList.LoadSettings();
        Assert.Equal(["learn"], withoutList.Settings.Chat.McpServers.Select(s => s.Name));

        withoutList.DeleteMcpServer("learn");
        using var reloaded = CreateService(path);
        reloaded.LoadSettings();

        Assert.Empty(reloaded.Settings.Chat.McpServers);
    }

    [Fact]
    public void Mcp_server_save_rejects_invalid_or_duplicate_servers_without_writing()
    {
        using var service = CreateService();
        service.SaveMcpServer(new McpServerSettings { Name = "pulsar", Url = "http://data.example/mcp" }, null);

        Assert.Throws<ArgumentException>(() => service.SaveMcpServer(new McpServerSettings { Name = "pulsar", Command = "s.exe" }, null));
        Assert.Throws<ArgumentException>(() => service.SaveMcpServer(new McpServerSettings { Name = "Bad", Url = "http://x/mcp" }, null));
        Assert.Throws<ArgumentException>(() => service.SaveMcpServer(new McpServerSettings { Name = "remote", Url = "http://x/mcp" },
                                                                     null,
                                                                     "Authorization",
                                                                     "Bearer test"));
        Assert.Throws<ArgumentException>(() => service.SaveMcpServer(new McpServerSettings { Name = "nokey", Url = "https://x/mcp" },
                                                                     null,
                                                                     "Authorization"));
        Assert.Equal(["learn", "pulsar"], service.Settings.Chat.McpServers.Select(s => s.Name));
    }

    [Fact(Skip = "Windows Credential Manager requires Windows.", SkipUnless = nameof(IsWindows))]
    public void Mcp_header_value_is_stored_as_a_credential_and_removed_with_the_server()
    {
        using var service = CreateService();
        const string secret = "Bearer test-secret-not-for-json";
        service.SaveMcpServer(new McpServerSettings { Name = "secured", Url = "https://mcp.example.com/mcp" }, null, "Authorization", secret);
        var reference = service.Settings.Chat.McpServers.Single(s => s.Name == "secured").Headers["Authorization"];
        var name = reference["credential:".Length..];
        _credentials.Add(name);

        Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(_folder, "settings.json")));
        Assert.Equal(secret, _secrets.Read(name));

        service.SaveMcpServer(new McpServerSettings { Name = "secured", Url = "https://mcp.example.com/mcp", Tools = ["a"] },
                              "secured",
                              "Authorization");
        Assert.Equal(reference, service.Settings.Chat.McpServers.Single(s => s.Name == "secured").Headers["Authorization"]);

        service.DeleteMcpServer("secured");
        Assert.Throws<InvalidOperationException>(() => _secrets.Read(name));
    }

    private string WriteSettings(string json)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "settings.json");
        File.WriteAllText(path, json);
        return path;
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
    public void Switching_provider_keeps_each_provider_key_and_models()
    {
        using var service = CreateService();
        service.SaveSettings("https://api.example.com/v1", "auto", "test-first");
        var first = TrackKey(service);
        var groq = ProviderPresets.All["Groq"].BaseUrl;

        service.SaveSettings(groq, "openai/gpt-oss-20b", "test-groq", endpointName: "groq");
        var second = TrackKey(service);
        service.SaveSettings(groq, "custom-model", endpointName: "Groq");

        Assert.Equal("Groq", service.Settings.Chat.Endpoint);
        Assert.Equal($"credential:{second}", service.Settings.ChatEndpoint.ApiKey);
        Assert.Contains("custom-model", service.Settings.ChatEndpoint.Models);
        Assert.Contains("qwen/qwen3.8-27b", service.Settings.ChatEndpoint.Models);
        Assert.Equal($"credential:{first}", service.Settings.Endpoints["local"].ApiKey);
        Assert.Equal("test-first", _secrets.Read(first));

        using var reloaded = CreateService();
        reloaded.LoadSettings();
        Assert.Equal(service.Settings.ChatEndpoint.Models, reloaded.Settings.ChatEndpoint.Models);
        Assert.Equal("custom-model", reloaded.Settings.Chat.Model);
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
    [InlineData("https://api.example.com/v1", "none", null, StepMode.Structured)]
    [InlineData("https://api.example.com/v1", "low", "low", StepMode.Structured)]
    [InlineData("http://localhost:11434/v1", "none", "none", StepMode.Structured)]
    public void Automatic_settings_defer_tool_mode_until_detection_and_keep_reasoning_preferences(string url,
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

    [Fact]
    public void Legacy_mesh_enabled_flag_maps_to_central_server_mode()
    {
        var path = WriteSettings("""{ "Models": { "Chat": { "Mesh": { "Enabled": true, "HubUrl": "ws://192.168.1.10:5250/ws", "NodeName": "Desk" } } } }""");
        using var service = CreateService(path);
        service.LoadSettings();

        Assert.Equal(MeshMode.CentralServer, service.Settings.Chat.Mesh.EffectiveMode);
        Assert.True(service.Settings.Chat.Mesh.IsEnabled);
    }

    [Fact]
    public void Mesh_validation_rejects_p2p_without_pair_key_and_central_without_hub_url()
    {
        using var service = CreateService();

        Assert.Throws<ArgumentException>(() => service.SaveMeshSettings(MeshMode.PeerToPeer, "PC-A"));
        Assert.Throws<ArgumentException>(() => service.SaveMeshSettings(MeshMode.CentralServer, "PC-A", hubUrl: ""));
    }

    [Fact(Skip = "Windows Credential Manager requires Windows.", SkipUnless = nameof(IsWindows))]
    public void Mesh_mode_switching_persists_pair_key_and_token_credentials_and_supports_disable_all()
    {
        using var service = CreateService();
        service.SaveMeshSettings(MeshMode.PeerToPeer, "PC-A", pairKey: "lan-secret-1");
        var pairKeyRef = service.Settings.Chat.Mesh.PairKey;
        Assert.NotNull(pairKeyRef);
        var pairKeyCred = pairKeyRef[MeshSettings.CredentialPrefix.Length..];
        _credentials.Add(pairKeyCred);

        Assert.Equal(MeshMode.PeerToPeer, service.Settings.Chat.Mesh.EffectiveMode);
        Assert.DoesNotContain("lan-secret-1", File.ReadAllText(Path.Combine(_folder, "settings.json")));
        Assert.Equal("lan-secret-1", _secrets.Read(pairKeyCred));

        // Switching to Disabled keeps the saved pair key so re-enabling P2P does not require re-typing it.
        service.SaveMeshSettings(MeshMode.Disabled, "PC-A");
        Assert.Equal(MeshMode.Disabled, service.Settings.Chat.Mesh.EffectiveMode);
        Assert.False(service.Settings.Chat.Mesh.IsEnabled);
        Assert.Equal(pairKeyRef, service.Settings.Chat.Mesh.PairKey);

        // Switching to Central Server saves hub token while keeping saved pair key.
        service.SaveMeshSettings(MeshMode.CentralServer, "PC-A", hubUrl: "ws://127.0.0.1:5250/ws", token: "hub-secret-1");
        var tokenRef = service.Settings.Chat.Mesh.Token;
        Assert.NotNull(tokenRef);
        var tokenCred = tokenRef[MeshSettings.CredentialPrefix.Length..];
        _credentials.Add(tokenCred);
        Assert.Equal("hub-secret-1", _secrets.Read(tokenCred));

        // Reloading from disk preserves mode and credential references.
        using var reloaded = CreateService();
        reloaded.LoadSettings();
        Assert.Equal(MeshMode.CentralServer, reloaded.Settings.Chat.Mesh.EffectiveMode);
        Assert.Equal(pairKeyRef, reloaded.Settings.Chat.Mesh.PairKey);
        Assert.Equal(tokenRef, reloaded.Settings.Chat.Mesh.Token);
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
