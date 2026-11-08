using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace WindowsAxon.ScriptHost;

internal sealed partial record HostRequest(string Workspace,
                                           string Script,
                                           string Language,
                                           string Hash,
                                           string Output,
                                           string Cancel,
                                           string UserData,
                                           string? Python)
{
    private static readonly string[] Required =
    [
        "--workspace",
        "--script",
        "--language",
        "--hash",
        "--output",
        "--cancel",
        "--user-data"
    ];

    // Validation steps 1-3. A failure here means the host has no trusted path for an output or status file.
    public static HostRequest Parse(IReadOnlyList<string> args)
    {
        if (args.Count % 2 != 0)
        {
            throw new ArgumentException("Arguments must be name and value pairs.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i += 2)
        {
            var name = args[i];
            if (!Required.Contains(name) && name != "--python")
            {
                throw new ArgumentException($"Unknown argument {name}.");
            }

            if (!values.TryAdd(name, args[i + 1]))
            {
                throw new ArgumentException($"Duplicate argument {name}.");
            }
        }

        foreach (var name in Required)
        {
            if (!values.ContainsKey(name))
            {
                throw new ArgumentException($"Missing argument {name}.");
            }
        }

        var language = values["--language"];
        if (language is not ("powershell" or "python"))
        {
            throw new ArgumentException("--language must be powershell or python.");
        }

        var python = values.GetValueOrDefault("--python");
        if ((language == "python") != (python is not null))
        {
            throw new ArgumentException("--python is required for python and not allowed for powershell.");
        }

        var workspace = Full(values["--workspace"]);
        var script = Full(values["--script"]);
        var output = Full(values["--output"]);
        var cancel = Full(values["--cancel"]);
        if (!IsInside(script, workspace) || !IsInside(output, workspace) || !IsInside(cancel, workspace))
        {
            throw new ArgumentException("--script, --output, and --cancel must be inside --workspace.");
        }

        var userData = Full(values["--user-data"]);
        if (!UserDataPattern().IsMatch(userData))
        {
            throw new ArgumentException("--user-data must be a user's AppData\\Local\\WindowsAxon folder.");
        }

        if (!IsInside(workspace, Path.Combine(userData, "work")))
        {
            throw new ArgumentException("--workspace must be inside the WindowsAxon work folder.");
        }

        return new(workspace,
                   script,
                   language,
                   values["--hash"],
                   output,
                   cancel,
                   userData,
                   python is null ? null : Full(python));
    }

    // Validation steps 4-6. Returns the hashed bytes so the interpreter receives exactly what was checked.
    public (byte[] Code, string Interpreter) Verify(string systemDirectory)
    {
        var code = File.ReadAllBytes(Script);
        if (Convert.ToHexString(SHA256.HashData(code)) != Hash)
        {
            throw new InvalidDataException("The script does not match the approved hash.");
        }

        if (Language == "powershell")
        {
            return (code, Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"));
        }

        var expected = Path.Combine(UserData, "python", ".venv", "Scripts", "python.exe");
        if (!string.Equals(Python, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("--python is not WindowsAxon's Python environment.");
        }

        return (code, expected);
    }

    private static string Full(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"Path must be fully qualified: {path}");
        }

        return Path.GetFullPath(path);
    }

    private static bool IsInside(string path, string folder) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[A-Za-z]:\\Users\\[^\\]+\\AppData\\Local\\WindowsAxon$", RegexOptions.IgnoreCase)]
    private static partial Regex UserDataPattern();
}
