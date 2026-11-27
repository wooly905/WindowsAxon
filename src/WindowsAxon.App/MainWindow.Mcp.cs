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

    private SettingsTabPanel CreateMcpTab()
    {
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        McpServerSettings? Selected() => (list.SelectedItem as ListViewItem)?.Tag as McpServerSettings;

        var confirmText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        var confirmYes = new Button { Content = "Delete", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var confirmNo = new Button { Content = "Cancel" };
        var confirm = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        confirm.Children.Add(confirmText);
        confirm.Children.Add(Row(confirmYes, confirmNo));
        confirmNo.Click += (_, _) => confirm.Visibility = Visibility.Collapsed;

        var listViewRoot = new Grid { RowSpacing = 12 };
        listViewRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listViewRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        listViewRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var editorHeading = SectionHeading("MCP server");
        var editorFields = new StackPanel { Spacing = 12 };

        var saveButton = new Button
        {
            Content = "Save",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        var backButton = new Button { Content = "Back" };
        var editorViewRoot = CreateScrollableTab(editorFields, ActionFooter(Row(saveButton, backButton)));
        editorViewRoot.Visibility = Visibility.Collapsed;

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
            editorViewRoot.Visibility = Visibility.Collapsed;
            listViewRoot.Visibility = Visibility.Visible;
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

        foreach (var child in new UIElement[]
                 {
                     editorHeading,
                     editorError,
                     name,
                     description,
                     transport,
                     httpFields,
                     stdioFields,
                     tools,
                     autoApprove,
                     autoApproveNote,
                 })
        {
            editorFields.Children.Add(child);
        }

        editorFields.Children.Add(new TextBlock
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
            editorHeading.Text = server is null ? "New MCP server" : "Edit MCP server";
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
            listViewRoot.Visibility = Visibility.Collapsed;
            editorViewRoot.Visibility = Visibility.Visible;
        }

        static List<string> Split(string text, params char[] separators) =>
            [.. text.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        saveButton.Click += (_, _) =>
        {
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
        backButton.Click += (_, _) =>
        {
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

        var listHeader = new StackPanel { Spacing = 8 };
        listHeader.Children.Add(SectionHeading("MCP servers"));
        listHeader.Children.Add(new TextBlock
        {
            Text = "WindowsAxon connects to the servers that are on when a chat starts and gives their tools to the agent. Changes apply to new chats.",
            TextWrapping = TextWrapping.Wrap,
        });

        var listFooter = new StackPanel { Spacing = 8 };
        listFooter.Children.Add(Row(add, edit, delete));
        listFooter.Children.Add(confirm);
        listFooter.Children.Add(status);

        Grid.SetRow(list, 1);
        Grid.SetRow(listFooter, 2);
        listViewRoot.Children.Add(listHeader);
        listViewRoot.Children.Add(list);
        listViewRoot.Children.Add(listFooter);

        var root = new Grid();
        root.Children.Add(listViewRoot);
        root.Children.Add(editorViewRoot);

        ShowList();
        UpdateButtons();

        return new SettingsTabPanel(root, Cleanup: () => headerValue.Password = "");
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
