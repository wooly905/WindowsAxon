using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowsAxon.Windows;

/// <summary>Unicode text access to the Win32 clipboard. Call from a dedicated thread such as <see cref="ApartmentThread"/>.</summary>
internal static class Win32Clipboard
{
    private const uint UnicodeText = 13;
    private const uint Moveable = 0x0002;
    private static readonly nint MessageOnlyParent = new(-3);

    public static string? GetText()
    {
        using var owner = Open();
        if (!IsClipboardFormatAvailable(UnicodeText))
        {
            return null;
        }

        var data = GetClipboardData(UnicodeText);
        if (data == nint.Zero)
        {
            return null;
        }

        var pointer = GlobalLock(data);
        if (pointer == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            // Read up to the terminator, bounded by the allocation size in case the data is not terminated.
            var maxChars = (int)Math.Min(int.MaxValue, (long)GlobalSize(data) / sizeof(char));
            var text = Marshal.PtrToStringUni(pointer, maxChars);
            var end = text.IndexOf('\0');
            return end >= 0 ? text[..end] : text;
        }
        finally
        {
            GlobalUnlock(data);
        }
    }

    public static void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var bytes = (nuint)((text.Length + 1) * sizeof(char));
        var memory = GlobalAlloc(Moveable, bytes);
        if (memory == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var pointer = GlobalLock(memory);
            if (pointer == nint.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
                Marshal.WriteInt16(pointer, text.Length * sizeof(char), 0);
            }
            finally
            {
                GlobalUnlock(memory);
            }

            using var owner = Open();
            if (!EmptyClipboard())
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (SetClipboardData(UnicodeText, memory) == nint.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            // The system owns the memory after a successful SetClipboardData.
            memory = nint.Zero;
        }
        finally
        {
            if (memory != nint.Zero)
            {
                GlobalFree(memory);
            }
        }
    }

    private static ClipboardSession Open()
    {
        // SetClipboardData fails when the clipboard was opened without an owner window, so use a message-only window.
        var window = CreateWindowEx(0, "STATIC", null, 0, 0, 0, 0, 0, MessageOnlyParent, nint.Zero, nint.Zero, nint.Zero);
        if (window == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        // Another application may hold the clipboard briefly, so retry before failing.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(window))
            {
                return new ClipboardSession(window);
            }

            Thread.Sleep(100);
        }

        var error = Marshal.GetLastWin32Error();
        DestroyWindow(window);
        throw new InvalidOperationException("The clipboard is in use by another application. Try again.", new Win32Exception(error));
    }

    private readonly struct ClipboardSession(nint window) : IDisposable
    {
        public void Dispose()
        {
            CloseClipboard();
            DestroyWindow(window);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint extendedStyle,
                                              string className,
                                              string? windowName,
                                              uint style,
                                              int x,
                                              int y,
                                              int width,
                                              int height,
                                              nint parent,
                                              nint menu,
                                              nint instance,
                                              nint parameter);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalFree(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll")]
    private static extern nuint GlobalSize(nint memory);
}
