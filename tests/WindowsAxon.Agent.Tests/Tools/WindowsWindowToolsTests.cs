using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Tools;
using WindowsAxon.App;

namespace WindowsAxon.Agent.Tests.Tools;

public sealed class WindowsWindowToolsTests
{
    private static readonly DesktopWindow Paint = new(123, 42, 1, "mspaint", "Untitled - Paint");

    [Fact]
    public void List_filters_by_process_or_title_and_excludes_WindowsAxon()
    {
        var ownWindow = Paint with
        {
            ProcessId = Environment.ProcessId
        };
        var localized = Paint with
        {
            Title = "未命名 - 小畫家"
        };
        var tools = new WindowsWindowTools(() => [localized, ownWindow],
                                           _ =>
                                           {
                                           },
                                           _ => true,
                                           TimeSpan.Zero);

        Assert.Equal("mspaint", Assert.Single(tools.List("PAINT")).AppName);
        Assert.Equal("未命名 - 小畫家", Assert.Single(tools.List("小畫家")).Title);
        Assert.Empty(tools.List("not-running"));
    }

    [Fact]
    public void List_limits_results_and_gives_each_window_a_distinct_id()
    {
        var tools = new WindowsWindowTools(() => Enumerable.Repeat(Paint, 25).ToArray(),
                                           _ =>
                                           {
                                           },
                                           _ => true,
                                           TimeSpan.Zero);

        var windows = tools.List();

        Assert.Equal(20, windows.Count);
        Assert.Equal(20, windows.Select(window => window.Id).Distinct().Count());
    }

    [Fact]
    public async Task Close_rejects_arbitrary_ids_and_previous_list_ids()
    {
        var requested = false;
        var tools = new WindowsWindowTools(() => [Paint], _ => requested = true, _ => true, TimeSpan.Zero);
        var oldId = Assert.Single(tools.List()).Id;
        tools.List();

        await Assert.ThrowsAsync<ArgumentException>(() => tools.CloseAsync("123", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => tools.CloseAsync(oldId, TestContext.Current.CancellationToken));
        Assert.False(requested);
    }

    [Fact]
    public async Task Close_rechecks_the_target_after_approval()
    {
        var requested = false;
        var tools = new WindowsWindowTools(() => [Paint], _ => requested = true, _ => false, TimeSpan.Zero);
        var id = Assert.Single(tools.List()).Id;

        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.CloseAsync(id,
                                                                                   TestContext.Current.CancellationToken));

        Assert.False(requested);
    }

    [Fact]
    public async Task Close_reports_success_only_after_verification_and_removes_the_id()
    {
        DesktopWindow? target = null;
        var open = true;
        var tools = new WindowsWindowTools(() => [Paint],
                                           window =>
                                           {
                                               target = window;
                                               open = false;
                                           },
                                           _ => open,
                                           TimeSpan.Zero);
        var id = Assert.Single(tools.List()).Id;

        var result = await tools.CloseAsync(id, TestContext.Current.CancellationToken);

        Assert.Equal(Paint, target);
        Assert.Contains("closure was verified", result);
        await Assert.ThrowsAsync<ArgumentException>(() => tools.CloseAsync(id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Close_waits_for_the_application_to_handle_the_request()
    {
        var checks = 0;
        var tools = new WindowsWindowTools(() => [Paint],
                                           _ =>
                                           {
                                           },
                                           _ => ++checks < 3,
                                           TimeSpan.FromSeconds(1));
        var id = Assert.Single(tools.List()).Id;

        var result = await tools.CloseAsync(id, TestContext.Current.CancellationToken);

        Assert.Contains("closure was verified", result);
        Assert.Equal(3, checks);
    }

    [Fact]
    public async Task A_save_prompt_or_refused_close_is_not_reported_as_success()
    {
        var requested = false;
        var tools = new WindowsWindowTools(() => [Paint], _ => requested = true, _ => true, TimeSpan.Zero);
        var id = Assert.Single(tools.List()).Id;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => tools.CloseAsync(id,
                                                                                               TestContext.Current.CancellationToken));

        Assert.True(requested);
        Assert.Contains("still open", error.Message);
        Assert.Contains("save prompt", error.Message);
        Assert.Contains("Do not report that it is closed", error.Message);
    }

    [Fact]
    public async Task Windows_close_errors_are_preserved()
    {
        var tools = new WindowsWindowTools(() => [Paint],
                                           _ => throw new InvalidOperationException("Access denied."),
                                           _ => true,
                                           TimeSpan.Zero);
        var id = Assert.Single(tools.List()).Id;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => tools.CloseAsync(id,
                                                                                               TestContext.Current.CancellationToken));

        Assert.Equal("Access denied.", error.Message);
    }

    [Fact]
    public async Task A_verification_error_after_request_is_not_reported_as_closed()
    {
        var requested = false;
        var tools = new WindowsWindowTools(() => [Paint],
                                           _ => requested = true,
                                           _ => requested ? throw new InvalidOperationException("Could not verify the window.") : true,
                                           TimeSpan.Zero);
        var id = Assert.Single(tools.List()).Id;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => tools.CloseAsync(id,
                                                                                               TestContext.Current.CancellationToken));

        Assert.True(requested);
        Assert.Equal("Could not verify the window.", error.Message);
    }

    [Fact]
    public async Task Cancellation_before_execution_prevents_a_close_request()
    {
        var requested = false;
        var tools = new WindowsWindowTools(() => [Paint], _ => requested = true, _ => true, TimeSpan.Zero);
        var id = Assert.Single(tools.List()).Id;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.CloseAsync(id, cancellation.Token));

        Assert.False(requested);
    }

    [Fact]
    public async Task Cancellation_after_a_request_does_not_report_success()
    {
        using var cancellation = new CancellationTokenSource();
        var tools = new WindowsWindowTools(() => [Paint],
                                           _ => cancellation.Cancel(),
                                           _ => true,
                                           TimeSpan.FromSeconds(10));
        var id = Assert.Single(tools.List()).Id;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.CloseAsync(id, cancellation.Token));
    }

    [Fact]
    public void Every_close_requires_approval_with_the_target_title()
    {
        var tools = new WindowsWindowTools(() => [Paint],
                                           _ =>
                                           {
                                           },
                                           _ => true,
                                           TimeSpan.Zero);
        var catalog = new ToolCatalog();
        ToolRegistration.Add(catalog, tools);
        var id = Assert.Single(tools.List()).Id;
        var call = new FunctionCallContent("1",
                                           "ui_close_window",
                                           new Dictionary<string, object?> { ["windowId"] = id });
        var policy = new ApprovalPolicy(catalog, [tools]);
        policy.AllowForConversation(call.Name);

        var decision = policy.Decide(call);

        Assert.Equal(ApprovalOutcome.Ask, decision.Outcome);
        Assert.False(decision.AllowForConversation);
        Assert.Contains(Paint.Title, decision.Note);
        Assert.True(catalog.Find(call.Name)!.NeedsApproval);
        Assert.False(catalog.Find("ui_list_windows")!.NeedsApproval);
    }
}
