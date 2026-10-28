using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsAxon.Agent.Selection;
using Microsoft.Extensions.AI;

namespace WindowsAxon.Agent.Tools;

/// <summary>enable_tools: the model enables one tool group for this request.</summary>
public sealed class EnableToolsFunction : AIFunction
{
    private static readonly Dictionary<string, string> GroupDescriptions = new()
    {
        ["system"] = "system information, performance, disks, processes, services",
        ["files"] = "list, find, read, copy, move, delete files; folder owner and permissions (acl_get)",
        ["registry"] = "read and change HKCU registry values",
        ["printers"] = "printers, default printer, print queue",
        ["eventlog"] = "Windows event logs",
        ["personalize"] = "wallpaper and theme",
        ["devices"] = "volume, brightness, power plan",
        ["settings"] = "open Settings pages, Control Panel items, Disk Management (diskmgmt.msc), Device Manager, and management consoles via admin_tool_open for the user to see; cannot read values from them",
        ["network"] = "network configuration, ping, DNS, ports",
        ["apps"] = "list apps, refresh the app index, find and install apps with winget",
        ["browser"] = "show and control a web page in Chrome or Edge when the user asks to open a site or work in a page",
        ["web"] = "search and read public web pages in a hidden browser; no window is shown",
        ["desktop_ui"] = "control an open app or Settings window (ui_snapshot, ui_click, ui_type, ui_press_keys); draw in Paint. Use when the user asks to work in a window or when a PowerShell script fails or cannot do the task",
        ["scripts"] = "write and run a PowerShell or Python script (script_create, script_run) to read or change the system when no dedicated tool can do the task",
        ["utilities"] = "calculator, clipboard, undo changes",
        ["docs"] = "fetch a learn.microsoft.com article and search official Microsoft code samples",
    };

    private readonly ToolCatalog _catalog;
    private readonly ToolSelectionState _state;
    private readonly JsonElement _schema;

    public EnableToolsFunction(ToolCatalog catalog, ToolSelectionState state)
    {
        _catalog = catalog;
        _state = state;
        var groups = new JsonArray();
        foreach (var group in catalog.Groups)
        {
            groups.Add(group);
        }

        _schema = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["group"] = new JsonObject { ["type"] = "string", ["enum"] = groups } },
            ["required"] = new JsonArray("group"),
        });

        Description = "Enable one tool group for this request. Groups:\n" +
            string.Join("\n", catalog.Groups.Select(g => $"{g}: {GroupDescriptions.GetValueOrDefault(g, g)}"));
    }

    public override string Name => "enable_tools";

    public override string Description
    {
        get;
    }

    public override JsonElement JsonSchema => _schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments,
                                                          CancellationToken cancellationToken)
    {
        var group = arguments.TryGetValue("group", out var value) ? value?.ToString() : null;
        if (group is null || !_catalog.Groups.Contains(group))
        {
            return ValueTask.FromResult<object?>($"Unknown group '{group}'. Groups: {string.Join(", ", _catalog.Groups)}.");
        }

        return ValueTask.FromResult<object?>(_state.Activate($"group:{group}", _catalog.ToolsInGroup(group))
            .Describe());
    }
}
