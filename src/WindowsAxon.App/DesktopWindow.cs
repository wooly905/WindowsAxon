namespace WindowsAxon.App;

internal sealed record DesktopWindow(nint Handle,
                                     int ProcessId,
                                     long ProcessStartTime,
                                     string ProcessName,
                                     string Title);
