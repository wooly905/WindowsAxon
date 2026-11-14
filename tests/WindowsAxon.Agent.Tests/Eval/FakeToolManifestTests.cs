using WindowsAxon.Agent.Tests.Fakes;
using WindowsAxon.Agent.Tools;
using WindowsAxon.Eval;

namespace WindowsAxon.Agent.Tests.Eval;

public class FakeToolManifestTests
{
    [Fact]
    public void Manifest_loads_and_builds_a_catalog_with_groups_and_risk()
    {
        var specs = FakeToolManifest.Load(RepoPaths.File("eval/tools.json"));
        var catalog = FakeToolManifest.BuildCatalog(specs, new Dictionary<string, string>(), []);

        Assert.Equal(RiskLevel.Change, catalog.Find("wallpaper_set")!.Risk);
        Assert.True(catalog.Find("app_launch")!.PolicyDriven);
        Assert.True(catalog.IsBaseTool("app_find"));
        Assert.Contains("network", catalog.Groups);
    }

    [Fact]
    public async Task Fake_tool_records_the_call_and_returns_the_mock_output()
    {
        var specs = FakeToolManifest.Load(RepoPaths.File("eval/tools.json"));
        var calls = new List<ToolCallRecord>();
        var catalog = FakeToolManifest.BuildCatalog(specs,
                                                    new Dictionary<string, string> { ["net_ping"] = "timeout" },
                                                    calls);

        var result = await catalog.Find("net_ping")!.Function.InvokeAsync(new AIFunctionArguments { ["host"] = "1.1.1.1" },
                                                                          TestContext.Current.CancellationToken);

        Assert.Equal("timeout", result?.ToString());
        var call = Assert.Single(calls);
        Assert.Equal("1.1.1.1", call.Args["host"]);
        Assert.Equal("object", catalog.Find("net_ping")!.Function.JsonSchema.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Every_skill_tool_exists_in_the_manifest()
    {
        var names = FakeToolManifest.Load(RepoPaths.File("eval/tools.json")).Select(s => s.Name).ToHashSet();
        var skills = await WindowsAxon.Agent.Skills.SkillCatalog.LoadAsync([(RepoPaths.File("skills"), true)],
                                                                         cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(skills.Problems);
        Assert.All(skills.Skills.SelectMany(s => s.Tools), tool => Assert.Contains(tool, names));
    }
}
