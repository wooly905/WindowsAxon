using System.Runtime.InteropServices;

namespace WindowsAxon.Windows;

// The interfaces below declare UIAutomationClient.idl members in vtable order; ReservedN members fill unused slots.
[ComImport]
[Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomation
{
    void Reserved1();
    void Reserved2();
    void Reserved3();
    IUIAutomationElement ElementFromHandle(nint hwnd);
    void Reserved5();
    IUIAutomationElement GetFocusedElement();
    void Reserved7();
    void Reserved8();
    void Reserved9();
    void Reserved10();
    void Reserved11();
    void Reserved12();
    void Reserved13();
    void Reserved14();
    void Reserved15();
    void Reserved16();
    void Reserved17();
    void Reserved18();
    IUIAutomationCondition CreateTrueCondition();
    void Reserved20();
    IUIAutomationCondition CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value);
    void Reserved22();
    IUIAutomationCondition CreateAndCondition(IUIAutomationCondition first, IUIAutomationCondition second);
    void Reserved24();
    void Reserved25();
    IUIAutomationCondition CreateOrCondition(IUIAutomationCondition first, IUIAutomationCondition second);
}
