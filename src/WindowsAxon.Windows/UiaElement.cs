namespace WindowsAxon.Windows;

internal sealed class UiaElement(IUIAutomationElement native)
{
    private const int BoundingRectangleProperty = 30001;
    private const int ProcessIdProperty = 30002;
    private const int ControlTypeProperty = 30003;
    private const int NameProperty = 30005;
    private const int AutomationIdProperty = 30011;
    private const int IsPasswordProperty = 30019;
    private const int NativeWindowHandleProperty = 30020;
    private const int IsOffscreenProperty = 30022;

    private const int InvokePattern = 10000;
    private const int ValuePattern = 10002;
    private const int RangeValuePattern = 10003;
    private const int ExpandCollapsePattern = 10005;
    private const int SelectionItemPattern = 10010;
    private const int TextPattern = 10014;
    private const int TogglePattern = 10015;

    public string Name => Property(NameProperty) as string ?? "";

    public string AutomationId => Property(AutomationIdProperty) as string ?? "";

    public int ProcessId => Convert.ToInt32(Property(ProcessIdProperty) ?? 0);

    public nint NativeWindowHandle => new(Convert.ToInt32(Property(NativeWindowHandleProperty) ?? 0));

    public bool IsOffscreen => Property(IsOffscreenProperty) is true;

    public bool IsPassword => Property(IsPasswordProperty) is true;

    public ControlType ControlType => (ControlType)Convert.ToInt32(Property(ControlTypeProperty) ?? 0);

    public string ControlTypeName => "ControlType." + ControlType;

    public ScreenRect BoundingRectangle => Property(BoundingRectangleProperty) is double[] { Length: 4 } bounds
        ? new(bounds[0], bounds[1], bounds[2], bounds[3])
        : default;

    public void SetFocus() => Uia.Call(native.SetFocus);

    public UiaElement? FindFirst(TreeScope scope, UiaCondition condition) =>
        Uia.Call(() => native.FindFirst(scope, condition.Native)) is { } element ? new(element) : null;

    public IReadOnlyList<UiaElement> FindAll(TreeScope scope, UiaCondition condition)
    {
        var elements = Uia.Call(() => native.FindAll(scope, condition.Native));
        if (elements is null)
        {
            return [];
        }

        var count = elements.Length;
        var result = new List<UiaElement>(count);
        for (var index = 0; index < count; index++)
        {
            result.Add(new(elements.GetElement(index)));
        }

        return result;
    }

    public bool TryInvoke() => Try<IUIAutomationInvokePattern>(InvokePattern, pattern => pattern.Invoke());

    public bool TryToggle() => Try<IUIAutomationTogglePattern>(TogglePattern, pattern => pattern.Toggle());

    public bool TrySelect() => Try<IUIAutomationSelectionItemPattern>(SelectionItemPattern, pattern => pattern.Select());

    public bool TryExpand() => Try<IUIAutomationExpandCollapsePattern>(ExpandCollapsePattern, pattern => pattern.Expand());

    public bool TryCollapse() => Try<IUIAutomationExpandCollapsePattern>(ExpandCollapsePattern, pattern => pattern.Collapse());

    public IUIAutomationValuePattern? ValuePatternOrNull() => Pattern<IUIAutomationValuePattern>(ValuePattern);

    public IUIAutomationRangeValuePattern? RangeValuePatternOrNull() => Pattern<IUIAutomationRangeValuePattern>(RangeValuePattern);

    public string? DocumentText(int maxLength) =>
        Pattern<IUIAutomationTextPattern>(TextPattern) is { } text ? Uia.Call(() => text.DocumentRange.GetText(maxLength)) : null;

    private T? Pattern<T>(int patternId) where T : class => Uia.Call(() => native.GetCurrentPattern(patternId)) as T;

    private bool Try<T>(int patternId, Action<T> action) where T : class
    {
        if (Pattern<T>(patternId) is not { } pattern)
        {
            return false;
        }

        Uia.Call(() => action(pattern));
        return true;
    }

    private object? Property(int propertyId) => Uia.Call(() => native.GetCurrentPropertyValue(propertyId));
}
