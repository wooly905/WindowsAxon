using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Tools;
using WindowsAxon.Agent.Undo;
using Microsoft.Extensions.AI;

namespace WindowsAxon.Windows;

public sealed class WindowsToolSet : IDisposable
{
    private readonly BrowserTools _browser;
    private readonly WebTools _web;
    private readonly DocsTools _docs;

    public IReadOnlyList<IApprovalRule> ApprovalRules
    {
        get;
    }

    public WindowsToolSet(ToolCatalog catalog,
                          IChatClient model,
                          string dataFolder,
                          string session,
                          Func<string, nint> resolveWindow,
                          Action emergencyStop,
                          Action<bool> inputActive)
    {
        var journal = new UndoJournal(Path.Combine(dataFolder, "undo"), TimeProvider.System);
        var files = new FileTools(journal, session);
        var registry = new RegistryTools(journal, session);
        var settings = new SettingsTools(journal, session);
        var software = new SoftwareTools();
        var utilities = new UtilityTools();
        var ui = new DesktopUiTools(resolveWindow, emergencyStop, inputActive);
        var paint = new PaintTools(ui);
        var scripts = new ScriptTools(model, dataFolder, session);
        _browser = new BrowserTools(dataFolder);
        _web = new WebTools(dataFolder);
        _docs = new DocsTools();
        foreach (var provider in new object[] {
            new SystemTools(),
            files,
            registry,
            settings,
            software,
            utilities,
            _browser,
            _web,
            ui,
            new ScreenshotTools(ui),
            paint,
            scripts,
            _docs
        })
        {
            ToolRegistration.Add(catalog, provider);
        }

        new UndoTools(journal, [files, registry, settings], session).Register(catalog);
        ApprovalRules = [_browser, ui, paint, scripts, software, utilities, _docs, new UndoApprovalRule(journal)];
    }

    public void Dispose()
    {
        _web.Dispose();
        Task.Run(async () =>
        {
            await _browser.DisposeAsync();
            await _docs.DisposeAsync();
        }).GetAwaiter().GetResult();
    }

    /// <summary>Closes the hidden research browser and leaves the visible browser windows open.</summary>
    public void CloseResearchBrowser() => _web.Close();

    public void StopBrowser()
    {
        _browser.CancelPending();
        _web.Close();
    }

    private sealed class UndoApprovalRule(UndoJournal journal) : IApprovalRule
    {
        public bool AppliesTo(string toolName) => toolName == "undo_last";

        public ApprovalDecision Decide(FunctionCallContent call)
        {
            var id = call.Arguments is { } arguments
                && arguments.TryGetValue("id", out var value) ? value?.ToString() : null;
            var entry = id is null ? journal.List().FirstOrDefault(journal.IsNewest) : journal.Find(id);
            return new(ApprovalOutcome.Ask,
                       false,
                       entry is null ? "Restore a recorded setting change?" : $"Restore {entry.Target} from {entry.NewValue} to {entry.PreviousValue}? Later changes may be overwritten when force=true.");
        }
    }
}
