using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;

namespace WindowsAxon.Windows;

internal static class ScriptHostLaunch
{
    internal static void Start(ProcessStartInfo start) => Process.Start(start)?.Dispose();

    internal static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!GetTokenInformation(identity.AccessToken.DangerousGetHandle(), 20, out var elevation,
                                 sizeof(int), out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return elevation != 0;
    }

    internal static string Resolve()
    {
        const string name = "WindowsAxon.ScriptHost.exe";
        var beside = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(beside))
        {
            return beside;
        }
        using var stream = Assembly.GetEntryAssembly()?.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("WindowsAxon Script Host is missing. Reinstall WindowsAxon.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Extract(buffer.ToArray(), Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsAxon", "script-host"));
    }

    internal static string Extract(byte[] bytes, string root)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var folder = Path.Combine(root, hash);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "WindowsAxon.ScriptHost.exe");
        if (!File.Exists(path) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != hash)
        {
            var temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(temporary, bytes);
            if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(temporary))) != hash)
            {
                throw new InvalidOperationException("WindowsAxon Script Host failed SHA-256 verification.");
            }
            File.Move(temporary, path, overwrite: true);
        }
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != hash)
        {
            throw new InvalidOperationException("WindowsAxon Script Host failed SHA-256 verification.");
        }
        return path;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(nint token, int informationClass, out int information,
                                                   int length, out int returnLength);
}
