using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace WindowsAxon.Agent.Skills;

public sealed record SkillInfo(string Name,
                               string Description,
                               IReadOnlyList<string> Tools,
                               string Folder,
                               bool BuiltIn);

public sealed record SkillProblem(string Folder, bool BuiltIn, string Message)
{
    public override string ToString() => $"{Path.Combine(Folder, SkillCatalog.FileName)}: {Message}";
}

/// <summary>
/// A snapshot of the skills for one chat. The MAF skill loader is the only parser that decides whether a skill loads,
/// so the skill info, the metadata.tools, and the content that the model sees all come from the same MAF read.
/// YamlDotNet is used only to explain why MAF did not accept a SKILL.md.
/// </summary>
public sealed partial class SkillCatalog
{
    public const string FileName = "SKILL.md";

    private const string LoaderRejected =
        "The skill loader did not accept this SKILL.md. Check that the frontmatter keys are lowercase, the values are simple text, and the file can be read.";

    private static readonly IDeserializer Yaml = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();

    private readonly List<SkillInfo> _skills = [];
    private readonly List<AgentSkill> _agentSkills = [];
    private readonly List<SkillProblem> _problems = [];

    // Every skill folder name seen so far, valid or not, so a broken built-in skill still keeps its name.
    private readonly Dictionary<string, bool> _claimed = new(StringComparer.OrdinalIgnoreCase);

    internal static SkillCatalog Empty => new();

    public IReadOnlyList<SkillInfo> Skills => _skills;

    public IReadOnlyList<SkillProblem> Problems => _problems;

    /// <summary>The MAF skills of this snapshot, for the skills provider.</summary>
    internal IReadOnlyList<AgentSkill> AgentSkills => _agentSkills;

    public SkillInfo? Find(string name) => _skills.FirstOrDefault(s => s.Name == name);

    /// <param name="roots">Skill roots in priority order. A later skill with the same name is rejected.</param>
    /// <param name="knownTools">Registered tool names. When set, a skill with an unknown tool in metadata.tools is rejected.</param>
    public static async Task<SkillCatalog> LoadAsync(IEnumerable<(string Root, bool BuiltIn)> roots,
                                                     IReadOnlySet<string>? knownTools = null,
                                                     CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var agent = new ChatClientAgent(new UnavailableChatClient());
        var context = new AgentSkillsSourceContext(agent, await agent.CreateSessionAsync(cancellationToken: cancellationToken));
        var catalog = new SkillCatalog();
        foreach (var (root, builtIn) in roots)
        {
            string[] folders;
            try
            {
                folders = Directory.Exists(root) ? Directory.GetDirectories(root) : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                catalog._problems.Add(new SkillProblem(root, builtIn, "The skills folder cannot be read: " + ex.Message));
                continue;
            }

            var read = await catalog.ReadRootAsync(root, folders, builtIn, context, cancellationToken);
            foreach (var folder in folders.Order(StringComparer.OrdinalIgnoreCase))
            {
                catalog.Add(folder, builtIn, knownTools, read);
            }
        }

        return catalog;
    }

    /// <summary>The MAF read of one skill folder: the skill, or the error message when the folder could not be read.</summary>
    private sealed record FolderRead(AgentFileSkill? Skill, string? Error);

    /// <summary>
    /// Reads every skill under one root with one MAF source. Only a SKILL.md directly in a skill folder counts.
    /// If MAF cannot read the root, each skill folder is read alone, so one bad folder does not stop the others.
    /// </summary>
    private async Task<Dictionary<string, FolderRead>> ReadRootAsync(string root,
                                                                     string[] folders,
                                                                     bool builtIn,
                                                                     AgentSkillsSourceContext context,
                                                                     CancellationToken cancellationToken)
    {
        var read = new Dictionary<string, FolderRead>(StringComparer.OrdinalIgnoreCase);
        try
        {
            AddRead(read, root, await ReadAsync(root, context, cancellationToken), builtIn);
            return read;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        foreach (var folder in folders.Where(f => File.Exists(Path.Combine(f, FileName))))
        {
            try
            {
                // Reported as a nested skill when it is not directly in a skill folder, the same as a root read.
                AddRead(read, root, await ReadAsync(folder, context, cancellationToken), builtIn);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                read[Normalize(folder)] = new FolderRead(null, "The skill cannot be read: " + ex.Message);
            }
        }

        return read;
    }

    private void AddRead(Dictionary<string, FolderRead> read, string root, List<AgentFileSkill> skills, bool builtIn)
    {
        foreach (var skill in skills)
        {
            if (SamePath(Path.GetDirectoryName(Normalize(skill.Path)), root))
            {
                read[Normalize(skill.Path)] = new FolderRead(skill, null);
            }
            else if (!_problems.Any(p => SamePath(p.Folder, skill.Path)))
            {
                _problems.Add(new SkillProblem(skill.Path,
                                               builtIn,
                                               "SKILL.md is in a nested folder and is not loaded. Put each skill folder directly in the skills folder."));
            }
        }
    }

    private static async Task<List<AgentFileSkill>> ReadAsync(string root,
                                                              AgentSkillsSourceContext context,
                                                              CancellationToken cancellationToken)
    {
        using var source = new AgentFileSkillsSource(root,
                                                     options: new AgentFileSkillsSourceOptions { AllowedScriptExtensions = [] });
        return [.. (await source.GetSkillsAsync(context, cancellationToken)).OfType<AgentFileSkill>()];
    }

    private void Add(string folder, bool builtIn, IReadOnlySet<string>? knownTools, Dictionary<string, FolderRead> read)
    {
        var name = Path.GetFileName(folder);
        if (!File.Exists(Path.Combine(folder, FileName)))
        {
            // A built-in folder keeps its name even without a SKILL.md, the same rule as SkillLibrary.
            if (builtIn)
            {
                _claimed.TryAdd(name, true);
            }

            return;
        }

        if (_claimed.TryGetValue(name, out var ownerBuiltIn))
        {
            Problem($"The name '{name}' is already used by a {(ownerBuiltIn ? "built-in" : "personal")} skill.");
            return;
        }

        _claimed[name] = builtIn;

        // MAF skips links below a root, but reads a link given as the root, so the fallback read would differ.
        if (IsLink(folder))
        {
            Problem("The skill folder is a link (junction or symbolic link). Put the skill files in a normal folder.");
            return;
        }

        var result = read.GetValueOrDefault(Normalize(folder));
        if (result?.Skill is not { } skill)
        {
            Problem(result?.Error ?? Diagnose(folder));
            return;
        }

        if (skill.Frontmatter.Name != name)
        {
            Problem($"The folder name must be the same as the skill name '{skill.Frontmatter.Name}'.");
            return;
        }

        // MAF compares metadata keys without case, so "Tools" is the same key as "tools".
        var tools = skill.Frontmatter.Metadata?.GetValueOrDefault("tools")?.ToString() ?? "";
        var names = tools.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Any(n => !ToolName().IsMatch(n)))
        {
            Problem("metadata.tools must be one line of tool names separated by spaces, for example tools: \"net_ping net_dns_lookup\".");
            return;
        }

        var unknown = names.Where(t => knownTools is not null && !knownTools.Contains(t)).ToList();
        if (unknown.Count > 0)
        {
            Problem($"metadata.tools has tools that do not exist: {string.Join(", ", unknown)}.");
            return;
        }

        _skills.Add(new SkillInfo(name, skill.Frontmatter.Description ?? "", names, folder, builtIn));
        _agentSkills.Add(skill);

        void Problem(string message) => _problems.Add(new SkillProblem(folder, builtIn, message));
    }

    private static bool IsLink(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Why MAF did not accept a SKILL.md. Used only for the message; it never makes a skill load.</summary>
    private static string Diagnose(string folder)
    {
        try
        {
            return Diagnose(File.ReadAllText(Path.Combine(folder, FileName)), folder) ?? LoaderRejected;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "SKILL.md cannot be read: " + ex.Message;
        }
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex ToolName();

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static bool SamePath(string? a, string b) =>
        a is not null && string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits SKILL.md into the frontmatter lines and the body.</summary>
    internal static bool TrySplit(string text, out string[] frontmatter, out string body, out string? error)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        frontmatter = [];
        body = "";
        if (lines.Length < 3 || lines[0].Trim() != "---")
        {
            error = "SKILL.md must start with a --- frontmatter block.";
            return false;
        }

        var end = Array.FindIndex(lines, 1, l => l.Trim() == "---");
        if (end < 0)
        {
            error = "The frontmatter block has no closing ---.";
            return false;
        }

        frontmatter = lines[1..end];
        body = string.Join('\n', lines[(end + 1)..]);
        error = null;
        return true;
    }

    /// <summary>The first problem in a SKILL.md, or null when none is found.</summary>
    internal static string? Diagnose(string text, string folder)
    {
        if (!TrySplit(text, out var lines, out _, out var error))
        {
            return error;
        }

        Frontmatter? frontmatter;
        try
        {
            frontmatter = Yaml.Deserialize<Frontmatter>(string.Join('\n', lines));
        }
        catch (YamlException ex)
        {
            return "The frontmatter is not valid YAML: " + ex.Message;
        }

        if (string.IsNullOrWhiteSpace(frontmatter?.Name))
        {
            return "The frontmatter has no name.";
        }

        if (!AgentSkillFrontmatter.ValidateName(frontmatter.Name, out var reason)
            || !AgentSkillFrontmatter.ValidateDescription(frontmatter.Description, out reason)
            || !AgentSkillFrontmatter.ValidateCompatibility(frontmatter.Compatibility, out reason))
        {
            return reason;
        }

        return string.Equals(Path.GetFileName(folder.TrimEnd('\\', '/')), frontmatter.Name, StringComparison.Ordinal)
            ? null
            : $"The folder name must be the same as the skill name '{frontmatter.Name}'.";
    }
}
