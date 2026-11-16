using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Settings;
using WindowsAxon.Agent.Tools;

namespace WindowsAxon.Windows;

/// <summary>The tools of one external MCP server, registered as mcp_{server}_{tool}. Every call asks for approval.</summary>
public sealed class McpServerTools : IApprovalRule, IAsyncDisposable
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);

    private readonly McpClient _client;
    private readonly IReadOnlyList<McpClientTool> _tools;
    private readonly HashSet<string> _names = [];

    private McpServerTools(McpServerSettings server, McpClient client, IReadOnlyList<McpClientTool> tools)
    {
        Server = server;
        _client = client;
        _tools = tools;
    }

    public McpServerSettings Server
    {
        get;
    }

    /// <summary>Tools in the Tools list that the server does not have.</summary>
    public IReadOnlyList<string> MissingTools => [.. Server.Tools.Where(name => _tools.All(t => t.Name != name))];

    public IReadOnlyCollection<string> ToolNames => _names;

    /// <summary>Connects to the server and reads its tool list.</summary>
    public static async Task<McpServerTools> ConnectAsync(McpServerSettings server,
                                                          Func<string, string?> readSecret,
                                                          CancellationToken cancellationToken)
    {
        server.Validate();
        IClientTransport transport = server.Url is not null
            ? new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = server.Name,
                Endpoint = new Uri(server.Url),
                AdditionalHeaders = server.Headers.ToDictionary(h => h.Key,
                                                                h => readSecret(h.Value[McpServerSettings.CredentialPrefix.Length..]) ?? ""),
            })
            : new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.Name,
                Command = server.Command!,
                Arguments = server.Args,
            });
        var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            var selected = tools.Where(t => (server.Tools.Count == 0 || server.Tools.Contains(t.Name))
                                            && McpServerSettings.IsValidToolName(server.ToolName(t.Name)))
                .ToList();
            return new McpServerTools(server, client, selected);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public void Register(ToolCatalog catalog)
    {
        foreach (var tool in _tools)
        {
            var name = Server.ToolName(tool.Name);
            // A trusted server is policy-driven, so its rule decides; otherwise the outbound change asks every time.
            catalog.Add(tool.WithName(name),
                        Server.Group,
                        Server.AutoApprove ? RiskLevel.Read : RiskLevel.Change,
                        policyDriven: true,
                        timeout: CallTimeout,
                        outbound: !Server.AutoApprove);
            _names.Add(name);
        }
    }

    public bool AppliesTo(string toolName) => _names.Contains(toolName);

    public ApprovalDecision Decide(FunctionCallContent call) =>
        Server.AutoApprove
            ? new(ApprovalOutcome.AutoApprove, false, null)
            : new(ApprovalOutcome.Ask,
                  false,
                  Server.Url is not null
                      ? $"The arguments will be sent to MCP server '{Server.Name}' at {Server.Url}."
                      : $"The arguments will be sent to MCP server '{Server.Name}', a program on this computer ({Server.Command}).");

    public bool AlwaysAsks(FunctionCallContent call) => !Server.AutoApprove;

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
