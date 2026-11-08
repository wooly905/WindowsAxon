using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowsAxon.Windows;

public static class UiSafety
{
    public static void CheckProcess(string name)
    {
        if (new[] {
            "WindowsAxon.App",
            "CredentialUIBroker",
            "consent",
            "SecurityHealthHost",
            "SecHealthUI",
            "mstsc",
            "msrdc",
            "KeePass",
            "KeePassXC",
            "1Password",
            "Bitwarden",
            "LastPass"
        }.Contains(name,
                   StringComparer.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Security, credential, password-manager, remote-desktop, and WindowsAxon windows cannot be controlled.");
        }
    }

    internal static void CheckElement(UiaElement element)
    {
        if (element.IsPassword
            || System.Text.RegularExpressions.Regex.IsMatch(element.Name + " " + element.AutomationId,
                                                            @"password|passcode|one.?time|verification.?code|security.?code|\botp\b|credit.?card|card.?number|\bcvv\b|\bcvc\b",
                                                            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            throw new UnauthorizedAccessException("Password, verification-code, and payment controls require the user.");
        }
    }
}

internal sealed class DesktopInput : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly nint _window;
    private readonly uint _threadId;
    private readonly Thread _thread;
    private readonly HashSet<ushort> _pressed = [];
    private readonly nint _previousDpi;

    public DesktopInput(nint window, Action emergencyStop)
    {
        _window = window;
        _previousDpi = SetThreadDpiAwarenessContext(new nint(-4));
        var started = new TaskCompletionSource<uint>();
        // RegisterHotKey and GetMessage must run on the same dedicated thread.
        _thread = new Thread(() =>
        {
            if (!RegisterHotKey(nint.Zero, 1, 0x4003, 0x51))
            {
                started.SetException(new Win32Exception(Marshal.GetLastWin32Error()));
                return;
            }
            started.SetResult(GetCurrentThreadId());
            try
            {
                while (GetMessage(out var message, nint.Zero, 0, 0) > 0)
                {
                    if (message.Message == 0x312)
                    {
                        _stop.Cancel();
                        emergencyStop();
                    }
                }
            }
            finally
            {
                UnregisterHotKey(nint.Zero, 1);
            }
        })
        {
            IsBackground = true
        };
        _thread.Start();
        try
        {
            _threadId = started.Task.GetAwaiter().GetResult();
        }
        catch
        {
            _stop.Dispose();
            if (_previousDpi != nint.Zero)
            {
                SetThreadDpiAwarenessContext(_previousDpi);
            }

            throw;
        }
        ShowWindow(window, 9);
        SetForegroundWindow(window);
        try
        {
            Check(CancellationToken.None);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Check(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _stop.Token.ThrowIfCancellationRequested();
        if (GetForegroundWindow() != _window)
        {
            throw new InvalidOperationException("Input stopped because another window became active.");
        }
    }

    public void Key(ushort key, bool up, CancellationToken token)
    {
        Check(token);
        Send(new Input
        {
            Type = 1,
            Data = new InputData
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = key,
                    Flags = up ? 2u : 0u
                }
            }
        });
        if (up)
        {
            _pressed.Remove(key);
        }
        else
        {
            _pressed.Add(key);
        }
    }

    public void Text(string text, CancellationToken token)
    {
        foreach (var character in text)
        {
            Check(token);
            Send(new Input
            {
                Type = 1,
                Data = new InputData
                {
                    Keyboard = new KeyboardInput
                    {
                        Scan = character,
                        Flags = 4
                    }
                }
            });
            Send(new Input
            {
                Type = 1,
                Data = new InputData
                {
                    Keyboard = new KeyboardInput
                    {
                        Scan = character,
                        Flags = 6
                    }
                }
            });
        }
    }

    public void Shortcut(string shortcut, CancellationToken token)
    {
        var parts = shortcut.Split('+');
        var keys = parts.Select(KeyCode).ToArray();
        var down = new Stack<ushort>();
        try
        {
            foreach (var key in keys)
            {
                Key(key, false, token);
                down.Push(key);
            }
        }
        finally
        {
            foreach (var key in down)
            {
                Send(new Input
                {
                    Type = 1,
                    Data = new InputData
                    {
                        Keyboard = new KeyboardInput
                        {
                            VirtualKey = key,
                            Flags = 2
                        }
                    }
                });
            }
        }
    }

    private static ushort KeyCode(string key) => key.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" => 0x11,
        "ALT" => 0x12,
        "SHIFT" => 0x10,
        "ENTER" => 0x0d,
        "TAB" => 9,
        "ESC" or "ESCAPE" => 0x1b,
        "DELETE" => 0x2e,
        "BACKSPACE" => 8,
        "SPACE" => 0x20,
        "LEFT" => 0x25,
        "UP" => 0x26,
        "RIGHT" => 0x27,
        "DOWN" => 0x28,
        _ when key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]) => char.ToUpperInvariant(key[0]),
        _ when key.StartsWith('F')
        && int.TryParse(key[1..], out var number)
        && number is >= 1 and <= 24 => (ushort)(0x70 + number - 1),
        _ => throw new ArgumentException("Unsupported shortcut key."),
    };

    public void Drag(ScreenRect bounds, IReadOnlyList<ScreenPoint> points, CancellationToken token)
    {
        if (points.Count < 2)
        {
            throw new ArgumentException("A drag requires two or more points.");
        }

        if (points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X is < 0 or > 1000
            || point.Y is < 0 or > 1000))
        {
            throw new ArgumentException("Coordinates must be 0-1000.");
        }

        void Move(ScreenPoint point)
        {
            Check(token);
            if (point.X is < 0 or > 1000 || point.Y is < 0 or > 1000)
            {
                throw new ArgumentException("Coordinates must be 0-1000.");
            }

            var x = bounds.X + bounds.Width * point.X / 1000;
            var y = bounds.Y + bounds.Height * point.Y / 1000;
            Send(new Input
            {
                Data = new InputData
                {
                    Mouse = new MouseInput
                    {
                        X = (int)((x - GetSystemMetrics(76)) * 65535 / (GetSystemMetrics(78) - 1)),
                        Y = (int)((y - GetSystemMetrics(77)) * 65535 / (GetSystemMetrics(79) - 1)),
                        Flags = 0x8000 | 0x4000 | 0x2000 | 1,
                    }
                }
            });
        }
        Move(points[0]);
        Check(token);
        Send(new Input { Data = new InputData { Mouse = new MouseInput { Flags = 2 } } });
        try
        {
            Thread.Sleep(50);
            foreach (var point in points.Skip(1))
            {
                Move(point);
                Thread.Sleep(50);
            }
        }
        finally
        {
            Send(new Input { Data = new InputData { Mouse = new MouseInput { Flags = 4 } } });
        }
    }

    private static void Send(Input input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Dispose()
    {
        foreach (var key in _pressed)
        {
            Send(new Input
            {
                Type = 1,
                Data = new InputData
                {
                    Keyboard = new KeyboardInput
                    {
                        VirtualKey = key,
                        Flags = 2
                    }
                }
            });
        }

        PostThreadMessage(_threadId, 0x12, nint.Zero, nint.Zero);
        _thread.Join(1000);
        _stop.Dispose();
        if (_previousDpi != nint.Zero)
        {
            SetThreadDpiAwarenessContext(_previousDpi);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputData Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)]
        public MouseInput Mouse;
        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nuint Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MessageInfo
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint window, int id);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MessageInfo message, nint window, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint thread, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);
}
