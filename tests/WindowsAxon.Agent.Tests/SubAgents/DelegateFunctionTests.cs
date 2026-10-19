using WindowsAxon.Agent.Settings;
using WindowsAxon.Agent.SubAgents;
using WindowsAxon.Agent.Tests.Fakes;
using NSubstitute;

namespace WindowsAxon.Agent.Tests.SubAgents;

public class DelegateFunctionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ToolStep(string action, string args) => $$"""{"thought":"t","action":"{{action}}","args":{{args}}}""";

    private static string Delegate(string agent, string task) =>
        ToolStep(DelegateFunction.ToolName, $$"""{"agent":"{{agent}}","task":"{{task}}"}""");

    private static string Final(string answer, string action = "final_answer") => $$"""{"thought":"t","action":"{{action}}","answer":"{{answer}}"}""";

    private static async Task<(AgentRunner Runner,
                               WindowsAxonSession Session,
                               ListAuditLog Audit,
                               IApprovalPrompter Prompter)> Create(ScriptedChatClient model)
    {
        var deps = AgentFactoryTests.Dependencies(model,
                                                  new ChatSettings
                                                  {
                                                      ToolSelection = ToolSelectionMode.SubAgents,
                                                      AutoApproveTools = false
                                                  });
        var session = await new AgentFactory(deps).CreateSessionAsync(Ct);
        var prompter = Substitute.For<IApprovalPrompter>();
        var audit = (ListAuditLog)deps.Audit;
        return (new AgentRunner(session, prompter, audit), session, audit, prompter);
    }

    [Fact]
    public async Task Main_agent_has_delegate_instead_of_group_tools()
    {
        var (_, session, _, _) = await Create(new ScriptedChatClient());

        var names = session.Tools.Select(t => t.Name).ToList();
        Assert.Contains(DelegateFunction.ToolName, names);
        Assert.DoesNotContain("net_ping", names);
        Assert.DoesNotContain("wallpaper_set", names);
        Assert.DoesNotContain("enable_tools", names);
    }

    [Fact]
    public async Task Sub_agent_tool_results_stay_out_of_the_main_context()
    {
        var model = new ScriptedChatClient()
            .Reply(Delegate("diagnostics", "Ping 1.1.1.1."))
            .Reply(ToolStep("net_ping", """{"host":"1.1.1.1"}"""))
            .Reply(Final("1.1.1.1 replies."))
            .Reply(Final("It is up."));
        var (runner, session, audit, _) = await Create(model);

        var result = await runner.RunAsync("is 1.1.1.1 up?", cancellationToken: Ct);

        Assert.Equal(RunStatus.Answered, result.Status);
        Assert.Equal("It is up.", result.Text);
        Assert.Contains("- diagnostics: system information", model.Calls[0].Messages[0].Text);
        Assert.Contains("Tools: net_ping.", model.Calls[0].Messages[0].Text);
        var subSystem = model.Calls[1].Messages[0].Text;
        Assert.Contains("diagnostics agent", subSystem);
        Assert.DoesNotContain("wallpaper_set", subSystem);
        Assert.Contains(model.Calls[2].Messages, m => m.Text.Contains("reply from 1.1.1.1"));
        var main = string.Join("\n", model.Calls[3].Messages.Select(m => m.Text));
        Assert.Contains("Agent diagnostics finished.\nAnswer: 1.1.1.1 replies.", main);
        Assert.DoesNotContain("reply from 1.1.1.1", main);
        Assert.Contains("agent:diagnostics", session.Selection.ActivationHistory);
        Assert.Equal(4, session.Selection.StepCount);
        var ping = Assert.Single(audit.Entries, e => e is { Kind: "call", Tool: "net_ping" });
        Assert.Equal("diagnostics", ping.Agent);
        Assert.Contains(audit.Entries, e => e is { Kind: "call", Tool: DelegateFunction.ToolName, Success: true });
    }

    [Theory]
    [InlineData(ApprovalAnswer.Allow, "succeeded")]
    [InlineData(ApprovalAnswer.Deny, "denied")]
    public async Task Sub_agent_approvals_reach_the_user_and_changes_return_to_the_main_agent(ApprovalAnswer answer,
                                                                                            string status)
    {
        var model = new ScriptedChatClient()
            .Reply(Delegate("config", "Set the wallpaper to C:\\\\b.jpg."))
            .Reply(ToolStep("wallpaper_set", """{"path":"C:\\b.jpg"}"""))
            .Reply(Final("Finished."))
            .Reply(Final("Done."));
        var (runner, _, audit, prompter) = await Create(model);
        prompter.AskAsync(default!, default!, Ct).ReturnsForAnyArgs(answer);

        var result = await runner.RunAsync("set wallpaper", cancellationToken: Ct);

        Assert.Equal("Done.", result.Text);
        await prompter.ReceivedWithAnyArgs(1).AskAsync(default!, default!, Ct);
        Assert.Contains(audit.Entries, e => e is { Kind: "approval", Tool: "wallpaper_set" });
        var main = string.Join("\n", model.Calls[3].Messages.Select(m => m.Text));
        Assert.Contains("Actions:\n- wallpaper_set ", main);
        Assert.Contains($" {status}", main);
    }

    [Fact]
    public async Task Sub_agent_question_is_returned_to_the_main_agent()
    {
        var model = new ScriptedChatClient()
            .Reply(Delegate("diagnostics", "Ping the server."))
            .Reply(Final("Which host?", "ask_user"))
            .Reply(Final("Which host should I ping?", "ask_user"));
        var (runner, _, _, _) = await Create(model);

        var result = await runner.RunAsync("ping the server", cancellationToken: Ct);

        Assert.Equal(RunStatus.AskedUser, result.Status);
        var main = string.Join("\n", model.Calls[2].Messages.Select(m => m.Text));
        Assert.Contains("Agent diagnostics needs information from the user.\nQuestion: Which host?", main);
    }

    [Fact]
    public async Task Native_mode_runs_the_sub_agent_with_native_tool_calls()
    {
        var model = new ScriptedChatClient()
            .ReplyCall(DelegateFunction.ToolName,
                       new Dictionary<string, object?> { ["agent"] = "diagnostics", ["task"] = "Ping 1.1.1.1." })
            .ReplyCall("net_ping", new Dictionary<string, object?> { ["host"] = "1.1.1.1" })
            .Reply("1.1.1.1 replies.")
            .Reply("It is up.");
        var deps = AgentFactoryTests.Dependencies(model,
                                                  new ChatSettings
                                                  {
                                                      StepMode = StepMode.Native,
                                                      ToolSelection = ToolSelectionMode.SubAgents
                                                  });
        var session = await new AgentFactory(deps).CreateSessionAsync(Ct);
        var runner = new AgentRunner(session, Substitute.For<IApprovalPrompter>(), deps.Audit);

        var result = await runner.RunAsync("is 1.1.1.1 up?", cancellationToken: Ct);

        Assert.Equal("It is up.", result.Text);
        Assert.Contains(model.Calls[1].Options!.Tools!, t => t.Name == "net_ping");
        Assert.DoesNotContain(model.Calls[1].Options!.Tools!, t => t.Name == DelegateFunction.ToolName);
        var delegateResult = model.Calls[3].Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.Contains("Answer: 1.1.1.1 replies.", delegateResult.Result?.ToString());
    }

    [Fact]
    public async Task Stop_during_a_sub_agent_approval_cancels_the_request()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var model = new ScriptedChatClient()
            .Reply(Delegate("config", "Set the wallpaper to C:\\\\b.jpg."))
            .Reply(ToolStep("wallpaper_set", """{"path":"C:\\b.jpg"}"""))
            .Reply(Final("Stopped earlier."));
        var (runner, _, audit, prompter) = await Create(model);
        prompter.AskAsync(default!, default!, Ct).ReturnsForAnyArgs<Task<ApprovalAnswer>>(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        var stopped = await runner.RunAsync("set wallpaper", cancellationToken: cts.Token);
        var next = await runner.RunAsync("never mind", cancellationToken: Ct);

        Assert.Equal(RunStatus.Cancelled, stopped.Status);
        Assert.DoesNotContain(audit.Entries, e => e is { Kind: "call", Tool: "wallpaper_set" });
        Assert.Equal("Stopped earlier.", next.Text);
    }

    [Fact]
    public async Task Unknown_agent_is_reported_to_the_model()
    {
        var model = new ScriptedChatClient()
            .Reply(Delegate("printers", "List printers."))
            .Reply(Final("No printer agent."));
        var (runner, _, _, _) = await Create(model);

        await runner.RunAsync("list printers", cancellationToken: Ct);

        var main = string.Join("\n", model.Calls[1].Messages.Select(m => m.Text));
        Assert.Contains("Unknown agent 'printers'. Agents: diagnostics, config.", main);
    }

    [Fact]
    public async Task Sub_agent_repeated_calls_do_not_stop_the_orchestrator_from_delegating_again()
    {
        var ping = ToolStep("net_ping", """{"host":"1.1.1.1"}""");
        var model = new ScriptedChatClient()
            .Reply(Delegate("diagnostics", "Check 1.1.1.1."))
            .Reply(ping)
            .Reply(ping)
            .Reply(ping)
            .Reply(Delegate("config", "Set the wallpaper to C:\\\\b.jpg."))
            .Reply(ToolStep("wallpaper_set", """{"path":"C:\\b.jpg"}"""))
            .Reply(Final("Wallpaper updated."))
            .Reply(Final("Fallback succeeded."));
        var (runner, session, _, prompter) = await Create(model);
        prompter.AskAsync(default!, default!, Ct).ReturnsForAnyArgs(ApprovalAnswer.Allow);

        var result = await runner.RunAsync("check then fallback", cancellationToken: Ct);

        Assert.Equal(RunStatus.Answered, result.Status);
        Assert.Equal("Fallback succeeded.", result.Text);
        Assert.False(session.Selection.MustFinish);
        Assert.Equal(["agent:diagnostics", "agent:config"], session.Selection.ActivationHistory);
    }
}
