using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Documents;
using Microsoft.Extensions.AI;
using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Tools;

namespace WindowsAxon.Windows;

public sealed class DesktopUiTools(Func<string, nint> resolveWindow, Action emergencyStop, Action<bool> inputActive) : IApprovalRule
{
    private readonly Dictionary<string, (string WindowId, AutomationElement Element)> _refs = [];

    internal AutomationElement Root(string windowId)
    {
        var root = AutomationElement.FromHandle(resolveWindow(windowId));
        using var process = Process.GetProcessById(root.Current.ProcessId);
        UiSafety.CheckProcess(process.ProcessName);
        return root;
    }

    [Tool("ui_focus_window",
          "desktop_ui",
          "Bring a window ID from ui_list_windows to the front. Blocks security, credential, remote-desktop, password-manager, and WindowsAxon windows.")]
    public Task<string> Focus(string windowId)
    {
        return StaThread.Run(() =>
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
        return StaThread.Run<object>(() =>
        {
            _refs.Clear();
            AutomationElement root = Root(windowId);
            AutomationElementCollection all = root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
            List<object> result = [];

            foreach (AutomationElement element in all)
            {
                if (element.Current.IsOffscreen || element.Current.ControlType == ControlType.Text
                    || element.Current.ControlType == ControlType.Pane)
                {
                    continue;
                }

                string id = Guid.NewGuid().ToString("N")[..8];
                _refs.Add(id, (windowId, element));
                result.Add(new
                {
                    Ref = id,
                    Type = element.Current.ControlType.ProgrammaticName,
                    Name = element.Current.Name,
                    AutomationId = element.Current.AutomationId,
                    IsPassword = element.Current.IsPassword
                });

                if (result.Count == 50)
                {
                    break;
                }
            }
            return new
            {
                Title = root.Current.Name,
                Elements = result
            };
        });
    }

    private (string WindowId, AutomationElement Element) Element(string reference)
    {
        if (!_refs.TryGetValue(reference, out var target))
        {
            throw new ArgumentException("Take a new ui_snapshot; the ref is expired or unknown.");
        }

        var root = Root(target.WindowId);
        if (target.Element.Current.ProcessId != root.Current.ProcessId || target.Element.Current.IsOffscreen)
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
        return StaThread.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Element(reference);
            UiSafety.CheckElement(target.Element);
            if (target.Element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
            {
                ((InvokePattern)invoke).Invoke();
            }
            else if (target.Element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
            {
                ((TogglePattern)toggle).Toggle();
            }
            else if (target.Element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
            {
                ((SelectionItemPattern)select).Select();
            }
            else
            {
                Input(target.WindowId,
                      cancellationToken,
                      input => input.Drag(target.Element.Current.BoundingRectangle,
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
        return StaThread.Run(() =>
        {
            var target = Element(reference);
            UiSafety.CheckElement(target.Element);
            cancellationToken.ThrowIfCancellationRequested();
            if (target.Element.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
            {
                var pattern = (ValuePattern)value;
                if (pattern.Current.IsReadOnly)
                {
                    throw new ArgumentException("Control is read-only.");
                }

                pattern.SetValue(text);
                if (pattern.Current.Value != text)
                {
                    throw new InvalidOperationException("Text could not be verified.");
                }

                return "Control text verified.";
            }
            if (target.Element.Current.ControlType != ControlType.Edit)
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
        return StaThread.Run(() =>
        {
            Input(windowId,
                  cancellationToken,
                  input =>
                  {
                      var focused = AutomationElement.FocusedElement;
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
        return StaThread.Run(() =>
        {
            var target = Element(reference);
            UiSafety.CheckElement(target.Element);
            Input(target.WindowId,
                  cancellationToken,
                  input => input.Drag(target.Element.Current.BoundingRectangle,
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
        return StaThread.Run(() =>
        {
            var element = reference is null ? Root(windowId) : Element(reference)
                .Element;
            UiSafety.CheckElement(element);
            var text = element.TryGetCurrentPattern(TextPattern.Pattern,
                                                    out var pattern) ? ((TextPattern)pattern).DocumentRange.GetText(1500)
                : element.TryGetCurrentPattern(ValuePattern.Pattern,
                                               out var value) ? ((ValuePattern)value).Current.Value : element.Current.Name;
            return text.Length > 1500 ? text[..1500] : text;
        });
    }

    internal void Input(string windowId, CancellationToken cancellationToken, Action<DesktopInput> action)
    {
        Root(windowId);
        InputAt(resolveWindow(windowId), cancellationToken, action);
    }

    internal void DialogInput(string windowId,
                              AutomationElement dialog,
                              CancellationToken cancellationToken,
                              Action<DesktopInput> action)
    {
        if (dialog.Current.ProcessId != Root(windowId).Current.ProcessId || dialog.Current.NativeWindowHandle == 0)
        {
            throw new ArgumentException("Dialog does not belong to the selected application.");
        }

        InputAt(new nint(dialog.Current.NativeWindowHandle), cancellationToken, action);
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
            && _refs.TryGetValue(reference, out var target) ? target.Element.Current.Name : "selected window";
        return new(ApprovalOutcome.Ask,
                   false,
                   $"Control: {name}. Mouse and keyboard actions stop if another window becomes active. Press Ctrl+Alt+Q to stop input.");
    }
}
