using WindowsAxon.Agent.Selection;
using WindowsAxon.Agent.Skills;
using WindowsAxon.Agent.Tests.Fakes;
using WindowsAxon.Agent.Tools;
using NSubstitute;

namespace WindowsAxon.Agent.Tests.Selection;

public class ToolScopeChatClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Harness
    {
        public ToolCatalog Catalog { get; } = new();

        public ToolSelectionState State
        {
            get;
        }

        public ScriptedChatClient Inner { get; } = new();

        public IStepLimitPrompter Prompter { get; } = Substitute.For<IStepLimitPrompter>();

        public List<AITool> Tools
        {
            get;
        }

        public ToolScopeChatClient Client
        {
            get;
        }

        public Harness(bool scopeTools = true, int maxActiveTools = 14)
        {
            foreach (var (name, group) in new[] {
                ("app_find", "apps"),
                ("net_ping", "network"),
                ("net_dns_lookup", "network"),
                ("fs_list_dir", "files")
            })
            {
                Catalog.Add(AIFunctionFactory.Create(() => "", name), group, RiskLevel.Read, alwaysInclude: name == "app_find");
            }

            State = new ToolSelectionState(Catalog, maxActiveTools);
            Tools = [.. Catalog.BuildAgentTools(e => e.Function), AIFunctionFactory.Create(() => "", "load_skill")];
            var skills = SkillCatalogWith(new SkillInfo("wifi-no-internet",
                                                        "d",
                                                        ["net_ping", "net_dns_lookup"],
                                                        @"C:\s\wifi-no-internet",
                                                        true));
            Client = new ToolScopeChatClient(Inner, State, skills, Prompter, scopeTools);
        }

        public async Task<ChatOptions> Send(params ChatMessage[] messages)
        {
            Inner.Reply("ok");
            await Client.GetResponseAsync(messages.Length == 0 ? [new ChatMessage(ChatRole.User, "hi")] : messages,
                                          new ChatOptions { Tools = Tools },
                                          Ct);
            return Inner.Calls[^1].Options!;
        }
    }

    private static SkillCatalog SkillCatalogWith(SkillInfo skill)
    {
        var root = Directory.CreateTempSubdirectory("windowsaxon-scope-");
        var dir = Directory.CreateDirectory(Path.Combine(root.FullName, skill.Name));
        File.WriteAllText(Path.Combine(dir.FullName, "SKILL.md"),
                          $"---\nname: {skill.Name}\ndescription: {skill.Description}\nmetadata:\n  tools: \"{string.Join(' ', skill.Tools)}\"\n---\nbody");
        return SkillCatalog.Load([(root.FullName, true)]);
    }

    private static ChatMessage[] LoadSkillHistory(string callId = "s1") =>
    [
        new(ChatRole.User, "wifi broken"),
        new(ChatRole.Assistant,
            [new FunctionCallContent(callId,
                                     "load_skill",
                                     new Dictionary<string, object?> { ["skillName"] = "wifi-no-internet" })]),
        new(ChatRole.Tool, [new FunctionResultContent(callId, "# Steps ...")]),
    ];

    [Fact]
    public async Task Only_base_and_active_tools_are_sent()
    {
        var h = new Harness();

        var options = await h.Send();

        Assert.Equal(["app_find", "load_skill"], options.Tools!.Select(t => t.Name));
    }

    [Fact]
    public async Task Loaded_skill_enables_its_tools()
    {
        var h = new Harness();

        var options = await h.Send(LoadSkillHistory());

        Assert.Equal(["app_find", "net_ping", "net_dns_lookup", "load_skill"], options.Tools!.Select(t => t.Name));
    }

    [Fact]
    public async Task Skill_activation_that_removes_a_group_says_so_in_the_load_skill_result()
    {
        var h = new Harness(maxActiveTools: 2);
        h.State.Activate("group:files", ["fs_list_dir"]);
        var history = LoadSkillHistory();

        await h.Send(history);

        var result = history[2].Contents.OfType<FunctionResultContent>().Single().Result?.ToString();
        Assert.EndsWith("Enabled: wifi-no-internet. Removed: files (fs_list_dir are no longer available).", result);
    }

    [Fact]
    public async Task Duplicate_call_ids_in_the_history_do_not_break_the_call()
    {
        var h = new Harness();
        ChatMessage[] history =
        [
            new(ChatRole.User, "check app"),
            new(ChatRole.Assistant, [new FunctionCallContent("call_0", "app_find", new Dictionary<string, object?>())]),
            new(ChatRole.Tool, [new FunctionResultContent("call_0", "found")]),
            .. LoadSkillHistory("call_0"),
        ];

        var options = await h.Send(history);

        Assert.Equal(["app_find", "net_ping", "net_dns_lookup", "load_skill"], options.Tools!.Select(t => t.Name));
    }

    [Fact]
    public async Task A_load_skill_with_a_reused_call_id_is_still_processed()
    {
        var h = new Harness();
        ChatMessage[] first =
        [
            new(ChatRole.User, "hi"),
            new(ChatRole.Assistant,
                [new FunctionCallContent("call_0",
                                         "load_skill",
                                         new Dictionary<string, object?> { ["skillName"] = "no-such-skill" })]),
            new(ChatRole.Tool, [new FunctionResultContent("call_0", "Skill not found.")]),
        ];
        await h.Send(first);

        var options = await h.Send([.. first, .. LoadSkillHistory("call_0")]);

        Assert.Equal(["app_find", "net_ping", "net_dns_lookup", "load_skill"], options.Tools!.Select(t => t.Name));
    }

    [Fact]
    public async Task Each_model_call_counts_one_step()
    {
        var h = new Harness();

        await h.Send();
        await h.Send();

        Assert.Equal(2, h.State.StepCount);
    }

    [Fact]
    public async Task At_the_limit_the_user_can_extend_one_time()
    {
        var h = new Harness();
        h.Prompter.AskToContinueAsync(ToolSelectionState.BaseLimit, Arg.Any<CancellationToken>()).Returns(true);
        for (var i = 0; i < ToolSelectionState.BaseLimit; i++)
        {
            h.State.CountStep();
        }

        var options = await h.Send();

        Assert.True(h.State.ExtensionUsed);
        Assert.False(h.State.MustFinish);
        Assert.NotNull(options.Tools);
    }

    [Fact]
    public async Task At_the_limit_a_no_forces_a_final_answer_with_no_tools()
    {
        var h = new Harness();
        h.Prompter.AskToContinueAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
        for (var i = 0; i < ToolSelectionState.BaseLimit; i++)
        {
            h.State.CountStep();
        }

        var options = await h.Send();

        Assert.True(h.State.MustFinish);
        Assert.Null(options.Tools);
        Assert.Equal(ToolScopeChatClient.FinishMessage, h.Inner.Calls[^1].Messages[^1].Text);
    }

    [Fact]
    public async Task After_the_extension_the_limit_does_not_ask_again()
    {
        var h = new Harness();
        h.State.Extend();
        for (var i = 0; i < ToolSelectionState.BaseLimit + ToolSelectionState.ExtensionSteps; i++)
        {
            h.State.CountStep();
        }

        var options = await h.Send();

        await h.Prompter.DidNotReceiveWithAnyArgs().AskToContinueAsync(default, Ct);
        Assert.Null(options.Tools);
    }

    [Fact]
    public async Task All_mode_sends_every_tool()
    {
        var h = new Harness(scopeTools: false);

        var options = await h.Send();

        Assert.Equal(5, options.Tools!.Count);
    }
}
