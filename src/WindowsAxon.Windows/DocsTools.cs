using System.ComponentModel;
using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Tools;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace WindowsAxon.Windows;

public sealed class DocsTools : IApprovalRule, IAsyncDisposable
{
    internal const string CodeSampleToolName = "microsoft_code_sample_search";

    private static readonly string[] CodeSampleLanguages =
    [
        "csharp",
        "javascript",
        "typescript",
        "python",
        "powershell",
        "azurecli",
        "al",
        "sql",
        "java",
        "kusto",
        "cpp",
        "go",
        "rust",
        "ruby",
        "php"
    ];

    private McpClient? _client;
    private bool _noticeAccepted;

    private async Task<McpClient> Client(CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            _client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri("https://learn.microsoft.com/api/mcp") }),
                                                  cancellationToken: cancellationToken);
            await _client.ListToolsAsync(cancellationToken: cancellationToken);
        }
        return _client;
    }

    private async Task<string> Call(string name,
                                    Dictionary<string, object?> arguments,
                                    CancellationToken cancellationToken)
    {
        var result = await (await Client(cancellationToken)).CallToolAsync(name,
                                                                           arguments,
                                                                           cancellationToken: cancellationToken);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(content => content.Text));
        if (result.IsError == true)
        {
            throw new InvalidOperationException(text);
        }

        _noticeAccepted = true;
        return text;
    }

    [WindowsTool("docs_search", ToolCatalog.BaseGroup, policyDriven: true, timeoutSeconds: 60)]
    [Description("Search Microsoft Learn technical documentation through its official MCP server. The query is sent to Microsoft.")]
    public Task<string> Search(string query, CancellationToken cancellationToken) => Call("microsoft_docs_search",
                                                                                          new()
                                                                                          {
                                                                                              ["query"] = query
                                                                                          },
                                                                                          cancellationToken);

    [WindowsTool("docs_fetch", "docs", policyDriven: true, timeoutSeconds: 60)]
    [Description("Fetch a Microsoft Learn article through its official MCP server. Requires an https learn.microsoft.com URL.")]
    public Task<string> Fetch(string url, CancellationToken cancellationToken)
    {
        var uri = BrowserTools.WebUrl(url);
        if (uri.Scheme != "https" || uri.Host != "learn.microsoft.com")
        {
            throw new ArgumentException("Use an https Microsoft Learn article URL.");
        }

        return Call("microsoft_docs_fetch",
                    new()
                    {
                        ["url"] = uri.AbsoluteUri
                    },
                    cancellationToken);
    }

    [WindowsTool("docs_code_search", "docs", policyDriven: true, timeoutSeconds: 60)]
    [Description("Search official Microsoft code samples through its official MCP server. The query and optional language are sent to Microsoft.")]
    public Task<string> SearchCode(string query,
                                   [Description("Optional. One of: csharp, javascript, typescript, python, powershell, azurecli, al, sql, java, kusto, cpp, go, rust, ruby, php. Omit to search every language.")] string? language = null,
                                   CancellationToken cancellationToken = default) =>
        Call(CodeSampleToolName, CodeSampleArguments(query, language), cancellationToken);

    internal static Dictionary<string, object?> CodeSampleArguments(string query, string? language)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Query is required.");
        }

        var arguments = new Dictionary<string, object?>
        {
            ["query"] = query.Trim()
        };
        if (string.IsNullOrWhiteSpace(language))
        {
            return arguments;
        }

        var canonical = CodeSampleLanguages.FirstOrDefault(name => name.Equals(language.Trim(),
                                                                                StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("language must be one of: " + string.Join(", ", CodeSampleLanguages) + ".");
        arguments["language"] = canonical;
        return arguments;
    }

    public bool AppliesTo(string toolName) => toolName is "docs_search" or "docs_fetch" or "docs_code_search";

    public ApprovalDecision Decide(FunctionCallContent call) => _noticeAccepted
        ? new(ApprovalOutcome.AutoApprove, false, null) : new(ApprovalOutcome.Ask,
                                                              false,
                                                              "Your documentation query, code-sample query, or article URL will be sent to Microsoft Learn.");

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }
    }
}
