using WindowsAxon.Agent.Skills;

namespace WindowsAxon.Agent.Tests.Skills;

public class SkillCatalogTests
{
    private const string Wifi = """
        ---
        name: wifi-no-internet
        description: Diagnose a connection that shows connected but has no internet.
        metadata:
          tools: "net_get_config net_ping  net_dns_lookup settings_open"
        ---
        # Steps
        1. Run net_get_config.
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Load_reads_name_description_and_tools()
    {
        var root = Directory.CreateTempSubdirectory("windowsaxon-skills-").FullName;
        Write(root, "wifi-no-internet", Wifi);
        Write(root, "calc", "---\nname: calc\ndescription: Calculate.\n---\nbody");

        var catalog = await SkillCatalog.LoadAsync([(root, true)], cancellationToken: Ct);

        var skill = catalog.Find("wifi-no-internet")!;
        Assert.StartsWith("Diagnose", skill.Description);
        Assert.Equal(["net_get_config", "net_ping", "net_dns_lookup", "settings_open"], skill.Tools);
        Assert.True(skill.BuiltIn);
        Assert.Empty(catalog.Find("calc")!.Tools);
    }

    [Fact]
    public async Task Metadata_keys_are_read_without_case_and_a_duplicate_key_keeps_the_first_value()
    {
        var root = Directory.CreateTempSubdirectory("windowsaxon-skills-").FullName;
        Write(root, "upper", "---\nname: upper\ndescription: U.\nmetadata:\n  Tools: \"net_ping\"\n---\nBody.");
        Write(root, "dup", "---\nname: dup\ndescription: D.\nmetadata:\n  tools: \"net_ping\"\n  tools: \"app_find\"\n---\nBody.");

        var catalog = await SkillCatalog.LoadAsync([(root, true)], cancellationToken: Ct);

        Assert.Equal(["net_ping"], catalog.Find("upper")!.Tools);
        Assert.Equal(["net_ping"], catalog.Find("dup")!.Tools);
    }

    [Fact]
    public async Task A_tools_list_is_reported_as_a_format_problem()
    {
        var root = Directory.CreateTempSubdirectory("windowsaxon-skills-").FullName;
        Write(root, "list", "---\nname: list\ndescription: L.\nmetadata:\n  tools:\n    - net_ping\n---\nBody.");

        var catalog = await SkillCatalog.LoadAsync([(root, true)], new HashSet<string> { "net_ping" }, Ct);

        Assert.Empty(catalog.Skills);
        Assert.Contains("one line of tool names", Assert.Single(catalog.Problems).Message);
    }

    [Fact]
    public async Task A_nested_skill_folder_is_not_loaded_and_cannot_take_a_built_in_name()
    {
        var builtIn = Directory.CreateTempSubdirectory("windowsaxon-builtin-").FullName;
        var user = Directory.CreateTempSubdirectory("windowsaxon-user-").FullName;
        Write(builtIn, "wifi-no-internet", Wifi);
        Write(Path.Combine(user, "outer"), "wifi-no-internet", Wifi.Replace("Diagnose", "Evil"));

        var catalog = await SkillCatalog.LoadAsync([(builtIn, true), (user, false)], cancellationToken: Ct);

        var skill = Assert.Single(catalog.Skills);
        Assert.True(skill.BuiltIn);
        Assert.Single(catalog.AgentSkills);
        Assert.Contains(catalog.Problems, p => p.Message.Contains("nested"));
    }

    [Fact]
    public async Task A_linked_skill_folder_is_not_loaded()
    {
        var target = Directory.CreateTempSubdirectory("windowsaxon-target-").FullName;
        var root = Directory.CreateTempSubdirectory("windowsaxon-skills-").FullName;
        Write(target, "wifi-no-internet", Wifi);
        var link = Path.Combine(root, "wifi-no-internet");
        using (var mklink = System.Diagnostics.Process.Start("cmd.exe", ["/c", "mklink", "/J", link, Path.Combine(target, "wifi-no-internet")]))
        {
            await mklink.WaitForExitAsync(Ct);
            Assert.Equal(0, mklink.ExitCode);
        }

        var catalog = await SkillCatalog.LoadAsync([(root, false)], cancellationToken: Ct);

        Assert.Empty(catalog.Skills);
        Assert.Contains("link", Assert.Single(catalog.Problems).Message);
    }

    [Theory]
    [InlineData("# no frontmatter", "frontmatter")]
    [InlineData("---\ndescription: x\n---\nbody", "no name")]
    [InlineData("---\nname: [unclosed\n---\nbody", "YAML")]
    [InlineData("---\nname: other\ndescription: x\n---\nbody", "folder")]
    public async Task A_skill_that_the_loader_rejects_reports_the_reason(string text, string reason)
    {
        var root = Directory.CreateTempSubdirectory("windowsaxon-skills-").FullName;
        Write(root, "x", text);

        var catalog = await SkillCatalog.LoadAsync([(root, false)], cancellationToken: Ct);

        Assert.Empty(catalog.Skills);
        Assert.Contains(reason, Assert.Single(catalog.Problems).Message);
    }

    [Fact]
    public async Task Load_reads_roots_in_order_and_the_first_name_wins()
    {
        var builtIn = Directory.CreateTempSubdirectory("windowsaxon-builtin-");
        var user = Directory.CreateTempSubdirectory("windowsaxon-user-");
        Write(builtIn.FullName, "wifi-no-internet", Wifi);
        Write(user.FullName, "wifi-no-internet", Wifi.Replace("Diagnose", "Evil"));
        Write(user.FullName, "bad", "no frontmatter");

        var catalog = await SkillCatalog.LoadAsync([
            (builtIn.FullName, true),
            (user.FullName, false),
            (@"C:\does\not\exist", false)
        ],
                                                   cancellationToken: Ct);

        var skill = Assert.Single(catalog.Skills);
        Assert.True(skill.BuiltIn);
        Assert.StartsWith("Diagnose", catalog.Find("wifi-no-internet")!.Description);
        Assert.Equal(2, catalog.Problems.Count); // duplicate + bad
    }

    [Fact]
    public async Task A_broken_built_in_skill_keeps_its_name()
    {
        var builtIn = Directory.CreateTempSubdirectory("windowsaxon-builtin-");
        var user = Directory.CreateTempSubdirectory("windowsaxon-user-");
        Write(builtIn.FullName, "wifi-no-internet", "no frontmatter");
        Write(user.FullName, "wifi-no-internet", Wifi);

        var catalog = await SkillCatalog.LoadAsync([(builtIn.FullName, true), (user.FullName, false)], cancellationToken: Ct);

        Assert.Empty(catalog.Skills);
        Assert.Contains(catalog.Problems, p => !p.BuiltIn && p.Message.Contains("built-in"));
    }

    [Fact]
    public async Task An_unreadable_skill_does_not_stop_the_other_skills()
    {
        var builtIn = Directory.CreateTempSubdirectory("windowsaxon-builtin-");
        var user = Directory.CreateTempSubdirectory("windowsaxon-user-");
        Write(builtIn.FullName, "wifi-no-internet", Wifi);
        Write(user.FullName, "locked", "---\nname: locked\ndescription: Locked.\n---\nBody.\n");
        Write(user.FullName, "open", "---\nname: open\ndescription: Open.\n---\nBody.\n");
        using var lockFile = File.Open(Path.Combine(user.FullName, "locked", "SKILL.md"), FileMode.Open, FileAccess.Read, FileShare.None);

        var catalog = await SkillCatalog.LoadAsync([(builtIn.FullName, true), (user.FullName, false)], cancellationToken: Ct);

        Assert.Equal(["wifi-no-internet", "open"], catalog.Skills.Select(s => s.Name));
        Assert.Equal("locked", Path.GetFileName(Assert.Single(catalog.Problems).Folder));
    }

    private static void Write(string root, string folder, string text)
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, folder));
        File.WriteAllText(Path.Combine(dir.FullName, "SKILL.md"), text);
    }
}
