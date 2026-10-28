using Microsoft.Extensions.AI;

namespace WindowsAxon.Agent.Approval;

/// <summary>With a remote endpoint, reading files, pages, or windows sends the content out of the computer.</summary>
public sealed class SensitiveReadRule(bool endpointIsRemote, string endpointHost) : IApprovalRule
{
    private static readonly HashSet<string> Tools = [
"fs_read_text",
        "browser_read",
        "ui_read_text",
        "browser_snapshot",
        "browser_find",
        "web_search",
        "web_read",
        "ui_snapshot",
        "ui_screenshot"
];

    public bool AppliesTo(string toolName) => Tools.Contains(toolName);

    public ApprovalDecision Decide(FunctionCallContent call) =>
        endpointIsRemote
        ? new ApprovalDecision(ApprovalOutcome.Ask, true, $"The content will be sent to {endpointHost}.")
        : new ApprovalDecision(ApprovalOutcome.AutoApprove, false, null);
}
