using WindowsAxon.Agent.Tools;

namespace WindowsAxon.Agent.Selection;

/// <summary>Active skills and groups, and the step counter, for the current request of one session.</summary>
public sealed class ToolSelectionState(ToolCatalog catalog, int maxActiveTools, StepBudget? budget = null)
{
    public const int BaseLimit = StepBudget.BaseLimit;
    public const int AppControlLimit = StepBudget.AppControlLimit;
    public const int ExtensionSteps = StepBudget.ExtensionSteps;

    private readonly Dictionary<string, (IReadOnlyList<string> Tools, long LastUsed)> _active = [];
    private long _clock;
    private readonly List<string> _history = [];

    /// <summary>Every key activated in the current request, in order (for the evaluation).</summary>
    public IReadOnlyList<string> ActivationHistory => _history;

    public IReadOnlyList<string> ActiveKeys => _active.Keys.ToList();

    public IReadOnlySet<string> ActiveToolNames => _active.Values.SelectMany(v => v.Tools).ToHashSet();

    /// <summary>Steps of the current request. A sub-agent shares the budget of the main agent.</summary>
    public StepBudget Budget { get; } = budget ?? new StepBudget();

    public int StepCount => Budget.StepCount;

    public int StepLimit => Budget.StepLimit;

    public bool ExtensionUsed => Budget.ExtensionUsed;

    /// <summary>The user did not extend the limit: the model may only give a final answer.</summary>
    public bool MustFinish
    {
        get => Budget.MustFinish;
        set => Budget.MustFinish = value;
    }

    /// <summary>The last run ended with ask_user; the next user message is a reply.</summary>
    public bool AwaitingReply
    {
        get; set;
    }

    /// <summary>ToolSelection = All: the model sees every tool, so every tool is active.</summary>
    public bool AllToolsActive
    {
        get; init;
    }

    public bool IsActive(string toolName) => AllToolsActive || catalog.IsBaseTool(toolName)
        || ActiveToolNames.Contains(toolName);

    public ActivationResult Activate(string key, IReadOnlyList<string> tools)
    {
        _history.Add(key);
        _active[key] = (tools, ++_clock);
        var appControlGroup = key.StartsWith("group:", StringComparison.Ordinal)
            && ToolCatalog.AppControlGroups.Contains(ActivationResult.ShortName(key));
        if (appControlGroup || tools.Any(IsAppControlTool))
        {
            Budget.AppControlUsed = true; // a group or a skill with browser or desktop_ui tools
        }

        var removed = new List<(string, IReadOnlyList<string>)>();
        while (CountedTools() > maxActiveTools && _active.Count > 1)
        {
            var oldest = _active.Where(a => a.Key != key).MinBy(a => a.Value.LastUsed);
            _active.Remove(oldest.Key);
            removed.Add((oldest.Key, oldest.Value.Tools.Where(t => !catalog.IsBaseTool(t)).ToList()));
        }

        return new ActivationResult(key, removed);
    }

    /// <summary>ToolSelection = SubAgents: records a delegate call in the activation history.</summary>
    public void RecordDelegation(string agent) => _history.Add($"agent:{agent}");

    public void MarkUsed(string toolName)
    {
        if (IsAppControlTool(toolName))
        {
            Budget.AppControlUsed = true; // covers ToolSelection = All, where no group is activated
        }

        foreach (var key in _active.Where(a => a.Value.Tools.Contains(toolName)).Select(a => a.Key).ToList())
        {
            _active[key] = (_active[key].Tools, ++_clock);
        }
    }

    public void CountStep() => Budget.CountStep();

    public void Extend() => Budget.Extend();

    public void StartRequest()
    {
        _active.Clear();
        _history.Clear();
        Budget.Reset();
        AwaitingReply = false;
    }

    private bool IsAppControlTool(string toolName) =>
        catalog.Find(toolName)?.Group is { } group && ToolCatalog.AppControlGroups.Contains(group);

    private int CountedTools() => ActiveToolNames.Count(t => !catalog.IsBaseTool(t));
}
