using System.Text.Json;
using WindowsAxon.Agent;
using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Selection;
using WindowsAxon.Agent.Settings;
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
    private ContentDialog? _activeDialog;
    private TaskCompletionSource<ContentDialogResult>? _promptCompletion;
    private static readonly TimeSpan UpdateCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly HttpClient UpdateHttp = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly CancellationTokenSource _closing = new();
    private readonly UpdateState _updateState = new(UpdateState.DefaultPath);
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
        }
        catch (Exception)
        {
            settingsError = "Could not load settings. Open Settings to configure the model.";
        }

        ViewModel = new ChatViewModel(_service);
        _service.EmergencyStopRequested += () => DispatcherQueue.TryEnqueue(() => ViewModel.SendCommand.Cancel());
        _service.InputActiveChanged += active => DispatcherQueue.TryEnqueue(() => InputBanner.Visibility = active ? Visibility.Visible : Visibility.Collapsed);
        _service.ToolStarted += tool => DispatcherQueue.TryEnqueue(() => ViewModel.ShowTool(tool));
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
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(460, 570));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "WindowsAxon.ico"));
        if (Environment.ProcessPath is { } executable)
        {
            UpdateInstaller.CleanupOld(executable);
        }

        _ = CheckForUpdatesAsync(_closing.Token);
        Closed += (_, _) =>
        {
            _closing.Cancel();
            ViewModel.SendCommand.Cancel();
            _activeDialog?.Hide();
            _promptCompletion?.TrySetCanceled();
            _service.Dispose();
        };
    }

    public Visibility ReplyVisibility(bool awaitingReply) => awaitingReply ? Visibility.Visible : Visibility.Collapsed;

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

    public async Task<ApprovalAnswer> AskAsync(FunctionCallContent call,
                                               ApprovalDecision decision,
                                               CancellationToken cancellationToken)
    {
        var arguments = JsonSerializer.Serialize(call.Arguments, new JsonSerializerOptions { WriteIndented = true });
        var result = await ShowPromptAsync("Allow this action?",
                                           $"Action: {call.Name}\n{decision.Note}\n{arguments}",
                                           "Allow",
                                           decision.AllowForConversation ? "Allow for this conversation" : "",
                                           "Deny",
                                           cancellationToken);
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

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = new TextBox
        {
            Header = "Endpoint URL",
            Text = _service.Settings.ChatEndpoint.BaseUrl,
            PlaceholderText = "http://localhost:11434/v1"
        };
        var model = new TextBox { Header = "Model name", Text = _service.Settings.Chat.Model };
        var api = new ComboBox
        {
            Header = "API",
            ItemsSource = Enum.GetValues<EndpointApi>(),
            SelectedItem = _service.Settings.ChatEndpoint.Api,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var apiKey = new PasswordBox
        {
            Header = "API key",
            PlaceholderText = "Optional",
            PasswordRevealMode = PasswordRevealMode.Hidden
        };
        var keyStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var removeKey = new CheckBox { Content = "Remove saved API key" };
        void UpdateKeyStatus()
        {
            var sameEndpoint = Uri.TryCreate(endpoint.Text.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)
                && uri == new Uri(_service.Settings.ChatEndpoint.BaseUrl.TrimEnd('/'));
            var hasKey = !string.IsNullOrEmpty(_service.Settings.ChatEndpoint.ApiKey);
            keyStatus.Text = hasKey && sameEndpoint
                ? "An API key is configured. Leave blank to keep it, or enter a replacement."
                : hasKey ? "The endpoint changed. Enter a key for this endpoint; the previous key will not be reused."
                : "Leave blank if this endpoint does not require a key.";
            removeKey.Visibility = hasKey ? Visibility.Visible : Visibility.Collapsed;
        }
        endpoint.TextChanged += (_, _) => UpdateKeyStatus();
        removeKey.Checked += (_, _) =>
        {
            apiKey.Password = "";
            apiKey.IsEnabled = false;
        };
        removeKey.Unchecked += (_, _) => apiKey.IsEnabled = true;
        UpdateKeyStatus();
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(endpoint);
        content.Children.Add(api);
        content.Children.Add(model);
        content.Children.Add(apiKey);
        content.Children.Add(keyStatus);
        content.Children.Add(removeKey);
        content.Children.Add(new TextBlock

        {
            Text = "API keys are stored in Windows Credential Manager. A remote model receives your messages and tool results.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = $"Settings: {DesktopChatService.SettingsPath}",
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        });
        content.Children.Add(error);
        var dialog = new ContentDialog
        {
            Title = "Model settings",
            Content = new ScrollViewer { Content = content, MaxHeight = 480 },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var previousSettings = _service.Settings;
            try
            {
                _service.SaveSettings(endpoint.Text,
                                      model.Text,
                                      apiKey.Password,
                                      removeKey.IsChecked == true,
                                      (EndpointApi)api.SelectedItem);
                ViewModel.SettingsChanged();
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                if (!ReferenceEquals(previousSettings, _service.Settings))
                {
                    ViewModel.SettingsChanged();
                    error.Text = "Settings were saved, but the previous API key could not be removed from Windows Credential Manager.";
                }
                else
                {
                    error.Text = ex is ArgumentException ? ex.Message : "Could not save settings or the API key. Check access to the settings folder and Windows Credential Manager.";
                }
                args.Cancel = true;
            }
        };
        await ShowDialogAsync(dialog, CancellationToken.None);
        apiKey.Password = "";
    }
}
