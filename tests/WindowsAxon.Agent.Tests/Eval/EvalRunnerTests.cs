using WindowsAxon.Agent.Settings;
using WindowsAxon.Agent.Tests.Fakes;
using WindowsAxon.Eval;

namespace WindowsAxon.Agent.Tests.Eval;

public class EvalRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EvalRunner Runner(ScriptedChatClient model) => new(() => model,
                                                                      new ChatSettings(),
                                                                      FakeToolManifest.Load(RepoPaths.File("eval/tools.json")),
                                                                      RepoPaths.File("skills"),
                                                                      RepoPaths.File("eval"),
                                                                      docsAvailable: false);

    private static string Step(string action, string args) => $$"""{"thought":"t","action":"{{action}}","args":{{args}}}""";

    private static string Final(string answer, string action = "final_answer") => $$"""{"thought":"t","action":"{{action}}","answer":"{{answer}}"}""";

    [Fact]
    public async Task Passing_case_is_scored_as_success()
    {
        var model = new ScriptedChatClient()
            .Reply(Step("enable_tools", """{"group":"personalize"}"""))
            .Reply(Step("theme_set_mode", """{"mode":"dark"}"""))
            .Reply(Final("Dark mode is on."));
        var c = new EvalCase
        {
            Id = "t1",
            Request = "use dark mode",
            ExpectGroup = "personalize",
            ExpectToolsAll = ["theme_set_mode"],
            ExpectArgs = new() { ["theme_set_mode"] = new() { ["mode"] = "dark" } }
        };

        var result = await Runner(model).RunCaseAsync(c, Ct);

        Assert.True(result.Success, string.Join("; ", result.Failures));
        Assert.True(result.SelectionCorrect);
        Assert.True(result.FirstToolCorrect);
        Assert.Equal(1, result.Approvals);
        Assert.Equal(0, result.Unapproved);
        Assert.Equal(3, result.Steps);
    }

    [Fact]
    public async Task Forbidden_tool_wrong_args_and_missing_text_are_failures()
    {
        var model = new ScriptedChatClient()
            .Reply(Step("enable_tools", """{"group":"network"}"""))
            .Reply(Step("net_ping", """{"host":"8.8.8.8"}"""))
            .Reply(Final("All good."));
        var c = new EvalCase
        {
            Id = "t2",
            Request = "ping 1.1.1.1",
            ExpectToolsAll = ["net_ping"],
            ForbidTools = ["net_ping"],
            ExpectArgs = new() { ["net_ping"] = new() { ["host"] = "1.1.1.1" } },
            ExpectAnswerContains = ["reachable"],
        };

        var result = await Runner(model).RunCaseAsync(c, Ct);

        Assert.False(result.Success);
        Assert.Contains("forbidden tool net_ping", result.Failures);
        Assert.Contains("net_ping was not called with host=1.1.1.1", result.Failures);
        Assert.Contains("answer does not contain 'reachable'", result.Failures);
    }

    [Fact]
    public async Task Sub_agents_mode_scores_the_group_by_its_agent_and_measures_the_main_context()
    {
        var model = new ScriptedChatClient()
            .Reply(Step("delegate", """{"agent":"config","task":"Use dark mode."}"""))
            .Reply(Step("theme_set_mode", """{"mode":"dark"}"""))
            .Reply(Final("Dark mode is on."))
            .Reply(Final("Dark mode is on."));
        var runner = new EvalRunner(() => model,
                                    new ChatSettings { ToolSelection = ToolSelectionMode.SubAgents },
                                    FakeToolManifest.Load(RepoPaths.File("eval/tools.json")),
                                    RepoPaths.File("skills"),
                                    RepoPaths.File("eval"),
                                    docsAvailable: false);
        var c = new EvalCase
        {
            Id = "t4",
            Request = "use dark mode",
            ExpectGroup = "personalize",
            ExpectToolsAll = ["theme_set_mode"],
        };

        var result = await runner.RunCaseAsync(c, Ct);

        Assert.True(result.Success, string.Join("; ", result.Failures));
        Assert.True(result.SelectionCorrect);
        Assert.Equal(1, result.Approvals);
        Assert.InRange(result.MainContextTokens, 100, 10_000);
    }

    [Fact]
    public async Task Ask_user_is_answered_with_the_case_replies()
    {
        var model = new ScriptedChatClient()
            .Reply(Final("Which app?", "ask_user"))
            .Reply(Step("app_find", """{"name":"paint"}"""))
            .Reply(Final("Found Paint."));
        var c = new EvalCase { Id = "t3", Request = "open it", Replies = ["paint"], ExpectToolsAll = ["app_find"] };

        var result = await Runner(model).RunCaseAsync(c, Ct);

        Assert.True(result.Success, string.Join("; ", result.Failures));
        Assert.Null(result.SelectionCorrect);
    }

    [Fact]
    public void Summary_computes_rates_and_the_m1_gate()
    {
        EvalResult R(bool ok, bool? sel, int invalid) => new("x", ok, sel, null, 3, 10, invalid, 0, 0, 1.0, [], "");
        var results = Enumerable.Range(0, 20).Select(i => R(i < 17, i < 18 ? true : false, i == 0 ? 1 : 0)).ToList();

        var summary = EvalSummary.From(results);

        Assert.Equal(0.85, summary.TaskSuccess, 3);
        Assert.Equal(0.90, summary.SelectionAccuracy, 3);
        Assert.Equal(1.0 / 201, summary.InvalidStepRate, 4);
        Assert.True(summary.PassesM1Gate(smallModel: false));
    }
}
