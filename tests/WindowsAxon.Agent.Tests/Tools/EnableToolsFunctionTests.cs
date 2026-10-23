using WindowsAxon.Agent.Selection;
using WindowsAxon.Agent.Tools;

namespace WindowsAxon.Agent.Tests.Tools;

public class EnableToolsFunctionTests
{
    private static (EnableToolsFunction Function, ToolSelectionState State) Create()
    {
        var catalog = new ToolCatalog();
        catalog.Add(AIFunctionFactory.Create(() => "", "net_ping"), "network", RiskLevel.Read);
        catalog.Add(AIFunctionFactory.Create(() => "", "printer_list"), "printers", RiskLevel.Read);
        var state = new ToolSelectionState(catalog, 14);
        return (new EnableToolsFunction(catalog, state), state);
    }

    [Fact]
    public void Schema_has_an_enum_of_the_groups()
    {
        var (function, _) = Create();

        var groups = function.JsonSchema.GetProperty("properties").GetProperty("group").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString());

        Assert.Equal("enable_tools", function.Name);
        Assert.Equal(["network", "printers"], groups);
        Assert.Contains("network:", function.Description);
    }

    [Fact]
    public async Task Invoking_activates_the_group()
    {
        var (function, state) = Create();

        var result = await function.InvokeAsync(new AIFunctionArguments { ["group"] = "network" },
                                                TestContext.Current.CancellationToken);

        Assert.Equal("Enabled: network.", result?.ToString());
        Assert.True(state.IsActive("net_ping"));
    }

    [Fact]
    public async Task Unknown_group_returns_the_valid_names()
    {
        var (function, _) = Create();

        var result = await function.InvokeAsync(new AIFunctionArguments { ["group"] = "wifi" },
                                                TestContext.Current.CancellationToken);

        Assert.Equal("Unknown group 'wifi'. Groups: network, printers.", result?.ToString());
    }

    [Fact]
    public void Docs_group_description_mentions_code_samples()
    {
        var catalog = new ToolCatalog();
        catalog.Add(AIFunctionFactory.Create(() => "", "docs_code_search"), "docs", RiskLevel.Read, policyDriven: true);
        var function = new EnableToolsFunction(catalog, new ToolSelectionState(catalog, 14));

        Assert.Contains("docs: fetch a learn.microsoft.com article and search official Microsoft code samples",
                        function.Description);
    }
}
