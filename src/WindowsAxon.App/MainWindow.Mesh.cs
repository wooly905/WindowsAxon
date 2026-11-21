using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WindowsAxon.Mesh;

namespace WindowsAxon.App;

public sealed partial class MainWindow
{
    private void RefreshOnlinePeers(IReadOnlyList<string> peers)
    {
        OnlinePeersList.Items.Clear();
        var self = _service.Settings.Chat.Mesh.EffectiveNodeName;
        foreach (var peer in peers)
        {
            var isSelf = string.Equals(peer, self, StringComparison.OrdinalIgnoreCase);
            var label = isSelf ? $"{peer} (this computer)" : peer;
            var row = new StackPanel
            {
                Tag = peer,
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Padding = new Thickness(0, 2, 0, 2),
            };
            row.Children.Add(new FontIcon { Glyph = "\uE7F8", FontSize = 12 });
            row.Children.Add(new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis });
            OnlinePeersList.Items.Add(new ListViewItem
            {
                Tag = peer,
                Content = row,
                MinHeight = 28,
                Padding = new Thickness(6, 2, 6, 2),
            });
        }

        if (peers.Count == 0)
        {
            OnlinePeersList.Items.Add(new ListViewItem
            {
                Content = new TextBlock
                {
                    Text = _service.Settings.Chat.Mesh.Enabled ? "No computers online." : "Disabled",
                    FontSize = 12,
                },
                IsEnabled = false,
                MinHeight = 24,
                Padding = new Thickness(6, 2, 6, 2),
            });
        }

        RefreshMeshStatus();
    }

    private void RefreshMeshStatus()
    {
        MeshStatusBadge.Text = _service.MeshState switch
        {
            MeshConnectionState.Connected => $"{_service.OnlinePeers.Count} online",
            MeshConnectionState.Connecting => "Connecting...",
            MeshConnectionState.Error => "Offline",
            _ => _service.Settings.Chat.Mesh.Enabled ? "Offline" : "Off",
        };
    }

    private void OnlinePeersList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FrameworkElement { Tag: string peer })
        {
            InsertInput($"@{peer} ");
        }
    }

    private async void Network_Click(object sender, RoutedEventArgs e)
    {
        var mesh = _service.Settings.Chat.Mesh;
        var enabled = new ToggleSwitch
        {
            Header = "Distributed mode (connect to WindowsAxon.Hub)",
            IsOn = mesh.Enabled,
        };
        var hubUrl = new TextBox
        {
            Header = "Hub URL",
            PlaceholderText = "ws://192.168.1.10:5250/ws or wss://hub.example.com/ws",
            Text = mesh.HubUrl,
        };
        var nodeName = new TextBox
        {
            Header = "This computer's name",
            PlaceholderText = Environment.MachineName,
            Text = mesh.NodeName,
        };
        var token = new PasswordBox
        {
            Header = "Hub token",
            PlaceholderText = string.IsNullOrEmpty(mesh.Token) ? "Optional" : "Leave blank to keep saved token",
            PasswordRevealMode = PasswordRevealMode.Hidden,
        };
        var removeToken = new CheckBox
        {
            Content = "Remove saved hub token",
            Visibility = string.IsNullOrEmpty(mesh.Token) ? Visibility.Collapsed : Visibility.Visible,
        };
        removeToken.Checked += (_, _) =>
        {
            token.Password = "";
            token.IsEnabled = false;
        };
        removeToken.Unchecked += (_, _) => token.IsEnabled = true;

        var statusText = new TextBlock
        {
            Text = $"Status: {_service.MeshStatusMessage}",
            TextWrapping = TextWrapping.Wrap,
        };

        void OnStateChanged(MeshConnectionState _, string message) => DispatcherQueue.TryEnqueue(() => statusText.Text = $"Status: {message}");

        _service.MeshStateChanged += OnStateChanged;

        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(enabled);
        content.Children.Add(hubUrl);
        content.Children.Add(nodeName);
        content.Children.Add(token);
        content.Children.Add(removeToken);
        content.Children.Add(statusText);
        content.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = "Network (Distributed WindowsAxon)",
            Content = new ScrollViewer { Content = content, MaxHeight = 480 },
            PrimaryButtonText = "Save",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                _service.SaveMeshSettings(enabled.IsOn,
                                          hubUrl.Text,
                                          nodeName.Text,
                                          token.Password,
                                          removeToken.IsChecked == true);
                RefreshOnlinePeers(_service.OnlinePeers);
                RefreshMeshStatus();
                ViewModel.AddEvent(enabled.IsOn
                    ? $"Mesh enabled as '{_service.Settings.Chat.Mesh.EffectiveNodeName}' ({_service.Settings.Chat.Mesh.HubUrl})."
                    : "Mesh disabled.");
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                error.Text = ex.Message;
                args.Cancel = true;
            }
        };

        try
        {
            await ShowDialogAsync(dialog, CancellationToken.None);
        }
        finally
        {
            _service.MeshStateChanged -= OnStateChanged;
            token.Password = "";
        }
    }
}
