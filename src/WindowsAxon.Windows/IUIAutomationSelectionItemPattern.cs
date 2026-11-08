using System.Runtime.InteropServices;

namespace WindowsAxon.Windows;

[ComImport]
[Guid("a8efa66a-0fda-421a-9194-38021f3578ea")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationSelectionItemPattern
{
    void Select();
}
