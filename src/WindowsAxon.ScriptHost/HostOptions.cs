using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace WindowsAxon.ScriptHost;

internal sealed record HostOptions(string Workspace, string Script, string Language, string Hash,
                                   string Output, string Cancel, string? Python)
{
    internal static HostOptions Parse(string[] args, string workRoot)
    {
        string[] required = ["--workspace", "--script", "--language", "--hash", "--output", "--cancel"];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 == args.Length || (!required.Contains(args[i]) && args[i] != "--python")
                || string.IsNullOrWhiteSpace(args[i + 1]) || !values.TryAdd(args[i], args[i + 1]))
            {
                throw new ArgumentException("Unknown, missing, or extra host argument.");
            }
        }
        if (required.Any(key => !values.ContainsKey(key)))
        {
            throw new ArgumentException("Missing required host argument.");
        }
        var language = values["--language"];
        if (language is not ("powershell" or "python"))
        {
            throw new ArgumentException("language must be powershell or python.");
        }
        var workspace = Path.GetFullPath(values["--workspace"]);
        var script = Path.GetFullPath(values["--script"]);
        var output = Path.GetFullPath(values["--output"]);
        var cancel = Path.GetFullPath(values["--cancel"]);
        if (!Inside(script, workspace) || !Inside(output, workspace) || !Inside(cancel, workspace))
        {
            throw new ArgumentException("Script, output, and cancel paths must be inside the workspace.");
        }
        if (!Inside(workspace, Path.GetFullPath(workRoot)))
        {
            throw new ArgumentException("Workspace must be under WindowsAxon's work directory.");
        }
        return new(workspace, script, language, values["--hash"], output, cancel,
                   values.GetValueOrDefault("--python"));
    }

    private static bool Inside(string path, string directory) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase);

    internal (ProcessStartInfo Start, string Input) Validate()
    {
        var bytes = File.ReadAllBytes(Script);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != Hash)
        {
            throw new ArgumentException("The approved script was modified.");
        }
        var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        string[] arguments = ["-NoProfile", "-NonInteractive", "-Command", "-"];
        var input = "[Console]::OutputEncoding=[Text.Encoding]::UTF8\n$ErrorActionPreference='Stop'\ntry { & ([scriptblock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + Convert.ToBase64String(bytes) + "')))) } catch { [Console]::Error.WriteLine($_); exit 1 }\n\n";
        if (Language == "python")
        {
            var expected = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WindowsAxon", "python", ".venv", "Scripts", "python.exe"));
            if (Python is null || !Path.GetFullPath(Python).Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Python must be WindowsAxon's isolated interpreter.");
            }
            executable = expected;
            arguments = ["-I", "-X", "utf8", "-"];
            input = Encoding.UTF8.GetString(bytes);
        }
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Workspace,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        return (start, input);
    }
}
