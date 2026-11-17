using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsAxon.Agent.Settings;

namespace WindowsAxon.App;

public sealed partial class MainWindow
{
    private const string HttpTransport = "HTTP";
    private const string StdioTransport = "stdio (program on this computer)";

    private async void McpServers_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog { Title = "MCP servers", CloseButtonText = "Close" };
        dialog.Resources["ContentDialogMaxWidth"] = 820.0;
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MinHeight = 200, MaxHeight = 320 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        McpServerSettings? Selected() => (list.SelectedItem as ListViewItem)?.Tag as McpServerSettings;

        var confirmText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        var confirmYes = new Button { Content = "Delete", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var confirmNo = new Button { Content = "Cancel" };
        var confirm = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        confirm.Children.Add(confirmText);
        confirm.Children.Add(Row(confirmYes, confirmNo));
        confirmNo.Click += (_, _) => confirm.Visibility = Visibility.Collapsed;

        var listPanel = new StackPanel { Spacing = 12 };
        var editorPanel = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };

        void ShowList(string? select = null, string? message = null)
        {
            confirm.Visibility = Visibility.Collapsed;
            list.Items.Clear();
            foreach (var server in _service.Settings.Chat.McpServers)
            {
                var toggle = new ToggleSwitch
                {
                    IsOn = server.Enabled,
                    OnContent = "On",
                    OffContent = "Off",
                    MinWidth = 0,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                AutomationProperties.SetName(toggle, $"Use {server.Name} in new chats");
                toggle.Toggled += (_, _) =>
                {
                    try
                    {
                        _service.SetMcpServerEnabled(server.Name, toggle.IsOn);
                        status.Text = $"{server.Name} is {(toggle.IsOn ? "on" : "off")}. The change applies to new chats.";
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        status.Text = ex.Message;
                    }
                };
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var text = McpRowContent(server);
                Grid.SetColumn(toggle, 1);
                row.Children.Add(text);
                row.Children.Add(toggle);
                var item = new ListViewItem { Tag = server, Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                list.Items.Add(item);
                if (server.Name == select)
                {
                    list.SelectedItem = item;
                }
            }

            status.Text = message ?? (list.Items.Count == 0 ? "No MCP servers. Select New to add one." : "");
            editorPanel.Visibility = Visibility.Collapsed;
            listPanel.Visibility = Visibility.Visible;
            dialog.PrimaryButtonText = "";
            dialog.SecondaryButtonText = "";
        }

        var name = new TextBox { Header = "Name", PlaceholderText = "Lowercase letters and digits, for example microsoftlearn" };
        var description = new TextBox
        {
            Header = "Description",
            PlaceholderText = "Required. Describe the topics and data this server covers so the model knows when to use it, for example: Microsoft Learn documentation and code samples for Azure, .NET, Windows, and other Microsoft products",
            TextWrapping = TextWrapping.Wrap,
        };
        var transport = new ComboBox
        {
            Header = "Transport",
            ItemsSource = new[] { HttpTransport, StdioTransport },
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var url = new TextBox { Header = "URL", PlaceholderText = "http://server:8020/mcp" };
        var command = new TextBox { Header = "Command", PlaceholderText = "npx" };
        var arguments = new TextBox
        {
            Header = "Arguments (one per line)",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 96,
        };
        var tools = new TextBox
        {
            Header = "Tools",
            PlaceholderText = "Optional. Tool names separated by commas. Leave blank to use every tool.",
            TextWrapping = TextWrapping.Wrap,
        };
        var headerName = new TextBox { Header = "Secret header name", PlaceholderText = "Optional, for example Authorization" };
        var headerValue = new PasswordBox
        {
            Header = "Secret header value",
            PlaceholderText = "Saved in Windows Credential Manager",
            PasswordRevealMode = PasswordRevealMode.Hidden,
        };
        var autoApprove = new CheckBox { Content = "Allow tool calls without asking" };
        var autoApproveNote = new TextBlock
        {
            Text = "When selected, WindowsAxon runs this server's tools without the approval card, including tools that change data. Select it only for a server you trust.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        };
        var headerStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var editorError = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = ErrorBrush() };
        var httpFields = new StackPanel { Spacing = 12 };
        foreach (var child in new UIElement[] { url, headerName, headerValue, headerStatus })
        {
            httpFields.Children.Add(child);
        }

        var stdioFields = new StackPanel { Spacing = 12 };
        stdioFields.Children.Add(command);
        stdioFields.Children.Add(arguments);
        stdioFields.Children.Add(new TextBlock
        {
            Text = "WindowsAxon asks you before it starts this program for the first time in an app session. The program runs with your permissions.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        });
        transport.SelectionChanged += (_, _) =>
        {
            var http = (string)transport.SelectedItem == HttpTransport;
            httpFields.Visibility = http ? Visibility.Visible : Visibility.Collapsed;
            stdioFields.Visibility = http ? Visibility.Collapsed : Visibility.Visible;
        };
        foreach (var child in new UIElement[] { editorError, name, description, transport, httpFields, stdioFields, tools, autoApprove, autoApproveNote })
        {
            editorPanel.Children.Add(child);
        }
        editorPanel.Children.Add(new TextBlock
        {
            Text = "Tool results go to the model endpoint.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        });

        string? editing = null;
        void ShowEditor(McpServerSettings? server)
        {
            confirm.Visibility = Visibility.Collapsed;
            editing = server?.Name;
            name.Text = server?.Name ?? "";
            description.Text = server?.Description ?? "";
            transport.SelectedItem = server?.Command is not null ? StdioTransport : HttpTransport;
            url.Text = server?.Url ?? "";
            command.Text = server?.Command ?? "";
            arguments.Text = string.Join("\r", server?.Args ?? []);
            tools.Text = string.Join(", ", server?.Tools ?? []);
            autoApprove.IsChecked = server?.AutoApprove == true;
            var header = server?.Headers.FirstOrDefault();
            headerName.Text = header?.Key ?? "";
            headerValue.Password = "";
            headerStatus.Text = header is { Key: not null }
                ? "A header value is saved. Leave the value blank to keep it for the same URL, or enter a replacement. Clear the name to remove the header."
                : "Headers need HTTPS, except for localhost.";
            editorError.Text = "";
            listPanel.Visibility = Visibility.Collapsed;
            editorPanel.Visibility = Visibility.Visible;
            dialog.PrimaryButtonText = "Save";
            dialog.SecondaryButtonText = "Back";
        }

        static List<string> Split(string text, params char[] separators) =>
            [.. text.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        dialog.DefaultButton = ContentDialogButton.Primary;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            if (string.IsNullOrWhiteSpace(description.Text))
            {
                editorError.Text = "Enter a description. The model chooses an MCP server only by its name and description, so name the topics and data the server covers.";
                return;
            }

            var http = (string)transport.SelectedItem == HttpTransport;
            var server = new McpServerSettings
            {
                Name = name.Text.Trim(),
                Description = description.Text.Trim(),
                Url = http ? url.Text.Trim() : null,
                Command = http ? null : command.Text.Trim(),
                Args = http ? [] : Split(arguments.Text, '\r', '\n'),
                Tools = Split(tools.Text, ',', '\r', '\n'),
                AutoApprove = autoApprove.IsChecked == true,
                Enabled = _service.Settings.Chat.McpServers.FirstOrDefault(s => s.Name == editing)?.Enabled ?? true,
            };
            try
            {
                _service.SaveMcpServer(server, editing, http ? headerName.Text : "", http ? headerValue.Password : "");
                headerValue.Password = "";
                ShowList(server.Name, $"Saved {server.Name}. The change applies to new chats.");
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                editorError.Text = ex.Message;
            }
        };
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            headerValue.Password = "";
            ShowList(editing);
        };

        var add = new Button { Content = "New" };
        var edit = new Button { Content = "Edit" };
        var delete = new Button { Content = "Delete" };
        void UpdateButtons() => edit.IsEnabled = delete.IsEnabled = Selected() is not null;
        list.SelectionChanged += (_, _) =>
        {
            confirm.Visibility = Visibility.Collapsed;
            UpdateButtons();
        };
        add.Click += (_, _) => ShowEditor(null);
        edit.Click += (_, _) => ShowEditor(Selected());
        list.DoubleTapped += (_, _) =>
        {
            if (Selected() is { } server)
            {
                ShowEditor(server);
            }
        };
        McpServerSettings? pendingDelete = null;
        delete.Click += (_, _) =>
        {
            pendingDelete = Selected();
            if (pendingDelete is null)
            {
                return;
            }

            confirmText.Text = $"Delete the MCP server '{pendingDelete.Name}' and its saved header value?";
            confirm.Visibility = Visibility.Visible;
        };
        confirmYes.Click += (_, _) =>
        {
            confirm.Visibility = Visibility.Collapsed;
            if (pendingDelete is null || Selected() != pendingDelete)
            {
                return;
            }

            try
            {
                _service.DeleteMcpServer(pendingDelete.Name);
                ShowList(message: $"Deleted {pendingDelete.Name}. The change applies to new chats.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                status.Text = ex.Message;
            }
        };

        listPanel.Children.Add(new TextBlock
        {
            Text = "WindowsAxon connects to the servers that are on when a chat starts and gives their tools to the agent. Changes apply to new chats.",
            TextWrapping = TextWrapping.Wrap,
        });
        listPanel.Children.Add(list);
        listPanel.Children.Add(Row(add, edit, delete));
        listPanel.Children.Add(confirm);
        listPanel.Children.Add(status);
        var content = new StackPanel { Width = 720 };
        content.Children.Add(listPanel);
        content.Children.Add(editorPanel);
        dialog.Content = new ScrollViewer { Content = content, MaxHeight = 560 };
        ShowList();
        UpdateButtons();
        await ShowDialogAsync(dialog, CancellationToken.None);
        headerValue.Password = "";
    }

    private static StackPanel McpRowContent(McpServerSettings server)
    {
        var panel = new StackPanel { Padding = new Thickness(0, 4, 0, 4) };
        panel.Children.Add(new TextBlock { Text = server.Name, FontWeight = FontWeights.SemiBold });
        var target = server.Url ?? string.Join(" ", [server.Command, .. server.Args]);
        var toolText = server.Tools.Count == 0 ? "every tool" : string.Join(", ", server.Tools);
        panel.Children.Add(new TextBlock
        {
            Text = $"{(server.Url is not null ? "HTTP" : "stdio")}: {target}  ·  Tools: {toolText}  ·  {(server.AutoApprove ? "Runs without asking" : "Asks each time")}",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        });
        return panel;
    }
}
