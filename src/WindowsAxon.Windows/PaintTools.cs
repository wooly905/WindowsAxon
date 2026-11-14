using System.Diagnostics;
using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Tools;
using Microsoft.Extensions.AI;

namespace WindowsAxon.Windows;

/// <param name="skillsFolder">The personal skills folder, which paint_save must not write to.</param>
public sealed class PaintTools(DesktopUiTools ui, string? skillsFolder = null) : IApprovalRule
{
    public bool AppliesTo(string toolName) => toolName is "paint_new_canvas" or "paint_draw_shape" or "paint_draw_free" or "paint_add_text" or "paint_save";

    public ApprovalDecision Decide(FunctionCallContent call) => new(ApprovalOutcome.Ask,
                                                                    false,
                                                                    "Paint changes require approval for each action. Check the selected image before allowing.");

    private UiaElement Paint(string windowId)
    {
        var root = ui.Root(windowId);
        using var process = Process.GetProcessById(root.ProcessId);
        if (!process.ProcessName.Equals("mspaint", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Select a Paint window from ui_list_windows.");
        }

        return root;
    }

    private static UiaElement Control(UiaElement root,
                                             string name,
                                             string? automationId = null,
                                             ControlType? type = null)
    {
        var condition = Uia.Name(name);
        if (automationId is not null)
        {
            condition = Uia.Or(Uia.AutomationId(automationId), condition);
        }

        var typeCondition = type is null ? Uia.TrueCondition : Uia.Type(type.Value);
        var filter = type is null ? condition : Uia.And(condition, typeCondition);
        return root.FindFirst(TreeScope.Descendants, filter)
            ?? root.FindAll(TreeScope.Descendants, typeCondition)
            .FirstOrDefault(element => element.Name.TrimEnd(':')
                .Equals(name.TrimEnd(':'),
                    StringComparison.OrdinalIgnoreCase)
                || element.Name.StartsWith(name.TrimEnd(':') + ":", StringComparison.OrdinalIgnoreCase))
            ?? throw new NotSupportedException($"Paint control '{name}' is not available in '{root.Name}'. Available editable controls: {string.Join(", ", root.FindAll(TreeScope.Descendants, Uia.Type(ControlType.Edit))
            .Select(element => element.Name + " [" + element.AutomationId + "]"))}.");
    }

    private static void Activate(UiaElement element)
    {
        if (!element.TryInvoke() && !element.TrySelect() && !element.TryToggle())
        {
            throw new NotSupportedException("Paint control does not expose an activation pattern.");
        }
    }

    private static void SetValue(UiaElement element, string value)
    {
        var pattern = element.ValuePatternOrNull()
            ?? throw new NotSupportedException("Paint control does not support ValuePattern.");
        Uia.Call(() => pattern.SetValue(value));
    }

    private static ScreenRect Canvas(UiaElement root)
    {
        var canvas = Control(root, "Canvas", "image").BoundingRectangle;
        var window = root.BoundingRectangle;
        if (canvas.IsEmpty || canvas.Width <= 0 || canvas.Height <= 0 || !window.Contains(canvas))
        {
            throw new InvalidOperationException("The whole Paint canvas must be visible. Use Fit to window before drawing.");
        }

        return canvas;
    }

    private static void Style(UiaElement root, string? color, int? size)
    {
        if (color is not null)
        {
            var palette = Control(root, "Colors", type: ControlType.Group).FindAll(TreeScope.Descendants, Uia.Type(ControlType.ListItem));
            Activate(palette.FirstOrDefault(swatch => swatch.Name.Equals(color, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Paint has no palette color '{color}'. Use one of: {string.Join(", ", palette.Select(swatch => swatch.Name))}."));
        }

        if (size is not null)
        {
            if (size is < 1 or > 100)
            {
                throw new ArgumentException("Brush size must be 1-100.");
            }

            // Pencil and brushes show a size slider; shapes offer fixed outline sizes in a drop-down instead.
            if (root.FindFirst(TreeScope.Descendants, Uia.And(Uia.Name("Size"), Uia.Type(ControlType.Slider))) is not { } slider)
            {
                ShapeSize(root, size.Value);
                return;
            }

            var range = slider.RangeValuePatternOrNull()
                ?? throw new NotSupportedException("Brush size control does not support RangeValuePattern.");
            if (size < Uia.Call(() => range.CurrentMinimum) || size > Uia.Call(() => range.CurrentMaximum))
            {
                throw new ArgumentException("Requested size is outside Paint's supported range.");
            }

            Uia.Call(() => range.SetValue(size.Value));
        }
    }

    private static void ShapeSize(UiaElement root, int size)
    {
        var button = Control(root, "Size", type: ControlType.Button);
        if (!button.TryExpand())
        {
            Activate(button);
        }

        Thread.Sleep(300);
        var options = root.FindAll(TreeScope.Descendants, Uia.Type(ControlType.MenuItem))
            .Where(item => item.AutomationId.StartsWith("Size", StringComparison.Ordinal))
            .ToList();
        var option = options.FirstOrDefault(item => item.AutomationId == "Size" + size);
        if (option is null)
        {
            button.TryCollapse();
            throw new ArgumentException($"Shape outline size must be one of: {string.Join(", ", options.Select(item => item.AutomationId[4..]))}.");
        }

        Activate(option);
    }

    private static void WaitForTool(UiaElement root, string tool)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (Control(root, "Canvas", "image").Name.Contains(tool, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Thread.Sleep(50);
        }
        throw new InvalidOperationException($"Paint did not confirm the {tool} tool is active.");
    }

    [Tool("paint_new_canvas",
          "desktop_ui",
          "Create a new Paint image in a selected window. Preserves any unsaved-image prompt. Optional width and height use Paint's image Properties dialog. Requires English Paint controls when automation IDs are absent.",
          RiskLevel.Change)]
    public Task<string> New(string windowId,
                            int? width = null,
                            int? height = null,
                            CancellationToken cancellationToken = default)
    {
        return ApartmentThread.Run(ApartmentState.MTA, () =>
        {
            if (width is < 1 or > 10000 || height is < 1 or > 10000)
            {
                throw new ArgumentException("Dimensions must be 1-10000.");
            }

            var root = Paint(windowId);
            ui.Input(windowId,
                     cancellationToken,
                     input => input.Shortcut("Ctrl+N",
                                             cancellationToken));
            Thread.Sleep(300);
            if (root.FindAll(TreeScope.Descendants, Uia.Type(ControlType.Window))
                .Any(window => window.Name.Contains("save your work",
                                                    StringComparison.OrdinalIgnoreCase)
                    || window.Name.Contains("save changes",
                                            StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Paint is waiting for a save prompt. The image has not been replaced. Respond in Paint before continuing.");
            }

            if (width is not null || height is not null)
            {
                ui.Input(windowId,
                         cancellationToken,
                         input => input.Shortcut("Ctrl+E",
                                                 cancellationToken));
                Thread.Sleep(300);
                var dialog = root;
                if (width is not null)
                {
                    SetValue(Control(dialog, "Width", "WidthTextBox", ControlType.Edit), width.Value.ToString());
                }

                if (height is not null)
                {
                    SetValue(Control(dialog, "Height", "HeightTextBox", ControlType.Edit), height.Value.ToString());
                }

                Activate(Control(dialog, "OK", type: ControlType.Button));
            }
            Control(root, "Canvas", "image");
            return "New-image shortcut sent and canvas found. Check Paint for any save prompt before continuing.";
        });
    }

    [Tool("paint_draw_shape",
          "desktop_ui",
          "Draw line, rectangle, oval, circle, triangle, or arrow on a fully visible Paint canvas. Points use 0-1000 coordinates. Optional English palette color and outline size (Paint offers 1, 3, 5, or 8). Ctrl+Alt+Q stops input.",
          RiskLevel.Change)]
    public Task<string> Shape(string windowId,
                              string shape,
                              double startX,
                              double startY,
                              double endX,
                              double endY,
                              string? color = null,
                              int? size = null,
                              CancellationToken cancellationToken = default)
    {
        return ApartmentThread.Run(ApartmentState.MTA, () =>
        {
            var name = shape switch
            {
                "line" => "Line",
                "rectangle" => "Rectangle",
                "oval" or "circle" => "Oval",
                "triangle" => "Triangle",
                "arrow" => "Right arrow",
                _ => throw new ArgumentException("Unsupported shape.")
            };
            var root = Paint(windowId);
            Activate(Control(root, name, type: ControlType.Button));
            Style(root, color, size);
            Thread.Sleep(100);
            var canvas = Canvas(root);
            ui.Input(windowId,
                     cancellationToken,
                     input =>
                     {
                         if (shape == "circle")
                         {
                             input.Key(0x10, false, cancellationToken);
                         }

                         try
                         {
                             input.Drag(canvas, [new(startX, startY), new(endX, endY)], cancellationToken);
                         }
                         finally
                         {
                             if (shape == "circle")
                             {
                                 input.Key(0x10, true, CancellationToken.None);
                             }
                         }
                     });
            return "Shape input sent to Paint. The rendered pixels have not been verified.";
        });
    }

    [Tool("paint_draw_free",
          "desktop_ui",
          "Draw a freehand path with Paint's pencil. points is an array of [x,y] pairs on a 0-1000 canvas scale. Optional English palette color and size.",
          RiskLevel.Change)]
    public Task<string> Free(string windowId,
                             double[][] points,
                             string? color = null,
                             int? size = null,
                             CancellationToken cancellationToken = default)
    {
        return ApartmentThread.Run(ApartmentState.MTA, () =>
        {
            if (points.Length is < 2 or > 500 || points.Any(point => point.Length != 2))
            {
                throw new ArgumentException("Use 2-500 [x,y] pairs.");
            }

            var root = Paint(windowId);
            Activate(Control(root, "Pencil", "PencilTool", ControlType.Button));
            Style(root, color, size);
            WaitForTool(root, "Pencil");
            var canvas = Canvas(root);
            ui.Input(windowId,
                     cancellationToken,
                     input => input.Drag(canvas,
                                         points.Select(point => new ScreenPoint(point[0],
                                                                                point[1])).ToArray(),
                                         cancellationToken));
            return "Freehand input sent to Paint. The rendered pixels have not been verified.";
        });
    }

    [Tool("paint_add_text",
          "desktop_ui",
          "Add text in a Paint text box at a 0-1000 canvas position. The whole canvas must be visible.",
          RiskLevel.Change)]
    public Task<string> Text(string windowId,
                             string text,
                             double x,
                             double y,
                             CancellationToken cancellationToken = default)
    {
        return ApartmentThread.Run(ApartmentState.MTA, () =>
        {
            var root = Paint(windowId);
            Activate(Control(root, "Text", type: ControlType.Button));
            WaitForTool(root, "Text");
            var canvas = Canvas(root);
            ui.Input(windowId,
                     cancellationToken,
                     input =>
                     {
                         input.Drag(canvas,
                                    [new(x, y), new(Math.Min(x + 300, 1000), Math.Min(y + 100, 1000))],
                                    cancellationToken);
                         Thread.Sleep(150);
                         input.Text(text, cancellationToken);
                     });
            return "Text input sent to Paint. The rendered text has not been verified.";
        });
    }

    [Tool("paint_save",
          "desktop_ui",
          "Save a Paint image to a new absolute file path. Refuses overwriting an existing file. Uses Paint's Save As dialog and verifies that the resulting file exists.",
          RiskLevel.Change)]
    public Task<string> Save(string windowId, string path, CancellationToken cancellationToken)
    {
        return ApartmentThread.Run(ApartmentState.MTA, () =>
        {
            path = SkillsFolderGuard.Check(path, skillsFolder);
            if (File.Exists(path))
            {
                throw new ArgumentException("Choose a new file path; overwriting is not supported.");
            }

            if (!Directory.Exists(Path.GetDirectoryName(path)))
            {
                throw new ArgumentException("Destination folder does not exist.");
            }

            var format = Path.GetExtension(path)
                .ToLowerInvariant() switch
            {
                ".png" => "PNG",
                ".jpg" or ".jpeg" => "JPEG",
                ".bmp" => "24-bit Bitmap",
                ".gif" => "GIF",
                ".tif" or ".tiff" => "TIFF",
                _ => throw new ArgumentException("Use png, jpg, jpeg, bmp, gif, tif, or tiff."),
            };
            var root = Paint(windowId);
            Activate(Control(root,
                             "Pencil",
                             "PencilTool",
                             ControlType.Button));
            ui.Input(windowId,
                     cancellationToken,
                     input => input.Shortcut("Ctrl+Shift+S",
                                             cancellationToken));
            Thread.Sleep(500);
            var dialog = root.FindAll(TreeScope.Descendants, Uia.Type(ControlType.Window))
                .FirstOrDefault(window => window.FindFirst(TreeScope.Descendants,
                                                           Uia.AutomationId("FileTypeControlHost")) is not null)
                ?? throw new InvalidOperationException("Save As dialog not found. Check Paint for a prompt.");
            var combo = Control(dialog,
                                "Save as type:",
                                "FileTypeControlHost",
                                ControlType.ComboBox);
            // Native UIA exposes a collapsed combo box's list items only after it expands.
            combo.TryExpand();
            try
            {
                var option = combo.FindAll(TreeScope.Descendants, Uia.Type(ControlType.ListItem))
                    .FirstOrDefault(element => element.Name.StartsWith(format + " ",
                                                                       StringComparison.OrdinalIgnoreCase))
                    ?? throw new NotSupportedException("Paint does not offer the requested file format.");
                if (!option.TrySelect())
                {
                    throw new NotSupportedException("Paint file format option does not support selection.");
                }
            }
            finally
            {
                combo.TryCollapse();
            }

            Thread.Sleep(300);
            var fileName = Control(dialog,
                                   "File name:",
                                   "1001",
                                   ControlType.Edit);
            var fileNameValue = fileName.ValuePatternOrNull()
                ?? throw new NotSupportedException("Paint file name control does not support ValuePattern.");
            ui.DialogInput(windowId,
                           dialog,
                           cancellationToken,
                           input =>
                           {
                               fileName.SetFocus();
                               input.Shortcut("Ctrl+A",
                                              cancellationToken);
                               input.Text(path,
                                          cancellationToken);
                           });
            Thread.Sleep(200);
            if (!Uia.Call(() => fileNameValue.CurrentValue).Equals(path,
                                                                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Paint did not retain the requested output path.");
            }

            Activate(Control(dialog,
                             "Save",
                             "1",
                             ControlType.Button));
            for (var attempt = 0; attempt < 50; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(path)
                    && new FileInfo(path).Length > 0)
                {
                    if (!SignatureMatches(path))
                    {
                        throw new InvalidOperationException("The saved image format does not match its extension.");
                    }

                    return $"Saved image file and format verified: {path}.";
                }

                Thread.Sleep(100);
            }
            throw new InvalidOperationException("The saved file was not verified. Check Paint for a prompt.");
        });
    }

    internal static bool SignatureMatches(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[8];
        var count = stream.Read(header);
        header = header[..count];
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => header.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            ".jpg" or ".jpeg" => header.StartsWith(new byte[] { 255, 216 }),
            ".bmp" => header.StartsWith("BM"u8),
            ".gif" => header.StartsWith("GIF8"u8),
            ".tif" or ".tiff" => header.StartsWith(new byte[] { 73, 73, 42, 0 })
            || header.StartsWith(new byte[] {
    77,
    77,
    0,
    42
}),
            _ => false,
        };
    }
}
