using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsAxon.App;

public enum MessageKind
{
    Message,
    Event,
    Divider,
}

public sealed record StoredMessage(string Speaker, string Text, DateTimeOffset Time, MessageKind Kind = MessageKind.Message);

public sealed record Conversation(string Id, string Title, DateTimeOffset Updated, List<StoredMessage> Messages);

/// <summary>Saves each conversation transcript as one JSON file.</summary>
public sealed class ConversationStore(string folder)
{
    private static readonly TimeSpan DividerGap = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                       "WindowsAxon",
                                                       "conversations");

    public static string NewId() => Guid.NewGuid().ToString("N");

    public IReadOnlyList<Conversation> LoadAll()
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var conversations = new List<Conversation>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            if (!IsValidId(Path.GetFileNameWithoutExtension(path)))
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<Conversation>(File.ReadAllText(path), Options) is { } conversation
                    && conversation.Id == Path.GetFileNameWithoutExtension(path))
                {
                    conversations.Add(conversation);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // One unreadable file must not hide the other conversations.
            }
        }

        return [.. conversations.OrderByDescending(conversation => conversation.Updated)];
    }

    public void Save(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var path = PathFor(conversation.Id);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(conversation, Options));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public void Delete(string id) => File.Delete(PathFor(id));

    public static string GroupName(DateTimeOffset updated, DateTimeOffset now)
    {
        var days = (now.LocalDateTime.Date - updated.LocalDateTime.Date).Days;

        return days switch
        {
            <= 0 => "Today",
            1 => "Yesterday",
            < 7 => "Previous 7 days",
            _ => "Older",
        };
    }

    public static string DividerText(DateTimeOffset time, DateTimeOffset now)
    {
        var local = time.LocalDateTime;
        var clock = local.ToString("t", CultureInfo.CurrentCulture);
        var days = (now.LocalDateTime.Date - local.Date).Days;

        return days switch
        {
            0 => $"Today {clock}",
            1 => $"Yesterday {clock}",
            _ => $"{local.ToString("d", CultureInfo.CurrentCulture)} {clock}",
        };
    }

    public static bool NeedsDivider(DateTimeOffset? lastMessage, DateTimeOffset time) =>
        lastMessage is null || time - lastMessage.Value >= DividerGap;

    private string PathFor(string id)
    {
        // The id becomes a file name, so only accept the format NewId() creates.
        if (!IsValidId(id))
        {
            throw new ArgumentException("The conversation id is not valid.", nameof(id));
        }

        return Path.Combine(folder, $"{id}.json");
    }

    private static bool IsValidId(string id) => Guid.TryParseExact(id, "N", out _);
}
