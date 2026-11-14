using Microsoft.Extensions.AI;

namespace WindowsAxon.Agent.Tools;

public sealed record ToolEntry(string Name,
                               string Group,
                               RiskLevel Risk,
                               bool PolicyDriven,
                               TimeSpan Timeout,
                               AIFunction Function,
                               bool Artifact = false,
                               bool AlwaysInclude = false,
                               bool Outbound = false)
{
    public bool NeedsApproval => Outbound || PolicyDriven || Risk >= RiskLevel.Change;
}
