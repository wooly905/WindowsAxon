using Microsoft.Agents.AI;

namespace WindowsAxon.Agent.Skills;

/// <summary>The editor fields of a skill. Saving keeps every other frontmatter field, metadata.tools too, as written.</summary>
public sealed record SkillDraft(string Name, string Description, string Body);

/// <summary>
/// Creates, changes, and deletes personal skills.
/// Every write is checked with the same rules as SkillCatalog.LoadAsync before it reaches the personal folder.
/// </summary>
public sealed class SkillLibrary(string builtInRoot, string userRoot, IReadOnlySet<string> knownTools)
{
    public string UserRoot => userRoot;

    public Task<SkillCatalog> LoadAsync(CancellationToken cancellationToken = default) =>
        SkillCatalog.LoadAsync([(builtInRoot, true), (userRoot, false)], knownTools, cancellationToken);

    /// <summary>
    /// The editor fields of a loaded skill. Name and description come from the MAF read in the catalog.
    /// </summary>
    public static SkillDraft ReadDraft(SkillInfo skill)
    {
        ArgumentNullException.ThrowIfNull(skill);

        var text = File.ReadAllText(Path.Combine(skill.Folder, SkillCatalog.FileName));
        if (!SkillCatalog.TrySplit(text, out _, out var body, out var error))
        {
            throw new InvalidDataException(error);
        }

        return new SkillDraft(skill.Name, skill.Description, body);
    }

    /// <summary>
    /// Builds the complete skill folder in a staging folder, checks it, and then swaps it in.
    /// A failed swap restores the previous folder, so the folder name and the skill name never differ.
    /// </summary>
    /// <param name="originalName">The personal skill to change, or null to create a skill.</param>
    public async Task SaveAsync(SkillDraft draft, string? originalName = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var name = draft.Name.Trim();
        string[]? existing = null;
        string? existingFolder = null;
        if (originalName is not null)
        {
            existingFolder = PersonalFolder(originalName);
            if (!Directory.Exists(existingFolder))
            {
                throw new ArgumentException($"The personal skill '{originalName}' does not exist.");
            }

            var file = Path.Combine(existingFolder, SkillCatalog.FileName);
            if (File.Exists(file) && SkillCatalog.TrySplit(await File.ReadAllTextAsync(file, cancellationToken),
                                                           out var lines,
                                                           out _,
                                                           out _))
            {
                existing = lines;
            }
        }

        CheckName(name, originalName);
        var text = Compose(existing, draft with { Name = name });
        using var stage = new Staging(userRoot);
        var stagedRoot = Path.Combine(stage.Root, "skill");
        var staged = Path.Combine(stagedRoot, name);
        if (existingFolder is null)
        {
            Directory.CreateDirectory(staged);
        }
        else
        {
            CopyFolder(existingFolder, staged);
        }

        await File.WriteAllTextAsync(Path.Combine(staged, SkillCatalog.FileName), text, cancellationToken);
        await ValidateAsync(stagedRoot, staged, cancellationToken);

        var target = Path.Combine(userRoot, name);
        Directory.CreateDirectory(userRoot);
        if (existingFolder is null)
        {
            Directory.Move(staged, target);
            return;
        }

        var backup = Path.Combine(stage.Root, "backup");
        Directory.Move(existingFolder, backup);
        try
        {
            Directory.Move(staged, target);
        }
        catch
        {
            Directory.Move(backup, existingFolder);
            throw;
        }
    }

    /// <param name="name">The personal skill folder name. It can be a folder with an invalid SKILL.md.</param>
    public void Delete(string name) => Directory.Delete(PersonalFolder(name), recursive: true);

    private void CheckName(string name, string? originalName)
    {
        if (!AgentSkillFrontmatter.ValidateName(name, out var reason))
        {
            throw new ArgumentException(reason);
        }

        // A built-in folder keeps its name even when its SKILL.md is not valid.
        if (Directory.Exists(Path.Combine(builtInRoot, name)))
        {
            throw new ArgumentException($"A built-in skill named '{name}' exists. Choose another name.");
        }

        if (!string.Equals(name, originalName, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(Path.Combine(userRoot, name)))
        {
            throw new ArgumentException($"A personal skill named '{name}' exists. Choose another name.");
        }
    }

    private async Task ValidateAsync(string root, string folder, CancellationToken cancellationToken)
    {
        var catalog = await SkillCatalog.LoadAsync([(root, false)], knownTools, cancellationToken);
        // A nested SKILL.md is only a notice. It does not stop the skill from loading, so it does not stop a save.
        if (catalog.Problems.FirstOrDefault(p => SkillCatalog.SamePath(p.Folder, folder)) is { } problem)
        {
            throw new ArgumentException(problem.Message);
        }

        if (catalog.Skills.Count != 1)
        {
            throw new ArgumentException("No valid skill was found.");
        }
    }

    private static void CopyFolder(string source, string destination)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 };
        var entries = new DirectoryInfo(source).EnumerateFileSystemInfos("*", options).ToList();
        if (entries.Any(e => e.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            throw new ArgumentException("The skill folder contains a link. Edit this skill outside WindowsAxon.");
        }

        Directory.CreateDirectory(destination);
        foreach (var entry in entries)
        {
            var path = Path.Combine(destination, Path.GetRelativePath(source, entry.FullName));
            if (entry is DirectoryInfo)
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(entry.FullName, path);
            }
        }
    }

    private string PersonalFolder(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || Path.GetFileName(name) != name
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"'{name}' is not a skill folder name.");
        }

        return Path.Combine(userRoot, name);
    }

    /// <summary>Writes name and description. Every other frontmatter line is kept as written.</summary>
    internal static string Compose(string[]? existing, SkillDraft draft)
    {
        var description = string.Join(' ', draft.Description.Split((char[])['\r', '\n', '\t', ' '],
                                                                   StringSplitOptions.RemoveEmptyEntries));
        List<string> lines = [$"name: {draft.Name}", $"description: {Quote(description)}"];
        foreach (var (key, block) in Blocks(existing ?? []))
        {
            if (key is not ("name" or "description"))
            {
                lines.AddRange(block);
            }
        }

        var body = draft.Body.Replace("\r\n", "\n").Replace('\r', '\n');
        return $"---\n{string.Join('\n', lines)}\n---\n{body.TrimEnd('\n')}\n";
    }

    /// <summary>
    /// Quotes a value so the MAF loader reads it exactly as written. MAF only removes the outer quotes and
    /// does not process escapes, so the value must not need any.
    /// </summary>
    private static string Quote(string value) =>
        !value.Contains('"') && !value.Contains('\\') ? $"\"{value}\""
        : !value.Contains('\'') ? $"'{value}'"
        : throw new ArgumentException("The description cannot contain both ' and \" or \\. Remove one of them.");

    /// <summary>Top-level frontmatter fields with their indented lines. Lines before the first field have the key "".</summary>
    private static List<(string Key, List<string> Lines)> Blocks(string[] lines)
    {
        List<(string Key, List<string> Lines)> blocks = [];
        foreach (var line in lines)
        {
            var field = line.Length > 0 && !char.IsWhiteSpace(line[0]) && line[0] != '#' && line.Contains(':');
            if (field || blocks.Count == 0)
            {
                blocks.Add((field ? line[..line.IndexOf(':')].Trim() : "", []));
            }

            blocks[^1].Lines.Add(line);
        }

        return blocks;
    }

    /// <summary>A temporary folder next to the personal folder, on the same volume, so Directory.Move works.</summary>
    private sealed class Staging : IDisposable
    {
        public Staging(string userRoot)
        {
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(userRoot)))!;
            Root = Directory.CreateDirectory(Path.Combine(parent, "skills-staging", Guid.NewGuid().ToString("N"))).FullName;
        }

        public string Root
        {
            get;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
                var parent = Path.GetDirectoryName(Root)!;
                if (!Directory.EnumerateFileSystemEntries(parent).Any())
                {
                    Directory.Delete(parent);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
