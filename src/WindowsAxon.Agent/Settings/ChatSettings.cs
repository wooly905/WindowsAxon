
namespace WindowsAxon.Agent.Settings;

public sealed record ChatSettings
{
    public string Endpoint { get; init; } = "local";

    public string Model { get; init; } = "windowsaxon-qwen3.5-9b";

    public int ContextWindow { get; init; } = 16384;

    public StepMode StepMode { get; init; } = StepMode.Structured;

    public ArgsSchemaMode ArgsSchema { get; init; } = ArgsSchemaMode.PerTool;

    public bool Thinking
    {
        get; init;
    }

    /// <summary>The reasoning_effort value sent when Thinking is false. Null sends nothing.</summary>
    public string? ReasoningEffortWhenThinkingOff { get; init; } = "none";

    public ToolSelectionMode ToolSelection { get; init; } = ToolSelectionMode.Groups;

    public int MaxActiveTools { get; init; } = 14;

    /// <summary>Auto-approve ordinary reads only. Sensitive reads and changes retain their approval policy.</summary>
    public bool AutoApproveTools { get; init; } = true;

    /// <summary>Internal hosts that web lookup may read: host names, IP addresses, or CIDR ranges in private address space.</summary>
    public List<string> AllowedPrivateHosts { get; init; } = [];
}
