using WindowsAxon.Agent.Skills;

namespace WindowsAxon.Agent.Tests.Skills;

public class SkillLibraryTests
{
    private readonly string _builtIn = Directory.CreateTempSubdirectory("windowsaxon-builtin-").FullName;
    private readonly string _user = Path.Combine(Directory.CreateTempSubdirectory("windowsaxon-data-").FullName, "skills");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SkillLibrary Library() => new(_builtIn, _user, new HashSet<string> { "net_ping", "app_find" });

    /// <summary>Writes a SKILL.md by hand, because the editor does not set metadata.tools.</summary>
    private string WriteSkill(string folder, string name, string frontmatter = "")
    {
        var path = Directory.CreateDirectory(Path.Combine(_user, folder)).FullName;
        var file = Path.Combine(path, "SKILL.md");
        File.WriteAllText(file, $"---\nname: {name}\ndescription: \"Ping.\"\n{frontmatter}---\nBody.\n");
        return file;
    }

    [Fact]
    public async Task Save_writes_a_skill_that_both_loaders_read_and_keeps_other_fields()
    {
        var library = Library();
        var file = WriteSkill("my-ping", "my-ping", "license: MIT\nmetadata:\n  tools: \"net_ping app_find\"\n  owner:\n    team: \"it\"\n");

        await library.SaveAsync(new SkillDraft("my-ping", "Ping a host.", "New steps.\r\nThen stop."), "my-ping", Ct);

        var skill = Assert.Single((await library.LoadAsync(Ct)).Skills);
        Assert.Equal(["net_ping", "app_find"], skill.Tools);
        var text = File.ReadAllText(file);
        Assert.Contains("license: MIT", text);
        Assert.Contains("metadata:\n  tools: \"net_ping app_find\"\n  owner:\n    team: \"it\"\n", text);
        var draft = SkillLibrary.ReadDraft(skill);
        Assert.Equal("Ping a host.", draft.Description);
        Assert.Equal("New steps.\nThen stop.\n", draft.Body);
    }

    [Fact]
    public async Task Save_keeps_a_description_with_quotes_and_colons()
    {
        var library = Library();

        await library.SaveAsync(new SkillDraft("my-ping", "Ping: check \"host\" #1 \\ end", "Body."), cancellationToken: Ct);

        Assert.Equal("Ping: check \"host\" #1 \\ end", Assert.Single((await library.LoadAsync(Ct)).Skills).Description);
        Assert.Equal("Ping: check \"host\" #1 \\ end",
                     SkillLibrary.ReadDraft((await library.LoadAsync(Ct)).Skills[0]).Description);
        await Assert.ThrowsAsync<ArgumentException>(() => library.SaveAsync(new SkillDraft("my-ping", "It's \"x\"", ""),
                                                                            "my-ping",
                                                                            Ct));
    }

    [Theory]
    [InlineData("open-app", "built-in")]
    [InlineData("Bad Name", "")]
    public async Task Save_rejects_invalid_names_and_writes_nothing(string name, string message)
    {
        // The built-in folder has no valid SKILL.md; its name is still taken.
        Directory.CreateDirectory(Path.Combine(_builtIn, "open-app"));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Library().SaveAsync(new SkillDraft(name, "Open.", "Body."),
                                                                                       cancellationToken: Ct));

        Assert.Contains(message, ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_user, name)));
    }

    [Fact]
    public async Task Rename_keeps_resources_and_a_failed_rename_leaves_the_skill_unchanged()
    {
        var library = Library();
        await library.SaveAsync(new SkillDraft("my-ping", "Ping.", "Body."), cancellationToken: Ct);
        File.WriteAllText(Path.Combine(_user, "my-ping", "notes.txt"), "resource");

        using (File.Open(Path.Combine(_user, "my-ping", "notes.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => library.SaveAsync(new SkillDraft("net-ping", "Ping.", "Body."),
                                                                             "my-ping",
                                                                             Ct));
        }

        Assert.Equal("my-ping", Assert.Single((await library.LoadAsync(Ct)).Skills).Name);
        Assert.False(Directory.Exists(Path.Combine(_user, "net-ping")));

        await library.SaveAsync(new SkillDraft("net-ping", "Ping.", "Body."), "my-ping", Ct);

        Assert.Equal("net-ping", Assert.Single((await library.LoadAsync(Ct)).Skills).Name);
        Assert.Equal("resource", File.ReadAllText(Path.Combine(_user, "net-ping", "notes.txt")));
        Assert.False(Directory.Exists(Path.Combine(_user, "my-ping")));
    }

    [Fact]
    public async Task Saving_keeps_metadata_as_written()
    {
        var library = Library();
        var file = WriteSkill("my-ping", "my-ping", "metadata:\n  Tools: \"net_ping\"\n");
        var skill = Assert.Single((await library.LoadAsync(Ct)).Skills);

        await library.SaveAsync(SkillLibrary.ReadDraft(skill) with { Body = "New." }, "my-ping", Ct);

        Assert.Contains("metadata:\n  Tools: \"net_ping\"\n", File.ReadAllText(file));
        Assert.Equal(["net_ping"], Assert.Single((await library.LoadAsync(Ct)).Skills).Tools);
    }

    [Fact]
    public async Task Delete_removes_a_personal_skill_and_rejects_paths()
    {
        var library = Library();
        await library.SaveAsync(new SkillDraft("my-ping", "Ping.", "Body."), cancellationToken: Ct);

        library.Delete("my-ping");

        Assert.Empty((await library.LoadAsync(Ct)).Skills);
        Assert.Throws<ArgumentException>(() => library.Delete(".."));
        Assert.Throws<ArgumentException>(() => library.Delete(@"..\skills"));
    }

    [Fact]
    public async Task Save_accepts_an_empty_body()
    {
        var library = Library();

        await library.SaveAsync(new SkillDraft("my-ping", "Ping.", ""), cancellationToken: Ct);

        Assert.Single((await library.LoadAsync(Ct)).Skills);
    }

    [Fact]
    public async Task Save_rejects_an_unknown_tool_and_leaves_the_file_unchanged()
    {
        var file = WriteSkill("my-ping", "my-ping", "metadata:\n  tools: \"net_missing\"\n");
        var before = File.ReadAllText(file);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Library().SaveAsync(new SkillDraft("my-ping", "Ping.", "New."),
                                                                                       "my-ping",
                                                                                       Ct));

        Assert.Contains("net_missing", ex.Message);
        Assert.Equal(before, File.ReadAllText(file));
    }

    [Fact]
    public async Task Save_accepts_a_skill_with_a_nested_skill_md()
    {
        var library = Library();
        WriteSkill("my-ping", "my-ping");
        WriteSkill(Path.Combine("my-ping", "example"), "example");

        await library.SaveAsync(new SkillDraft("my-ping", "Ping.", "New."), "my-ping", Ct);

        Assert.Equal("New.\n", SkillLibrary.ReadDraft(Assert.Single((await library.LoadAsync(Ct)).Skills)).Body);
    }
}
