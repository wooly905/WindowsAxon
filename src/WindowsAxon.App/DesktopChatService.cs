using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsAxon.Agent;
using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Audit;
using WindowsAxon.Agent.Selection;
using WindowsAxon.Agent.Settings;
using WindowsAxon.Agent.Skills;
using Microsoft.Extensions.AI;
using WindowsAxon.Windows;

namespace WindowsAxon.App;

public sealed class DesktopChatService(IApprovalPrompter approvals,
                                       IStepLimitPrompter stepLimits,
                                       string? settingsPath = null) : IDisposable
{
    private const string ManagedCredentialPrefix = "credential:WindowsAxon/desktop/";
    private static readonly string DataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                             "WindowsAxon");
    private static readonly string? ConfigOverride = GetConfigOverride();

    public static string SettingsPath => ConfigOverride ?? Path.Combine(DataFolder, "settings.json");

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

    private bool _forceNewRequest;

    public ModelSettings Settings { get; private set; } = new();

    public void LoadSettings()
    {
        var path = settingsPath is not null
            || ConfigOverride is not null
            || File.Exists(_settingsPath) ? _settingsPath : Path.Combine(Environment.CurrentDirectory,
                                                                         "settings.local.json");
        if (File.Exists(path))
        {
            var settings = ModelSettings.Load(File.ReadAllText(path));
            ValidateEndpoint(settings.ChatEndpoint.BaseUrl);
            Settings = settings;
        }
        else if (settingsPath is not null || ConfigOverride is not null)
        {
            throw new FileNotFoundException("The settings file does not exist.", path);
        }
    }

    public async Task<RunResult> RunAsync(string text, CancellationToken cancellationToken)
    {
        if (_runner is null)
        {
            _toolSet?.Dispose();
            _model?.Dispose();
            _model = new EndpointFactory(Settings, new WindowsSecretStore()).CreateChatClient();
            var appTools = new WindowsAppTools();
            var catalog = appTools.BuildCatalog();
            var windowTools = new WindowsWindowTools();
            windowTools.AddTo(catalog);
            _toolSet = new WindowsToolSet(catalog,
                                          _model,
                                          DataFolder,
                                          Guid.NewGuid().ToString("N"),
                                          windowTools.ResolveHandle,
                                          () => EmergencyStopRequested?.Invoke(),
                                          active => InputActiveChanged?.Invoke(active));
            var skillsRoot = Path.Combine(AppContext.BaseDirectory, "app-skills");
            var audit = new ToolStartAuditLog(new JsonlAuditLog(Path.Combine(DataFolder, "logs")),
                                              tool => ToolStarted?.Invoke(tool));
            var dependencies = new AgentDependencies(Settings.Chat,
                                                     _model,
                                                     catalog,
                                                     SkillCatalog.Load([(skillsRoot, true)]),
                                                     [skillsRoot],
                                                     audit,
                                                     [
appTools,
                                                         windowTools,
                                                         .. _toolSet.ApprovalRules,
                                                         new SensitiveReadRule(Settings.IsChatRemote,
                                                                               new Uri(Settings.ChatEndpoint.BaseUrl).Host)

],
                                                     stepLimits,
                                                     DocsAvailable: true);
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
                             EndpointApi? api = null)
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
        var previousReference = Settings.ChatEndpoint.ApiKey;
        var reference = !removeApiKey && new Uri(Settings.ChatEndpoint.BaseUrl.TrimEnd('/')) == new Uri(baseUrl)
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
            [Settings.Chat.Endpoint] = Settings.ChatEndpoint with
            {
                BaseUrl = baseUrl,
                ApiKey = reference,
                Api = api ?? Settings.ChatEndpoint.Api,
            },
        };
        var updated = Settings with
        {
            Endpoints = endpoints,
            Chat = Settings.Chat with
            {
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
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var json = JsonSerializer.Serialize(new
        {
            Models = new
            {
                updated.Endpoints,
                updated.Chat
            }
        },
                                            options);
        var secrets = new WindowsSecretStore();
        var temporaryPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
        if (newCredential is not null)
        {
            secrets.Write(newCredential, apiKey);
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
        NewChat();
        Settings = updated;
        if (previousReference is not null && previousReference.StartsWith(ManagedCredentialPrefix,
                                                                          StringComparison.Ordinal)
            && Guid.TryParseExact(previousReference[ManagedCredentialPrefix.Length..], "N", out _)
            && !updated.Endpoints.Values.Any(endpoint => endpoint.ApiKey == previousReference))
        {
            secrets.Delete(previousReference["credential:".Length..]);
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
