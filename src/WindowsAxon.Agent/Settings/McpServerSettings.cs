using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WindowsAxon.Agent.Settings;

/// <summary>One external MCP server. Set Url for HTTP, or Command and Args for stdio.</summary>
public sealed partial record McpServerSettings
{
    public const string CredentialPrefix = "credential:";

    /// <summary>Every MCP tool group and sub-agent name starts with this prefix.</summary>
    public const string GroupPrefix = "mcp_";

    /// <summary>Lowercase letters and digits. The tools are registered as mcp_{Name}_{tool}.</summary>
    public string Name { get; init; } = "";

    public string? Description
    {
        get; init;
    }

    public string? Url
    {
        get; init;
    }

    public string? Command
    {
        get; init;
    }

    public List<string> Args { get; init; } = [];

    /// <summary>The server tools to register. Empty registers every tool.</summary>
    public List<string> Tools { get; init; } = [];

    /// <summary>False keeps the server in settings but does not connect to it in new chats.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>True runs this server's tools without the approval card. False asks before every call.</summary>
    public bool AutoApprove
    {
        get; init;
    }

    /// <summary>HTTP header name to a credential:&lt;name&gt; reference in Windows Credential Manager.</summary>
    public Dictionary<string, string> Headers { get; init; } = [];

    [JsonIgnore]
    public string Group => $"{GroupPrefix}{Name}";

    public string ToolName(string tool) => $"{Group}_{tool}";

    public void Validate()
    {
        if (!ServerName().IsMatch(Name))
        {
            throw new ArgumentException($"MCP server name '{Name}' must contain only lowercase letters and digits.");
        }

        if (string.IsNullOrWhiteSpace(Url) == string.IsNullOrWhiteSpace(Command))
        {
            throw new ArgumentException($"MCP server '{Name}' must set either Url or Command.");
        }

        if (Url is not null)
        {
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                || !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new ArgumentException($"MCP server '{Name}' needs an http or https Url without embedded credentials.");
            }

            if (Headers.Count > 0 && !EndpointRules.MaySendApiKey(Url))
            {
                throw new ArgumentException($"MCP server '{Name}' sends headers with secrets. Use https, or http only for localhost.");
            }
        }
        else if (Headers.Count > 0)
        {
            throw new ArgumentException($"MCP server '{Name}' uses stdio. Headers apply only to a Url.");
        }

        if (Headers.Values.Any(value => !value.StartsWith(CredentialPrefix, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"MCP server '{Name}' header values must be credential:<name> references, not secrets.");
        }

        if (Tools.FirstOrDefault(tool => !FunctionName().IsMatch(ToolName(tool))) is { } invalid)
        {
            throw new ArgumentException($"MCP tool name '{ToolName(invalid)}' must match [a-zA-Z0-9_-] and have 64 characters or fewer.");
        }
    }

    public static void ValidateAll(IEnumerable<McpServerSettings> servers)
    {
        var names = new HashSet<string>();
        foreach (var server in servers)
        {
            server.Validate();
            if (!names.Add(server.Name))
            {
                throw new ArgumentException($"MCP server name '{server.Name}' is used more than once.");
            }
        }
    }

    public static bool IsValidToolName(string name) => FunctionName().IsMatch(name);

    [GeneratedRegex("^[a-z0-9]{1,20}$")]
    private static partial Regex ServerName();

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex FunctionName();
}
