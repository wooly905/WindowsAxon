namespace WindowsAxon.Agent.Settings;

/// <summary>Which tool calls run without the approval card. Each level includes the level before it.</summary>
public enum ApprovalLevel
{
    /// <summary>Every tool call asks.</summary>
    Ask,

    /// <summary>Ordinary reads run without asking.</summary>
    Read,

    /// <summary>Reads and changes on this computer run without asking. Dangerous and network actions ask.</summary>
    Local,

    /// <summary>Every tool call runs without asking.</summary>
    All,
}
