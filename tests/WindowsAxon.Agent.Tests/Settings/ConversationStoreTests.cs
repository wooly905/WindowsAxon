using WindowsAxon.App;

namespace WindowsAxon.Agent.Tests.Settings;

public sealed class ConversationStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"windowsaxon-conversations-{Guid.NewGuid():N}");

    [Fact]
    public void Save_and_LoadAll_round_trip_newest_first()
    {
        var store = new ConversationStore(_folder);
        var older = new Conversation(ConversationStore.NewId(),
                                     "older",
                                     DateTimeOffset.Now.AddDays(-2),
                                     [new StoredMessage("You", "open Paint", DateTimeOffset.Now.AddDays(-2))]);
        var newer = new Conversation(ConversationStore.NewId(),
                                     "newer",
                                     DateTimeOffset.Now,
                                     [new StoredMessage("", "Called app_launch", DateTimeOffset.Now, MessageKind.Event)]);
        store.Save(older);
        store.Save(newer);

        var loaded = store.LoadAll();

        Assert.Equal(["newer", "older"], loaded.Select(conversation => conversation.Title));
        Assert.Equal(MessageKind.Event, loaded[0].Messages[0].Kind);
    }

    [Fact]
    public void LoadAll_skips_unreadable_files()
    {
        var store = new ConversationStore(_folder);
        store.Save(new Conversation(ConversationStore.NewId(), "kept", DateTimeOffset.Now, []));
        File.WriteAllText(Path.Combine(_folder, $"{ConversationStore.NewId()}.json"), "{ not json");

        Assert.Equal("kept", Assert.Single(store.LoadAll()).Title);
    }

    [Theory]
    [InlineData("..\\settings")]
    [InlineData("not-a-guid")]
    public void Delete_rejects_ids_that_are_not_conversation_ids(string id)
    {
        Assert.Throws<ArgumentException>(() => new ConversationStore(_folder).Delete(id));
    }

    [Theory]
    [InlineData(0, "Today")]
    [InlineData(1, "Yesterday")]
    [InlineData(6, "Previous 7 days")]
    [InlineData(7, "Older")]
    public void GroupName_uses_calendar_days(int daysAgo, string expected)
    {
        var now = new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 7)));

        Assert.Equal(expected, ConversationStore.GroupName(now.AddDays(-daysAgo), now));
    }

    [Fact]
    public void DividerText_names_today_and_yesterday()
    {
        var now = DateTimeOffset.Now;

        Assert.StartsWith("Today ", ConversationStore.DividerText(now, now));
        Assert.StartsWith("Yesterday ", ConversationStore.DividerText(now.AddDays(-1), now));
    }

    [Fact]
    public void NeedsDivider_after_first_message_or_a_long_gap()
    {
        var now = DateTimeOffset.Now;

        Assert.True(ConversationStore.NeedsDivider(null, now));
        Assert.False(ConversationStore.NeedsDivider(now.AddMinutes(-5), now));
        Assert.True(ConversationStore.NeedsDivider(now.AddMinutes(-15), now));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
