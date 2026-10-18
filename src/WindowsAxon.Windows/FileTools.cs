using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using WindowsAxon.Agent.Tools;
using WindowsAxon.Agent.Undo;

namespace WindowsAxon.Windows;

public sealed class FileTools(UndoJournal journal, string session) : IUndoRestorer
{
    public string Kind => "acl";

    [WindowsTool("fs_list_dir", "files")]
    [Description("List files and folders in a directory. sort: name, size, date. Maximum 200 entries.")]
    public object List(string path, string sort = "name", int limit = 50)
    {
        var entries = new DirectoryInfo(Path.GetFullPath(path)).EnumerateFileSystemInfos();
        entries = sort switch
        {
            "name" => entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase),
            "size" => entries.OrderByDescending(entry => entry is FileInfo file ? file.Length : 0),
            "date" => entries.OrderByDescending(entry => entry.LastWriteTimeUtc),
            _ => throw new ArgumentException("sort must be name, size, or date."),
        };
        return entries.Take(Math.Clamp(limit, 1, 200)).Select(entry => new
        {
            entry.FullName,
            Folder = entry is DirectoryInfo,
            Size = entry is FileInfo file ? file.Length : 0,
            entry.LastWriteTimeUtc
        })
            .ToArray();
    }

    [WindowsTool("fs_find_files", "files", timeoutSeconds: 60, artifact: true)]
    [Description("Find files under root by filename pattern, minimum size, and optional modifiedAfter ISO timestamp. Skips symbolic links and inaccessible folders. Maximum 200 results.")]
    public object Find(string root,
                       string pattern = "*",
                       long minimumSize = 0,
                       DateTimeOffset? modifiedAfter = null,
                       CancellationToken cancellationToken = default)
    {
        var folders = new Queue<string>();
        folders.Enqueue(Path.GetFullPath(root));
        var matches = new List<object>();
        while (folders.TryDequeue(out var folder) && matches.Count < 200)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    if (entry is DirectoryInfo)
                    {
                        folders.Enqueue(entry.FullName);
                    }
                    else if (entry is FileInfo file
                        && System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern,
                                                                                        file.Name,
                                                                                        ignoreCase: true)
                        && file.Length >= minimumSize
                        && (modifiedAfter is null
                        || file.LastWriteTimeUtc >= modifiedAfter.Value.UtcDateTime))
                    {
                        matches.Add(new
                        {
                            file.FullName,
                            file.Length,
                            file.LastWriteTimeUtc
                        });
                        if (matches.Count == 200)
                        {
                            break;
                        }
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // An inaccessible folder must not prevent searching the remaining folders.
            }
            catch (DirectoryNotFoundException)
            {
                // A folder can disappear while the directory tree is being enumerated.
            }
        }
        return matches;
    }

    [WindowsTool("fs_read_text", "files", policyDriven: true)]
    [Description("Read a text file, at most 20 KB. Credential files and binary files are refused. A remote model requires approval to receive the content.")]
    public string Read(string path)
    {
        path = SafeReadPath(path);
        using var file = File.OpenRead(path);
        var bytes = new byte[20 * 1024];
        var count = file.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        var data = bytes.AsSpan(0, count);
        var cut = file.Position < file.Length;
        var encoding = data.StartsWith(new byte[] { 255, 254 }) ? new UnicodeEncoding(false, false, true)
            : data.StartsWith(new byte[] {
                254,
                255
            }) ? (Encoding)new UnicodeEncoding(true, false, true) : new UTF8Encoding(false,
                                                                                     true);
        if (encoding is UnicodeEncoding)
        {
            data = data[2..];
        }
        else if (data.Contains((byte)0))
        {
            throw new ArgumentException("Only text files can be read.");
        }

        var chars = new char[encoding.GetMaxCharCount(data.Length)];
        encoding.GetDecoder().Convert(data, chars, flush: !cut, out _, out var written, out _);
        return new string(chars, 0, written).TrimStart('\uFEFF') + (cut ? "\n(cut at 20 KB)" : "");
    }

    public static string SafeReadPath(string path)
    {
        path = Path.GetFullPath(path);
        if (path[Path.GetPathRoot(path)!.Length..].Contains(':'))
        {
            throw new UnauthorizedAccessException("Alternate data streams cannot be read.");
        }

        var real = ResolvePath(path);
        var blocked = new[] {
            ".ssh",
            ".aws",
            ".azure",
            ".kube",
            ".gnupg",
            "Credentials",
            "Vault",
            "Protect",
            "passwords",
            "Login Data",
            "Cookies",
            ".env",
            ".git-credentials",
            "auth.json",
            "credentials.json"
        };
        if (real.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => blocked.Contains(part,
                                                                                                                 StringComparer.OrdinalIgnoreCase))
            || new[] { ".pem", ".pfx", ".p12", ".key", ".kdbx" }.Contains(Path.GetExtension(real),
                                                                          StringComparer.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Credential paths cannot be read.");
        }

        return real;
    }

    private static string ResolvePath(string path)
    {
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var part in path[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists)
            {
                current = info.ResolveLinkTarget(true)?.FullName ?? current;
            }
        }
        return current;
    }

    [WindowsTool("fs_copy", "files", RiskLevel.Change)]
    [Description("Copy one file. Refuses overwriting an existing destination.")]
    public string Copy(string source, string destination)
    {
        File.Copy(Path.GetFullPath(source), Path.GetFullPath(destination), false);
        return "Copied file.";
    }

    [WindowsTool("fs_move", "files", RiskLevel.Change)]
    [Description("Move one file or folder. Refuses overwriting an existing destination.")]
    public string Move(string source, string destination)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination, false);
        }

        return "Moved.";
    }

    [WindowsTool("fs_create_dir", "files", RiskLevel.Change)]
    [Description("Create a directory and missing parent directories.")]
    public string Create(string path)
    {
        Directory.CreateDirectory(Path.GetFullPath(path));
        return "Created directory.";
    }

    [WindowsTool("fs_delete", "files", RiskLevel.Danger)]
    [Description("Send one file or folder to the Windows Recycle Bin. Never permanently deletes. No automatic undo.")]
    public Task<string> Delete(string path)
    {
        return StaThread.Run(() =>
        {
            path = Path.GetFullPath(path);
            if (Path.GetPathRoot(path) == path)
            {
                throw new ArgumentException("A drive root cannot be deleted.");
            }

            RecycleBin.Send(path);
            return "Sent to Recycle Bin.";
        });
    }

    [WindowsTool("acl_get", "files")]
    [Description("Read a file or folder owner and access rules.")]
    public string AclGet(string path) => Security(path)
        .GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner);

    [WindowsTool("acl_set", "files", RiskLevel.Danger)]
    [Description("Grant or remove one explicit Allow rule. rights: Read, ReadAndExecute, Modify, FullControl. Blocks protected folders and other users' profiles. Preserves ownership and inheritance.")]
    public string AclSet(string path, string principal, string rights, bool inherit = false, bool remove = false)
    {
        path = WritableAclPath(path);
        if (!new[] { "Read", "ReadAndExecute", "Modify", "FullControl" }.Contains(rights))
        {
            throw new ArgumentException("Unsupported rights.");
        }

        var security = Security(path);
        var previous = ReadCurrent(path);
        var rule = new FileSystemAccessRule(new NTAccount(principal),
                                            Enum.Parse<FileSystemRights>(rights),
                                            inherit
            && Directory.Exists(path) ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
                                            PropagationFlags.None,
                                            AccessControlType.Allow);
        if (remove)
        {
            security.RemoveAccessRuleSpecific(rule);
        }
        else
        {
            security.AddAccessRule(rule);
        }

        SaveSecurity(path, security);
        journal.Record(session, Kind, "acl_set", path, previous, ReadCurrent(path));
        return "Access rule updated. Undo is available.";
    }

    public static string WritableAclPath(string path)
    {
        path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[] {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };
        var profiles = Directory.GetParent(user)?.FullName;
        if (Path.GetPathRoot(path)?.TrimEnd('\\') == path
            || roots.Where(root => root.Length > 0)
            .Any(root => Within(path,
                                root))
            || profiles is not null && Within(path, profiles) && !Within(path, user)
            || !ResolvePath(path).Equals(path, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Permissions on this path cannot be changed.");
        }

        return path;
    }

    private static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root.TrimEnd('\\') + "\\",
                           StringComparison.OrdinalIgnoreCase);

    private static FileSystemSecurity Security(string path) => Directory.Exists(path)
        ? new DirectoryInfo(path).GetAccessControl()
        : new FileInfo(path).GetAccessControl();

    private static void SaveSecurity(string path, FileSystemSecurity security)
    {
        if (security is DirectorySecurity directory)
        {
            new DirectoryInfo(path).SetAccessControl(directory);
        }
        else
        {
            new FileInfo(path).SetAccessControl((FileSecurity)security);
        }
    }

    public string? ReadCurrent(string target)
    {
        var descriptor = new RawSecurityDescriptor(Security(target).GetSecurityDescriptorBinaryForm(), 0);
        // Windows updates inheritance bookkeeping during SetAccessControl even when the access rules are unchanged.
        var flags = descriptor.ControlFlags & ~(ControlFlags.DiscretionaryAclAutoInherited | ControlFlags.DiscretionaryAclAutoInheritRequired);
        return new RawSecurityDescriptor(flags,
                                         descriptor.Owner,
                                         descriptor.Group,
                                         descriptor.SystemAcl,
                                         descriptor.DiscretionaryAcl).GetSddlForm(AccessControlSections.Access);
    }

    public void Restore(string target, string? previousValue)
    {
        target = WritableAclPath(target);
        var security = Security(target);
        security.SetSecurityDescriptorSddlForm(previousValue ?? throw new ArgumentException("Missing security descriptor."),
                                               AccessControlSections.Access);
        SaveSecurity(target, security);
    }
}
