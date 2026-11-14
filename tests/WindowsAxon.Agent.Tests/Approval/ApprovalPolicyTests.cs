using NSubstitute;
using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Tools;

namespace WindowsAxon.Agent.Tests.Approval;

public class ApprovalPolicyTests
{
    [Fact]
    public void Automatic_read_approval_does_not_run_the_rule_decision()
    {
        var catalog = new ToolCatalog();
        catalog.Add(AIFunctionFactory.Create(() => "", "read"), "local", RiskLevel.Read);
        var rule = Substitute.For<IApprovalRule>();
        rule.AppliesTo("read").Returns(true);
        var policy = new ApprovalPolicy(catalog, [rule], autoApprove: true);

        Assert.Equal(ApprovalOutcome.AutoApprove, policy.Decide(new FunctionCallContent("id", "read")).Outcome);
        rule.DidNotReceiveWithAnyArgs().Decide(default!);
        rule.Received(1).AlwaysAsks(Arg.Any<FunctionCallContent>());
    }

    private static ApprovalPolicy Policy(bool remote = false, bool autoApprove = false)
    {
        var catalog = new ToolCatalog();
        catalog.Add(AIFunctionFactory.Create(() => "", "wallpaper_set"), "personalize", RiskLevel.Change);
        catalog.Add(AIFunctionFactory.Create(() => "", "fs_delete"), "files", RiskLevel.Danger);
        catalog.Add(AIFunctionFactory.Create(() => "", "fs_read_text"), "files", RiskLevel.Read, policyDriven: true);
        return new ApprovalPolicy(catalog, [new SensitiveReadRule(remote, "api.example.com")], autoApprove);
    }

    private static FunctionCallContent Call(string name) => new("c1", name);

    [Theory]
    [InlineData("wallpaper_set")]
    [InlineData("fs_delete")]
    [InlineData("fs_read_text")]
    public void Auto_approve_cannot_bypass_sensitive_actions(string tool)
    {
        Assert.Equal(ApprovalOutcome.Ask, Policy(remote: true, autoApprove: true).Decide(Call(tool)).Outcome);
    }

    [Fact]
    public void Auto_approve_still_asks_for_an_unknown_tool()
    {
        Assert.Equal(ApprovalOutcome.Ask, Policy(autoApprove: true).Decide(Call("mystery")).Outcome);
    }

    [Fact]
    public void Auto_approve_is_the_default_setting()
    {
        Assert.True(new WindowsAxon.Agent.Settings.ChatSettings().AutoApproveTools);
    }

    [Fact]
    public void Change_tool_asks_without_conversation_grant()
    {
        var decision = Policy().Decide(Call("wallpaper_set"));

        Assert.Equal(new ApprovalDecision(ApprovalOutcome.Ask, false, null), decision);
    }

    [Fact]
    public void Change_tool_cannot_inherit_a_conversation_grant()
    {
        var policy = Policy();
        policy.AllowForConversation(Call("wallpaper_set"));

        Assert.Equal(ApprovalOutcome.Ask, policy.Decide(Call("wallpaper_set")).Outcome);

        policy.Reset();
        Assert.Equal(ApprovalOutcome.Ask, policy.Decide(Call("wallpaper_set")).Outcome);
    }

    [Fact]
    public void Danger_tool_asks_every_time()
    {
        var policy = Policy();
        policy.AllowForConversation(Call("fs_delete"));

        Assert.Equal(new ApprovalDecision(ApprovalOutcome.Ask, false, null), policy.Decide(Call("fs_delete")));
    }

    [Fact]
    public void Unknown_tool_asks_without_allow_always()
    {
        Assert.Equal(new ApprovalDecision(ApprovalOutcome.Ask, false, null), Policy().Decide(Call("mystery")));
    }

    [Fact]
    public void Sensitive_read_runs_without_approval_on_a_local_endpoint()
    {
        Assert.Equal(ApprovalOutcome.AutoApprove, Policy(remote: false).Decide(Call("fs_read_text")).Outcome);
    }

    [Fact]
    public void Sensitive_read_asks_on_a_remote_endpoint_and_names_the_host()
    {
        var policy = Policy(remote: true);

        var decision = policy.Decide(Call("fs_read_text"));

        Assert.Equal(ApprovalOutcome.Ask, decision.Outcome);
        Assert.True(decision.AllowForConversation);
        Assert.Equal("The content will be sent to api.example.com.", decision.Note);

        policy.AllowForConversation(Call("fs_read_text"));
        Assert.Equal(ApprovalOutcome.AutoApprove, policy.Decide(Call("fs_read_text")).Outcome);
    }

    [Fact]
    public void Sensitive_read_with_arguments_hides_the_conversation_option()
    {
        var call = new FunctionCallContent("1", "fs_read_text", new Dictionary<string, object?> { ["path"] = "a.txt" });

        var decision = Policy(remote: true).Decide(call);

        Assert.Equal(ApprovalOutcome.Ask, decision.Outcome);
        Assert.False(decision.AllowForConversation);
    }
}
