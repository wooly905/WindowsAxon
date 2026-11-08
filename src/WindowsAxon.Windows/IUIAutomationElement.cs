using System.Runtime.InteropServices;

namespace WindowsAxon.Windows;

[ComImport]
[Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElement
{
    void SetFocus();
    void Reserved2();
    IUIAutomationElement? FindFirst(TreeScope scope, IUIAutomationCondition condition);
    IUIAutomationElementArray? FindAll(TreeScope scope, IUIAutomationCondition condition);
    void Reserved5();
    void Reserved6();
    void Reserved7();
    [return: MarshalAs(UnmanagedType.Struct)]
    object? GetCurrentPropertyValue(int propertyId);
    void Reserved9();
    void Reserved10();
    void Reserved11();
    void Reserved12();
    void Reserved13();
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object? GetCurrentPattern(int patternId);
}
