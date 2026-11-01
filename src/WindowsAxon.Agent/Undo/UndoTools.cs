using System.ComponentModel;
using System.Text;
using WindowsAxon.Agent.Tools;

namespace WindowsAxon.Agent.Undo;

/// <summary>undo_list and undo_last.</summary>
public sealed class UndoTools(UndoJournal journal, IEnumerable<IUndoRestorer> restorers, string session)
{
    private readonly Dictionary<string, IUndoRestorer> _restorers = restorers.ToDictionary(r => r.Kind);

    [Tool("undo_list",
          "utilities",
          "List the changes that WindowsAxon made in the last 7 days, newest first.")]
    public string UndoList()
    {
        IReadOnlyList<UndoEntry> entries = journal.List();

        if (entries.Count == 0)
        {
            return "WindowsAxon made no changes in the last 7 days.";
        }

        StringBuilder text = new StringBuilder();

        foreach (var entry in entries)
        {
            var superseded = journal.IsNewest(entry) ? "" : " (superseded)";
            text.Append($"{entry.Id} {entry.Time.ToLocalTime():yyyy-MM-dd HH:mm} {entry.Tool} {entry.Target}: {entry.PreviousValue} -> {entry.NewValue}{superseded}\n");
        }

        return text.ToString();
    }

    [Tool("undo_last",
          "utilities",
          "Undo one change that WindowsAxon made. Without id, undo the newest change that can be undone.",
          RiskLevel.Change)]
    public string UndoLast([Description("ID from undo_list. Optional.")] string? id = null,
                           [Description("Restore even if the setting was changed after WindowsAxon changed it.")] bool force = false)
    {
        UndoEntry? entry = id is null
            ? journal.List().FirstOrDefault(e => _restorers.ContainsKey(e.Kind) && journal.IsNewest(e))
            : journal.Find(id);

        if (entry is null)
        {
            return id is null ? "There is no change to undo." : $"No change with ID {id} in the last 7 days.";
        }

        if (!journal.IsNewest(entry))
        {
            return $"This change is superseded by a later change to {entry.Target}. Undo the newest change first.";
        }

        if (!_restorers.TryGetValue(entry.Kind, out var restorer))
        {
            return $"WindowsAxon cannot undo {entry.Tool}.";
        }

        string? current = restorer.ReadCurrent(entry.Target);

        if (!force && current != entry.NewValue)
        {
            return $"This setting was changed after WindowsAxon changed it. Current: {current}. To restore {entry.PreviousValue} anyway, call undo_last with force=true.";
        }

        restorer.Restore(entry.Target, entry.PreviousValue); // if this throws, the journal is unchanged
        journal.Record(session, entry.Kind, "undo_last", entry.Target, current, entry.PreviousValue);

        return $"Restored {entry.Target} to {entry.PreviousValue}.";
    }
}
