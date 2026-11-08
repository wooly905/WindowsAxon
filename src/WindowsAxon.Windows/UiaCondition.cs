namespace WindowsAxon.Windows;

internal sealed class UiaCondition(IUIAutomationCondition native)
{
    internal IUIAutomationCondition Native { get; } = native;
}
