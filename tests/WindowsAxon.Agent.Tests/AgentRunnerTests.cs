using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Settings;
using WindowsAxon.Agent.Tests.Fakes;
using NSubstitute;

namespace WindowsAxon.Agent.Tests;

public class AgentRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ToolStep(string action, string args) => $$"""{"thought":"t","action":"{{action}}","args":{{args}}}""";

    private static string Final(string answer, string action = "final_answer") => $$"""{"thought":"t","action":"{{action}}","answer":"{{answer}}"}""";

    private static async Task<(AgentRunner Runner,
                               WindowsAxonSession Session,
                               ListAuditLog Audit,
                               IApprovalPrompter Prompter)> Create(ScriptedChatClient model, ChatSettings? chat = null)
    {
        var deps = AgentFactoryTests.Dependencies(model, chat);
        var session = await new AgentFactory(deps).CreateSessionAsync(Ct);
        var prompter = Substitute.For<IApprovalPrompter>();
        var audit = (ListAuditLog)deps.Audit;
        return (new AgentRunner(session, prompter, audit), session, audit, prompter);
    }

    [Fact]
    public async Task Reasoning_only_native_reply_switches_to_structured_steps_for_the_conversation()
    {
        var model = new ScriptedChatClient()
            .ReplyCall("enable_tools", new Dictionary<string, object?> { ["group"] = "network" })
            .ReplyWith((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant,
                                                                 [new TextReasoningContent("I need to ping the host.")]))
            {
                FinishReason = ChatFinishReason.Stop
            })
            .Reply(ToolStep("net_ping", """{"host":"1.1.1.1"}"""))
            .Reply(Final("The host answers."))
            .Reply(ToolStep("enable_tools", """{"group":"network"}"""))
            .Reply(ToolStep("net_ping", """{"host":"8.8.8.8"}"""))
            .Reply(Final("The second host answers."));
        var (runner, _, audit, _) = await Create(model, new ChatSettings { StepMode = StepMode.Native });

        var first = await runner.RunAsync("is 1.1.1.1 up?", cancellationToken: Ct);
        var second = await runner.RunAsync("check 8.8.8.8 too", cancellationToken: Ct);

        Assert.Equal(RunStatus.Answered, first.Status);
        Assert.Equal("The host answers.", first.Text);
        Assert.Equal(RunStatus.Answered, second.Status);
        Assert.Equal("The second host answers.", second.Text);
        Assert.IsType<ChatResponseFormatJson>(model.Calls[2].Options!.ResponseFormat);
        Assert.IsType<ChatResponseFormatJson>(model.Calls[4].Options!.ResponseFormat);
        Assert.Contains(model.Calls[2].Messages, m => m.Text.Contains("Enabled: network."));
        var fallback = Assert.Single(audit.Entries, e => e.Kind == "model_fallback");
        Assert.Equal("structured", fallback.Status);
        Assert.Contains("I need to ping the host.", fallback.Result);
        Assert.DoesNotContain(audit.Entries, e => e.Kind == "response" && e.Status == "empty");
    }

    [Fact]
    public async Task Reasoning_only_fallback_preserves_change_tool_approval()
    {
        var model = new ScriptedChatClient()
            .ReplyWith((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant,
                                                                 [new TextReasoningContent("I need to set the wallpaper.")])))
            .Reply(ToolStep("enable_tools", """{"group":"personalize"}"""))
            .Reply(ToolStep("wallpaper_set", """{"path":"C:\\b.jpg"}"""))
            .Reply(Final("Done."));
        var (runner, _, audit, prompter) = await Create(model,
                                                      new ChatSettings
                                                      {
                                                          StepMode = StepMode.Native,
                                                          AutoApproveTools = false
                                                      });
        prompter.AskAsync(default!, default!, Ct).ReturnsForAnyArgs(ApprovalAnswer.Allow);

        var result = await runner.RunAsync("set wallpaper", cancellationToken: Ct);

        Assert.Equal(RunStatus.Answered, result.Status);
        Assert.Equal("Done.", result.Text);
        await prompter.ReceivedWithAnyArgs(1).AskAsync(default!, default!, Ct);
        Assert.Contains(audit.Entries, e => e is { Kind: "call", Tool: "wallpaper_set", Decision: "approved" });
    }

    [Fact]
    public async Task Native_tool_call_with_reasoning_is_executed_without_fallback()
    {
        var model = new ScriptedChatClient()
            .ReplyWith((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant,
                                                                 [
                                                                     new TextReasoningContent("Enable network tools."),
                                                                     new FunctionCallContent("enable", "enable_tools",
                                                                                             new Dictionary<string, object?> { ["group"] = "network" })
                                                                 ])))
            .Reply("Ready.");
        var (runner, _, audit, _) = await Create(model, new ChatSettings { StepMode = StepMode.Native });

        var result = await runner.RunAsync("prepare network tools", cancellationToken: Ct);

        Assert.Equal("Ready.", result.Text);
        Assert.Null(model.Calls[1].Options!.ResponseFormat);
        Assert.Contains(audit.Entries, e => e is { Kind: "call", Tool: "enable_tools", Success: true });
        Assert.DoesNotContain(audit.Entries, e => e.Kind == "model_fallback");
    }

    [Fact]
    public async Task Native_reply_with_reasoning_and_an_answer_does_not_switch_modes()
    {
        var model = new ScriptedChatClient()
            .ReplyWith((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant,
                                                                 [new TextReasoningContent("Greeting."), new TextContent("Hello.")])))
            .Reply("Hi again.");
        var (runner, _, audit, _) = await Create(model, new ChatSettings { StepMode = StepMode.Native });

        var first = await runner.RunAsync("hello", cancellationToken: Ct);
        var second = await runner.RunAsync("hello again", cancellationToken: Ct);

        Assert.Equal("Hello.", first.Text);
        Assert.Equal("Hi again.", second.Text);
        Assert.Null(model.Calls[1].Options!.ResponseFormat);
        Assert.DoesNotContain(audit.Entries, e => e.Kind == "model_fallback");
    }

    [Fact]
    public async Task Empty_native_answer_after_enabling_tools_continues_with_the_active_group()
    {
        var model = new ScriptedChatClient()
            .ReplyCall("enable_tools", new Dictionary<string, object?> { ["group"] = "network" })
            .Reply("")
            .ReplyCall("net_ping", new Dictionary<string, object?> { ["host"] = "1.1.1.1" })
            .Reply("The host answers.");
        var (runner, session, audit, _) = await Create(model, new ChatSettings { StepMode = StepMode.Native });

        var result = await runner.RunAsync("is 1.1.1.1 up?", cancellationToken: Ct);

        Assert.Equal(new RunResult(RunStatus.Answered, "The host answers.", 4), result);
        Assert.Contains("group:network", session.Selection.ActiveKeys);
        Assert.Contains(model.Calls[2].Options!.Tools!, t => t.Name == "net_ping");
        Assert.Contains(model.Calls[2].Messages.SelectMany(m => m.Contents),
                        c => c is FunctionResultContent { Result: "Enabled: network." });
        Assert.Contains(audit.Entries, e => e is { Kind: "response", Status: "empty", Success: false });
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \n\t")]
    public async Task Repeated_empty_native_answers_fail_with_a_visible_message(string text)
    {
        var model = new ScriptedChatClient().Reply(text).Reply(text);
        var (runner, _, audit, _) = await Create(model, new ChatSettings { StepMode = StepMode.Native });

        var result = await runner.RunAsync("hello", cancellationToken: Ct);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Contains("empty response", result.Text);
        Assert.False(runner.AwaitingReply);
        Assert.Equal(2, model.Calls.Count);
        Assert.Equal(2, audit.Entries.Count(e => e.Kind == "response" && e.Status == "empty"));
    }

    [Fact]
    public async Task Empty_answer_recovery_still_requires_approval_for_change_tools()
    {
        var model = new ScriptedChatClient()
            .ReplyCall("enable_tools", new Dictionary<string, object?> { ["group"] = "personalize" })
            .Reply("")
            .ReplyCall("wallpaper_set", new Dictionary<string, object?> { ["path"] = "C:\\b.jpg" })
            .Reply("Done.");
        var (runner, _, audit, prompter) = await Create(model,
                                                      new ChatSettings
                                                      {
                                                          StepMode = StepMode.Native,
                                                          AutoApproveTools = false
                                                      });
        prompter.AskAsync(default!, default!, Ct).ReturnsForAnyArgs(ApprovalAnswer.Allow);

        var result = await runner.RunAsync("set wallpaper", cancellationToken: Ct);

        Assert.Equal(RunStatus.Answered, result.Status);
        Assert.Equal("Done.", result.Text);
        await prompter.ReceivedWithAnyArgs(1).AskAsync(default!, default!, Ct);
        Assert.Contains(audit.Entries, e => e is { Kind: "call", Tool: "wallpaper_set", Decision: "approved" });
    }

    [Fact]
    public async Task Enables_a_group_runs_a_read_tool_and_answers()
    {
        var model = new ScriptedChatClient()
            .Reply(ToolStep("enable_tools", """{"group":"network"}"""))
            .Reply(ToolStep("net_ping", """{"host":"1.1.1.1"}"""))
            .Reply(Final("The host answers."));
        var (runner, session, audit, _) = await Create(model);

        var result = await runner.RunAsync("is 1.1.1.1 up?", cancellationToken: Ct);

        Assert.Equal(new RunResult(RunStatus.Answered, "The host answers.", 3), result);
        Assert.Contains("<observation tool=\"net_ping\">reply from 1.1.1.1</observation>",
                        model.Calls[2].Messages.Select(m => m.Text));
        Assert.Contains(audit.Entries, e => e is { Kind: "call", Tool: "net_ping", Success: true });
        var enabled = Assert.Single(audit.Entries, e => e.Kind == "call" && e.Tool == "enable_tools");
        Assert.Contains("network", enabled.Arguments);
        Assert.False(string.IsNullOrWhiteSpace(enabled.Result));
        Assert.Contains(audit.Entries, e => e.Kind == "call_start" && e.CallId == enabled.CallId);
        Assert.Equal(0, session.Ledger.UnapprovedCalls);
    }

    [Fact]
    public async Task Change_tool_runs_after_the_user_allows_it()
    {
        var model = new ScriptedChatClient()
            .Reply(ToolStep("enable_tools", """{"group":"personalize"}"""))
            .Reply(ToolStep("wallpaper_set", """{"path":"C:\\b.jpg"}"""))
            .Reply(Final("Done."));
        var (runner, session, audit, prompter) = await Create(model);
        prompter.AskAsync(Arg.Any<FunctionCallContent>(), Arg.Any<ApprovalDecision>(), Arg.Any<CancellationToken>())
            .Returns(ApprovalAnswer.Allow);

        var result = await runner.RunAsync("set wallpaper", cancellationToken: Ct);

        Assert.Equal("Done.", result.Text);
        Assert.Contains(audit.Entries,
                        e => e is { Kind: "approval", Tool: "wallpaper_set", Decision: "approved", Risk: "Change" });
        Assert.Contains(audit.Entries, e => e is { Kind: "call", Tool: "wallpaper_set", Decision: "approved" });
        var approval = Assert.Single(audit.Entries, e => e.Kind == "approval" && e.Tool == "wallpaper_set");
        var call = Assert.Single(audit.Entries, e => e.Kind == "call" && e.Tool == "wallpaper_set");
        Assert.Equal(approval.CallId, call.CallId);
        Assert.False(string.IsNullOrEmpty(call.CallId));
        Assert.Equal(0, session.Ledger.UnapprovedCalls);
    }

    [Fact]
    public async Task Denied_tool_does_not_run_and_the_model_continues()
    {
        var model = new ScriptedChatClient()
            .Reply(ToolStep("enable_tools", """{"group":"personalize"}"""))
            .Reply(ToolStep("wallpaper_set", """{"path":"C:\\b.jpg"}"""))
            .Reply(Final("OK, I did not change it."));
        var (runner, _, audit, prompter) = await Create(model);
        prompter.AskAsync(default!, default!, Ct).ReturnsForAnyArgs(ApprovalAnswer.Deny);

        var result = await runner.RunAsync("set wallpaper", cancellationToken: Ct);

        Assert.Equal("OK, I did not change it.", result.Text);
        Assert.DoesNotContain(audit.Entries, e => e is { Kind: "call", Tool: "wallpaper_set" });
        Assert.Contains(audit.Entries, e => e is { Kind: "approval", Decision: "denied" });
    }

    [Fact]
    public async Task Allow_for_conversation_skips_the_next_card()
    {
        var model = new ScriptedChatClient()
            .Reply(ToolStep("enable_tools", """{"group":"personalize"}"""))
            .Reply(ToolStep("wallpaper_set", """{"path":"C:\\a.jpg"}"""))
            .Reply(ToolStep("wallpaper_set", """{"path":"C:\\b.jpg"}"""))
            .Reply(Final("Done."));
        var (runner, _, _, prompter) = await Create(model);
        prompter.AskAsync(default!, default!, Ct).ReturnsForAnyArgs(ApprovalAnswer.AllowForConversation);

        await runner.RunAsync("set two wallpapers", cancellationToken: Ct);

        await prompter.ReceivedWithAnyArgs(1).AskAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Reply_to_ask_user_continues_the_same_request()
    {
        var model = new ScriptedChatClient()
            .Reply(ToolStep("enable_tools", """{"group":"network"}"""))
            .Reply(Final("Which host?", "ask_user"))
            .Reply(ToolStep("net_ping", """{"host":"8.8.8.8"}"""))
            .Reply(Final("8.8.8.8 answers."));
        var (runner, session, _, _) = await Create(model);

        var first = await runner.RunAsync("ping something", cancellationToken: Ct);
        Assert.Equal(RunStatus.AskedUser, first.Status);
        Assert.True(runner.AwaitingReply);

        var second = await runner.RunAsync("8.8.8.8", cancellationToken: Ct);

        Assert.Equal(new RunResult(RunStatus.Answered, "8.8.8.8 answers.", 4), second);
        Assert.Contains("group:network", session.Selection.ActiveKeys);
        Assert.Contains(model.Calls[2].Messages, m => m.Text == "ping something");
    }

    [Fact]
    public async Task New_request_button_resets_the_request()
    {
        var model = new ScriptedChatClient()
            .Reply(ToolStep("enable_tools", """{"group":"network"}"""))
            .Reply(Final("Which host?", "ask_user"))
            .Reply(Final("Dark mode is on."));
        var (runner, session, _, _) = await Create(model);
        await runner.RunAsync("ping something", cancellationToken: Ct);

        var result = await runner.RunAsync("set dark mode", forceNewRequest: true, cancellationToken: Ct);

        Assert.Equal(1, result.Steps);
        Assert.Empty(session.Selection.ActiveKeys);
    }

    [Fact]
    public async Task Stop_returns_cancelled_and_the_next_message_is_a_new_request()
    {
        var model = new ScriptedChatClient().Reply(Final("never"));
        var (runner, _, _, _) = await Create(model);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await runner.RunAsync("hi", cancellationToken: cts.Token);

        Assert.Equal(RunStatus.Cancelled, result.Status);
        Assert.False(runner.AwaitingReply);
    }

    [Fact]
    public async Task Model_failure_is_reported_without_an_exception()
    {
        var model = new ScriptedChatClient().ReplyWith((_, _) => throw new HttpRequestException("503 Service Unavailable"));
        var (runner, _, _, _) = await Create(model);

        var result = await runner.RunAsync("hi", cancellationToken: Ct);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.StartsWith("The model service is busy or not available.", result.Text);
    }

    [Fact]
    public async Task Stop_while_the_card_is_open_leaves_the_conversation_usable()
    {
        var model = new ScriptedChatClient()
            .Reply(ToolStep("enable_tools", """{"group":"personalize"}"""))
            .Reply(ToolStep("wallpaper_set", """{"path":"C:\\b.jpg"}"""))
            .Reply(Final("Hello."));
        var (runner, _, audit, prompter) = await Create(model);
        using var cts = new CancellationTokenSource();
        prompter.AskAsync(default!, default!, Ct).ReturnsForAnyArgs<Task<ApprovalAnswer>>(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        var stopped = await runner.RunAsync("set wallpaper", cancellationToken: cts.Token);
        var next = await runner.RunAsync("say hello", cancellationToken: Ct);

        Assert.Equal(RunStatus.Cancelled, stopped.Status);
        Assert.Equal(new RunResult(RunStatus.Answered, "Hello.", 1), next);
        Assert.DoesNotContain(audit.Entries, e => e is { Kind: "call", Tool: "wallpaper_set" });
    }

    [Fact]
    public async Task Stop_after_approval_leaves_the_conversation_usable()
    {
        using var cts = new CancellationTokenSource();
        var model = new ScriptedChatClient()
            .Reply(ToolStep("enable_tools", """{"group":"personalize"}"""))
            .Reply(ToolStep("wallpaper_set", """{"path":"C:\\b.jpg"}"""))
            .ReplyWith((_, _) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            })
            .Reply(Final("Hello."));
        var (runner, _, _, prompter) = await Create(model);
        prompter.AskAsync(default!, default!, Ct).ReturnsForAnyArgs(ApprovalAnswer.Allow);

        var stopped = await runner.RunAsync("set wallpaper", cancellationToken: cts.Token);
        var next = await runner.RunAsync("say hello", cancellationToken: Ct);

        Assert.Equal(RunStatus.Cancelled, stopped.Status);
        Assert.Equal(new RunResult(RunStatus.Answered, "Hello.", 1), next);
    }

    [Fact]
    public async Task Repeated_identical_calls_end_with_a_forced_final_answer()
    {
        var ping = ToolStep("net_ping", """{"host":"1.1.1.1"}""");
        var model = new ScriptedChatClient()
            .Reply(ToolStep("enable_tools", """{"group":"network"}"""))
            .Reply(ping)
            .Reply(ping)
            .Reply(ping)
            .Reply(Final("The host answers."));
        var (runner, _, audit, _) = await Create(model);

        var result = await runner.RunAsync("is 1.1.1.1 up?", cancellationToken: Ct);

        Assert.Equal(RunStatus.Answered, result.Status);
        Assert.Equal("The host answers.", result.Text);
        Assert.DoesNotContain(audit.Entries, e => e is { Kind: "response", Status: "empty" });
    }

    [Fact]
    public async Task Model_timeout_is_reported_as_failed()
    {
        var model = new ScriptedChatClient().ReplyWith((_, _) => throw new TaskCanceledException("The operation was cancelled because it exceeded the configured timeout."));
        var (runner, _, _, _) = await Create(model);

        var result = await runner.RunAsync("hi", cancellationToken: Ct);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.StartsWith("The model service is busy or not available.", result.Text);
    }
}
