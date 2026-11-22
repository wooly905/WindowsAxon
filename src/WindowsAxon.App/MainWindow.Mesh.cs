using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WindowsAxon.Agent.Settings;
using WindowsAxon.Mesh;

namespace WindowsAxon.App;

public sealed partial class MainWindow
{
    private void RefreshOnlinePeers(IReadOnlyList<string> peers)
    {
        var enabled = _service.Settings.Chat.Mesh.IsEnabled;
        OnlinePeersPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        OnlinePeersList.Items.Clear();
        if (!enabled)
        {
            RefreshMeshStatus();
            return;
        }

        var self = _service.EffectiveMeshNodeName;
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
                    Text = "No computers online.",
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
        OnlinePeersPanel.Visibility = _service.Settings.Chat.Mesh.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        MeshStatusBadge.Text = _service.MeshState switch
        {
            MeshConnectionState.Connected => $"{_service.OnlinePeers.Count} online",
            MeshConnectionState.Connecting => "Connecting...",
            MeshConnectionState.Error => "Offline",
            _ => _service.Settings.Chat.Mesh.IsEnabled ? "Offline" : "Off",
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
        var modeSelector = new RadioButtons
        {
            Header = "Network mode",
            Items =
            {
                new RadioButton { Content = "Disabled (standalone)", Tag = MeshMode.Disabled },
                new RadioButton { Content = "Peer-to-Peer (same LAN)", Tag = MeshMode.PeerToPeer },
                new RadioButton { Content = "Central Server (Internet)", Tag = MeshMode.CentralServer },
            },
            SelectedIndex = mesh.EffectiveMode switch
            {
                MeshMode.PeerToPeer => 1,
                MeshMode.CentralServer => 2,
                _ => 0,
            },
        };

        var standaloneHint = new TextBlock
        {
            Text = "Standalone mode. Network discovery and remote requests are turned off.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        };

        var nodeName = new TextBox
        {
            Header = "This computer's name",
            PlaceholderText = Environment.MachineName,
            Text = mesh.NodeName,
        };

        // Peer-to-Peer controls
        var pairKey = new PasswordBox
        {
            Header = "Pair key",
            PlaceholderText = string.IsNullOrEmpty(mesh.PairKey)
                ? "Enter the same pair key on every computer in this LAN"
                : "Leave blank to keep saved pair key",
            PasswordRevealMode = PasswordRevealMode.Peek,
        };
        var removePairKey = new CheckBox
        {
            Content = "Remove saved pair key",
            Visibility = string.IsNullOrEmpty(mesh.PairKey) ? Visibility.Collapsed : Visibility.Visible,
        };
        removePairKey.Checked += (_, _) =>
        {
            pairKey.Password = "";
            pairKey.IsEnabled = false;
        };
        removePairKey.Unchecked += (_, _) => pairKey.IsEnabled = true;

        var p2pHint = new TextBlock
        {
            Text = "Computers on the same LAN with the same pair key discover each other automatically and communicate over an encrypted connection.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        };
        var p2pPanel = new StackPanel { Spacing = 10 };
        p2pPanel.Children.Add(pairKey);
        p2pPanel.Children.Add(removePairKey);
        p2pPanel.Children.Add(p2pHint);

        // Central Server controls
        var hubUrl = new TextBox
        {
            Header = "Hub URL",
            PlaceholderText = "ws://192.168.1.10:5250/ws or wss://hub.example.com/ws",
            Text = mesh.HubUrl,
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

        var hubPanel = new StackPanel { Spacing = 10 };
        hubPanel.Children.Add(hubUrl);
        hubPanel.Children.Add(token);
        hubPanel.Children.Add(removeToken);

        MeshMode SelectedMode() => modeSelector.SelectedIndex switch
        {
            1 => MeshMode.PeerToPeer,
            2 => MeshMode.CentralServer,
            _ => MeshMode.Disabled,
        };

        void UpdateModeVisibility()
        {
            var mode = SelectedMode();
            standaloneHint.Visibility = mode == MeshMode.Disabled ? Visibility.Visible : Visibility.Collapsed;
            nodeName.Visibility = mode != MeshMode.Disabled ? Visibility.Visible : Visibility.Collapsed;
            p2pPanel.Visibility = mode == MeshMode.PeerToPeer ? Visibility.Visible : Visibility.Collapsed;
            hubPanel.Visibility = mode == MeshMode.CentralServer ? Visibility.Visible : Visibility.Collapsed;
        }

        modeSelector.SelectionChanged += (_, _) => UpdateModeVisibility();
        UpdateModeVisibility();

        var statusText = new TextBlock
        {
            Text = $"Status: {_service.MeshStatusMessage}",
            TextWrapping = TextWrapping.Wrap,
        };

        void OnStateChanged(MeshConnectionState _, string message) => DispatcherQueue.TryEnqueue(() => statusText.Text = $"Status: {message}");

        _service.MeshStateChanged += OnStateChanged;

        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(modeSelector);
        content.Children.Add(standaloneHint);
        content.Children.Add(nodeName);
        content.Children.Add(p2pPanel);
        content.Children.Add(hubPanel);
        content.Children.Add(statusText);
        content.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = "Network (Distributed WindowsAxon)",
            Content = new ScrollViewer { Content = content, MaxHeight = 520 },
            PrimaryButtonText = "Save",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            error.Text = "";
            try
            {
                var mode = SelectedMode();
                _service.SaveMeshSettings(mode,
                                          nodeName.Text,
                                          hubUrl.Text,
                                          token.Password,
                                          removeToken.IsChecked == true,
                                          pairKey.Password,
                                          removePairKey.IsChecked == true);
                RefreshOnlinePeers(_service.OnlinePeers);
                RefreshMeshStatus();
                ViewModel.AddEvent(mode switch
                {
                    MeshMode.PeerToPeer => $"Peer-to-peer mode enabled as '{_service.EffectiveMeshNodeName}'.",
                    MeshMode.CentralServer => $"Central server mode enabled as '{_service.EffectiveMeshNodeName}' ({_service.Settings.Chat.Mesh.HubUrl}).",
                    _ => "Network disabled (standalone mode).",
                });
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
            pairKey.Password = "";
        }
    }
}
