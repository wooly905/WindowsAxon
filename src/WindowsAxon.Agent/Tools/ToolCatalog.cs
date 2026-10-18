using Microsoft.Extensions.AI;

namespace WindowsAxon.Agent.Tools;

public sealed record ToolEntry(string Name,
                               string Group,
                               RiskLevel Risk,
                               bool PolicyDriven,
                               TimeSpan Timeout,
                               AIFunction Function,
                               bool Artifact = false)
{
    public bool NeedsApproval => PolicyDriven || Risk >= RiskLevel.Change;
}

/// <summary>All tools that WindowsAxon can run, with group, risk, and timeout.</summary>
public sealed class ToolCatalog
{
    public const string BaseGroup = "base";

    /// <summary>Base tools that are not in the catalog: AgentSkillsProvider adds the first two, AgentFactory adds enable_tools.</summary>
    public static readonly IReadOnlySet<string> ExtraBaseToolNames = new HashSet<string>
    {
        "load_skill",
        "read_skill_resource",
        "enable_tools"
    };

    public static readonly IReadOnlySet<string> AppControlGroups = new HashSet<string> { "browser", "desktop_ui" };

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly List<ToolEntry> _entries = [];

    public IReadOnlyList<ToolEntry> Entries => _entries;

    public IReadOnlyList<string> Groups => _entries.Select(e => e.Group).Where(g => g != BaseGroup).Distinct().ToList();

    public void Add(AIFunction function,
                    string group,
                    RiskLevel risk,
                    bool policyDriven = false,
                    TimeSpan? timeout = null,
                    bool artifact = false)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentException.ThrowIfNullOrWhiteSpace(group);

        if (Find(function.Name) is not null)
        {
            throw new InvalidOperationException($"Tool '{function.Name}' is already registered.");
        }

        _entries.Add(new ToolEntry(function.Name,
                                   group,
                                   risk,
                                   policyDriven,
                                   timeout ?? DefaultTimeout,
                                   function,
                                   artifact));
    }

    public ToolEntry? Find(string name) => _entries.FirstOrDefault(e => e.Name == name);

    public IReadOnlyList<string> ToolsInGroup(string group) => _entries.Where(e => e.Group == group).Select(e => e.Name).ToList();

    public bool IsBaseTool(string name) => ExtraBaseToolNames.Contains(name) || Find(name)?.Group == BaseGroup;

    public IList<AITool> BuildAgentTools(Func<ToolEntry, AIFunction> wrap, Func<ToolEntry, bool>? include = null)
    {
        return [.. _entries.Where(entry => include?.Invoke(entry) != false).Select(entry =>
        {
            var wrapped = wrap(entry);
            return entry.NeedsApproval ? new ApprovalRequiredAIFunction(wrapped) : (AITool)wrapped;
        })];
    }
}
