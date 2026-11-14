using WindowsAxon.Agent.Skills;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WindowsAxon.App;

public sealed partial class MainWindow
{
    /// <param name="Info">The loaded skill, or null when the skill was not loaded.</param>
    private sealed record SkillRow(string Name, bool BuiltIn, string Folder, SkillInfo? Info, string? Error);

    private async void Skills_Click(object sender, RoutedEventArgs e)
    {
        SkillLibrary library;
        try
        {
            // The first call builds the tool set to read the tool names, so it runs off the UI thread.
            library = await Task.Run(DesktopChatService.CreateSkillLibrary);
        }
        catch (Exception ex)
        {
            ViewModel.AddEvent("Could not open skills: " + ex.Message);
            return;
        }

        var dialog = new ContentDialog { Title = "Skills", CloseButtonText = "Close" };
        dialog.Resources["ContentDialogMaxWidth"] = 820.0;
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MinHeight = 240, MaxHeight = 320 };
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
            row is { BuiltIn: false }
            && string.Equals(Path.GetDirectoryName(row.Folder), Path.TrimEndingDirectorySeparator(library.UserRoot),
                             StringComparison.OrdinalIgnoreCase);

        // Loading reads every SKILL.md, so it runs off the UI thread. A failure is shown, not thrown.
        async Task<string?> RefreshAsync(string? select)
        {
            SkillCatalog catalog;
            try
            {
                catalog = await Task.Run(() => library.LoadAsync());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                list.Items.Clear();
                return "Could not read the skills: " + ex.Message;
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

        var listPanel = new StackPanel { Spacing = 12 };
        var editorPanel = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
        async Task ShowListAsync(string? select = null, string? message = null)
        {
            HideConfirm();
            status.Text = await RefreshAsync(select) ?? message ?? "";
            editorPanel.Visibility = Visibility.Collapsed;
            listPanel.Visibility = Visibility.Visible;
            dialog.PrimaryButtonText = "";
            dialog.SecondaryButtonText = "";
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
            Height = 280,
            FontFamily = new FontFamily("Consolas"),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(body, ScrollBarVisibility.Auto);
        var editorError = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = ErrorBrush() };
        string? editing = null;
        foreach (var child in new UIElement[]
                 {
                     editorError,
                     name,
                     description,
                     body,
                 })
        {
            editorPanel.Children.Add(child);
        }

        async void ShowEditor(SkillRow? row)
        {
            SkillDraft draft;
            try
            {
                draft = row?.Info is { } info ? await Task.Run(() => SkillLibrary.ReadDraft(info)) : new SkillDraft("", "", "");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                status.Text = $"Cannot open {row!.Name}: {ex.Message} Fix or delete it in the skills folder.";
                return;
            }

            HideConfirm();
            var readOnly = row?.BuiltIn == true;
            editing = row is null || readOnly ? null : row.Name;
            name.Text = draft.Name;
            description.Text = draft.Description;
            body.Text = draft.Body;

            name.IsReadOnly = description.IsReadOnly = body.IsReadOnly = readOnly;
            editorError.Text = readOnly ? "Built-in skills are read-only." : "";
            listPanel.Visibility = Visibility.Collapsed;
            editorPanel.Visibility = Visibility.Visible;
            dialog.PrimaryButtonText = readOnly ? "" : "Save";
            dialog.SecondaryButtonText = "Back";
        }

        // Footer buttons stay visible when the editor is taller than the dialog; Cancel keeps the dialog open.
        dialog.DefaultButton = ContentDialogButton.Primary;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var draft = new SkillDraft(name.Text, description.Text, body.Text);
            dialog.IsPrimaryButtonEnabled = false;
            try
            {
                await Task.Run(() => library.SaveAsync(draft, editing));
                await ShowListAsync(draft.Name.Trim(), $"Saved {draft.Name.Trim()}. The change applies to new chats.");
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                editorError.Text = ex.Message;
            }
            finally
            {
                dialog.IsPrimaryButtonEnabled = true;
            }
        };
        dialog.SecondaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            await ShowListAsync(editing);
        };

        var add = new Button { Content = "New" };
        var edit = new Button { Content = "Edit" };
        var delete = new Button { Content = "Delete" };
        void UpdateButtons()
        {
            var row = Selected();
            edit.IsEnabled = row?.Info is not null;
            edit.Content = row?.BuiltIn == true ? "View" : "Edit";
            delete.IsEnabled = IsPersonalSkill(row);
        }

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
            if (Selected() is not { } row || !IsPersonalSkill(row))
            {
                return;
            }

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
                        await Task.Run(() => library.Delete(Path.GetFileName(row.Folder)));
                        await ShowListAsync(message: $"Deleted {row.Name}.");
                    }
                    catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
                    {
                        status.Text = ex.Message;
                    }
                });
        };

        listPanel.Children.Add(new TextBlock
        {
            Text = $"Personal skills: {library.UserRoot}\nChanges apply to new chats. A personal skill cannot use the name of a built-in skill.",
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        listPanel.Children.Add(list);
        listPanel.Children.Add(Row(add, edit, delete));
        listPanel.Children.Add(confirm);
        listPanel.Children.Add(status);
        var content = new StackPanel { Width = 720 };
        content.Children.Add(listPanel);
        content.Children.Add(editorPanel);
        dialog.Content = content;
        await ShowListAsync();
        UpdateButtons();
        await ShowDialogAsync(dialog, CancellationToken.None);
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
