namespace WindowsAxon.Agent.Tools;

[AttributeUsage(AttributeTargets.Method)]
public sealed class ToolAttribute(string name,
                                  string group,
                                  string description,
                                  RiskLevel risk = RiskLevel.Read,
                                  bool policyDriven = false,
                                  int timeoutSeconds = 30,
                                  bool artifact = false,
                                  bool alwaysInclude = false,
                                  bool outbound = false) : Attribute
{
    public string Name { get; } = name;

    public string Group { get; } = group;

    /// <summary>The tool description sent to the model.</summary>
    public string Description { get; } = description;

    public RiskLevel Risk { get; } = risk;

    public bool Outbound { get; } = outbound;

    public bool PolicyDriven { get; } = policyDriven;

    public int TimeoutSeconds { get; } = timeoutSeconds;

    /// <summary>A successful result is passed back to the main agent when a sub-agent runs the tool.</summary>
    public bool Artifact { get; } = artifact;

    /// <summary>The model always sees the tool; it does not count toward MaxActiveTools.</summary>
    public bool AlwaysInclude { get; } = alwaysInclude;
}
