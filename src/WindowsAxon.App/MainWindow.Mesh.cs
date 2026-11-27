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
            ViewModel.SelectConversation(null);
            RefreshMeshStatus();
            return;
        }

        var self = _service.EffectiveMeshNodeName;
        var online = peers.Where(p => !IsSelf(p)).ToList();
        var offline = ViewModel.PeersWithConversation
            .Where(p => !IsSelf(p) && !online.Contains(p, StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The row for this computer opens the local chat.
        AddPeerRow(self, $"{self} (this computer)", isOnline: true);
        foreach (var peer in online)
        {
            AddPeerRow(peer, peer, isOnline: true);
        }

        if (online.Count == 0)
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

        foreach (var peer in offline)
        {
            AddPeerRow(peer, $"{peer} (offline)", isOnline: false);
        }

        var active = ViewModel.ActivePeer ?? self;
        OnlinePeersList.SelectedItem = OnlinePeersList.Items
            .OfType<ListViewItem>()
            .FirstOrDefault(item => item.Tag is string tag && string.Equals(tag, active, StringComparison.OrdinalIgnoreCase));

        RefreshMeshStatus();

        bool IsSelf(string peer) => string.Equals(peer, self, StringComparison.OrdinalIgnoreCase);
    }

    private void AddPeerRow(string peer, string label, bool isOnline)
    {
        // ItemClick reports the item's Content, so the row needs the tag too.
        var row = new StackPanel
        {
            Tag = peer,
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Padding = new Thickness(0, 2, 0, 2),
            Opacity = isOnline ? 1 : 0.6,
        };
        row.Children.Add(new FontIcon { Glyph = "", FontSize = 12 });
        row.Children.Add(new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis });
        if (ViewModel.UnreadFor(peer) is > 0 and var unread)
        {
            row.Children.Add(new InfoBadge { Value = unread, VerticalAlignment = VerticalAlignment.Center });
        }

        OnlinePeersList.Items.Add(new ListViewItem
        {
            Tag = peer,
            Content = row,
            MinHeight = 28,
            Padding = new Thickness(6, 2, 6, 2),
        });
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
            var isSelf = string.Equals(peer, _service.EffectiveMeshNodeName, StringComparison.OrdinalIgnoreCase);
            ViewModel.SelectConversation(isSelf ? null : peer);
            CommandInput.Focus(FocusState.Programmatic);
        }
    }

    private SettingsTabPanel CreateNetworkTab()
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
            PasswordRevealMode = PasswordRevealMode.Peek,
        };
        var removePairKey = new CheckBox
        {
            Content = "Remove saved pair key",
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
            PasswordRevealMode = PasswordRevealMode.Hidden,
        };
        var removeToken = new CheckBox
        {
            Content = "Remove saved hub token",
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

        void RefreshSecretIndicators(MeshSettings current)
        {
            pairKey.PlaceholderText = string.IsNullOrEmpty(current.PairKey)
                ? "Enter the same pair key on every computer in this LAN"
                : "Leave blank to keep saved pair key";
            removePairKey.Visibility = string.IsNullOrEmpty(current.PairKey) ? Visibility.Collapsed : Visibility.Visible;
            token.PlaceholderText = string.IsNullOrEmpty(current.Token)
                ? "Optional"
                : "Leave blank to keep saved token";
            removeToken.Visibility = string.IsNullOrEmpty(current.Token) ? Visibility.Collapsed : Visibility.Visible;
        }

        RefreshSecretIndicators(mesh);

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

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(SectionHeading("Network (Distributed WindowsAxon)"));
        content.Children.Add(modeSelector);
        content.Children.Add(standaloneHint);
        content.Children.Add(nodeName);
        content.Children.Add(p2pPanel);
        content.Children.Add(hubPanel);
        content.Children.Add(statusText);

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
                var mode = SelectedMode();
                _service.SaveMeshSettings(mode,
                                          nodeName.Text,
                                          hubUrl.Text,
                                          token.Password,
                                          removeToken.IsChecked == true,
                                          pairKey.Password,
                                          removePairKey.IsChecked == true);
                token.Password = "";
                pairKey.Password = "";
                removeToken.IsChecked = false;
                removePairKey.IsChecked = false;
                RefreshSecretIndicators(_service.Settings.Chat.Mesh);
                RefreshOnlinePeers(_service.OnlinePeers);
                RefreshMeshStatus();
                ViewModel.AddEvent(mode switch
                {
                    MeshMode.PeerToPeer => $"Peer-to-peer mode enabled as '{_service.EffectiveMeshNodeName}'.",
                    MeshMode.CentralServer => $"Central server mode enabled as '{_service.EffectiveMeshNodeName}' ({_service.Settings.Chat.Mesh.HubUrl}).",
                    _ => "Network disabled (standalone mode).",
                });
                SetFeedback(feedback, "Network settings saved.", isError: false);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                SetFeedback(feedback, ex.Message, isError: true);
            }
        };

        var panel = CreateScrollableTab(content, ActionFooter(feedback, saveButton));

        return new SettingsTabPanel(panel,
                                    Cleanup: () =>
                                    {
                                        _service.MeshStateChanged -= OnStateChanged;
                                        token.Password = "";
                                        pairKey.Password = "";
                                    });
    }
}
