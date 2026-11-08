using System.Runtime.InteropServices;

namespace WindowsAxon.Windows;

[ComImport]
[Guid("59213f4f-7346-49e5-b120-80555987a148")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationRangeValuePattern
{
    void SetValue(double value);

    double CurrentValue { get; }

    bool CurrentIsReadOnly
    {
        [return: MarshalAs(UnmanagedType.Bool)]
        get;
    }

    double CurrentMaximum { get; }

    double CurrentMinimum { get; }
}
