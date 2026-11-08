using System.Runtime.InteropServices;

namespace WindowsAxon.Windows;

/// <summary>Thin wrapper over the native Win32 UI Automation client (UIAutomationCore COM API).</summary>
internal static class Uia
{
    private const int NameProperty = 30005;
    private const int AutomationIdProperty = 30011;
    private const int ControlTypeProperty = 30003;

    private static readonly Lazy<IUIAutomation> Automation = new(() => (IUIAutomation)Activator.CreateInstance(System.Type.GetTypeFromCLSID(new Guid("e22ad333-b25f-460c-83d0-0581107395c9"), true)!)!);

    public static UiaCondition TrueCondition => new(Call(() => Automation.Value.CreateTrueCondition()));

    public static UiaElement FromHandle(nint handle) => new(Call(() => Automation.Value.ElementFromHandle(handle)));

    public static UiaElement? Focused() => Call(() => Automation.Value.GetFocusedElement()) is { } element ? new(element) : null;

    public static UiaCondition Name(string name) => Property(NameProperty, name);

    public static UiaCondition AutomationId(string automationId) => Property(AutomationIdProperty, automationId);

    public static UiaCondition Type(ControlType type) => Property(ControlTypeProperty, (int)type);

    public static UiaCondition And(UiaCondition first, UiaCondition second) => new(Call(() => Automation.Value.CreateAndCondition(first.Native, second.Native)));

    public static UiaCondition Or(UiaCondition first, UiaCondition second) => new(Call(() => Automation.Value.CreateOrCondition(first.Native, second.Native)));

    private static UiaCondition Property(int property, object value) => new(Call(() => Automation.Value.CreatePropertyCondition(property, value)));

    internal static T Call<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (COMException error) when (error.HResult is unchecked((int)0x80040201) or unchecked((int)0x80070578))
        {
            throw new InvalidOperationException("The UI element is no longer available. Take a new ui_snapshot.", error);
        }
        catch (COMException error) when (error.HResult == unchecked((int)0x80040200))
        {
            throw new InvalidOperationException("The UI element is disabled.", error);
        }
        catch (COMException error) when (error.HResult == unchecked((int)0x80040204))
        {
            throw new NotSupportedException("The UI element does not support this operation.", error);
        }
    }

    internal static void Call(Action action) => Call(() =>
    {
        action();
        return true;
    });
}
