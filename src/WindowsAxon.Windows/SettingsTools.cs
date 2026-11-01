using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WindowsAxon.Agent.Tools;
using WindowsAxon.Agent.Undo;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace WindowsAxon.Windows;

public sealed class SettingsTools(UndoJournal journal, string session) : IUndoRestorer
{
    public string Kind => "settings";

    private const string PersonalizeKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize";
    private static readonly HashSet<string> SettingsPages = [
"display",
        "sound",
        "network-wifi",
        "network-ethernet",
        "network-status",
        "bluetooth",
        "printers",
        "windowsupdate",
        "appsfeatures",
        "startupapps",
        "personalization",
        "personalization-background",
        "personalization-colors",
        "powersleep",
        "battery",
        "about",
        "storage"
];
    private static readonly HashSet<string> ControlPanels = [
"Microsoft.ProgramsAndFeatures",
        "Microsoft.NetworkAndSharingCenter",
        "Microsoft.DevicesAndPrinters",
        "Microsoft.PowerOptions",
        "Microsoft.CredentialManager",
        "Microsoft.Sound",
        "Microsoft.System",
        "Microsoft.Mouse",
        "Microsoft.Keyboard"
];
    private static readonly HashSet<string> AdminTools = [
"devmgmt.msc",
        "eventvwr.msc",
        "services.msc",
        "taskschd.msc",
        "compmgmt.msc",
        "diskmgmt.msc",
        "printmanagement.msc",
        "perfmon.exe",
        "resmon.exe",
        "msinfo32.exe"
];

    [Tool("settings_open",
          "settings",
          "Open a Windows Settings page from a fixed allowlist: display, sound, network-wifi, network-ethernet, network-status, bluetooth, printers, windowsupdate, appsfeatures, startupapps, personalization, personalization-background, personalization-colors, powersleep, battery, about, storage.")]
    public string OpenSettings(string page)
    {
        if (!SettingsPages.Contains(page))
        {
            throw new ArgumentException("Unsupported Settings page.");
        }

        return Shell("ms-settings:" + page);
    }

    [Tool("control_panel_open",
          "settings",
          "Open an allowlisted canonical Control Panel item: Microsoft.ProgramsAndFeatures, Microsoft.NetworkAndSharingCenter, Microsoft.DevicesAndPrinters, Microsoft.PowerOptions, Microsoft.CredentialManager, Microsoft.Sound, Microsoft.System, Microsoft.Mouse, Microsoft.Keyboard.")]
    public string OpenControlPanel(string name)
    {
        if (!ControlPanels.Contains(name))
        {
            throw new ArgumentException("Unsupported Control Panel item.");
        }

        return Shell("control.exe", ["/name", name]);
    }

    [Tool("admin_tool_open",
          "settings",
          "Launch a Windows management console: Disk Management (diskmgmt.msc), Device Manager (devmgmt.msc), eventvwr.msc, services.msc, taskschd.msc, compmgmt.msc, printmanagement.msc, perfmon.exe, resmon.exe, or msinfo32.exe. Windows handles any UAC prompt. Reports opened only after verifying a visible window; otherwise reports an unverified launch request.")]
    public string OpenAdmin(string name) => OpenAdmin(name, LaunchAdminAndVerify);

    internal static string OpenAdmin(string name, Func<ProcessStartInfo, bool> launchAndVerify)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(launchAndVerify);
        if (!AdminTools.Contains(name))
        {
            throw new ArgumentException("Unsupported management console.", nameof(name));
        }

        var path = Path.Combine(Environment.SystemDirectory, name);
        var isSnapIn = name.EndsWith(".msc", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(isSnapIn ? Path.Combine(Environment.SystemDirectory, "mmc.exe") : path)
        {
            UseShellExecute = true
        };
        if (isSnapIn)
        {
            start.ArgumentList.Add(path);
        }

        return launchAndVerify(start)
            ? $"Opened {name}. A visible application window was verified."
            : $"Launch requested for {name}, but a visible application window was not verified. Windows may be waiting for a UAC prompt. Ask the user to check Windows; do not report that it is open.";
    }

    private static bool LaunchAdminAndVerify(ProcessStartInfo start)
    {
        using var process = Process.Start(start);
        if (process is null)
        {
            return false;
        }

        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            process.Refresh();
            if (process.HasExited)
            {
                return false;
            }
            if (process.MainWindowHandle != nint.Zero && IsWindowVisible(process.MainWindowHandle))
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }

    private static string Shell(string file, string[]? arguments = null)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = true };
        foreach (var argument in arguments ?? [])
        {
            start.ArgumentList.Add(argument);
        }

        Process.Start(start)?.Dispose();
        return "Windows accepted the open request. The resulting window has not been verified.";
    }

    private string Change(string target, string tool, string value)
    {
        var previous = ReadCurrent(target);
        if (target == "theme-file" && (string.IsNullOrWhiteSpace(previous) || !File.Exists(previous)))
        {
            throw new InvalidOperationException("The current theme cannot be restored; no theme change was made.");
        }

        try
        {
            Restore(target, value);
        }
        catch
        {
            var partial = ReadCurrent(target);
            if (partial != previous)
            {
                journal.Record(session, Kind, tool, target, previous, partial);
            }

            throw;
        }
        var actual = ReadCurrent(target);
        journal.Record(session, Kind, tool, target, previous, actual);
        return $"Setting updated and read back: {actual}. Undo is available.";
    }

    [Tool("wallpaper_get",
          "personalize",
          "Read the current wallpaper path and style values.")]
    public string? Wallpaper() => ReadCurrent("wallpaper");

    [Tool("wallpaper_set",
          "personalize",
          "Set wallpaper to an existing jpg, jpeg, png, or bmp file. style: fill, fit, stretch, center, tile. Undo supported.",
          RiskLevel.Change)]
    public string WallpaperSet(string path, string style = "fill")
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path) || !new[] { ".jpg", ".jpeg", ".png", ".bmp" }.Contains(Path.GetExtension(path),
                                                                                      StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Choose an existing image file.");
        }

        var values = style switch
        {
            "fill" => ("10", "0"),
            "fit" => ("6", "0"),
            "stretch" => ("2", "0"),
            "center" => ("0", "0"),
            "tile" => ("0", "1"),
            _ => throw new ArgumentException("Unsupported wallpaper style.")
        };
        return Change("wallpaper",
                      "wallpaper_set",
                      JsonSerializer.Serialize(new[] { path, values.Item1, values.Item2 }));
    }

    [Tool("theme_get",
          "personalize",
          "Read apps/Windows light-mode values and the current theme file.")]
    public object Theme() => new { Mode = ReadCurrent("theme-mode"), File = ReadCurrent("theme-file") };

    [Tool("theme_set_mode",
          "personalize",
          "Set light or dark mode for apps, windows, or both. Undo supported.",
          RiskLevel.Change)]
    public string ThemeMode(string mode, string target = "both")
    {
        if (mode is not ("light" or "dark") || target is not ("apps" or "windows" or "both"))
        {
            throw new ArgumentException("Invalid mode or target.");
        }

        var current = JsonSerializer.Deserialize<int[]>(ReadCurrent("theme-mode")!)!;
        if (target is "apps" or "both")
        {
            current[0] = mode == "light" ? 1 : 0;
        }

        if (target is "windows" or "both")
        {
            current[1] = mode == "light" ? 1 : 0;
        }

        return Change("theme-mode", "theme_set_mode", JsonSerializer.Serialize(current));
    }

    [Tool("theme_list",
          "personalize",
          "List installed Windows and current-user theme files.")]
    public object Themes() => ThemeFiles().ToArray();

    private static IEnumerable<string> ThemeFiles() => new[] {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                     "Resources",
                     "Themes"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "Microsoft",
                     "Windows",
                     "Themes")
    }
        .Where(Directory.Exists)
        .SelectMany(folder => Directory.EnumerateFiles(folder,
                                                       "*.theme",
                                                       SearchOption.AllDirectories));

    [Tool("theme_apply",
          "personalize",
          "Apply a theme file returned by theme_list. Undo supported.",
          RiskLevel.Change)]
    public string ThemeApply(string path)
    {
        path = Path.GetFullPath(path);
        if (!ThemeFiles().Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Use a path from theme_list.");
        }

        return Change("theme-file", "theme_apply", path);
    }

    [Tool("volume_get",
          "devices",
          "Read default audio output device, level, and mute state.")]
    public object Volume()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return new
        {
            device.FriendlyName,
            Level = device.AudioEndpointVolume.MasterVolumeLevelScalar * 100,
            Mute = device.AudioEndpointVolume.Mute
        };
    }

    [Tool("volume_set",
          "devices",
          "Set volume 0-100, optionally mute or unmute. Undo supported.",
          RiskLevel.Change)]
    public string VolumeSet(int level, bool? mute = null)
    {
        if (level is < 0 or > 100)
        {
            throw new ArgumentException("Volume must be 0-100.");
        }

        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return Change("volume",
                      "volume_set",
                      JsonSerializer.Serialize(new[] {
                          level.ToString(System.Globalization.CultureInfo.InvariantCulture),
                          (mute ?? device.AudioEndpointVolume.Mute).ToString()
                      }));
    }

    [Tool("brightness_set",
          "devices",
          "Set built-in display brightness 0-100. External monitors are not supported. Undo supported.",
          RiskLevel.Change)]
    public string Brightness(int level)
    {
        if (level is < 0 or > 100)
        {
            throw new ArgumentException("Brightness must be 0-100.");
        }

        return Change("brightness", "brightness_set", level.ToString());
    }

    [Tool("power_plan_list",
          "devices",
          "Read available Windows power plan GUIDs and names.")]
    public Task<string> PowerPlans(CancellationToken cancellationToken) => WindowsCommand.RunAsync("powercfg.exe",
                                                                                                   ["/list"],
                                                                                                   cancellationToken);

    [Tool("power_plan_set",
          "devices",
          "Activate an existing Windows power plan GUID. Undo supported.",
          RiskLevel.Change)]
    public string PowerPlan(string id)
    {
        return Change("power-plan", "power_plan_set", Guid.Parse(id).ToString());
    }

    [Tool("printer_set_default",
          "printers",
          "Set an installed printer as the Windows default. Undo supported.",
          RiskLevel.Change)]
    public string DefaultPrinter(string name)
    {
        if (!SystemTools.Query("SELECT Name FROM Win32_Printer")
            .Cast<Dictionary<string, object>>()
            .Any(printer => printer["Name"].ToString() == name))
        {
            throw new ArgumentException("Printer is not installed.");
        }

        return Change("default-printer", "printer_set_default", name);
    }

    public string? ReadCurrent(string target)
    {
        switch (target)
        {
            case "wallpaper":
                using (var key = Registry.CurrentUser.OpenSubKey("Control Panel\\Desktop"))
                {
                    return JsonSerializer.Serialize(new[] {
                        key?.GetValue("Wallpaper")?.ToString() ?? "",
                        key?.GetValue("WallpaperStyle")?.ToString() ?? "0",
                        key?.GetValue("TileWallpaper")?.ToString() ?? "0"
                    });
                }

            case "theme-mode":
                using (var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey))
                {
                    return JsonSerializer.Serialize(new[] {
                        Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 1)),
                        Convert.ToInt32(key?.GetValue("SystemUsesLightTheme",
                                                      1))
                    });
                }

            case "theme-file":
                using (var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes"))
                {
                    return key?.GetValue("CurrentTheme")?.ToString();
                }

            case "volume":
                using (var enumerator = new MMDeviceEnumerator())
                {
                    using (var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                    {
                        return JsonSerializer.Serialize(new[] {
                            (device.AudioEndpointVolume.MasterVolumeLevelScalar * 100).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            device.AudioEndpointVolume.Mute.ToString()
                        });
                    }
                }

            case "brightness":
                return SystemTools.Query("SELECT CurrentBrightness FROM WmiMonitorBrightness", "root\\wmi")
                    .Cast<Dictionary<string, object>>()
                    .FirstOrDefault()?["CurrentBrightness"].ToString() ?? throw new NotSupportedException("No built-in brightness control is available.");
            case "power-plan":
                var text = WindowsCommand.RunAsync("powercfg.exe", ["/getactivescheme"], CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                return System.Text.RegularExpressions.Regex.Match(text,
                                                                  @"[a-fA-F0-9]{8}-(?:[a-fA-F0-9]{4}-){3}[a-fA-F0-9]{12}")
                    .Value;
            case "default-printer":
                uint length = 0;
                GetDefaultPrinter(null, ref length);
                if (length == 0)
                {
                    return null;
                }

                var buffer = new StringBuilder((int)length);
                if (!GetDefaultPrinter(buffer, ref length))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                return buffer.ToString();
            default:
                throw new ArgumentException("Unknown setting.");
        }
    }

    public void Restore(string target, string? previousValue)
    {
        switch (target)
        {
            case "wallpaper":
                var wallpaper = JsonSerializer.Deserialize<string[]>(previousValue!)!;
                using (var key = Registry.CurrentUser.CreateSubKey("Control Panel\\Desktop"))
                {
                    key.SetValue("WallpaperStyle", wallpaper[1]);
                    key.SetValue("TileWallpaper", wallpaper[2]);
                }
                if (!SystemParametersInfo(20, 0, wallpaper[0], 3))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                break;
            case "theme-mode":
                var mode = JsonSerializer.Deserialize<int[]>(previousValue!)!;
                using (var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey))
                {
                    key.SetValue("AppsUseLightTheme", mode[0], RegistryValueKind.DWord);
                    key.SetValue("SystemUsesLightTheme", mode[1], RegistryValueKind.DWord);
                }
                SendMessageTimeout(new nint(0xffff), 0x1a, nint.Zero, "ImmersiveColorSet", 2, 1000, out _);
                break;
            case "theme-file":
                if (string.IsNullOrWhiteSpace(previousValue) || !File.Exists(previousValue))
                {
                    throw new ArgumentException("Theme file not found.");
                }

                Shell(previousValue);
                for (var attempt = 0; attempt < 50 && !Matches(target, ReadCurrent(target), previousValue); attempt++)
                {
                    Thread.Sleep(100);
                }

                break;
            case "volume":
                var volume = JsonSerializer.Deserialize<string[]>(previousValue!)!;
                using (var enumerator = new MMDeviceEnumerator())
                {
                    using (var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                    {
                        device.AudioEndpointVolume.MasterVolumeLevelScalar = float.Parse(volume[0],
                                                                                         System.Globalization.CultureInfo.InvariantCulture) / 100;
                        device.AudioEndpointVolume.Mute = bool.Parse(volume[1]);
                    }
                }

                break;
            case "brightness":
                using (var search = new ManagementObjectSearcher("root\\wmi",
                                                                 "SELECT * FROM WmiMonitorBrightnessMethods"))
                {
                    using (var monitors = search.Get())
                    {
                        if (monitors.Count == 0)
                        {
                            throw new NotSupportedException("No built-in brightness control is available.");
                        }

                        foreach (ManagementObject monitor in monitors)
                        {
                            using (monitor)
                            {
                                monitor.InvokeMethod("WmiSetBrightness", [1u, byte.Parse(previousValue!)]);
                            }
                        }
                    }
                }

                break;
            case "power-plan":
                WindowsCommand.RunAsync("powercfg.exe",
                                        ["/setactive", Guid.Parse(previousValue!).ToString()],
                                        CancellationToken.None).GetAwaiter().GetResult();
                break;
            case "default-printer":
                if (!SetDefaultPrinter(previousValue))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                break;
            default:
                throw new ArgumentException("Unknown setting.");
        }
        if (!Matches(target, ReadCurrent(target), previousValue))
        {
            throw new InvalidOperationException("The requested setting was not verified. Check Windows before continuing.");
        }
    }

    private static bool Matches(string target, string? actual, string? expected)
    {
        if (target != "volume")
        {
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        var left = JsonSerializer.Deserialize<string[]>(actual!)!;
        var right = JsonSerializer.Deserialize<string[]>(expected!)!;
        if (left[1] != right[1])
        {
            return false;
        }

        var actualLevel = float.Parse(left[0], System.Globalization.CultureInfo.InvariantCulture);
        var expectedLevel = float.Parse(right[0], System.Globalization.CultureInfo.InvariantCulture);
        return Math.Abs(actualLevel - expectedLevel) < 0.5;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, string value, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeout(nint window,
                                                  uint message,
                                                  nint wParam,
                                                  string lParam,
                                                  uint flags,
                                                  uint timeout,
                                                  out nint result);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultPrinter(string? name);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDefaultPrinter(StringBuilder? name, ref uint length);
}
