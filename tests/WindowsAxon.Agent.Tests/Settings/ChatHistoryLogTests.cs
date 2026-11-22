using System.Text.Json;
using WindowsAxon.Agent;
using WindowsAxon.App;

namespace WindowsAxon.Agent.Tests.Settings;

public sealed class ChatHistoryLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"windowsaxon-chat-history-{Guid.NewGuid():N}");

    [Fact]
    public void Append_writes_local_outgoing_and_incoming_entries_to_same_daily_jsonl_file()
    {
        var log = new ChatHistoryLog(_folder);
        var day = new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 21)));

        log.Append(new ChatHistoryEntry(day, ChatDirection.Local, "PC-A", null, "show disk usage", "100 GB free", RunStatus.Answered, 2, ["system_info"]));
        log.Append(new ChatHistoryEntry(day.AddMinutes(1), ChatDirection.Outgoing, "PC-A", "PC-B", "open Paint", "Opened Paint", RunStatus.Answered, 1, ["app_launch"]));
        log.Append(new ChatHistoryEntry(day.AddMinutes(2), ChatDirection.Incoming, "PC-A", "PC-C", "check network", "Online", RunStatus.Answered, 1, ["network_info", "ping_host"]));

        var path = Path.Combine(_folder, "chat-20261121.jsonl");
        Assert.Equal(path, log.PathFor(day));
        var lines = File.ReadAllLines(path);
        Assert.Equal(3, lines.Length);

        using var localDoc = JsonDocument.Parse(lines[0]);
        Assert.Equal("local", localDoc.RootElement.GetProperty("direction").GetString());
        Assert.Equal("PC-A", localDoc.RootElement.GetProperty("localNode").GetString());
        Assert.Equal(JsonValueKind.Null, localDoc.RootElement.GetProperty("peerNode").ValueKind);
        Assert.Equal("answered", localDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal(["system_info"], localDoc.RootElement.GetProperty("tools").EnumerateArray().Select(e => e.GetString()).ToArray());

        using var outgoingDoc = JsonDocument.Parse(lines[1]);
        Assert.Equal("outgoing", outgoingDoc.RootElement.GetProperty("direction").GetString());
        Assert.Equal("PC-B", outgoingDoc.RootElement.GetProperty("peerNode").GetString());

        using var incomingDoc = JsonDocument.Parse(lines[2]);
        Assert.Equal("incoming", incomingDoc.RootElement.GetProperty("direction").GetString());
        Assert.Equal("PC-C", incomingDoc.RootElement.GetProperty("peerNode").GetString());
        Assert.Equal(["network_info", "ping_host"], incomingDoc.RootElement.GetProperty("tools").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public void Append_splits_entries_across_daily_files()
    {
        var log = new ChatHistoryLog(_folder);
        var day1 = new DateTimeOffset(2026, 11, 21, 23, 55, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 21)));
        var day2 = day1.AddMinutes(10);

        log.Append(new ChatHistoryEntry(day1, ChatDirection.Local, "PC-A", null, "first", "one", RunStatus.Answered, 1, []));
        log.Append(new ChatHistoryEntry(day2, ChatDirection.Local, "PC-A", null, "second", "two", RunStatus.Answered, 1, []));

        Assert.Single(File.ReadAllLines(Path.Combine(_folder, "chat-20261121.jsonl")));
        Assert.Single(File.ReadAllLines(Path.Combine(_folder, "chat-20261122.jsonl")));
    }

    [Fact]
    public void Append_masks_card_numbers_in_prompt_and_response()
    {
        var log = new ChatHistoryLog(_folder);
        var now = DateTimeOffset.Now;

        log.Append(new ChatHistoryEntry(now,
                                        ChatDirection.Local,
                                        "PC-A",
                                        null,
                                        "card 4111-1111-1111-1111",
                                        "found 4111 1111 1111 1111",
                                        RunStatus.Answered,
                                        1,
                                        []));

        var line = Assert.Single(File.ReadAllLines(log.PathFor(now)));
        Assert.DoesNotContain("4111-1111-1111-1111", line);
        Assert.DoesNotContain("4111 1111 1111 1111", line);
    }

    [Fact]
    public void IsExpired_keeps_messages_within_eight_hours_and_expires_older_ones()
    {
        var now = DateTimeOffset.Now;

        Assert.False(ChatHistoryLog.IsExpired(now.AddHours(-7).AddMinutes(-59), now));
        Assert.False(ChatHistoryLog.IsExpired(now.AddHours(-8), now));
        Assert.True(ChatHistoryLog.IsExpired(now.AddHours(-8).AddSeconds(-1), now));
    }

    [Fact]
    public void DividerText_names_today_and_yesterday()
    {
        var now = DateTimeOffset.Now;

        Assert.StartsWith("Today ", ChatHistoryLog.DividerText(now, now));
        Assert.StartsWith("Yesterday ", ChatHistoryLog.DividerText(now.AddDays(-1), now));
    }

    [Fact]
    public void NeedsDivider_after_first_message_or_a_long_gap()
    {
        var now = DateTimeOffset.Now;

        Assert.True(ChatHistoryLog.NeedsDivider(null, now));
        Assert.False(ChatHistoryLog.NeedsDivider(now.AddMinutes(-5), now));
        Assert.True(ChatHistoryLog.NeedsDivider(now.AddMinutes(-15), now));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
