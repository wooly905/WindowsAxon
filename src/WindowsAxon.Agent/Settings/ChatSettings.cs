
namespace WindowsAxon.Agent.Settings;

public sealed record ChatSettings
{
    public string Endpoint { get; init; } = "local";

    public string Model { get; init; } = "auto";

    public int ContextWindow { get; init; } = 16384;

    public StepMode StepMode { get; init; } = StepMode.Structured;

    public ArgsSchemaMode ArgsSchema { get; init; } = ArgsSchemaMode.PerTool;

    public bool Thinking
    {
        get; init;
    }

    /// <summary>The reasoning_effort value sent when Thinking is true, e.g. low, medium, high. Null sends nothing.</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>The reasoning_effort value sent when Thinking is false. Null sends nothing.</summary>
    public string? ReasoningEffortWhenThinkingOff { get; init; } = "none";

    public ToolSelectionMode ToolSelection { get; init; } = ToolSelectionMode.Groups;

    public int MaxActiveTools { get; init; } = 14;

    /// <summary>Auto-approve ordinary reads only. Sensitive reads and changes retain their approval policy.</summary>
    public bool AutoApproveTools { get; init; } = true;

    /// <summary>Approve every tool call without asking, including changes, dangerous actions, and network requests.</summary>
    public bool ApproveAllTools { get; init; }

    /// <summary>The approval level. Null uses AutoApproveTools and ApproveAllTools from older settings files.</summary>
    public ApprovalLevel? ApprovalLevel { get; init; }

    public ApprovalLevel ResolveApprovalLevel() =>
        ApprovalLevel ?? (ApproveAllTools ? Settings.ApprovalLevel.All
            : AutoApproveTools ? Settings.ApprovalLevel.Read
            : Settings.ApprovalLevel.Ask);

    /// <summary>Internal hosts that web lookup may read: host names, IP addresses, or CIDR ranges in private address space.</summary>
    public List<string> AllowedPrivateHosts { get; init; } = [];

    /// <summary>External MCP servers whose tools the agent can use.</summary>
    public List<McpServerSettings> McpServers { get; init; } = [];

    /// <summary>Distributed mesh settings for connecting to WindowsAxon.Hub.</summary>
    public MeshSettings Mesh { get; init; } = new();
}
