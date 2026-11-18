using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using WindowsAxon.Agent;
using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Audit;
using WindowsAxon.Agent.Selection;
using WindowsAxon.Agent.Settings;
using WindowsAxon.Agent.Skills;
using WindowsAxon.Agent.SubAgents;
using WindowsAxon.Agent.Tools;
using WindowsAxon.Windows;

namespace WindowsAxon.App;

public sealed class DesktopChatService(IApprovalPrompter approvals,
                                       IStepLimitPrompter stepLimits,
                                       string? settingsPath = null) : IDisposable
{
    private const string ManagedCredentialPrefix = "credential:WindowsAxon/desktop/";
    private const string McpCredentialPrefix = "WindowsAxon/mcp/";
    private static readonly JsonSerializerOptions SettingsJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly string DataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                             "WindowsAxon");
    private static readonly string? ConfigOverride = GetConfigOverride();

    public static string SettingsPath => ConfigOverride ?? Path.Combine(DataFolder, "settings.json");

    public static string BuiltInSkillsFolder => Path.Combine(AppContext.BaseDirectory, "app-skills");

    public static string UserSkillsFolder => WindowsToolSet.SkillsFolder(DataFolder);

    private static readonly TimeSpan McpConnectTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The names of every tool that a chat registers: the built-in tools and the listed tools of each MCP server.</summary>
    public IReadOnlySet<string> ToolNames =>
        KnownTools.Value.Concat(Settings.Chat.McpServers.Where(s => s.Enabled).SelectMany(s => s.Tools.Select(s.ToolName))).ToHashSet();

    // PublicationOnly: a failed build is not cached, so the next call tries again.
    private static readonly Lazy<IReadOnlySet<string>> KnownTools = new(() =>
    {
        var catalog = new ToolCatalog();
        using var model = new UnavailableChatClient();
        // An empty app index, so the names are read without scanning the installed apps.
        CreateTools(catalog,
                    new WindowsAppTools(() => [], (_, _) => Task.FromResult("")),
                    new WindowsWindowTools(),
                    model,
                    "skills",
                    () => { },
                    _ => { },
                    null).Dispose();
        return catalog.Entries.Select(e => e.Name).ToHashSet();
    }, LazyThreadSafetyMode.PublicationOnly);

    public SkillLibrary CreateSkillLibrary() => new(BuiltInSkillsFolder, UserSkillsFolder, ToolNames);

    /// <summary>Registers every chat tool. RunAsync and ToolNames both use it, so they cannot differ.</summary>
    private static WindowsToolSet CreateTools(ToolCatalog catalog,
                                              WindowsAppTools appTools,
                                              WindowsWindowTools windowTools,
                                              IChatClient model,
                                              string session,
                                              Action emergencyStop,
                                              Action<bool> inputActive,
                                              IEnumerable<string>? allowedPrivateHosts)
    {
        ToolRegistration.Add(catalog, appTools);
        ToolRegistration.Add(catalog, windowTools);
        return new WindowsToolSet(catalog,
                                  model,
                                  DataFolder,
                                  session,
                                  windowTools.ResolveHandle,
                                  emergencyStop,
                                  inputActive,
                                  allowedPrivateHosts);
    }

    private readonly string _settingsPath = settingsPath is null ? SettingsPath : Path.GetFullPath(settingsPath);

    private static string? GetConfigOverride()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--config");
        return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null;
    }

    private IChatClient? _model;
    private AgentRunner? _runner;
    private WindowsToolSet? _toolSet;

    public event Action? EmergencyStopRequested;

    public event Action<bool>? InputActiveChanged;

    public event Action<string>? ToolStarted;

    /// <summary>A message for the chat event list, for example an MCP server that did not connect.</summary>
    public event Action<string>? Notice;

    private bool _forceNewRequest;

    // Stdio MCP servers that the user allowed to start, by server, command, and arguments. Kept until the app exits.
    private readonly HashSet<string> _allowedMcpPrograms = [];

    public ModelSettings Settings { get; private set; } = WithDefaultMcpServers(new ModelSettings());

    public static string DefaultMcpServersPath => Path.Combine(AppContext.BaseDirectory, "default-mcp-servers.json");

    public void LoadSettings()
    {
        var path = settingsPath is not null
            || ConfigOverride is not null
            || File.Exists(_settingsPath) ? _settingsPath : Path.Combine(Environment.CurrentDirectory,
                                                                         "settings.local.json");
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            var settings = ModelSettings.Load(json);
            if (!HasMcpServers(json))
            {
                settings = WithDefaultMcpServers(settings);
            }

            ValidateEndpoint(settings.ChatEndpoint.BaseUrl);
            McpServerSettings.ValidateAll(settings.Chat.McpServers);
            Settings = settings;
        }
        else if (settingsPath is not null || ConfigOverride is not null)
        {
            throw new FileNotFoundException("The settings file does not exist.", path);
        }
    }

    /// <summary>A settings file without Models.Chat.McpServers gets the servers in default-mcp-servers.json. An empty list stays empty.</summary>
    private static ModelSettings WithDefaultMcpServers(ModelSettings settings)
    {
        if (!File.Exists(DefaultMcpServersPath))
        {
            return settings;
        }

        var servers = JsonSerializer.Deserialize<List<McpServerSettings>>(File.ReadAllText(DefaultMcpServersPath),
                                                                         new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        return settings with { Chat = settings.Chat with { McpServers = servers } };
    }

    private static bool HasMcpServers(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Child(document.RootElement, "Models") is { } models
            && Child(models, "Chat") is { } chat
            && Child(chat, "McpServers") is not null;

        // ModelSettings.Load matches property names without case.
        static JsonElement? Child(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object
                ? element.EnumerateObject()
                    .Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    .Select(p => (JsonElement?)p.Value)
                    .FirstOrDefault()
                : null;
    }

    public async Task<RunResult> RunAsync(string text, CancellationToken cancellationToken)
    {
        if (_runner is null)
        {
            _toolSet?.Dispose();
            _model?.Dispose();
            var factory = await new EndpointFactory(Settings, new WindowsSecretStore()).ResolveAsync(cancellationToken);
            _model = factory.CreateChatClient();
            var appTools = new WindowsAppTools();
            var windowTools = new WindowsWindowTools();
            var catalog = new ToolCatalog();
            _toolSet = CreateTools(catalog,
                                   appTools,
                                   windowTools,
                                   _model,
                                   Guid.NewGuid().ToString("N"),
                                   () => EmergencyStopRequested?.Invoke(),
                                   active => InputActiveChanged?.Invoke(active),
                                   Settings.Chat.AllowedPrivateHosts);
            var mcpAgents = await AddMcpServersAsync(catalog, _toolSet, cancellationToken);
            var audit =new ToolStartAuditLog(new JsonlAuditLog(Path.Combine(DataFolder, "logs")),
                                              tool => ToolStarted?.Invoke(tool));
            var toolNames = catalog.Entries.Select(e => e.Name).ToHashSet();
            // Off the UI thread: loading reads every SKILL.md.
            var skills = await Task.Run(() => SkillCatalog.LoadAsync([(BuiltInSkillsFolder, true), (UserSkillsFolder, false)],
                                                                     toolNames,
                                                                     cancellationToken),
                                        cancellationToken);
            var dependencies = new AgentDependencies(factory.Settings.Chat,
                                                     _model,
                                                     catalog,
                                                     skills,
                                                     audit,
                                                     [
appTools,
                                                         windowTools,
                                                         .. _toolSet.ApprovalRules,
                                                         new SensitiveReadRule(Settings.IsChatRemote,
                                                                               new Uri(Settings.ChatEndpoint.BaseUrl).Host)

],
                                                     stepLimits,
                                                     mcpAgents);
            var session = await new AgentFactory(dependencies).CreateSessionAsync(cancellationToken);
            _runner = new AgentRunner(session, approvals, audit);
        }

        var force = _forceNewRequest;
        _forceNewRequest = false;
        using var stop = cancellationToken.Register(() => _toolSet?.StopBrowser());
        try
        {
            return await _runner.RunAsync(text, forceNewRequest: force, cancellationToken: cancellationToken);
        }
        finally
        {
            _toolSet?.CloseResearchBrowser();
        }
    }

    /// <summary>Connects to the configured MCP servers in parallel and registers their tools. A server that fails is skipped.</summary>
    private async Task<IReadOnlyList<SubAgentDefinition>> AddMcpServersAsync(ToolCatalog catalog,
                                                                             WindowsToolSet toolSet,
                                                                             CancellationToken cancellationToken)
    {
        var servers = new List<McpServerSettings>();
        foreach (var server in Settings.Chat.McpServers.Where(s => s.Enabled))
        {
            if (server.Command is null || await MayStartAsync(server, cancellationToken))
            {
                servers.Add(server);
            }
            else
            {
                Notice?.Invoke($"MCP server '{server.Name}' was not started.");
            }
        }

        var secrets = new WindowsSecretStore();
        var connections = await Task.WhenAll(servers.Select(async server =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(McpConnectTimeout);
            try
            {
                return await McpServerTools.ConnectAsync(server, secrets.Read, timeout.Token);
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    var reason = timeout.IsCancellationRequested
                        ? $"no response in {McpConnectTimeout.TotalSeconds:0} seconds"
                        : ex.Message;
                    Notice?.Invoke($"MCP server '{server.Name}' did not connect: {reason}");
                }
                return null;
            }
        }));

        // The tool set owns each connection from here, so a canceled run still closes it.
        List<SubAgentDefinition> agents = [];
        foreach (var connection in connections.OfType<McpServerTools>())
        {
            var server = connection.Server;
            toolSet.AddMcpServer(catalog, connection);
            if (connection.MissingTools.Count > 0)
            {
                Notice?.Invoke($"MCP server '{server.Name}' does not have these tools: {string.Join(", ", connection.MissingTools)}.");
            }
            agents.Add(new SubAgentDefinition(server.Group,
                                              server.Description ?? $"tools of the {server.Name} MCP server",
                                              [server.Group],
                                              []));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return agents;
    }

    /// <summary>A stdio MCP server runs a program on this computer, so the user allows it once per app session.</summary>
    private async Task<bool> MayStartAsync(McpServerSettings server, CancellationToken cancellationToken)
    {
        var key = JsonSerializer.Serialize(new
        {
            server.Name,
            server.Command,
            server.Args
        });
        if (_allowedMcpPrograms.Contains(key))
        {
            return true;
        }

        var call = new FunctionCallContent($"mcp_start_{server.Name}",
                                           "mcp_server_start",
                                           new Dictionary<string, object?>
                                           {
                                               ["server"] = server.Name,
                                               ["command"] = server.Command,
                                               ["args"] = server.Args
                                           });
        var answer = await approvals.AskAsync(call,
                                              new ApprovalDecision(ApprovalOutcome.Ask,
                                                                   false,
                                                                   $"WindowsAxon will start this program on this computer as MCP server '{server.Name}'. Allow it only if you trust the program."),
                                              cancellationToken);
        if (answer == ApprovalAnswer.Deny)
        {
            return false;
        }

        _allowedMcpPrograms.Add(key);
        return true;
    }

    public void StartNewRequest() => _forceNewRequest = true;

    public void NewChat()
    {
        _toolSet?.Dispose();
        _toolSet = null;
        _runner = null;
        _forceNewRequest = false;
        _model?.Dispose();
        _model = null;
    }

    public void SaveSettings(string baseUrl,
                             string model,
                             string apiKey = "",
                             bool removeApiKey = false,
                             EndpointApi? api = null,
                             string? endpointName = null)
    {
        ValidateEndpoint(baseUrl);
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Enter a model name.");
        }

        baseUrl = new Uri(baseUrl.Trim()).AbsoluteUri.TrimEnd('/');
        apiKey = apiKey.Trim();
        if (apiKey.Contains('\0'))
        {
            throw new ArgumentException("The API key contains an invalid character.");
        }
        var name = endpointName ?? Settings.Chat.Endpoint;
        bool Matches(string key) => string.Equals(key, name, StringComparison.OrdinalIgnoreCase);
        name = Settings.Endpoints.Keys.FirstOrDefault(Matches) ?? ProviderPresets.All.Keys.FirstOrDefault(Matches) ?? name;
        var current = Settings.Endpoints.GetValueOrDefault(name)
            ?? ProviderPresets.All.GetValueOrDefault(name)
            ?? new EndpointSettings { BaseUrl = baseUrl };
        var previousReference = current.ApiKey;
        var reference = !removeApiKey && new Uri(current.BaseUrl.TrimEnd('/')) == new Uri(baseUrl)
            ? previousReference : null;
        var newCredential = !removeApiKey && apiKey.Length > 0 ? $"WindowsAxon/desktop/{Guid.NewGuid():N}" : null;
        if (newCredential is not null)
        {
            reference = $"credential:{newCredential}";
        }
        if (!string.IsNullOrEmpty(reference) && !EndpointRules.MaySendApiKey(baseUrl))
        {
            throw new ArgumentException("Use HTTPS for a remote endpoint with an API key. HTTP is allowed only for localhost.");
        }

        var endpoints = new Dictionary<string, EndpointSettings>(Settings.Endpoints)
        {
            [name] = current with
            {
                BaseUrl = baseUrl,
                ApiKey = reference,
                Api = api ?? current.Api,
                Models = current.Models.Contains(model.Trim()) ? current.Models : [.. current.Models, model.Trim()],
            },
        };
        var updated = Settings with
        {
            Endpoints = endpoints,
            Chat = Settings.Chat with
            {
                Endpoint = name,
                Model = model.Trim()
            },
        };
        updated = updated with
        {
            Chat = updated.Chat with
            {
                StepMode = updated.UsesResponsesApi ? StepMode.Native : Settings.Chat.StepMode,
                ReasoningEffortWhenThinkingOff = (updated.IsChatRemote || updated.UsesResponsesApi)
            && Settings.Chat.ReasoningEffortWhenThinkingOff == "none"
            ? null : Settings.Chat.ReasoningEffortWhenThinkingOff,
            },
        };
        var secrets = new WindowsSecretStore();
        WriteSettings(updated, secrets, newCredential, apiKey);
        NewChat();
        EndpointFactory.ClearApiDetectionCache();
        Settings = updated;
        if (previousReference is not null && previousReference.StartsWith(ManagedCredentialPrefix,
                                                                          StringComparison.Ordinal)
            && Guid.TryParseExact(previousReference[ManagedCredentialPrefix.Length..], "N", out _)
            && !updated.Endpoints.Values.Any(endpoint => endpoint.ApiKey == previousReference))
        {
            secrets.Delete(previousReference["credential:".Length..]);
        }
    }

    /// <summary>Saves the thinking mode and its reasoning_effort. Null effort sends nothing.</summary>
    public void SaveReasoning(bool thinking, string? reasoningEffort)
    {
        var updated = Settings with
        {
            Chat = Settings.Chat with { Thinking = thinking, ReasoningEffort = reasoningEffort }
        };
        WriteSettings(updated, new WindowsSecretStore(), null, null);
        NewChat();
        Settings = updated;
    }

    /// <summary>Saves a new or changed MCP server. The change applies to new chats.</summary>
    /// <param name="originalName">The name of the server to replace, or null to add a server.</param>
    /// <param name="headerName">The one secret header for an HTTP server, or blank for no header.</param>
    /// <param name="headerValue">The header value, saved in Windows Credential Manager. Blank keeps the saved value for the same Url.</param>
    public void SaveMcpServer(McpServerSettings server, string? originalName, string headerName = "", string headerValue = "")
    {
        ArgumentNullException.ThrowIfNull(server);

        var previous = originalName is null ? null : Settings.Chat.McpServers.FirstOrDefault(s => s.Name == originalName);
        headerName = headerName.Trim();
        headerValue = headerValue.Trim();
        if (headerValue.Contains('\0'))
        {
            throw new ArgumentException("The header value contains an invalid character.");
        }

        string? newCredential = null;
        var headers = new Dictionary<string, string>();
        if (server.Url is not null && headerName.Length > 0)
        {
            if (headerValue.Length > 0)
            {
                newCredential = $"{McpCredentialPrefix}{Guid.NewGuid():N}";
                headers[headerName] = McpServerSettings.CredentialPrefix + newCredential;
            }
            else if (previous?.Url == server.Url && previous.Headers.Values.FirstOrDefault() is { } saved)
            {
                headers[headerName] = saved;
            }
            else
            {
                throw new ArgumentException("Enter the header value.");
            }
        }

        var updatedServer = server with { Headers = headers };
        var servers = Settings.Chat.McpServers.ToList();
        var index = previous is null ? -1 : servers.IndexOf(previous);
        if (index >= 0)
        {
            servers[index] = updatedServer;
        }
        else
        {
            servers.Add(updatedServer);
        }

        McpServerSettings.ValidateAll(servers);
        SaveMcpServers(servers, newCredential, headerValue);
    }

    /// <summary>Turns a server on or off for new chats.</summary>
    public void SetMcpServerEnabled(string name, bool enabled) =>
        SaveMcpServers([.. Settings.Chat.McpServers.Select(s => s.Name == name ? s with { Enabled = enabled } : s)], null, null);

    public void DeleteMcpServer(string name) =>
        SaveMcpServers([.. Settings.Chat.McpServers.Where(s => s.Name != name)], null, null);

    private void SaveMcpServers(List<McpServerSettings> servers, string? newCredential, string? secret)
    {
        var updated = Settings with
        {
            Chat = Settings.Chat with { McpServers = servers }
        };
        var secrets = new WindowsSecretStore();
        WriteSettings(updated, secrets, newCredential, secret);
        var unused = Settings.Chat.McpServers.SelectMany(s => s.Headers.Values)
            .Except(servers.SelectMany(s => s.Headers.Values))
            .Select(reference => reference[McpServerSettings.CredentialPrefix.Length..])
            .Where(name => name.StartsWith(McpCredentialPrefix, StringComparison.Ordinal));
        Settings = updated;
        foreach (var name in unused)
        {
            secrets.Delete(name);
        }
    }

    /// <summary>Writes the settings file in one step. A new secret is written first and removed when the file write fails.</summary>
    private void WriteSettings(ModelSettings updated, WindowsSecretStore secrets, string? newCredential, string? secret)
    {
        var json = JsonSerializer.Serialize(new
        {
            Models = new
            {
                updated.Endpoints,
                updated.Chat
            }
        },
                                            SettingsJsonOptions);
        var temporaryPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
        if (newCredential is not null)
        {
            secrets.Write(newCredential, secret!);
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        catch
        {
            if (newCredential is not null)
            {
                secrets.Delete(newCredential);
            }
            throw;
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public static void ValidateEndpoint(string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("Enter an http or https endpoint URL, without embedded credentials.");
        }
    }

    public void Dispose() => NewChat();

    private sealed class ToolStartAuditLog(IAuditLog inner, Action<string> toolStarted) : IAuditLog
    {
        public void Write(AuditEntry entry)
        {
            inner.Write(entry);
            if (entry.Kind == "call_start")
            {
                toolStarted(entry.Agent is null ? entry.Tool : $"{entry.Agent}: {entry.Tool}");
            }
        }
    }
}
