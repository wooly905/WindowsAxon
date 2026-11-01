using WindowsAxon.Agent.Tools;

namespace WindowsAxon.Agent.Tests.Tools;

public class ToolCatalogTests
{
    private static AIFunction Fn(string name) => AIFunctionFactory.Create(() => name, name);

    [Fact]
    public void Change_danger_and_policy_tools_are_wrapped_for_approval()
    {
        var catalog = new ToolCatalog();
        catalog.Add(Fn("net_ping"), "network", RiskLevel.Read);
        catalog.Add(Fn("wallpaper_set"), "personalize", RiskLevel.Change);
        catalog.Add(Fn("fs_delete"), "files", RiskLevel.Danger);
        catalog.Add(Fn("app_launch"), "apps", RiskLevel.Read, policyDriven: true, alwaysInclude: true);

        var tools = catalog.BuildAgentTools(e => e.Function);

        Assert.IsNotType<ApprovalRequiredAIFunction>(tools.Single(t => t.Name == "net_ping"));
        Assert.IsType<ApprovalRequiredAIFunction>(tools.Single(t => t.Name == "wallpaper_set"));
        Assert.IsType<ApprovalRequiredAIFunction>(tools.Single(t => t.Name == "fs_delete"));
        Assert.IsType<ApprovalRequiredAIFunction>(tools.Single(t => t.Name == "app_launch"));
    }

    [Fact]
    public void Groups_and_base_tools_are_reported()
    {
        var catalog = new ToolCatalog();
        catalog.Add(Fn("app_find"), "apps", RiskLevel.Read, alwaysInclude: true);
        catalog.Add(Fn("app_list"), "apps", RiskLevel.Read);
        catalog.Add(Fn("net_ping"), "network", RiskLevel.Read);
        catalog.Add(Fn("net_dns_lookup"), "network", RiskLevel.Read);

        Assert.Equal(["apps", "network"], catalog.Groups);
        Assert.Equal(["app_find", "app_list"], catalog.ToolsInGroup("apps"));
        Assert.Equal(["net_ping", "net_dns_lookup"], catalog.ToolsInGroup("network"));
        Assert.True(catalog.IsBaseTool("app_find"));
        Assert.True(catalog.IsBaseTool("load_skill"));
        Assert.False(catalog.IsBaseTool("app_list"));
        Assert.False(catalog.IsBaseTool("net_ping"));
        Assert.Equal(TimeSpan.FromSeconds(30), catalog.Find("net_ping")!.Timeout);
    }

    [Fact]
    public void Missing_function_is_rejected_with_its_parameter_name()
    {
        var catalog = new ToolCatalog();

        var error = Assert.Throws<ArgumentNullException>(() => catalog.Add(null!, "network", RiskLevel.Read));

        Assert.Equal("function", error.ParamName);
        Assert.Empty(catalog.Entries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Missing_group_is_rejected_without_registering_the_tool(string? group)
    {
        var catalog = new ToolCatalog();

        var error = Assert.ThrowsAny<ArgumentException>(() => catalog.Add(Fn("net_ping"), group!, RiskLevel.Read));

        Assert.Equal("group", error.ParamName);
        Assert.Empty(catalog.Entries);
    }

    [Fact]
    public void Duplicate_tool_names_are_rejected()
    {
        var catalog = new ToolCatalog();
        catalog.Add(Fn("net_ping"), "network", RiskLevel.Read);

        Assert.Throws<InvalidOperationException>(() => catalog.Add(Fn("net_ping"), "network", RiskLevel.Read));
    }
}
