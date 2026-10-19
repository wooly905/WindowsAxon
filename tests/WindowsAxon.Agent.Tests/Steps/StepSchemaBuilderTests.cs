using System.Text.Json;
using WindowsAxon.Agent.Settings;
using WindowsAxon.Agent.Steps;

namespace WindowsAxon.Agent.Tests.Steps;

public class StepSchemaBuilderTests
{
    private static readonly AIFunction Ping =
        AIFunctionFactory.Create((string host, int count = 4) => "",
                                 "net_ping",
                                 "Ping a host. Returns the time of each reply.");

    private static readonly AIFunction Info =
        AIFunctionFactory.Create(() => "", "system_get_info", "OS version, CPU, RAM.");

    [Fact]
    public void Per_tool_schema_has_one_branch_per_tool_and_a_final_branch()
    {
        var schema = StepSchemaBuilder.Build([Ping, Info], ArgsSchemaMode.PerTool);

        var branches = schema.GetProperty("anyOf").EnumerateArray().ToList();
        Assert.Equal(3, branches.Count);

        var ping = branches[0].GetProperty("properties");
        Assert.Equal("net_ping", ping.GetProperty("action").GetProperty("const").GetString());
        var args = ping.GetProperty("args");
        Assert.Equal("object", args.GetProperty("type").GetString());
        Assert.True(args.GetProperty("properties").TryGetProperty("host", out _));
        Assert.Contains("host", args.GetProperty("required").EnumerateArray().Select(e => e.GetString()));

        var final = branches[2].GetProperty("properties");
        var actions = final.GetProperty("action").GetProperty("enum").EnumerateArray().Select(e => e.GetString());
        Assert.Equal(["final_answer", "ask_user"], actions);
        Assert.Contains("answer", branches[2].GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Per_tool_schema_with_no_tools_allows_only_the_final_branch()
    {
        var schema = StepSchemaBuilder.Build([], ArgsSchemaMode.PerTool);

        Assert.Single(schema.GetProperty("anyOf").EnumerateArray());
    }

    [Fact]
    public void Free_schema_has_an_action_enum_and_a_free_args_object()
    {
        var schema = StepSchemaBuilder.Build([Ping, Info], ArgsSchemaMode.Free);

        var properties = schema.GetProperty("properties");
        var actions = properties.GetProperty("action").GetProperty("enum").EnumerateArray().Select(e => e.GetString());
        Assert.Equal(["net_ping", "system_get_info", "final_answer", "ask_user"], actions);
        Assert.Equal("object", properties.GetProperty("args").GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Array, schema.GetProperty("required").ValueKind);
    }

    [Fact]
    public void DescribeTools_writes_one_compact_line_per_tool()
    {
        var text = StepSchemaBuilder.DescribeTools([Ping, Info]);

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("- net_ping(host: string, count?: integer): Ping a host.", lines[0]);
        Assert.Equal("- system_get_info(): OS version, CPU, RAM.", lines[1]);
    }

    [Fact]
    public void DescribeTools_keeps_the_group_list_of_enable_tools()
    {
        var enableTools = AIFunctionFactory.Create((string group) => "",
                                                   "enable_tools",
                                                   "Enable one tool group for this request. Groups:\nfiles: folder owner (acl_get)");

        var text = StepSchemaBuilder.DescribeTools([enableTools]);

        Assert.Contains("Groups:\nfiles: folder owner (acl_get)", text);
    }
}
