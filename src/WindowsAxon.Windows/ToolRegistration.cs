using System.ComponentModel;
using System.Reflection;
using WindowsAxon.Agent.Tools;
using Microsoft.Extensions.AI;

namespace WindowsAxon.Windows;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class WindowsToolAttribute(string name,
                                           string group,
                                           RiskLevel risk = RiskLevel.Read,
                                           bool policyDriven = false,
                                           int timeoutSeconds = 30,
                                           bool artifact = false) : Attribute
{
    public string Name { get; } = name;

    public string Group { get; } = group;

    public RiskLevel Risk { get; } = risk;

    public bool PolicyDriven { get; } = policyDriven;

    public int TimeoutSeconds { get; } = timeoutSeconds;

    /// <summary>A successful result is passed back to the main agent when a sub-agent runs the tool.</summary>
    public bool Artifact { get; } = artifact;
}

public static class ToolRegistration
{
    public static void Add(ToolCatalog catalog, object provider)
    {
        foreach (var method in provider.GetType().GetMethods())
        {
            if (method.GetCustomAttribute<WindowsToolAttribute>() is not { } tool)
            {
                continue;
            }

            catalog.Add(new WindowsFunction(AIFunctionFactory.Create(method,
                                                                     provider,
                                                                     tool.Name,
                                                                     method.GetCustomAttribute<DescriptionAttribute>()?.Description),
                                            provider is BrowserTools browser ? browser.CancelPending : null),
                        tool.Group,
                        tool.Risk,
                        tool.PolicyDriven,
                        TimeSpan.FromSeconds(tool.TimeoutSeconds),
                        tool.Artifact);
        }
    }

    private sealed class WindowsFunction(AIFunction inner, Action? cancel) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments,
                                                                    CancellationToken cancellationToken)
        {
            using var stop = cancel is null ? default : cancellationToken.Register(cancel);
            return await Task.Run(async () => await base.InvokeCoreAsync(arguments, cancellationToken),
                                  cancellationToken);
        }
    }
}
