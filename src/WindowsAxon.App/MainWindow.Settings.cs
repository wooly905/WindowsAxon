using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WindowsAxon.Agent.Settings;

namespace WindowsAxon.App;

public sealed partial class MainWindow
{
    private sealed record SettingsTabPanel(UIElement Content, Action? OnSelected = null, Action? Cleanup = null);

    private sealed record SettingsTab(string Label, string Glyph, SettingsTabPanel Panel);

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var tabs = new[]
        {
            new SettingsTab("LLM", "\uE713", CreateLlmTab()),
            new SettingsTab("Tools", "\uE90F", CreateToolsTab()),
            new SettingsTab("Network", "\uE774", CreateNetworkTab()),
            new SettingsTab("MCP", "\uE968", CreateMcpTab()),
            new SettingsTab("Skills", "\uE945", CreateSkillsTab()),
        };

        var width = Root.ActualWidth > 0 ? Math.Clamp(Root.ActualWidth - 96, 560, 820) : 820;
        var height = Root.ActualHeight > 0 ? Math.Clamp(Root.ActualHeight - 180, 420, 560) : 560;

        var tabList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
        };
        AutomationProperties.SetName(tabList, "Settings sections");

        var contentHost = new Grid
        {
            Padding = new Thickness(16, 0, 0, 0),
        };

        foreach (var tab in tabs)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Padding = new Thickness(4, 6, 4, 6),
            };
            row.Children.Add(new FontIcon { Glyph = tab.Glyph, FontSize = 14 });
            row.Children.Add(new TextBlock { Text = tab.Label, VerticalAlignment = VerticalAlignment.Center });

            var item = new ListViewItem
            {
                Content = row,
                MinHeight = 36,
                Padding = new Thickness(8, 2, 8, 2),
            };
            AutomationProperties.SetName(item, tab.Label);
            tabList.Items.Add(item);

            tab.Panel.Content.Visibility = Visibility.Collapsed;
            contentHost.Children.Add(tab.Panel.Content);
        }

        tabList.SelectionChanged += (_, _) =>
        {
            var selectedIndex = tabList.SelectedIndex;

            if (selectedIndex < 0 || selectedIndex >= tabs.Length)
            {
                return;
            }

            for (var i = 0; i < tabs.Length; i++)
            {
                tabs[i].Panel.Content.Visibility = i == selectedIndex ? Visibility.Visible : Visibility.Collapsed;
            }

            tabs[selectedIndex].Panel.OnSelected?.Invoke();
        };

        var leftPane = new Border
        {
            Child = tabList,
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(0, 0, 8, 0),
        };

        var layout = new Grid
        {
            Width = width,
            Height = height,
        };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(168) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(contentHost, 1);
        layout.Children.Add(leftPane);
        layout.Children.Add(contentHost);

        tabList.SelectedIndex = 0;

        var dialog = new ContentDialog
        {
            Title = "Settings",
            Content = layout,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.None,
        };
        dialog.Resources["ContentDialogMaxWidth"] = width + 64;
        dialog.Resources["ContentDialogMaxHeight"] = height + 160;

        try
        {
            await ShowDialogAsync(dialog, CancellationToken.None);
        }
        finally
        {
            foreach (var tab in tabs)
            {
                tab.Panel.Cleanup?.Invoke();
            }
        }
    }

    private SettingsTabPanel CreateLlmTab()
    {
        var endpoints = ProviderPresets.MergeWith(_service.Settings.Endpoints);
        var provider = new ComboBox
        {
            Header = "Provider",
            ItemsSource = endpoints.Keys.ToList(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        EndpointSettings Selected() => endpoints[(string)provider.SelectedItem];

        var endpoint = new TextBox
        {
            Header = "Endpoint URL",
            PlaceholderText = "http://localhost:11434/v1",
        };
        var model = new TextBox { Header = "Model name" };
        var api = new ComboBox
        {
            Header = "API",
            ItemsSource = Enum.GetValues<EndpointApi>(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var apiKey = new PasswordBox
        {
            Header = "API key",
            PlaceholderText = "Optional",
            PasswordRevealMode = PasswordRevealMode.Hidden,
        };
        var keyStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var removeKey = new CheckBox { Content = "Remove saved API key" };

        void UpdateKeyStatus()
        {
            var sameEndpoint = Uri.TryCreate(endpoint.Text.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)
                && uri == new Uri(Selected().BaseUrl.TrimEnd('/'));
            var hasKey = !string.IsNullOrEmpty(Selected().ApiKey);
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

        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

        provider.SelectionChanged += (_, _) =>
        {
            var selected = Selected();
            var isCurrent = (string)provider.SelectedItem == _service.Settings.Chat.Endpoint;
            endpoint.Text = selected.BaseUrl;
            api.SelectedItem = selected.Api;
            model.Text = isCurrent ? _service.Settings.Chat.Model : selected.Models.FirstOrDefault() ?? "";
            apiKey.Password = "";
            removeKey.IsChecked = false;
            SetFeedback(feedback, "", isError: false);
            UpdateKeyStatus();
        };
        provider.SelectedItem = endpoints.Keys.First(name => string.Equals(name,
                                                                           _service.Settings.Chat.Endpoint,
                                                                           StringComparison.OrdinalIgnoreCase));

        var thinking = new ToggleSwitch { Header = "Thinking", IsOn = _service.Settings.Chat.Thinking };
        const string DefaultEffort = "Model default";
        var currentEffort = _service.Settings.Chat.ReasoningEffort ?? DefaultEffort;
        var effort = new ComboBox
        {
            Header = "Reasoning effort",
            ItemsSource = new[] { DefaultEffort, "minimal", "low", "medium", "high", currentEffort }.Distinct().ToList(),
            SelectedItem = currentEffort,
            IsEnabled = thinking.IsOn,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        thinking.Toggled += (_, _) => effort.IsEnabled = thinking.IsOn;

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(SectionHeading("LLM"));
        content.Children.Add(provider);
        content.Children.Add(endpoint);
        content.Children.Add(api);
        content.Children.Add(model);
        content.Children.Add(thinking);
        content.Children.Add(effort);
        content.Children.Add(apiKey);
        content.Children.Add(keyStatus);
        content.Children.Add(removeKey);
        content.Children.Add(new TextBlock
        {
            Text = "API keys are stored in Windows Credential Manager. A remote model receives your messages and tool results.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = $"Settings: {DesktopChatService.SettingsPath}",
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        content.Children.Add(new TextBlock { Text = "Get an API key:" });

        foreach (var (name, url) in new[]
                 {
                     ("Nvidia", "https://build.nvidia.com/settings/api-keys"),
                     ("Google", "https://aistudio.google.com/apikey"),
                     ("Groq", "https://console.groq.com/keys"),
                 })
        {
            content.Children.Add(new HyperlinkButton
            {
                Content = $"{name}: {url}",
                NavigateUri = new Uri(url),
                Padding = new Thickness(0),
            });
        }

        var saveButton = new Button
        {
            Content = "Save",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        saveButton.Click += (_, _) =>
        {
            SetFeedback(feedback, "", isError: false);
            var previousSettings = _service.Settings;

            try
            {
                _service.SaveSettings(endpoint.Text,
                                      model.Text,
                                      apiKey.Password,
                                      removeKey.IsChecked == true,
                                      (EndpointApi)api.SelectedItem,
                                      (string)provider.SelectedItem);
                var selectedEffort = (string?)effort.SelectedItem;
                _service.SaveReasoning(thinking.IsOn, selectedEffort is null or DefaultEffort ? null : selectedEffort);

                endpoints = ProviderPresets.MergeWith(_service.Settings.Endpoints);
                apiKey.Password = "";
                removeKey.IsChecked = false;
                UpdateKeyStatus();
                ViewModel.SettingsChanged();
                SetFeedback(feedback, "LLM settings saved.", isError: false);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                if (!ReferenceEquals(previousSettings, _service.Settings))
                {
                    endpoints = ProviderPresets.MergeWith(_service.Settings.Endpoints);
                    apiKey.Password = "";
                    removeKey.IsChecked = false;
                    UpdateKeyStatus();
                    ViewModel.SettingsChanged();
                    SetFeedback(feedback,
                                "Settings were saved, but the previous API key could not be removed from Windows Credential Manager.",
                                isError: true);
                }
                else
                {
                    SetFeedback(feedback,
                                ex is ArgumentException
                                    ? ex.Message
                                    : "Could not save settings or the API key. Check access to the settings folder and Windows Credential Manager.",
                                isError: true);
                }
            }
        };

        var footer = ActionFooter(feedback, saveButton);
        var panel = CreateScrollableTab(content, footer);

        return new SettingsTabPanel(panel, Cleanup: () => apiKey.Password = "");
    }

    private SettingsTabPanel CreateToolsTab()
    {
        var currentLevel = _service.Settings.Chat.ResolveApprovalLevel();
        var approval = new ComboBox
        {
            Header = "Tool approval",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        foreach (var (level, label) in new[]
                 {
                     (ApprovalLevel.Ask, "Ask for every action"),
                     (ApprovalLevel.Read, "Allow reads"),
                     (ApprovalLevel.Local, "Allow actions on this computer"),
                     (ApprovalLevel.All, "Allow all actions"),
                 })
        {
            approval.Items.Add(new ComboBoxItem { Content = label, Tag = level });
        }

        var approvalNote = new TextBlock { TextWrapping = TextWrapping.Wrap };
        approval.SelectionChanged += (_, _) => approvalNote.Text = (ApprovalLevel)((ComboBoxItem)approval.SelectedItem).Tag switch
        {
            ApprovalLevel.Ask => "Every tool call shows the approval card.",
            ApprovalLevel.Read => "Ordinary reads run without asking. Changes, dangerous actions, and network requests ask.",
            ApprovalLevel.Local => "Reads and changes on this computer run without asking. File contents go to the model endpoint. Dangerous actions and network requests ask.",
            _ => "Every tool call runs without asking, including deletes, scripts, registry changes, and network requests.",
        };
        approval.SelectedIndex = (int)currentLevel;

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(SectionHeading("Tools"));
        content.Children.Add(approval);
        content.Children.Add(approvalNote);

        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var saveButton = new Button
        {
            Content = "Save",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        saveButton.Click += (_, _) =>
        {
            SetFeedback(feedback, "", isError: false);

            try
            {
                var selectedLevel = (ApprovalLevel)((ComboBoxItem)approval.SelectedItem).Tag;

                if (selectedLevel != currentLevel)
                {
                    _service.SaveApprovalLevel(selectedLevel);
                    currentLevel = selectedLevel;
                    ViewModel.SettingsChanged();
                }

                SetFeedback(feedback, "Tool settings saved.", isError: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetFeedback(feedback,
                            "Could not save tool settings. Check access to the settings folder.",
                            isError: true);
            }
        };

        var panel = CreateScrollableTab(content, ActionFooter(feedback, saveButton));

        return new SettingsTabPanel(panel);
    }

    private static TextBlock SectionHeading(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
    };

    private static Border ActionFooter(params UIElement[] children)
    {
        var stack = new StackPanel { Spacing = 8 };

        foreach (var child in children)
        {
            stack.Children.Add(child);
        }

        return new Border
        {
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 12, 0, 0),
            Child = stack,
        };
    }

    private static Grid CreateScrollableTab(UIElement scrollableContent, FrameworkElement footer)
    {
        var grid = new Grid { RowSpacing = 12 };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var scroll = new ScrollViewer
        {
            Content = scrollableContent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 12, 0),
        };
        Grid.SetRow(footer, 1);
        grid.Children.Add(scroll);
        grid.Children.Add(footer);

        return grid;
    }

    private static void SetFeedback(TextBlock block, string message, bool isError)
    {
        block.Text = message;
        block.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        block.Foreground = isError
            ? ErrorBrush()
            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    }
}
