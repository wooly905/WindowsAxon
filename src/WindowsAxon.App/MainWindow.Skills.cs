using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WindowsAxon.Agent.Skills;

namespace WindowsAxon.App;

public sealed partial class MainWindow
{
    /// <param name="Info">The loaded skill, or null when the skill was not loaded.</param>
    private sealed record SkillRow(string Name, bool BuiltIn, string Folder, SkillInfo? Info, string? Error);

    private SettingsTabPanel CreateSkillsTab()
    {
        var cts = new CancellationTokenSource();
        var dialogToken = cts.Token;
        SkillLibrary? library = null;
        var loadGeneration = 0;
        var isEditorOpen = false;

        var loadingRing = new ProgressRing { IsActive = false, Width = 24, Height = 24 };
        var loadingPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        loadingPanel.Children.Add(loadingRing);
        loadingPanel.Children.Add(new TextBlock { Text = "Loading skills...", VerticalAlignment = VerticalAlignment.Center });

        var list = new ListView { SelectionMode = ListViewSelectionMode.Single };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var confirmText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        var confirmYes = new Button { Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var confirmNo = new Button { Content = "Cancel" };
        var confirm = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        confirm.Children.Add(confirmText);
        confirm.Children.Add(Row(confirmYes, confirmNo));
        Action? onConfirm = null;
        confirmYes.Click += (_, _) =>
        {
            confirm.Visibility = Visibility.Collapsed;
            onConfirm?.Invoke();
        };
        confirmNo.Click += (_, _) => confirm.Visibility = Visibility.Collapsed;

        void HideConfirm()
        {
            confirm.Visibility = Visibility.Collapsed;
            onConfirm = null;
        }

        void Ask(string message, string yes, Action action)
        {
            confirmText.Text = message;
            confirmYes.Content = yes;
            onConfirm = action;
            confirm.Visibility = Visibility.Visible;
        }

        SkillRow? Selected() => (list.SelectedItem as ListViewItem)?.Tag as SkillRow;

        bool IsPersonalSkill(SkillRow? row) =>
            library is not null
            && row is { BuiltIn: false }
            && string.Equals(Path.GetDirectoryName(row.Folder),
                             Path.TrimEndingDirectorySeparator(library.UserRoot),
                             StringComparison.OrdinalIgnoreCase);

        var add = new Button { Content = "New", IsEnabled = false };
        var edit = new Button { Content = "Edit", IsEnabled = false };
        var delete = new Button { Content = "Delete", IsEnabled = false };

        void UpdateButtons()
        {
            var row = Selected();
            add.IsEnabled = library is not null;
            edit.IsEnabled = library is not null && row?.Info is not null;
            edit.Content = row?.BuiltIn == true ? "View" : "Edit";
            delete.IsEnabled = IsPersonalSkill(row);
        }

        var listViewRoot = new Grid { RowSpacing = 12 };
        listViewRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listViewRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        listViewRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var editorHeading = SectionHeading("Skill");
        var editorFields = new StackPanel { Spacing = 12 };
        var saveButton = new Button
        {
            Content = "Save",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        var backButton = new Button { Content = "Back" };
        var editorViewRoot = CreateScrollableTab(editorFields, ActionFooter(Row(saveButton, backButton)));
        editorViewRoot.Visibility = Visibility.Collapsed;

        // Loading reads every SKILL.md, so it runs off the UI thread. A failure is shown, not thrown.
        async Task<string?> RefreshAsync(string? select)
        {
            if (library is null)
            {
                return null;
            }

            var currentLibrary = library;
            SkillCatalog catalog;

            try
            {
                catalog = await Task.Run(() => currentLibrary.LoadAsync(), dialogToken);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                list.Items.Clear();

                return "Could not read the skills: " + ex.Message;
            }

            if (dialogToken.IsCancellationRequested)
            {
                return null;
            }

            var rows = catalog.Skills.Select(s => new SkillRow(s.Name, s.BuiltIn, s.Folder, s, null))
                .Concat(catalog.Problems.Select(p => new SkillRow(Path.GetFileName(p.Folder), p.BuiltIn, p.Folder, null, p.Message)))
                .OrderBy(r => !r.BuiltIn)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            list.Items.Clear();

            foreach (var row in rows)
            {
                var item = new ListViewItem { Tag = row, Content = SkillRowContent(row) };
                list.Items.Add(item);

                if (row.Name == select && !row.BuiltIn)
                {
                    list.SelectedItem = item;
                }
            }

            return null;
        }

        async Task ShowListAsync(string? select = null, string? message = null)
        {
            HideConfirm();
            isEditorOpen = false;
            var errorMessage = await RefreshAsync(select);

            if (dialogToken.IsCancellationRequested)
            {
                return;
            }

            status.Text = errorMessage ?? message ?? "";
            loadingRing.IsActive = false;
            loadingPanel.Visibility = Visibility.Collapsed;
            editorViewRoot.Visibility = Visibility.Collapsed;
            listViewRoot.Visibility = Visibility.Visible;
            UpdateButtons();
        }

        async void LoadOnSelected()
        {
            if (isEditorOpen)
            {
                return;
            }

            var generation = ++loadGeneration;

            if (library is null)
            {
                listViewRoot.Visibility = Visibility.Collapsed;
                editorViewRoot.Visibility = Visibility.Collapsed;
                loadingPanel.Visibility = Visibility.Visible;
                loadingRing.IsActive = true;
            }

            try
            {
                // The first call builds the tool set to read the tool names, so it runs off the UI thread.
                var created = await Task.Run(_service.CreateSkillLibrary, dialogToken);

                if (generation != loadGeneration || dialogToken.IsCancellationRequested)
                {
                    return;
                }

                library = created;
                await ShowListAsync(Selected()?.Name);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (generation != loadGeneration || dialogToken.IsCancellationRequested)
                {
                    return;
                }

                loadingRing.IsActive = false;
                loadingPanel.Visibility = Visibility.Collapsed;
                listViewRoot.Visibility = Visibility.Visible;
                status.Text = "Could not open skills: " + ex.Message;
                UpdateButtons();
            }
        }

        var name = new TextBox { Header = "Name", PlaceholderText = "lowercase-with-hyphens" };
        var description = new TextBox
        {
            Header = "Description",
            PlaceholderText = "What the skill does and when to use it",
            TextWrapping = TextWrapping.Wrap,
        };
        var body = new TextBox
        {
            Header = "Instructions",
            PlaceholderText = "Steps for the model to follow",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 260,
            FontFamily = new FontFamily("Consolas"),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(body, ScrollBarVisibility.Auto);
        var editorError = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = ErrorBrush() };
        string? editing = null;

        foreach (var child in new UIElement[]
                 {
                     editorHeading,
                     editorError,
                     name,
                     description,
                     body,
                 })
        {
            editorFields.Children.Add(child);
        }

        async void ShowEditor(SkillRow? row)
        {
            SkillDraft draft;

            try
            {
                draft = row?.Info is { } info ? await Task.Run(() => SkillLibrary.ReadDraft(info), dialogToken) : new SkillDraft("", "", "");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                status.Text = $"Cannot open {row!.Name}: {ex.Message} Fix or delete it in the skills folder.";

                return;
            }

            if (dialogToken.IsCancellationRequested)
            {
                return;
            }

            HideConfirm();
            var readOnly = row?.BuiltIn == true;
            editing = row is null || readOnly ? null : row.Name;
            editorHeading.Text = readOnly ? "View skill" : row is null ? "New skill" : "Edit skill";
            name.Text = draft.Name;
            description.Text = draft.Description;
            body.Text = draft.Body;

            name.IsReadOnly = description.IsReadOnly = body.IsReadOnly = readOnly;
            editorError.Text = readOnly ? "Built-in skills are read-only." : "";
            saveButton.Visibility = readOnly ? Visibility.Collapsed : Visibility.Visible;
            isEditorOpen = true;
            listViewRoot.Visibility = Visibility.Collapsed;
            editorViewRoot.Visibility = Visibility.Visible;
        }

        saveButton.Click += async (_, _) =>
        {
            if (library is null)
            {
                return;
            }

            var currentLibrary = library;
            var draft = new SkillDraft(name.Text, description.Text, body.Text);
            saveButton.IsEnabled = false;
            backButton.IsEnabled = false;

            try
            {
                await Task.Run(() => currentLibrary.SaveAsync(draft, editing), dialogToken);
                await ShowListAsync(draft.Name.Trim(), $"Saved {draft.Name.Trim()}. The change applies to new chats.");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                editorError.Text = ex.Message;
            }
            finally
            {
                if (!dialogToken.IsCancellationRequested)
                {
                    saveButton.IsEnabled = true;
                    backButton.IsEnabled = true;
                }
            }
        };
        backButton.Click += async (_, _) => await ShowListAsync(editing);

        // A pending delete belongs to the row that was selected when Delete was clicked.
        list.SelectionChanged += (_, _) =>
        {
            HideConfirm();
            UpdateButtons();
        };
        add.Click += (_, _) => ShowEditor(null);
        edit.Click += (_, _) => ShowEditor(Selected());
        list.DoubleTapped += (_, _) =>
        {
            if (Selected()?.Info is not null)
            {
                ShowEditor(Selected());
            }
        };
        delete.Click += (_, _) =>
        {
            if (library is null || Selected() is not { } row || !IsPersonalSkill(row))
            {
                return;
            }

            var currentLibrary = library;
            Ask($"Delete the personal skill '{row.Name}' and every file in its folder? This cannot be undone.",
                "Delete",
                async () =>
                {
                    if (Selected() != row)
                    {
                        return;
                    }

                    try
                    {
                        await Task.Run(() => currentLibrary.Delete(Path.GetFileName(row.Folder)), dialogToken);
                        await ShowListAsync(message: $"Deleted {row.Name}.");
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
                    {
                        status.Text = ex.Message;
                    }
                });
        };

        var listHeader = new StackPanel { Spacing = 8 };
        listHeader.Children.Add(SectionHeading("Skills"));
        listHeader.Children.Add(new TextBlock
        {
            Text = $"Personal skills: {DesktopChatService.UserSkillsFolder}\nChanges apply to new chats. A personal skill cannot use the name of a built-in skill.",
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
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
        root.Children.Add(loadingPanel);

        return new SettingsTabPanel(root,
                                    OnSelected: LoadOnSelected,
                                    Cleanup: () =>
                                    {
                                        cts.Cancel();
                                        cts.Dispose();
                                    });
    }

    private static StackPanel SkillRowContent(SkillRow row)
    {
        var panel = new StackPanel { Padding = new Thickness(0, 4, 0, 4) };
        panel.Children.Add(new TextBlock
        {
            Text = $"{row.Name}  ·  {(row.BuiltIn ? "Built-in" : "Personal")}",
            FontWeight = FontWeights.SemiBold,
        });

        if (row.Error is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Not loaded: " + row.Error,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = ErrorBrush(),
            });
        }

        return panel;
    }

    private static Brush ErrorBrush() => (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        foreach (var child in children)
        {
            row.Children.Add(child);
        }

        return row;
    }
}
