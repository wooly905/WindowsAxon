using WindowsAxon.Agent.Tools;

namespace WindowsAxon.Agent.SubAgents;

/// <summary>One sub-agent: the tools it owns and the rules added to its prompt.</summary>
public sealed record SubAgentDefinition(string Name,
                                        string Description,
                                        IReadOnlyList<string> Groups,
                                        IReadOnlyList<string> Rules,
                                        IReadOnlyList<string>? ExtraTools = null,
                                        IReadOnlyList<string>? ExcludedTools = null)
{
    public bool Includes(ToolEntry entry) =>
        (Groups.Contains(entry.Group) && ExcludedTools?.Contains(entry.Name) != true)
        || ExtraTools?.Contains(entry.Name) == true;
}

/// <summary>The sub-agents of ToolSelection = SubAgents. The main agent keeps the groups in MainGroups.</summary>
public static class SubAgentCatalog
{
    public static readonly IReadOnlySet<string> MainGroups = new HashSet<string>
    {
        ToolCatalog.BaseGroup,
        "utilities",
        "docs"
    };

    private static readonly string[] PaintTools =
    [
        "paint_new_canvas",
        "paint_draw_shape",
        "paint_draw_free",
        "paint_add_text",
        "paint_save"
    ];

    private const string SnapshotRule = "To act inside a window or web page, take a snapshot first, then use the ref numbers.";
    private const string NoSendRule = "Do not click send, buy, delete, or similar buttons unless the task asks for it.";

    public static readonly IReadOnlyList<SubAgentDefinition> All =
    [
        new("diagnostics",
            "system information, performance, disks, processes, services; network configuration, ping, DNS, ports; Windows event logs",
            ["system", "network", "eventlog"],
            []),
        new("files",
            "list, find, read, copy, move, delete files and folders; folder permissions",
            ["files"],
            []),
        new("config",
            "wallpaper and theme; volume, brightness, power plan; read and change HKCU registry values",
            ["personalize", "devices", "registry"],
            []),
        new("printers",
            "printers, default printer, print queue, test page",
            ["printers"],
            []),
        new("apps",
            "list apps, find and install apps with winget; open Settings pages, Control Panel items, and management consoles such as Disk Management or Device Manager for the user to see",
            ["apps", "settings"],
            [
                "For Windows management consoles, use admin_tool_open. Disk Management is diskmgmt.msc; Device Manager is devmgmt.msc.",
                "Settings pages and consoles are opened for the user to see; you cannot read values from them.",
            ]),
        new("browser",
            "control a web page in Chrome or Edge: open a site, read, find, click, type",
            ["browser"],
            [
                "To open a web site, even in a named browser such as Chrome or Edge, use browser_open.",
                SnapshotRule,
                NoSendRule,
            ]),
        new("desktop",
            "control an app window that is already open: read its controls and text, click, type, press keys, take a screenshot. Last resort",
            ["desktop_ui"],
            [
                "Find the window with ui_list_windows first.",
                SnapshotRule,
                NoSendRule,
            ],
            ExtraTools: ["ui_list_windows"],
            ExcludedTools: PaintTools),
        new("paint",
            "draw shapes, freehand paths, and text in a Paint window that is already open, and save the image",
            [],
            [
                "Find the Paint window with ui_list_windows first. If Paint is not open, say so.",
                "Use ui_screenshot to check the drawing when needed.",
            ],
            ExtraTools: [.. PaintTools, "ui_list_windows", "ui_focus_window", "ui_screenshot"]),
        new("scripts",
            "write and run a PowerShell or Python script when no other agent can do the task",
            ["scripts"],
            [
                "Create the script with script_create, then run it with script_run.",
                "A script exit code of 0 confirms only script completion, not that an application opened.",
            ]),
    ];

    public static SubAgentDefinition? Find(string name) => All.FirstOrDefault(a => a.Name == name);
}
