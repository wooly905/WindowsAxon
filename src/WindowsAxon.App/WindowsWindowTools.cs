using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Tools;
using Microsoft.Extensions.AI;
using WindowsAxon.Windows;

namespace WindowsAxon.App;

internal sealed class WindowsWindowTools : IApprovalRule
{
    private readonly Func<IReadOnlyList<DesktopWindow>> _readWindows;
    private readonly Action<DesktopWindow> _requestClose;
    private readonly Func<DesktopWindow, bool> _isOpen;
    private readonly TimeSpan _closeTimeout;
    private readonly Dictionary<string, DesktopWindow> _listed = [];

    public WindowsWindowTools() : this(ReadWindows, RequestClose, IsOpen, TimeSpan.FromSeconds(10))
    {
    }

    internal WindowsWindowTools(Func<IReadOnlyList<DesktopWindow>> readWindows,
                                Action<DesktopWindow> requestClose,
                                Func<DesktopWindow, bool> isOpen,
                                TimeSpan closeTimeout)
    {
        _readWindows = readWindows;
        _requestClose = requestClose;
        _isOpen = isOpen;
        _closeTimeout = closeTimeout;
    }

    [Tool("ui_list_windows",
          "desktop_ui",
          "Find visible top-level windows by application process name or title. Use name to filter, for example 'paint'. Returns up to 20 windows with IDs for ui_close_window. If several match, ask the user which window to close.",
          alwaysInclude: true)]
    internal IReadOnlyList<WindowInfo> List(string name = "")
    {
        string filter = name.Trim();
        _listed.Clear();
        List<WindowInfo> result = new List<WindowInfo>();

        foreach (DesktopWindow? window in _readWindows().Where(window => window.ProcessId != Environment.ProcessId
            && (window.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || window.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase))).Take(20))
        {
            string id = Guid.NewGuid().ToString("N");
            _listed.Add(id, window);
            result.Add(new WindowInfo(id, window.ProcessName, window.Title));
        }

        return result;
    }

    [Tool("ui_close_window",
          "desktop_ui",
          "Request a normal close of one window using its ID from ui_list_windows. Requires approval. Preserves the application's save prompts. Reports success only after the window closes; never dismiss a save prompt automatically.",
          RiskLevel.Change,
          policyDriven: true,
          alwaysInclude: true)]
    internal async Task<string> CloseAsync(string windowId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_listed.TryGetValue(windowId, out DesktopWindow? window))
        {
            throw new ArgumentException("The window ID is not in the last window list. Use ui_list_windows first.");
        }
        if (!_isOpen(window))
        {
            throw new InvalidOperationException("The listed window no longer exists. Use ui_list_windows again.");
        }

        UiSafety.CheckProcess(window.ProcessName);
        _requestClose(window);
        Stopwatch wait = Stopwatch.StartNew();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_isOpen(window))
            {
                _listed.Remove(windowId);
                return $"Closed window: {window.Title} ({window.ProcessName}). The window's closure was verified.";
            }

            if (wait.Elapsed >= _closeTimeout)
            {
                break;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new InvalidOperationException("Close requested, but the window is still open. It may be waiting for a save prompt or may have refused to close. Ask the user to check the application. Do not report that it is closed.");
    }

    public bool AppliesTo(string toolName) => toolName == "ui_close_window";

    internal nint ResolveHandle(string windowId)
    {
        if (!_listed.TryGetValue(windowId, out var window) || !_isOpen(window))
        {
            throw new ArgumentException("Use ui_list_windows to select an existing window.");
        }

        UiSafety.CheckProcess(window.ProcessName);
        return window.Handle;
    }

    public ApprovalDecision Decide(FunctionCallContent call)
    {
        string? id = call.Arguments is { } arguments
            && arguments.TryGetValue("windowId", out var value) ? value?.ToString() : null;
        string target = id is not null && _listed.TryGetValue(id, out var window)
            ? $"Close '{window.Title}' ({window.ProcessName})?" : "Close the selected window?";

        return new ApprovalDecision(ApprovalOutcome.Ask,
                                    false,
                                    $"{target} Unsaved changes may require a response in the application.");
    }

    private static IReadOnlyList<DesktopWindow> ReadWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Window tools require Windows.");
        }

        List<DesktopWindow> windows = [];

        if (!EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || GetWindow(handle, 4) != nint.Zero)
            {
                return true;
            }

            DesktopWindow? window = ReadWindow(handle);

            if (window is not null && !string.IsNullOrWhiteSpace(window.Title))
            {
                windows.Add(window);
            }

            return true;
        },
                         nint.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return windows;
    }

    private static DesktopWindow? ReadWindow(nint handle)
    {
        if (!IsWindow(handle) || GetWindowThreadProcessId(handle, out var processId) == 0
            || processId == Environment.ProcessId)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            var title = new StringBuilder(GetWindowTextLength(handle) + 1);
            GetWindowText(handle, title, title.Capacity);

            return new DesktopWindow(handle,
                                     process.Id,
                                     process.StartTime.ToUniversalTime().Ticks,
                                     process.ProcessName,
                                     title.ToString());
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private static bool IsOpen(DesktopWindow window)
    {
        if (!IsWindow(window.Handle))
        {
            return false;
        }

        if (GetWindowThreadProcessId(window.Handle, out var processId) == 0)
        {
            throw new InvalidOperationException("Could not verify the selected window's owner.");
        }
        if (processId != window.ProcessId)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(window.ProcessId);
            return process.StartTime.ToUniversalTime().Ticks == window.ProcessStartTime;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void RequestClose(DesktopWindow window)
    {
        if (!IsOpen(window))
        {
            throw new InvalidOperationException("The selected window no longer exists.");
        }

        if (!PostMessage(window.Handle, 0x0010, nint.Zero, nint.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private delegate bool EnumWindowsCallback(nint handle, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint handle);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint handle, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint handle, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint handle, uint message, nint wParam, nint lParam);
}
