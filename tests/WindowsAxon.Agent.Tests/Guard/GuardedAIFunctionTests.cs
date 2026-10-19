using WindowsAxon.Agent.Audit;
using WindowsAxon.Agent.Guard;
using WindowsAxon.Agent.Selection;
using WindowsAxon.Agent.Tests.Fakes;
using WindowsAxon.Agent.Tools;

namespace WindowsAxon.Agent.Tests.Guard;

public class GuardedAIFunctionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Harness
    {
        public ToolCatalog Catalog { get; } = new();

        public ToolSelectionState State
        {
            get;
        }

        public CallTracker Tracker { get; } = new();

        public ApprovalLedger Ledger { get; } = new() { CurrentRequest = "do it" };

        public ListAuditLog Audit { get; } = new();

        public int Runs
        {
            get; private set;
        }

        public Harness(Func<string, string> body,
                       RiskLevel risk = RiskLevel.Read,
                       TimeSpan? timeout = null,
                       bool active = true)
        {
            Catalog.Add(AIFunctionFactory.Create((string x) =>
            {
                Runs++;
                return body(x);
            },
                                                 "net_ping"),
                        "network",
                        risk,
                        timeout: timeout);
            State = new ToolSelectionState(Catalog, 14);
            if (active)
            {
                State.Activate("group:network", ["net_ping"]);
            }
        }

        public GuardedAIFunction Guard => new(Catalog.Find("net_ping")!, State, Tracker, Ledger, Audit);

        public async Task<string?> Call(string x = "a") =>
            (await Guard.InvokeAsync(new AIFunctionArguments { ["x"] = x }, Ct))?.ToString();
    }

    [Fact]
    public async Task Inactive_tool_is_refused_without_running()
    {
        var h = new Harness(_ => "ok", active: false);

        Assert.Equal(GuardedAIFunction.NotEnabledMessage, await h.Call());
        Assert.Equal(0, h.Runs);
        Assert.Equal("blocked", h.Audit.Entries.Last().Status);
        Assert.Equal(GuardedAIFunction.NotEnabledMessage, h.Audit.Entries.Last().Result);
    }

    [Fact]
    public async Task Active_tool_runs_and_is_audited()
    {
        var h = new Harness(x => "pong " + x);

        Assert.Equal("pong a", await h.Call());
        var entry = Assert.Single(h.Audit.Entries, e => e.Kind == "call");
        Assert.Equal(("call", "net_ping", "do it", true, "not needed"),
                     (entry.Kind, entry.Tool, entry.Request, entry.Success, entry.Decision));
        Assert.Contains("\"x\"", entry.Arguments);
        Assert.Equal("pong a", entry.Result);
        Assert.Equal("succeeded", entry.Status);
        Assert.True(entry.DurationMs >= 0);
        var start = Assert.Single(h.Audit.Entries, e => e.Kind == "call_start");
        Assert.Equal("started", start.Status);
        Assert.Equal(start.CallId, entry.CallId);
        Assert.False(string.IsNullOrEmpty(entry.CallId));
    }

    [Fact]
    public async Task Same_call_twice_in_a_row_is_not_run_again()
    {
        var h = new Harness(_ => "ok");

        await h.Call("a");
        Assert.Equal(GuardedAIFunction.RepeatMessage, await h.Call("a"));
        Assert.Equal(1, h.Runs);

        Assert.Equal("ok", await h.Call("b"));
        Assert.Equal(2, h.Runs);
        Assert.Equal(3, h.Audit.Entries.Count(e => e.Kind == "call"));
        Assert.Contains(h.Audit.Entries, e => e.Status == "blocked" && e.Result == GuardedAIFunction.RepeatMessage);
    }

    [Fact]
    public async Task Exception_becomes_a_short_error_observation()
    {
        var h = new Harness(_ => throw new InvalidOperationException("boom " + new string('z', 500)));

        var result = await h.Call();

        Assert.StartsWith("Error: boom", result);
        Assert.True(result!.Length <= "Error: ".Length + GuardedAIFunction.MaxErrorChars);
        var entry = Assert.Single(h.Audit.Entries, e => e.Kind == "call");
        Assert.False(entry.Success);
        Assert.Equal("failed", entry.Status);
        Assert.Equal("Error: boom " + new string('z', 500), entry.Result);
        Assert.Contains("System.InvalidOperationException: boom", entry.Error);
        Assert.Contains(nameof(Exception_becomes_a_short_error_observation), entry.Error);
    }

    [Fact]
    public async Task Two_errors_in_a_row_force_a_final_answer()
    {
        var h = new Harness(_ => throw new InvalidOperationException("boom"));

        await h.Call("a");
        Assert.False(h.State.MustFinish);
        await h.Call("b");

        Assert.True(h.State.MustFinish);
    }

    [Fact]
    public async Task Slow_tool_times_out()
    {
        var catalog = new ToolCatalog();
        catalog.Add(AIFunctionFactory.Create(async (CancellationToken token) =>
        {
            await Task.Delay(5000, token);
            return "late";
        },
                                             "net_ping"),
                    "network",
                    RiskLevel.Read,
                    timeout: TimeSpan.FromMilliseconds(50));
        var state = new ToolSelectionState(catalog, 14);
        state.Activate("group:network", ["net_ping"]);
        var audit = new ListAuditLog();
        var guard = new GuardedAIFunction(catalog.Find("net_ping")!,
                                          state,
                                          new CallTracker(),
                                          new ApprovalLedger(),
                                          audit);

        var result = await guard.InvokeAsync(new AIFunctionArguments(), Ct);

        Assert.Equal("Error: the tool did not finish in 0 seconds.", result?.ToString());
        var entry = Assert.Single(audit.Entries, e => e.Kind == "call");
        Assert.Equal("timed_out", entry.Status);
        Assert.NotNull(entry.Error);
    }

    [Fact]
    public async Task Stop_by_the_caller_is_not_swallowed()
    {
        var h = new Harness(_ => "ok");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await h.Guard.InvokeAsync(new AIFunctionArguments { ["x"] = "a" }, cts.Token));
    }

    [Fact]
    public async Task Stop_before_the_tool_starts_writes_no_audit_entry()
    {
        var h = new Harness(_ => "ok");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await h.Guard.InvokeAsync(new AIFunctionArguments { ["x"] = "a" }, cts.Token));

        Assert.Empty(h.Audit.Entries);
        Assert.Equal(0, h.Runs);
    }

    [Fact]
    public async Task Stop_while_the_tool_runs_is_audited_and_rethrown()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new ToolCatalog();
        catalog.Add(AIFunctionFactory.Create(async (CancellationToken token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "late";
        },
                                             "net_ping"),
                    "network",
                    RiskLevel.Change,
                    timeout: TimeSpan.FromMinutes(1));
        var state = new ToolSelectionState(catalog, 14);
        state.Activate("group:network", ["net_ping"]);
        var ledger = new ApprovalLedger { CurrentRequest = "do it" };
        var audit = new ListAuditLog();
        var guard = new GuardedAIFunction(catalog.Find("net_ping")!, state, new CallTracker(), ledger, audit);

        var call = guard.InvokeAsync(new AIFunctionArguments(), cts.Token).AsTask();
        await started.Task.WaitAsync(Ct);
        Assert.Equal("call_start", Assert.Single(audit.Entries).Kind);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);
        var entry = Assert.Single(audit.Entries, e => e.Kind == "call");
        Assert.Equal(("call", "net_ping", "do it", false, "missing"),
                     (entry.Kind, entry.Tool, entry.Request, entry.Success, entry.Decision));
        Assert.Equal("cancelled", entry.Status);
        Assert.NotNull(entry.Error);
    }

    [Fact]
    public async Task Long_output_is_cut_and_says_so()
    {
        var h = new Harness(_ => new string('y', 4000));

        var result = await h.Call();

        Assert.StartsWith(new string('y', GuardedAIFunction.MaxObservationChars), result);
        Assert.EndsWith("... (cut: showing 1500 of 4000 characters)", result);
        Assert.Equal(new string('y', 4000), h.Audit.Entries.Last().Result);
    }

    [Fact]
    public async Task Card_number_across_the_error_cut_is_masked()
    {
        var h = new Harness(_ => throw new InvalidOperationException(new string('e', 290) + " 4111111111111111"));

        var result = await h.Call();

        Assert.DoesNotContain("41111", result);
    }

    [Fact]
    public async Task Card_numbers_are_masked_in_the_observation()
    {
        var h = new Harness(_ => "copied 4111 1111 1111 1111");

        Assert.Equal($"copied {CardNumberMasker.Replacement}", await h.Call());
    }

    [Fact]
    public async Task Change_tool_without_an_approval_record_is_counted()
    {
        var h = new Harness(_ => "ok", RiskLevel.Change);

        await h.Call();

        Assert.Equal(1, h.Ledger.UnapprovedCalls);
    }

    [Fact]
    public async Task Third_identical_call_forces_a_final_answer()
    {
        var runs = 0;
        var catalog = new ToolCatalog();
        catalog.Add(AIFunctionFactory.Create(() =>
        {
            runs++;
            return "ok";
        },
                                             "net_ping"),
                    "network",
                    RiskLevel.Read);
        var state = new ToolSelectionState(catalog, 14);
        state.Activate("group:network", ["net_ping"]);
        var tools = catalog.BuildAgentTools(e => new GuardedAIFunction(e,
                                                                       state,
                                                                       new CallTracker(),
                                                                       new ApprovalLedger(),
                                                                       new ListAuditLog()));
        var inner = new ScriptedChatClient().ReplyCall("net_ping")
            .ReplyCall("net_ping")
            .ReplyCall("net_ping")
            .Reply("done");
        using var client = new FunctionInvokingChatClient(inner);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "go")],
                                                     new ChatOptions { Tools = tools },
                                                     Ct);

        Assert.Equal(1, runs);
        Assert.True(state.MustFinish);
        var last = response.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last();
        Assert.Equal("Stopped: the same action was repeated 3 times.", last.Result?.ToString());
    }

    [Fact]
    public async Task Third_identical_sub_agent_call_terminates_without_setting_must_finish()
    {
        var runs = 0;
        var catalog = new ToolCatalog();
        catalog.Add(AIFunctionFactory.Create(() =>
        {
            runs++;
            return "ok";
        },
                                             "net_ping"),
                    "network",
                    RiskLevel.Read);
        var state = new ToolSelectionState(catalog, 14);
        state.Activate("group:network", ["net_ping"]);
        var tools = catalog.BuildAgentTools(e => new GuardedAIFunction(e,
                                                                       state,
                                                                       new CallTracker(),
                                                                       new ApprovalLedger(),
                                                                       new ListAuditLog(),
                                                                       new WindowsAxon.Agent.SubAgents.SubAgentTrace("diagnostics")));
        var inner = new ScriptedChatClient().ReplyCall("net_ping")
            .ReplyCall("net_ping")
            .ReplyCall("net_ping")
            .Reply("never");
        using var client = new FunctionInvokingChatClient(inner);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "go")],
                                                     new ChatOptions { Tools = tools },
                                                     Ct);

        Assert.Equal(1, runs);
        Assert.Equal(3, inner.Calls.Count);
        Assert.False(state.MustFinish);
        var last = response.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last();
        Assert.Equal("Stopped: the same action was repeated 3 times.", last.Result?.ToString());
    }

    [Fact]
    public async Task Image_result_keeps_the_image_and_audits_only_the_text()
    {
        var catalog = new ToolCatalog();
        catalog.Add(AIFunctionFactory.Create(() => Task.FromResult<IEnumerable<AIContent>>(
                [new TextContent("Screenshot of Paint."), new DataContent(new byte[] { 1, 2, 3 }, "image/png")]),
                                             "ui_screenshot"),
                    "desktop_ui",
                    RiskLevel.Read);
        var state = new ToolSelectionState(catalog, 14);
        state.Activate("group:desktop_ui", ["ui_screenshot"]);
        var audit = new ListAuditLog();
        var guard = new GuardedAIFunction(catalog.Find("ui_screenshot")!,
                                          state,
                                          new CallTracker(),
                                          new ApprovalLedger(),
                                          audit);

        var result = Assert.IsAssignableFrom<IEnumerable<AIContent>>(await guard.InvokeAsync(new AIFunctionArguments(), Ct));

        Assert.Equal("Screenshot of Paint.", Assert.Single(result.OfType<TextContent>()).Text);
        Assert.Equal("image/png", Assert.Single(result.OfType<DataContent>()).MediaType);
        Assert.Equal("Screenshot of Paint.", Assert.Single(audit.Entries, e => e.Kind == "call").Result);
    }
}
