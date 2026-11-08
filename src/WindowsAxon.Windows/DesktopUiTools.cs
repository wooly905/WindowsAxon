using System.Diagnostics;
using Microsoft.Extensions.AI;
using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Tools;

namespace WindowsAxon.Windows;

public sealed class DesktopUiTools(Func<string, nint> resolveWindow, Action emergencyStop, Action<bool> inputActive) : IApprovalRule
{
    private readonly Dictionary<string, (string WindowId, UiaElement Element)> _refs = [];

    internal UiaElement Root(string windowId)
    {
        var root = Uia.FromHandle(resolveWindow(windowId));
        using var process = Process.GetProcessById(root.ProcessId);
        UiSafety.CheckProcess(process.ProcessName);
        return root;
    }

    [Tool("ui_focus_window",
          "desktop_ui",
          "Bring a window ID from ui_list_windows to the front. Blocks security, credential, remote-desktop, password-manager, and WindowsAxon windows.")]
    public Task<string> Focus(string windowId)
    {
        return ApartmentThread.Run(ApartmentState.MTA, () =>
        {
            var root = Root(windowId);
            var handle = resolveWindow(windowId);
            DesktopInput.ShowWindow(handle, 9);
            DesktopInput.SetForegroundWindow(handle);

            if (DesktopInput.GetForegroundWindow() != handle)
            {
                root.SetFocus();
            }

            if (DesktopInput.GetForegroundWindow() != handle)
            {
                throw new InvalidOperationException("Windows did not allow foreground focus.");
            }

            return "Foreground window verified.";
        });
    }

    [Tool("ui_snapshot",
          "desktop_ui",
          "Read up to 50 visible interactive controls of a listed window, with fresh refs, control types, names and automation IDs. Does not read password values. Remote endpoints require approval to receive content.",
          policyDriven: true)]
    public Task<object> Snapshot(string windowId)
    {
        return ApartmentThread.Run<object>(ApartmentState.MTA, () =>
        {
            _refs.Clear();
            var root = Root(windowId);
            var all = root.FindAll(TreeScope.Descendants, Uia.TrueCondition);
            List<object> result = [];

            foreach (var element in all)
            {
                var type = element.ControlType;
                if (element.IsOffscreen || type == ControlType.Text || type == ControlType.Pane)
                {
                    continue;
                }

                string id = Guid.NewGuid().ToString("N")[..8];
                _refs.Add(id, (windowId, element));
                result.Add(new
                {
                    Ref = id,
                    Type = element.ControlTypeName,
                    Name = element.Name,
                    AutomationId = element.AutomationId,
                    IsPassword = element.IsPassword
                });

                if (result.Count == 50)
                {
                    break;
                }
            }
            return new
            {
                Title = root.Name,
                Elements = result
            };
        });
    }

    private (string WindowId, UiaElement Element) Element(string reference)
    {
        if (!_refs.TryGetValue(reference, out var target))
        {
            throw new ArgumentException("Take a new ui_snapshot; the ref is expired or unknown.");
        }

        var root = Root(target.WindowId);
        if (target.Element.ProcessId != root.ProcessId || target.Element.IsOffscreen)
        {
            throw new ArgumentException("The ref is no longer visible. Take a new ui_snapshot.");
        }

        return target;
    }

    [Tool("ui_click",
          "desktop_ui",
          "Invoke, toggle, select, or click a ref from the last ui_snapshot. Every call requires approval, including send, buy, delete, and submit.",
          RiskLevel.Danger,
          policyDriven: true)]
    public Task<string> Click(string reference, CancellationToken cancellationToken)
    {
        return ApartmentThread.Run(ApartmentState.MTA, () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Element(reference);
            UiSafety.CheckElement(target.Element);
            if (!target.Element.TryInvoke() && !target.Element.TryToggle() && !target.Element.TrySelect())
            {
                Input(target.WindowId,
                      cancellationToken,
                      input => input.Drag(target.Element.BoundingRectangle,
                                          [new(500,
                                               500), new(500,
                                                         500)],
                                          cancellationToken));
            }

            return "Selected control activated. Take a new snapshot to verify the result.";
        });
    }

    [Tool("ui_type",
          "desktop_ui",
          "Replace text in an editable control by ref. Refuses password, verification-code and payment controls. Uses ValuePattern where available.",
          RiskLevel.Change,
          policyDriven: true)]
    public Task<string> Type(string reference, string text, CancellationToken cancellationToken)
    {
        return ApartmentThread.Run(ApartmentState.MTA, () =>
        {
            var target = Element(reference);
            UiSafety.CheckElement(target.Element);
            cancellationToken.ThrowIfCancellationRequested();
            if (target.Element.ValuePatternOrNull() is { } pattern)
            {
                if (Uia.Call(() => pattern.CurrentIsReadOnly))
                {
                    throw new ArgumentException("Control is read-only.");
                }

                Uia.Call(() => pattern.SetValue(text));
                if (Uia.Call(() => pattern.CurrentValue) != text)
                {
                    throw new InvalidOperationException("Text could not be verified.");
                }

                return "Control text verified.";
            }
            if (target.Element.ControlType != ControlType.Edit)
            {
                throw new ArgumentException("Target is not an editable text control.");
            }

            Input(target.WindowId,
                  cancellationToken,
                  input =>
                  {
                      target.Element.SetFocus();
                      input.Shortcut("Ctrl+A", cancellationToken);
                      input.Text(text, cancellationToken);
                  });
            return "Text input sent; use ui_read_text to verify the result.";
        });
    }

    [Tool("ui_press_keys",
          "desktop_ui",
          "Send one key or shortcut to a specified window ID. Refuses protected focused fields. Foreground is checked throughout; Ctrl+Alt+Q stops input.",
          RiskLevel.Change,
          policyDriven: true)]
    public Task<string> Press(string windowId, string keys, CancellationToken cancellationToken)
    {
        return ApartmentThread.Run(ApartmentState.MTA, () =>
        {
            Input(windowId,
                  cancellationToken,
                  input =>
                  {
                      var focused = Uia.Focused();
                      if (focused is not null)
                      {
                          UiSafety.CheckElement(focused);
                      }

                      input.Shortcut(keys, cancellationToken);
                  });
            return "Shortcut sent. Verify the application result.";
        });
    }

    [Tool("ui_drag",
          "desktop_ui",
          "Drag inside an element by ref. Coordinates are relative to its bounds, from 0 to 1000. Ctrl+Alt+Q stops input.",
          RiskLevel.Change,
          policyDriven: true)]
    public Task<string> Drag(string reference,
                             double startX,
                             double startY,
                             double endX,
                             double endY,
                             CancellationToken cancellationToken)
    {
        return ApartmentThread.Run(ApartmentState.MTA, () =>
        {
            var target = Element(reference);
            UiSafety.CheckElement(target.Element);
            Input(target.WindowId,
                  cancellationToken,
                  input => input.Drag(target.Element.BoundingRectangle,
                                      [new(startX,
                                           startY), new(endX,
                                                        endY)],
                                      cancellationToken));
            return "Drag sent. Verify the application result.";
        });
    }

    [Tool("ui_read_text",
          "desktop_ui",
          "Read at most 1500 characters of a window or a snapshot ref. Refuses password fields. Remote endpoints require approval.",
          policyDriven: true)]
    public Task<string> Read(string windowId, string? reference = null)
    {
        return ApartmentThread.Run(ApartmentState.MTA, () =>
        {
            var element = reference is null ? Root(windowId) : Element(reference)
                .Element;
            UiSafety.CheckElement(element);
            var text = element.DocumentText(1500)
                ?? (element.ValuePatternOrNull() is { } value ? Uia.Call(() => value.CurrentValue) : element.Name);
            return text.Length > 1500 ? text[..1500] : text;
        });
    }

    internal void Input(string windowId, CancellationToken cancellationToken, Action<DesktopInput> action)
    {
        Root(windowId);
        InputAt(resolveWindow(windowId), cancellationToken, action);
    }

    internal void DialogInput(string windowId,
                              UiaElement dialog,
                              CancellationToken cancellationToken,
                              Action<DesktopInput> action)
    {
        if (dialog.ProcessId != Root(windowId).ProcessId || dialog.NativeWindowHandle == 0)
        {
            throw new ArgumentException("Dialog does not belong to the selected application.");
        }

        InputAt(dialog.NativeWindowHandle, cancellationToken, action);
    }

    private void InputAt(nint handle, CancellationToken cancellationToken, Action<DesktopInput> action)
    {
        inputActive(true);
        try
        {
            using var input = new DesktopInput(handle, emergencyStop);
            action(input);
            input.Check(cancellationToken);
        }
        finally
        {
            inputActive(false);
        }
    }

    public bool AppliesTo(string toolName) => toolName is "ui_click" or "ui_type" or "ui_press_keys" or "ui_drag";

    public ApprovalDecision Decide(FunctionCallContent call)
    {
        var reference = call.Arguments is { } arguments
            && arguments.TryGetValue("reference", out var value) ? value?.ToString() : null;
        var name = reference is not null
            && _refs.TryGetValue(reference, out var target) ? target.Element.Name : "selected window";
        return new(ApprovalOutcome.Ask,
                   false,
                   $"Control: {name}. Mouse and keyboard actions stop if another window becomes active. Press Ctrl+Alt+Q to stop input.");
    }
}
