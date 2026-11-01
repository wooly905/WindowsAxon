using System.Text.Json;
using WindowsAxon.Agent.Tools;
using WindowsAxon.Agent.Undo;
using Microsoft.Win32;

namespace WindowsAxon.Windows;

public sealed class RegistryTools(UndoJournal journal, string session) : IUndoRestorer
{
    public string Kind => "registry";

    private sealed record ValueSnapshot(string Type, JsonElement Value);

    [Tool("registry_read",
          "registry",
          "Read one registry value from HKCU or HKLM. Returns its type and unexpanded value.")]
    public string? Read(string path, string name = "") => ReadCurrent(Target(path, name));

    [Tool("registry_set_value",
          "registry",
          "Set one HKCU value. type: REG_SZ, REG_EXPAND_SZ, REG_DWORD, REG_QWORD, REG_MULTI_SZ. Policy keys are blocked. Undo supported.",
          RiskLevel.Danger)]
    public string Set(string path, string name, string type, JsonElement value)
    {
        var target = Target(path, name);
        if (!new[] { "REG_SZ", "REG_EXPAND_SZ", "REG_DWORD", "REG_QWORD", "REG_MULTI_SZ" }.Contains(type))
        {
            throw new ArgumentException("Unsupported registry value type.");
        }

        ValidatePath(path, write: true);
        var previous = ReadCurrent(target);
        Restore(target, JsonSerializer.Serialize(new ValueSnapshot(type, value)));
        journal.Record(session, Kind, "registry_set_value", target, previous, ReadCurrent(target));
        return "Registry value updated. Undo is available.";
    }

    [Tool("registry_delete_value",
          "registry",
          "Delete one HKCU registry value. Never deletes a key tree. Policy keys are blocked. Undo supported.",
          RiskLevel.Danger)]
    public string Delete(string path, string name)
    {
        var target = Target(path, name);
        ValidatePath(path, write: true);
        var previous = ReadCurrent(target) ?? throw new ArgumentException("The value does not exist.");
        Restore(target, null);
        journal.Record(session, Kind, "registry_delete_value", target, previous, null);
        return "Registry value deleted. Undo is available.";
    }

    public static void ValidatePath(string path, bool write)
    {
        ArgumentNullException.ThrowIfNull(path);

        var parts = path.Replace('/', '\\').Split('\\', 2);
        if (parts.Length != 2
            || parts[1].Split('\\').Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".."))
        {
            throw new ArgumentException("Use HKCU\\key or HKLM\\key without empty or relative components.");
        }

        if (parts[0] is not ("HKCU" or "HKLM") || write && parts[0] != "HKCU")
        {
            throw new UnauthorizedAccessException("Writes are allowed only under HKCU.");
        }

        if (write
            && new[] { "Software\\Policies", "Software\\Microsoft\\Windows\\CurrentVersion\\Policies" }
            .Any(blocked => parts[1].Equals(blocked, StringComparison.OrdinalIgnoreCase)
                || parts[1].StartsWith(blocked + "\\",
                                       StringComparison.OrdinalIgnoreCase)))
        {
            throw new UnauthorizedAccessException("Policy registry keys cannot be changed.");
        }
    }

    private static string Target(string path, string name) => JsonSerializer.Serialize(new[] {
        path.Replace('/', '\\'),
        name
    });

    private static (string Path, string Name) Parse(string target)
    {
        var parts = JsonSerializer.Deserialize<string[]>(target)!;
        return (parts[0], parts[1]);
    }

    private static RegistryKey Root(string path) => path.StartsWith("HKCU\\", StringComparison.Ordinal) ? Registry.CurrentUser : Registry.LocalMachine;

    public string? ReadCurrent(string target)
    {
        var (path, name) = Parse(target);
        ValidatePath(path, false);
        using var key = Root(path).OpenSubKey(path[5..]);
        if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var type = key.GetValueKind(name) switch
        {
            RegistryValueKind.String => "REG_SZ",
            RegistryValueKind.ExpandString => "REG_EXPAND_SZ",
            RegistryValueKind.DWord => "REG_DWORD",
            RegistryValueKind.QWord => "REG_QWORD",
            RegistryValueKind.MultiString => "REG_MULTI_SZ",
            RegistryValueKind.Binary => "REG_BINARY",
            RegistryValueKind.None => "REG_NONE",
            _ => throw new ArgumentException("Unsupported registry value type."),
        };
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return JsonSerializer.Serialize(new ValueSnapshot(type, JsonSerializer.SerializeToElement(value)));
    }

    public void Restore(string target, string? previousValue)
    {
        var (path, name) = Parse(target);
        ValidatePath(path, true);
        if (previousValue is null)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(path[5..], true);
            existing?.DeleteValue(name, false);
            return;
        }
        var snapshot = JsonSerializer.Deserialize<ValueSnapshot>(previousValue)!;
        var (value, kind) = snapshot.Type switch
        {
            "REG_SZ" => ((object)snapshot.Value.GetString()!, RegistryValueKind.String),
            "REG_EXPAND_SZ" => ((object)snapshot.Value.GetString()!, RegistryValueKind.ExpandString),
            "REG_DWORD" => ((object)(snapshot.Value.TryGetInt32(out var dword) ? dword : unchecked((int)snapshot.Value.GetUInt32())), RegistryValueKind.DWord),
            "REG_QWORD" => ((object)(snapshot.Value.TryGetInt64(out var qword) ? qword : unchecked((long)snapshot.Value.GetUInt64())), RegistryValueKind.QWord),
            "REG_MULTI_SZ" => ((object)snapshot.Value.EnumerateArray()
            .Select(v => v.GetString()!)
            .ToArray(), RegistryValueKind.MultiString),
            "REG_BINARY" => ((object)snapshot.Value.GetBytesFromBase64(), RegistryValueKind.Binary),
            "REG_NONE" => ((object)snapshot.Value.GetBytesFromBase64(), RegistryValueKind.None),
            _ => throw new ArgumentException("Unsupported registry value type."),
        };
        using var key = Registry.CurrentUser.CreateSubKey(path[5..], true);
        key.SetValue(name, value, kind);
    }
}
