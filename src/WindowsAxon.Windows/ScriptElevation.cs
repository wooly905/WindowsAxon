using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;

namespace WindowsAxon.Windows;

// Seam for elevated script_run so tests never start the administrator host.
internal sealed record ScriptElevation(Func<bool> IsElevated,
                                       Func<string> ResolveHost,
                                       Func<ProcessStartInfo, Process?> Launch)
{
    public const string HostFileName = "WindowsAxon.ScriptHost.exe";

    public static ScriptElevation Default(string dataFolder) =>
        new(() => Environment.IsPrivilegedProcess, () => ResolveHostPath(dataFolder), Process.Start);

    // dotnet run places the host beside the app; the single-file release embeds it instead.
    private static string ResolveHostPath(string dataFolder)
    {
        var beside = Path.Combine(AppContext.BaseDirectory, HostFileName);
        if (File.Exists(beside))
        {
            return beside;
        }

        using var stream = Assembly.GetEntryAssembly()?.GetManifestResourceStream(HostFileName)
            ?? throw new FileNotFoundException("WindowsAxon Script Host is missing from this build.", HostFileName);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var path = Path.Combine(dataFolder, "script-host", hash, HostFileName);
        if (File.Exists(path) && Hash(path) == hash)
        {
            return path;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
        if (Hash(path) != hash)
        {
            throw new InvalidDataException("The extracted WindowsAxon Script Host does not match the embedded copy.");
        }

        return path;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
