using System.Runtime.InteropServices;

namespace WindowsAxon.Windows;

[ComImport]
[Guid("32eba289-3583-42c9-9c59-3b6d9a1e9b6a")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationTextPattern
{
    void Reserved1();
    void Reserved2();
    void Reserved3();
    void Reserved4();
    IUIAutomationTextRange DocumentRange { get; }
}
