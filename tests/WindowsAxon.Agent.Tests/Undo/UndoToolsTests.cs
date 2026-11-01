using WindowsAxon.Agent.Tests.Fakes;
using WindowsAxon.Agent.Tools;
using WindowsAxon.Agent.Undo;

namespace WindowsAxon.Agent.Tests.Undo;

public class UndoToolsTests
{
    private sealed class FakeWallpaper : IUndoRestorer
    {
        public string Value { get; set; } = "b.jpg";

        public string Kind => "wallpaper";

        public string? ReadCurrent(string target) => Value;

        public void Restore(string target, string? previousValue) => Value = previousValue!;
    }

    private static (UndoTools Tools, UndoJournal Journal, FakeWallpaper Wallpaper, ManualTimeProvider Time) Create()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero));
        var journal = new UndoJournal(Directory.CreateTempSubdirectory("windowsaxon-undo-").FullName, time);
        var wallpaper = new FakeWallpaper();
        journal.Record("s1", "wallpaper", "wallpaper_set", "wallpaper", "a.jpg", "b.jpg");
        time.Now = time.Now.AddMinutes(1);
        return (new UndoTools(journal, [wallpaper], "s2"), journal, wallpaper, time);
    }

    [Fact]
    public void UndoLast_restores_the_previous_value_and_records_the_undo()
    {
        var (tools, journal, wallpaper, _) = Create();

        var result = tools.UndoLast();

        Assert.Equal("a.jpg", wallpaper.Value);
        Assert.Equal("Restored wallpaper to a.jpg.", result);
        Assert.Equal("undo_last", journal.List()[0].Tool);
    }

    [Fact]
    public void An_entry_that_was_already_undone_is_superseded()
    {
        var (tools, journal, _, _) = Create();
        var original = journal.List().Single();
        tools.UndoLast();

        var result = tools.UndoLast(original.Id);

        Assert.Equal("This change is superseded by a later change to wallpaper. Undo the newest change first.", result);
    }

    [Fact]
    public void A_setting_changed_elsewhere_is_not_restored_without_force()
    {
        var (tools, _, wallpaper, _) = Create();
        wallpaper.Value = "c.jpg";

        var result = tools.UndoLast();

        Assert.Equal("c.jpg", wallpaper.Value);
        Assert.Equal("This setting was changed after WindowsAxon changed it. Current: c.jpg. To restore a.jpg anyway, call undo_last with force=true.",
                     result);

        tools.UndoLast(force: true);
        Assert.Equal("a.jpg", wallpaper.Value);
    }

    [Fact]
    public void UndoList_shows_entries_and_marks_superseded_ones()
    {
        var (tools, _, _, _) = Create();
        tools.UndoLast();

        var lines = tools.UndoList().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Contains("undo_last wallpaper: b.jpg -> a.jpg", lines[0]);
        Assert.EndsWith("(superseded)", lines[1]);
    }

    [Fact]
    public void Empty_journal_says_so()
    {
        var journal = new UndoJournal(Directory.CreateTempSubdirectory("windowsaxon-undo-").FullName, TimeProvider.System);
        var tools = new UndoTools(journal, [], "s1");

        Assert.Equal("WindowsAxon made no changes in the last 7 days.", tools.UndoList());
        Assert.Equal("There is no change to undo.", tools.UndoLast());
    }

    [Fact]
    public void Register_adds_undo_list_as_read_and_undo_last_as_change()
    {
        var catalog = new ToolCatalog();
        ToolRegistration.Add(catalog, new UndoTools(new UndoJournal(Path.GetTempPath(), TimeProvider.System), [], "s1"));

        Assert.Equal(RiskLevel.Read, catalog.Find("undo_list")!.Risk);
        Assert.Equal(RiskLevel.Change, catalog.Find("undo_last")!.Risk);
        Assert.Equal("utilities", catalog.Find("undo_last")!.Group);
    }
}
