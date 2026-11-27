using System.Diagnostics;
using System.Text.Json;
using WindowsAxon.Agent;
using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Selection;
using WindowsAxon.App.Updates;
using Microsoft.Extensions.AI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace WindowsAxon.App;

public sealed partial class MainWindow : Window, IApprovalPrompter, IStepLimitPrompter
{
    private readonly DesktopChatService _service;
    private readonly SemaphoreSlim _promptLock = new(1, 1);
    private ContentDialog? _activeDialog;
    private TaskCompletionSource<ContentDialogResult>? _promptCompletion;
    private static readonly TimeSpan UpdateCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly HttpClient UpdateHttp = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly CancellationTokenSource _closing = new();
    private readonly UpdateState _updateState = new(UpdateState.DefaultPath);
    private readonly DispatcherTimer _pruneTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private ReleaseInfo? _availableUpdate;
    private bool _updating;

    public ChatViewModel ViewModel
    {
        get;
    }

    public MainWindow()
    {
        _service = new DesktopChatService(this, this);
        string? settingsError = null;
        try
        {
            _service.LoadSettings();
            _service.StartMesh();
        }
        catch (Exception)
        {
            settingsError = "Could not load settings. Open Settings to configure the model.";
        }

        ViewModel = new ChatViewModel(_service);
        _service.EmergencyStopRequested += () => DispatcherQueue.TryEnqueue(() => ViewModel.SendCommand.Cancel());
        _service.InputActiveChanged += active => DispatcherQueue.TryEnqueue(() => InputBanner.Visibility = active ? Visibility.Visible : Visibility.Collapsed);
        _service.ToolStarted += tool => DispatcherQueue.TryEnqueue(() => ViewModel.ShowTool(tool));
        _service.Notice += message => DispatcherQueue.TryEnqueue(() => ViewModel.AddEvent(message));
        _service.PeersChanged += peers => DispatcherQueue.TryEnqueue(() => RefreshOnlinePeers(peers));
        _service.MeshStateChanged += (_, _) => DispatcherQueue.TryEnqueue(RefreshMeshStatus);
        _service.RemoteRequestReceived += (caller, prompt) => DispatcherQueue.TryEnqueue(() => ViewModel.ShowRemoteRequest(caller, prompt));
        _service.RemoteToolStarted += (caller, tool) => DispatcherQueue.TryEnqueue(() => ViewModel.ShowRemoteTool(caller, tool));
        _service.RemoteResponseSent += (caller, status, text) => DispatcherQueue.TryEnqueue(() => ViewModel.ShowRemoteResponse(caller, status, text));
        _service.FileReceived += info => DispatcherQueue.TryEnqueue(() => ViewModel.ShowRemoteFileReceived(info.CallerNode,
                                                                                                           info.FileName,
                                                                                                           info.SizeBytes,
                                                                                                           info.SavedPath));
        // Deferred so the list is not rebuilt inside its own ItemClick handler.
        ViewModel.UnreadChanged += () => DispatcherQueue.TryEnqueue(() => RefreshOnlinePeers(_service.OnlinePeers));
        ViewModel.PropertyChanged += (_, e) =>
        {
            // Deferred so the x:Bind IsEnabled update re-enables the TextBox first.
            if (e.PropertyName == nameof(ChatViewModel.IsBusy) && !ViewModel.IsBusy)
            {
                DispatcherQueue.TryEnqueue(() => CommandInput.Focus(FocusState.Programmatic));
            }
        };
        if (settingsError is not null)
        {
            ViewModel.Status = settingsError;
        }

        InitializeComponent();
        RefreshOnlinePeers(_service.OnlinePeers);
        RefreshMeshStatus();
        _service.PruneExpiredArtifacts();
        _pruneTimer.Tick += (_, _) =>
        {
            ViewModel.PruneExpired();
            _service.PruneExpiredArtifacts();
            _service.PruneExpiredRemoteSessions();
        };
        _pruneTimer.Start();
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(1000, 900));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "WindowsAxon.ico"));

        if (Environment.ProcessPath is { } executable)
        {
            FileVersionText.Text = FileVersionInfo.GetVersionInfo(executable).FileVersion ?? string.Empty;
            UpdateInstaller.CleanupOld(executable);
        }

        _ = CheckForUpdatesAsync(_closing.Token);
        Closed += (_, _) =>
        {
            _pruneTimer.Stop();
            _closing.Cancel();
            ViewModel.SendCommand.Cancel();
            _activeDialog?.Hide();
            _promptCompletion?.TrySetCanceled();
            _service.Dispose();
        };
    }

    public Visibility ReplyVisibility(bool awaitingReply) => awaitingReply ? Visibility.Visible : Visibility.Collapsed;

    public Visibility BusyVisibility(bool isBusy) => isBusy ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IdleVisibility(bool isBusy) => isBusy ? Visibility.Collapsed : Visibility.Visible;

    private static Version CurrentVersion => typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);

    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(UpdateCheckDelay, cancellationToken);
            if (Environment.ProcessPath is { } executable)
            {
                UpdateInstaller.CleanupOld(executable);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var latest = await new UpdateChecker(UpdateHttp).GetLatestAsync(timeout.Token);
            if (latest is null || !UpdateChecker.ShouldPrompt(CurrentVersion, latest, _updateState.LoadSkippedVersion()))
            {
                return;
            }

            _availableUpdate = latest;
            UpdateBar.Message = $"WindowsAxon {latest.Version} is available (you have {UpdateChecker.Normalize(CurrentVersion)}).";
            UpdateBar.IsOpen = true;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is not { } release || _updating || ViewModel.IsBusy)
        {
            return;
        }

        _updating = true;
        UpdateActions.Visibility = Visibility.Collapsed;
        UpdateBar.Message = $"Downloading WindowsAxon {release.Version}...";
        try
        {
            if (Environment.ProcessPath is not { } executable || !UpdateInstaller.IsSingleFile(executable))
            {
                OpenReleasePage(release);
                UpdateBar.Message = "This copy cannot update itself. The release page was opened so you can download the update.";
                return;
            }

            var downloaded = await new UpdateInstaller(UpdateHttp).DownloadAsync(release,
                                                                                 Path.Combine(Path.GetTempPath(),
                                                                                              "WindowsAxon-update"),
                                                                                 _closing.Token);
            if (ViewModel.IsBusy)
            {
                UpdateBar.Message = $"WindowsAxon {release.Version} is downloaded and will install when the current request finishes.";
                while (ViewModel.IsBusy)
                {
                    await Task.Delay(500, _closing.Token);
                }
            }

            // Runs synchronously on the UI thread after the idle check, so no request can start before Close().
            UpdateInstaller.Install(executable,
                                    downloaded,
                                    path => UpdateInstaller.Launch(path, Environment.GetCommandLineArgs().Skip(1)));
            Close();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            UpdateBar.Severity = InfoBarSeverity.Error;
            UpdateBar.Message = "Could not install the update. The release page was opened so you can download it manually.";
            UpdateActions.Visibility = Visibility.Visible;
            OpenReleasePage(release);
        }
        finally
        {
            _updating = false;
        }
    }

    private void SkipUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is { } release)
        {
            try
            {
                _updateState.SaveSkippedVersion(release.Version);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        UpdateBar.IsOpen = false;
    }

    private static void OpenReleasePage(ReleaseInfo release)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(release.PageUrl.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    private void ConversationContent_SizeChanged(object sender, SizeChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                                   () =>
            ConversationScroll.ChangeView(null, ConversationScroll.ScrollableHeight, null, disableAnimation: true));

    private async void Attach_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            InsertInput($"\"{file.Path}\"");
        }
    }

    private void InsertInput(string text)
    {
        var start = Math.Min(CommandInput.SelectionStart, CommandInput.Text.Length);
        var before = start > 0 && !char.IsWhiteSpace(CommandInput.Text[start - 1]) ? " " : "";
        CommandInput.Text = CommandInput.Text.Insert(start, before + text);
        CommandInput.SelectionStart = start + before.Length + text.Length;
        CommandInput.Focus(FocusState.Programmatic);
    }

    private void CommandInput_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);
        if (e.Key != VirtualKey.Enter || shift)
        {
            return;
        }

        e.Handled = true;
        if (ViewModel.SendCommand.CanExecute(null))
        {
            ViewModel.SendCommand.Execute(null);
        }
    }

    public Task<ApprovalAnswer> AskAsync(FunctionCallContent call,
                                         ApprovalDecision decision,
                                         CancellationToken cancellationToken)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            return AskOnUiThreadAsync(call, decision, cancellationToken);
        }

        var tcs = new TaskCompletionSource<ApprovalAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    tcs.TrySetResult(await AskOnUiThreadAsync(call, decision, cancellationToken));
                }
                catch (OperationCanceledException ex)
                {
                    tcs.TrySetCanceled(ex.CancellationToken);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
        {
            tcs.TrySetResult(ApprovalAnswer.Deny);
        }

        return tcs.Task;
    }

    private async Task<ApprovalAnswer> AskOnUiThreadAsync(FunctionCallContent call,
                                                          ApprovalDecision decision,
                                                          CancellationToken cancellationToken)
    {
        var arguments = JsonSerializer.Serialize(call.Arguments, new JsonSerializerOptions { WriteIndented = true });
        var result = await ShowPromptAsync("Allow this action?",
                                           $"Action: {call.Name}\n{decision.Note}\n{arguments}",
                                           "Allow",
                                           decision.AllowForConversation ? "Allow this read again in this conversation" : "",
                                           "Deny",
                                           cancellationToken);
        ViewModel.AddEvent(result switch
        {
            ContentDialogResult.Primary => $"Allowed {call.Name}",
            ContentDialogResult.Secondary => $"Allowed {call.Name} for this conversation",
            _ => $"Denied {call.Name}",
        });

        return result switch
        {
            ContentDialogResult.Primary => ApprovalAnswer.Allow,
            ContentDialogResult.Secondary => ApprovalAnswer.AllowForConversation,
            _ => ApprovalAnswer.Deny,
        };
    }

    public async Task<bool> AskToContinueAsync(int stepsUsed, CancellationToken cancellationToken)
    {
        var result = await ShowPromptAsync($"WindowsAxon has used {stepsUsed} steps",
                                           "Continue for 10 more steps?",
                                           "Continue",
                                           "",
                                           "Finish now",
                                           cancellationToken);
        return result == ContentDialogResult.Primary;
    }

    private async Task<ContentDialogResult> ShowPromptAsync(string title,
                                                            string text,
                                                            string primary,
                                                            string secondary,
                                                            string close,
                                                            CancellationToken cancellationToken)
    {
        await _promptLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<ContentDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _promptCompletion = completion;
            PromptTitle.Text = title;
            PromptText.Text = text;
            PromptPrimary.Content = primary;
            PromptSecondary.Content = secondary;
            PromptSecondary.Visibility = string.IsNullOrEmpty(secondary) ? Visibility.Collapsed : Visibility.Visible;
            PromptClose.Content = close;
            PromptPanel.Visibility = Visibility.Visible;
            PromptClose.Focus(FocusState.Programmatic);
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            try
            {
                return await completion.Task;
            }
            finally
            {
                PromptPanel.Visibility = Visibility.Collapsed;
                _promptCompletion = null;
            }
        }
        finally
        {
            _promptLock.Release();
        }
    }

    private void Prompt_Click(object sender, RoutedEventArgs e) =>
        _promptCompletion?.TrySetResult(Enum.Parse<ContentDialogResult>(((Button)sender).Tag.ToString()!));

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        dialog.XamlRoot = Root.XamlRoot;
        _activeDialog = dialog;
        using var registration = cancellationToken.Register(() => DispatcherQueue.TryEnqueue(dialog.Hide));
        try
        {
            var result = await dialog.ShowAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            _activeDialog = null;
        }
    }
}
