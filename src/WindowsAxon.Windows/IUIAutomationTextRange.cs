using System.Runtime.InteropServices;

namespace WindowsAxon.Windows;

[ComImport]
[Guid("a543cc6a-f4ae-494b-8239-c814481187a8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationTextRange
{
    void Reserved1();
    void Reserved2();
    void Reserved3();
    void Reserved4();
    void Reserved5();
    void Reserved6();
    void Reserved7();
    void Reserved8();
    void Reserved9();
    [return: MarshalAs(UnmanagedType.BStr)]
    string GetText(int maxLength);
}
