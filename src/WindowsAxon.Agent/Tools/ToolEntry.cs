using Microsoft.Extensions.AI;

namespace WindowsAxon.Agent.Tools;

public sealed record ToolEntry(string Name,
                               string Group,
                               RiskLevel Risk,
                               bool PolicyDriven,
                               TimeSpan Timeout,
                               AIFunction Function,
                               bool Artifact = false,
                               bool AlwaysInclude = false)
{
    public bool NeedsApproval => PolicyDriven || Risk >= RiskLevel.Change;
}
