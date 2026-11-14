using WindowsAxon.Agent.Tools;

namespace WindowsAxon.Agent.Tests.Tools;

public class ToolRegistrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class OutboundProvider
    {
        [Tool("new_network_action", "network", "Send a request.", outbound: true)]
        public string Send() => "sent";
    }

    [Fact]
    public void Outbound_metadata_requires_approval_without_a_policy_rule_or_known_name()
    {
        var catalog = new ToolCatalog();
        ToolRegistration.Add(catalog, new OutboundProvider());
        var entry = catalog.Find("new_network_action")!;
        var policy = new WindowsAxon.Agent.Approval.ApprovalPolicy(catalog, [], autoApprove: true);

        Assert.True(entry.Outbound);
        Assert.True(entry.NeedsApproval);
        Assert.IsType<ApprovalRequiredAIFunction>(Assert.Single(catalog.BuildAgentTools(e => e.Function)));
        Assert.Equal(WindowsAxon.Agent.Approval.ApprovalOutcome.Ask,
            policy.Decide(new FunctionCallContent("call", entry.Name)).Outcome);
    }

    private sealed class Provider : ICancelableToolProvider
    {
        public int Canceled
        {
            get; private set;
        }

        [Tool("z_find", "apps", "Find an app.", alwaysInclude: true)]
        internal string Find(string name) => $"found {name}";

        [Tool("a_launch", "apps", "Launch an app.", RiskLevel.Change, policyDriven: true, timeoutSeconds: 5)]
        public async Task<string> LaunchAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return "started";
        }

        [Tool("m_count", "utilities", "Count the apps.")]
        private static int Count() => 3;

        public string NotATool() => "";

        public void CancelPending() => Canceled++;
    }

    [Fact]
    public async Task Non_public_and_static_methods_are_registered_in_declaration_order()
    {
        var catalog = new ToolCatalog();

        ToolRegistration.Add(catalog, new Provider());

        Assert.Equal(["z_find", "a_launch", "m_count"], catalog.Entries.Select(e => e.Name));
        var find = catalog.Find("z_find")!;
        Assert.True(find.AlwaysInclude);
        Assert.Equal("Find an app.", find.Function.Description);
        Assert.Equal("found paint", (await find.Function.InvokeAsync(new AIFunctionArguments { ["name"] = "paint" }, Ct))?.ToString());
        Assert.Equal("3", (await catalog.Find("m_count")!.Function.InvokeAsync(cancellationToken: Ct))?.ToString());
        var launch = catalog.Find("a_launch")!;
        Assert.False(launch.AlwaysInclude);
        Assert.True(launch.NeedsApproval);
        Assert.Equal(TimeSpan.FromSeconds(5), launch.Timeout);
    }

    [Fact]
    public async Task Canceling_a_call_cancels_the_pending_work_of_the_provider()
    {
        var catalog = new ToolCatalog();
        var provider = new Provider();
        ToolRegistration.Add(catalog, provider);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var call = catalog.Find("a_launch")!.Function.InvokeAsync(cancellationToken: cancel.Token).AsTask();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(1, provider.Canceled);
    }
}
