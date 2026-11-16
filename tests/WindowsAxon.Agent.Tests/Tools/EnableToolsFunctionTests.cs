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
    public void Extra_group_descriptions_describe_run_time_groups()
    {
        var catalog = new ToolCatalog();
        catalog.Add(AIFunctionFactory.Create(() => "", "web_search"), "web", RiskLevel.Read);
        catalog.Add(AIFunctionFactory.Create(() => "", "mcp_pulsar_get_product"), "mcp_pulsar", RiskLevel.Change, outbound: true);
        var function = new EnableToolsFunction(catalog,
                                               new ToolSelectionState(catalog, 14),
                                               new Dictionary<string, string> { ["mcp_pulsar"] = "product data" });

        Assert.Equal("Enable one tool group for this request.\n" +
                     "MCP servers. Use one first when the request matches its topic:\n" +
                     "mcp_pulsar: product data\n" +
                     "Built-in groups:\n" +
                     "web: search and read public web pages in a hidden browser; no window is shown",
                     function.Description);
    }
}
