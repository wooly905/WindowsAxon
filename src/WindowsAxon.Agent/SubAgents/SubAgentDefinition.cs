using WindowsAxon.Agent.Tools;

namespace WindowsAxon.Agent.SubAgents;

/// <summary>One sub-agent: the tools it owns and the rules added to its prompt.</summary>
public sealed record SubAgentDefinition(string Name,
                                        string Description,
                                        IReadOnlyList<string> Groups,
                                        IReadOnlyList<string> Rules,
                                        IReadOnlyList<string>? ExtraTools = null,
                                        IReadOnlyList<string>? ExcludedTools = null)
{
    public bool Includes(ToolEntry entry) =>
        (Groups.Contains(entry.Group) && !entry.AlwaysInclude && ExcludedTools?.Contains(entry.Name) != true)
        || ExtraTools?.Contains(entry.Name) == true;
}
