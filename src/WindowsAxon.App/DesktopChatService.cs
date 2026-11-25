using System.Collections.Concurrent;
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
using WindowsAxon.Mesh;
using WindowsAxon.Windows;

namespace WindowsAxon.App;

public sealed record DesktopRunResult(RunStatus Status, string Text, int Steps, string? RemotePeer = null);

public sealed class DesktopChatService(IApprovalPrompter approvals,
                                       IStepLimitPrompter stepLimits,
                                       string? settingsPath = null,
                                       string? chatHistoryFolder = null) : IDisposable
{
    private const string ManagedCredentialPrefix = "credential:WindowsAxon/desktop/";
    private const string McpCredentialPrefix = "WindowsAxon/mcp/";
    private const string MeshCredentialPrefix = "WindowsAxon/mesh/";
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
    private static readonly TimeSpan RemoteSessionIdleTimeout = TimeSpan.FromMinutes(30);

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
    private readonly ChatHistoryLog _chatHistory = new(chatHistoryFolder is null ? ChatHistoryLog.DefaultFolder : Path.GetFullPath(chatHistoryFolder));
    private readonly ArtifactStore _artifactStore = new(Path.Combine(DataFolder, "artifacts"));
    private readonly AsyncLocal<MeshCallContext?> _currentCallContext = new();
    private readonly ConcurrentDictionary<string, RemoteSessionEntry> _remoteSessions = new(StringComparer.OrdinalIgnoreCase);
    private List<string>? _activeLocalTools;
    private string _conversationId = Guid.NewGuid().ToString("N");

    internal Func<CancellationToken, Task<IChatClient>>? RemoteModelFactory { get; set; }

    internal int RemoteSessionCount => _remoteSessions.Count;

    private static string? GetConfigOverride()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--config");
        return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null;
    }

    private IChatClient? _model;
    private AgentRunner? _runner;
    private WindowsToolSet? _toolSet;
    private IMeshNode? _meshNode;
    private MeshJobService? _meshJobs;
    private ArtifactService? _meshArtifacts;
    private FileTransferService? _meshFiles;
    private MeshOrchestrator? _meshOrchestrator;

    public event Action? EmergencyStopRequested;

    public event Action<bool>? InputActiveChanged;

    public event Action<string>? ToolStarted;

    /// <summary>A message for the chat event list, for example an MCP server that did not connect.</summary>
    public event Action<string>? Notice;

    public event Action<IReadOnlyList<string>>? PeersChanged;

    public event Action<MeshConnectionState, string>? MeshStateChanged;

    public event Action<string, string>? RemoteRequestReceived;

    public event Action<string, string>? RemoteToolStarted;

    public event Action<string, RunStatus, string>? RemoteResponseSent;

    public event Action<ReceivedFileInfo>? FileReceived;

    public IReadOnlyList<string> OnlinePeers => _meshNode?.OnlinePeers ?? [];

    public IReadOnlyList<PeerInfo> OnlinePeerInfos => _meshNode?.PeerInfos ?? [];

    public MeshConnectionState MeshState => _meshNode?.State ?? MeshConnectionState.Disconnected;

    public string MeshStatusMessage => _meshNode?.StatusMessage ?? (Settings.Chat.Mesh.IsEnabled ? "Disconnected" : "Disabled");

    public string EffectiveMeshNodeName => _meshNode?.NodeName ?? Settings.Chat.Mesh.EffectiveNodeName;

    public void PruneExpiredArtifacts() => _artifactStore.PruneExpired();

    public void PruneExpiredRemoteSessions(TimeSpan? idleTimeout = null)
    {
        var timeout = idleTimeout ?? RemoteSessionIdleTimeout;
        var cutoff = DateTimeOffset.UtcNow - timeout;
        foreach (var (key, entry) in _remoteSessions)
        {
            if (entry.LastUsedUtc > cutoff || !entry.Gate.Wait(0))
            {
                continue;
            }

            try
            {
                if (!entry.IsDisposed
                    && entry.LastUsedUtc <= cutoff
                    && _remoteSessions.TryRemove(new KeyValuePair<string, RemoteSessionEntry>(key, entry)))
                {
                    entry.DisposeResources();
                }
            }
            finally
            {
                entry.Gate.Release();
            }
        }
    }

    private void ClearRemoteSessions()
    {
        foreach (var (key, entry) in _remoteSessions)
        {
            if (!_remoteSessions.TryRemove(new KeyValuePair<string, RemoteSessionEntry>(key, entry)))
            {
                continue;
            }

            if (entry.Gate.Wait(0))
            {
                try
                {
                    entry.DisposeResources();
                }
                finally
                {
                    entry.Gate.Release();
                }
            }
            else
            {
                entry.RetireOnRelease = true;
            }
        }
    }

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
            settings.Chat.Mesh.Validate();
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

    /// <summary>Runs <paramref name="text"/> on <paramref name="targetPeer"/>, or locally when it is null or this computer.</summary>
    /// <param name="conversationId">The remote computer keeps one agent session for all requests with this ID.</param>
    public async Task<DesktopRunResult> RunAsync(string text,
                                                 string? targetPeer,
                                                 string conversationId,
                                                 CancellationToken cancellationToken)
    {
        if (targetPeer is null || string.Equals(targetPeer, EffectiveMeshNodeName, StringComparison.OrdinalIgnoreCase))
        {
            return await RunAsync(text, cancellationToken);
        }

        if (!Settings.Chat.Mesh.IsEnabled || !OnlinePeers.Contains(targetPeer, StringComparer.OrdinalIgnoreCase))
        {
            return new DesktopRunResult(RunStatus.Failed, $"{targetPeer} is offline.", 0, targetPeer);
        }

        return await RunOnRemotePeerAsync(targetPeer, text, conversationId, cancellationToken);
    }

    public async Task<DesktopRunResult> RunAsync(string text, CancellationToken cancellationToken)
    {
        if (Settings.Chat.Mesh.IsEnabled
            && PeerMentionParser.TryParse(text, OnlinePeers, out var targetNode, out var remotePrompt))
        {
            if (string.IsNullOrWhiteSpace(remotePrompt))
            {
                return new DesktopRunResult(RunStatus.Failed, $"Enter a request after @{targetNode}.", 0);
            }

            if (string.Equals(targetNode, EffectiveMeshNodeName, StringComparison.OrdinalIgnoreCase))
            {
                text = remotePrompt;
            }
            else
            {
                return await RunOnRemotePeerAsync(targetNode, remotePrompt, null, cancellationToken);
            }
        }

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
            if (Settings.Chat.Mesh.IsEnabled)
            {
                ToolRegistration.Add(catalog,
                                     new MeshTools(() => _meshNode,
                                                   () => _meshFiles,
                                                   () => _meshArtifacts,
                                                   () => _meshOrchestrator,
                                                   () => EffectiveMeshNodeName,
                                                   () => _currentCallContext.Value,
                                                   msg => ToolStarted?.Invoke(msg)));
            }
            var mcpAgents = await AddMcpServersAsync(catalog, _toolSet, cancellationToken);
            var audit = new ToolStartAuditLog(new JsonlAuditLog(Path.Combine(DataFolder, "logs")),
                                              tool =>
                                              {
                                                  _activeLocalTools?.Add(tool);
                                                  ToolStarted?.Invoke(tool);
                                              });
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
        var tools = new List<string>();
        _activeLocalTools = tools;
        using var stop = cancellationToken.Register(() => _toolSet?.StopBrowser());
        try
        {
            var result = await _runner.RunAsync(text, forceNewRequest: force, cancellationToken: cancellationToken);
            TryAppendHistory(ChatDirection.Local, null, text, result.Text, result.Status, result.Steps, tools);
            return new DesktopRunResult(result.Status, result.Text, result.Steps);
        }
        finally
        {
            _activeLocalTools = null;
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
        _conversationId = Guid.NewGuid().ToString("N");
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
        ClearRemoteSessions();
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
        ClearRemoteSessions();
        Settings = updated;
    }

    /// <summary>Saves the tool approval level. The change applies to new chats.</summary>
    public void SaveApprovalLevel(ApprovalLevel level)
    {
        var updated = Settings with
        {
            Chat = Settings.Chat with { ApprovalLevel = level }
        };
        WriteSettings(updated, new WindowsSecretStore(), null, null);
        NewChat();
        ClearRemoteSessions();
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

    /// <summary>Writes the settings file in one step. New secrets are written first and removed when the file write fails.</summary>
    private void WriteSettings(ModelSettings updated, WindowsSecretStore secrets, string? newCredential, string? secret) =>
        WriteSettings(updated,
                      secrets,
                      newCredential is not null ? [(newCredential, secret!)] : []);

    private void WriteSettings(ModelSettings updated,
                               WindowsSecretStore secrets,
                               IReadOnlyList<(string Name, string Secret)> newCredentials)
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
        var writtenCredentials = new List<string>(newCredentials.Count);
        try
        {
            foreach (var (name, value) in newCredentials)
            {
                secrets.Write(name, value);
                writtenCredentials.Add(name);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        catch
        {
            foreach (var name in writtenCredentials)
            {
                secrets.Delete(name);
            }
            throw;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
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

    public void StartMesh()
    {
        StopMesh();
        var mesh = Settings.Chat.Mesh;
        var mode = mesh.EffectiveMode;
        if (mode == MeshMode.Disabled)
        {
            MeshStateChanged?.Invoke(MeshConnectionState.Disconnected, "Disabled");
            PeersChanged?.Invoke([]);
            return;
        }

        mesh.Validate();
        var secrets = new WindowsSecretStore();
        var capabilities = new PeerCapabilities(
            [
                MeshServiceNames.AgentTask,
                MeshServiceNames.FileTransfer,
                MeshServiceNames.ArtifactFetch,
                MeshServiceNames.Jobs,
            ],
            OsDescription: Environment.OSVersion.VersionString);
        IMeshNode node;
        if (mode == MeshMode.CentralServer)
        {
            var token = !string.IsNullOrEmpty(mesh.Token) && mesh.Token.StartsWith(MeshSettings.CredentialPrefix, StringComparison.Ordinal)
                ? secrets.Read(mesh.Token[MeshSettings.CredentialPrefix.Length..])
                : null;
            node = new MeshClient(new Uri(mesh.HubUrl.Trim()),
                                  mesh.EffectiveNodeName,
                                  token,
                                  HandleIncomingRemoteTaskAsync,
                                  capabilities);
        }
        else
        {
            var pairKey = secrets.Read(mesh.PairKey![MeshSettings.CredentialPrefix.Length..]);
            if (string.IsNullOrWhiteSpace(pairKey))
            {
                throw new InvalidOperationException("The saved pair key is empty. Open Network settings to re-enter it.");
            }

            node = new PeerMeshNode(mesh.EffectiveNodeName,
                                    pairKey,
                                    HandleIncomingRemoteTaskAsync,
                                    capabilities: capabilities);
        }

        var jobs = new MeshJobService(node);
        var artifacts = new ArtifactService(node, _artifactStore);
        var files = new FileTransferService(node);
        var orchestrator = new MeshOrchestrator(node,
                                                jobs,
                                                HandleIncomingOrchestratedTaskAsync,
                                                AskRemoteApprovalAsync);
        files.FileReceived += info =>
        {
            TryAppendHistory(ChatDirection.Incoming,
                             info.CallerNode,
                             $"File transfer: {info.FileName} ({info.SizeBytes:N0} bytes)",
                             $"Saved to {info.SavedPath}",
                             RunStatus.Answered,
                             1,
                             ["mesh_receive_file"]);
            FileReceived?.Invoke(info);
        };

        node.PeersChanged += peers => PeersChanged?.Invoke(peers);
        node.ConnectionStateChanged += (state, message) => MeshStateChanged?.Invoke(state, message);
        _meshJobs = jobs;
        _meshArtifacts = artifacts;
        _meshFiles = files;
        _meshOrchestrator = orchestrator;
        _meshNode = node;
        node.Start();
    }

    public void StopMesh()
    {
        ClearRemoteSessions();

        var previousJobs = Interlocked.Exchange(ref _meshJobs, null);
        previousJobs?.Dispose();
        _meshArtifacts = null;
        _meshFiles = null;
        _meshOrchestrator = null;

        var previous = Interlocked.Exchange(ref _meshNode, null);
        if (previous is not null)
        {
            _ = previous.DisposeAsync();
        }
    }

    public void SaveMeshSettings(MeshMode mode,
                                 string nodeName,
                                 string hubUrl = "",
                                 string token = "",
                                 bool removeToken = false,
                                 string pairKey = "",
                                 bool removePairKey = false)
    {
        hubUrl = hubUrl.Trim();
        nodeName = nodeName.Trim();
        token = token.Trim();
        pairKey = pairKey.Trim();
        if (token.Contains('\0'))
        {
            throw new ArgumentException("The hub token contains an invalid character.");
        }

        if (pairKey.Contains('\0'))
        {
            throw new ArgumentException("The pair key contains an invalid character.");
        }

        var previous = Settings.Chat.Mesh;
        var newCredentials = new List<(string Name, string Secret)>();

        var previousTokenRef = previous.Token;
        var tokenRef = !removeToken && string.Equals(previous.HubUrl.Trim(), hubUrl, StringComparison.OrdinalIgnoreCase)
            ? previousTokenRef
            : null;
        if (!removeToken && token.Length > 0)
        {
            var credName = $"{MeshCredentialPrefix}{Guid.NewGuid():N}";
            newCredentials.Add((credName, token));
            tokenRef = MeshSettings.CredentialPrefix + credName;
        }

        var previousPairKeyRef = previous.PairKey;
        var pairKeyRef = !removePairKey ? previousPairKeyRef : null;
        if (!removePairKey && pairKey.Length > 0)
        {
            var credName = $"{MeshCredentialPrefix}{Guid.NewGuid():N}";
            newCredentials.Add((credName, pairKey));
            pairKeyRef = MeshSettings.CredentialPrefix + credName;
        }

        var updatedMesh = new MeshSettings
        {
            Mode = mode,
            HubUrl = hubUrl,
            NodeName = nodeName,
            Token = tokenRef,
            PairKey = pairKeyRef,
        };
        updatedMesh.Validate();

        var updated = Settings with
        {
            Chat = Settings.Chat with { Mesh = updatedMesh }
        };
        var secrets = new WindowsSecretStore();
        WriteSettings(updated, secrets, newCredentials);
        Settings = updated;

        DeleteReplacedMeshCredential(secrets, previousTokenRef, tokenRef);
        DeleteReplacedMeshCredential(secrets, previousPairKeyRef, pairKeyRef);

        if (previous.IsEnabled != updatedMesh.IsEnabled)
        {
            NewChat();
        }

        StartMesh();
    }

    private static void DeleteReplacedMeshCredential(WindowsSecretStore secrets, string? previousRef, string? currentRef)
    {
        if (previousRef is not null
            && previousRef != currentRef
            && previousRef.StartsWith(MeshSettings.CredentialPrefix + MeshCredentialPrefix, StringComparison.Ordinal))
        {
            secrets.Delete(previousRef[MeshSettings.CredentialPrefix.Length..]);
        }
    }

    /// <param name="conversationId">A per-peer chat ID, or null to use the local chat's ID and its new-request flag.</param>
    private async Task<DesktopRunResult> RunOnRemotePeerAsync(string targetNode,
                                                              string prompt,
                                                              string? conversationId,
                                                              CancellationToken cancellationToken)
    {
        var tools = new List<string>();
        var node = _meshNode;
        if (node is null || node.State != MeshConnectionState.Connected)
        {
            var failed = new DesktopRunResult(RunStatus.Failed,
                                              $"Not connected to mesh network ({MeshStatusMessage}). Open Network in the sidebar to check settings.",
                                              0,
                                              targetNode);
            TryAppendHistory(ChatDirection.Outgoing, targetNode, prompt, failed.Text, failed.Status, failed.Steps, tools);
            return failed;
        }

        // A per-peer chat starts over with a new ID, so it never consumes the local chat's new-request flag.
        var force = conversationId is null && _forceNewRequest;
        if (conversationId is null)
        {
            _forceNewRequest = false;
        }

        try
        {
            var result = await node.RunRemoteTaskAsync(targetNode,
                                                       prompt,
                                                       conversationId ?? _conversationId,
                                                       force,
                                                       tool =>
                                                       {
                                                           tools.Add(tool);
                                                           ToolStarted?.Invoke($"[{targetNode}] {tool}");
                                                       },
                                                       (req, ct) => AskRemoteApprovalAsync(targetNode, req, ct),
                                                       cancellationToken);
            var runResult = new DesktopRunResult(MapRunStatus(result.Status), result.Text, result.Steps, targetNode);
            TryAppendHistory(ChatDirection.Outgoing, targetNode, prompt, runResult.Text, runResult.Status, runResult.Steps, tools);
            return runResult;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryAppendHistory(ChatDirection.Outgoing, targetNode, prompt, "Stopped.", RunStatus.Cancelled, 0, tools);
            throw;
        }
        catch (Exception ex)
        {
            TryAppendHistory(ChatDirection.Outgoing, targetNode, prompt, ex.Message, RunStatus.Failed, 0, tools);
            throw;
        }
    }

    /// <summary>Answers an approval request that a remote computer forwarded to this user.</summary>
    internal async Task<RemoteApprovalAnswer> AskRemoteApprovalAsync(string targetNode,
                                                                     TaskApprovalRequestPayload req,
                                                                     CancellationToken cancellationToken)
    {
        // The remote computer asks this user, so this user's "Allow all actions" answers for them.
        if (Settings.Chat.ResolveApprovalLevel() == ApprovalLevel.All)
        {
            return RemoteApprovalAnswer.Allow;
        }

        Dictionary<string, object?>? args = null;
        if (!string.IsNullOrWhiteSpace(req.ArgumentsJson))
        {
            try
            {
                args = JsonSerializer.Deserialize<Dictionary<string, object?>>(req.ArgumentsJson);
            }
            catch (JsonException)
            {
                args = new Dictionary<string, object?> { ["raw"] = req.ArgumentsJson };
            }
        }

        var call = new FunctionCallContent(req.ApprovalId, $"[{targetNode}] {req.ToolName}", args);
        var note = string.IsNullOrWhiteSpace(req.Note)
            ? $"Remote computer '{targetNode}' requests approval to run '{req.ToolName}'."
            : $"[Remote: {targetNode}] {req.Note}";
        var answer = await approvals.AskAsync(call,
                                              new ApprovalDecision(ApprovalOutcome.Ask, req.AllowForConversation, note),
                                              cancellationToken);
        return answer switch
        {
            ApprovalAnswer.Allow => RemoteApprovalAnswer.Allow,
            ApprovalAnswer.AllowForConversation => RemoteApprovalAnswer.AllowForConversation,
            _ => RemoteApprovalAnswer.Deny,
        };
    }

    private Task<RemoteTaskResult> HandleIncomingOrchestratedTaskAsync(IncomingOrchestratedTask task,
                                                                       CancellationToken cancellationToken)
    {
        var adapted = new IncomingRemoteTask(task.JobId,
                                             task.CallerNode,
                                             task.Prompt,
                                             task.JobId,
                                             ForceNewRequest: true,
                                             task.ReportProgress,
                                             task.RequestApprovalAsync);
        return ExecuteOneShotRemoteTaskAsync(adapted, task.CallContext, cancellationToken);
    }

    internal Task<RemoteTaskResult> HandleIncomingRemoteTaskAsync(IncomingRemoteTask task,
                                                                  CancellationToken cancellationToken) =>
        ExecutePersistentRemoteTaskAsync(task, null, cancellationToken);

    private async Task<RemoteTaskResult> ExecutePersistentRemoteTaskAsync(IncomingRemoteTask task,
                                                                          MeshCallContext? callContext,
                                                                          CancellationToken cancellationToken)
    {
        var key = $"{task.CallerNode}\0{task.ConversationId}";
        while (true)
        {
            var entry = _remoteSessions.GetOrAdd(key, _ => new RemoteSessionEntry());
            await entry.Gate.WaitAsync(cancellationToken);
            if (entry.IsDisposed)
            {
                entry.Gate.Release();
                continue;
            }

            try
            {
                if (entry.Runner is null)
                {
                    try
                    {
                        var (model, toolSet, runner) = await CreateRemoteSessionAsync(entry.Binding,
                                                                                      $"remote-{task.ConversationId}",
                                                                                      cancellationToken);
                        entry.Initialize(model, toolSet, runner);
                    }
                    catch
                    {
                        _remoteSessions.TryRemove(new KeyValuePair<string, RemoteSessionEntry>(key, entry));
                        entry.DisposeResources();
                        throw;
                    }
                }

                return await RunBoundRemoteTaskAsync(entry.Runner!,
                                                     entry.ToolSet!,
                                                     entry.Binding,
                                                     task,
                                                     callContext,
                                                     cancellationToken);
            }
            finally
            {
                entry.Touch();
                if (entry.RetireOnRelease)
                {
                    entry.DisposeResources();
                }

                entry.Gate.Release();
            }
        }
    }

    private async Task<RemoteTaskResult> ExecuteOneShotRemoteTaskAsync(IncomingRemoteTask task,
                                                                       MeshCallContext? callContext,
                                                                       CancellationToken cancellationToken)
    {
        var binding = new RemoteTaskBinding();
        var (model, toolSet, runner) = await CreateRemoteSessionAsync(binding, $"remote-{task.TaskId}", cancellationToken);
        using (model)
        using (toolSet)
        {
            return await RunBoundRemoteTaskAsync(runner, toolSet, binding, task, callContext, cancellationToken);
        }
    }

    private async Task<(IChatClient Model, WindowsToolSet ToolSet, AgentRunner Runner)> CreateRemoteSessionAsync(
        RemoteTaskBinding binding,
        string sessionId,
        CancellationToken cancellationToken)
    {
        IChatClient? model = null;
        WindowsToolSet? toolSet = null;
        try
        {
            ChatSettings chatSettings;
            if (RemoteModelFactory is not null)
            {
                model = await RemoteModelFactory(cancellationToken);
                chatSettings = Settings.Chat;
            }
            else
            {
                var factory = await new EndpointFactory(Settings, new WindowsSecretStore()).ResolveAsync(cancellationToken);
                model = factory.CreateChatClient();
                chatSettings = factory.Settings.Chat;
            }

            var appTools = new WindowsAppTools();
            var windowTools = new WindowsWindowTools();
            var catalog = new ToolCatalog();
            toolSet = CreateTools(catalog,
                                  appTools,
                                  windowTools,
                                  model,
                                  sessionId,
                                  () => { },
                                  _ => { },
                                  Settings.Chat.AllowedPrivateHosts);
            if (Settings.Chat.Mesh.IsEnabled)
            {
                ToolRegistration.Add(catalog,
                                     new MeshTools(() => _meshNode,
                                                   () => _meshFiles,
                                                   () => _meshArtifacts,
                                                   () => _meshOrchestrator,
                                                   () => EffectiveMeshNodeName,
                                                   () => _currentCallContext.Value,
                                                   tool =>
                                                   {
                                                       if (binding.CurrentTask is { } activeTask)
                                                       {
                                                           activeTask.ReportToolStarted(tool);
                                                           RemoteToolStarted?.Invoke(activeTask.CallerNode, tool);
                                                       }
                                                   }));
            }

            var audit = new ToolStartAuditLog(new JsonlAuditLog(Path.Combine(DataFolder, "logs")),
                                              tool =>
                                              {
                                                  binding.CurrentTools?.Add(tool);
                                                  if (binding.CurrentTask is { } activeTask)
                                                  {
                                                      activeTask.ReportToolStarted(tool);
                                                      RemoteToolStarted?.Invoke(activeTask.CallerNode, tool);
                                                  }
                                              });
            var toolNames = catalog.Entries.Select(e => e.Name).ToHashSet();
            var skills = await Task.Run(() => SkillCatalog.LoadAsync([(BuiltInSkillsFolder, true), (UserSkillsFolder, false)],
                                                                     toolNames,
                                                                     cancellationToken),
                                        cancellationToken);
            var dependencies = new AgentDependencies(chatSettings,
                                                     model,
                                                     catalog,
                                                     skills,
                                                     audit,
                                                     [
                                                         appTools,
                                                         windowTools,
                                                         .. toolSet.ApprovalRules,
                                                         new SensitiveReadRule(Settings.IsChatRemote,
                                                                               new Uri(Settings.ChatEndpoint.BaseUrl).Host)
                                                     ],
                                                     new RemoteStepLimitPrompter(),
                                                     []);
            var session = await new AgentFactory(dependencies).CreateSessionAsync(cancellationToken);
            var runner = new AgentRunner(session, new RemoteApprovalPrompter(binding), audit);
            return (model, toolSet, runner);
        }
        catch
        {
            toolSet?.Dispose();
            model?.Dispose();
            throw;
        }
    }

    private async Task<RemoteTaskResult> RunBoundRemoteTaskAsync(AgentRunner runner,
                                                                 WindowsToolSet toolSet,
                                                                 RemoteTaskBinding binding,
                                                                 IncomingRemoteTask task,
                                                                 MeshCallContext? callContext,
                                                                 CancellationToken cancellationToken)
    {
        var previousContext = _currentCallContext.Value;
        _currentCallContext.Value = callContext;
        RemoteRequestReceived?.Invoke(task.CallerNode, task.Prompt);
        var tools = new List<string>();
        binding.SetCurrent(task, tools);
        using var stop = cancellationToken.Register(() => toolSet.StopBrowser());
        try
        {
            var result = await runner.RunAsync(task.Prompt,
                                               forceNewRequest: task.ForceNewRequest,
                                               cancellationToken: cancellationToken);
            TryAppendHistory(ChatDirection.Incoming, task.CallerNode, task.Prompt, result.Text, result.Status, result.Steps, tools);
            RemoteResponseSent?.Invoke(task.CallerNode, result.Status, result.Text);
            return new RemoteTaskResult(MapRemoteStatus(result.Status), result.Text, result.Steps);
        }
        catch (OperationCanceledException)
        {
            TryAppendHistory(ChatDirection.Incoming, task.CallerNode, task.Prompt, "Stopped.", RunStatus.Cancelled, 0, tools);
            RemoteResponseSent?.Invoke(task.CallerNode, RunStatus.Cancelled, "Stopped.");
            throw;
        }
        catch (Exception ex)
        {
            TryAppendHistory(ChatDirection.Incoming, task.CallerNode, task.Prompt, ex.Message, RunStatus.Failed, 0, tools);
            RemoteResponseSent?.Invoke(task.CallerNode, RunStatus.Failed, ex.Message);
            throw;
        }
        finally
        {
            toolSet.CloseResearchBrowser();
            binding.ClearCurrent();
            _currentCallContext.Value = previousContext;
        }
    }

    private void TryAppendHistory(ChatDirection direction,
                                  string? peerNode,
                                  string prompt,
                                  string response,
                                  RunStatus status,
                                  int steps,
                                  IReadOnlyList<string> tools)
    {
        try
        {
            _chatHistory.Append(new ChatHistoryEntry(DateTimeOffset.Now,
                                                     direction,
                                                     EffectiveMeshNodeName,
                                                     peerNode,
                                                     prompt,
                                                     response,
                                                     status,
                                                     steps,
                                                     [.. tools]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // History logging must not break an active local or remote response.
        }
    }

    private static RunStatus MapRunStatus(RemoteRunStatus status) => status switch
    {
        RemoteRunStatus.Answered => RunStatus.Answered,
        RemoteRunStatus.AskedUser => RunStatus.AskedUser,
        RemoteRunStatus.Cancelled => RunStatus.Cancelled,
        _ => RunStatus.Failed,
    };

    private static RemoteRunStatus MapRemoteStatus(RunStatus status) => status switch
    {
        RunStatus.Answered => RemoteRunStatus.Answered,
        RunStatus.AskedUser => RemoteRunStatus.AskedUser,
        RunStatus.Cancelled => RemoteRunStatus.Cancelled,
        _ => RemoteRunStatus.Failed,
    };

    public void Dispose()
    {
        StopMesh();
        NewChat();
    }

    private sealed class RemoteTaskBinding
    {
        public IncomingRemoteTask? CurrentTask { get; private set; }

        public List<string>? CurrentTools { get; private set; }

        public void SetCurrent(IncomingRemoteTask task, List<string> tools)
        {
            CurrentTask = task;
            CurrentTools = tools;
        }

        public void ClearCurrent()
        {
            CurrentTask = null;
            CurrentTools = null;
        }
    }

    private sealed class RemoteSessionEntry
    {
        private long _lastUsedUtcTicks = DateTimeOffset.UtcNow.UtcTicks;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public RemoteTaskBinding Binding { get; } = new();

        public IChatClient? Model { get; private set; }

        public WindowsToolSet? ToolSet { get; private set; }

        public AgentRunner? Runner { get; private set; }

        public bool IsDisposed { get; private set; }

        public volatile bool RetireOnRelease;

        public DateTimeOffset LastUsedUtc => new(Interlocked.Read(ref _lastUsedUtcTicks), TimeSpan.Zero);

        public void Initialize(IChatClient model, WindowsToolSet toolSet, AgentRunner runner)
        {
            Model = model;
            ToolSet = toolSet;
            Runner = runner;
            Touch();
        }

        public void Touch() => Interlocked.Exchange(ref _lastUsedUtcTicks, DateTimeOffset.UtcNow.UtcTicks);

        public void DisposeResources()
        {
            IsDisposed = true;
            ToolSet?.Dispose();
            ToolSet = null;
            Model?.Dispose();
            Model = null;
            Runner = null;
        }
    }

    private sealed class RemoteApprovalPrompter(RemoteTaskBinding binding) : IApprovalPrompter
    {
        public async Task<ApprovalAnswer> AskAsync(FunctionCallContent call,
                                                   ApprovalDecision decision,
                                                   CancellationToken cancellationToken)
        {
            var task = binding.CurrentTask
                ?? throw new InvalidOperationException("No active remote task for approval.");
            var argsJson = JsonSerializer.Serialize(call.Arguments);
            var req = new TaskApprovalRequestPayload(task.TaskId,
                                                     Guid.NewGuid().ToString("N"),
                                                     call.Name,
                                                     argsJson,
                                                     decision.Note,
                                                     decision.AllowForConversation);
            var answer = await task.RequestApprovalAsync(req, cancellationToken);
            return answer switch
            {
                RemoteApprovalAnswer.Allow => ApprovalAnswer.Allow,
                RemoteApprovalAnswer.AllowForConversation => ApprovalAnswer.AllowForConversation,
                _ => ApprovalAnswer.Deny,
            };
        }
    }

    private sealed class RemoteStepLimitPrompter : IStepLimitPrompter
    {
        public Task<bool> AskToContinueAsync(int stepsUsed, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

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
