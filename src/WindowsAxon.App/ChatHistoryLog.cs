using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsAxon.Agent;
using WindowsAxon.Agent.Guard;

namespace WindowsAxon.App;

public enum MessageKind
{
    Message,
    Event,
    Divider,
}

public enum ChatDirection
{
    Local,
    Outgoing,
    Incoming,
}

public sealed record ChatHistoryEntry(DateTimeOffset Time,
                                      ChatDirection Direction,
                                      string LocalNode,
                                      string? PeerNode,
                                      string Prompt,
                                      string Response,
                                      RunStatus Status,
                                      int Steps,
                                      IReadOnlyList<string> Tools);

/// <summary>Appends local and remote chat exchanges to %LOCALAPPDATA%\WindowsAxon\logs\chat-YYYYMMDD.jsonl.</summary>
public sealed class ChatHistoryLog(string folder)
{
    public static readonly TimeSpan RetentionWindow = TimeSpan.FromHours(8);
    private static readonly TimeSpan DividerGap = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Lock _lock = new();

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                       "WindowsAxon",
                                                       "logs");

    public string PathFor(DateTimeOffset time) => Path.Combine(folder, $"chat-{time.ToLocalTime():yyyyMMdd}.jsonl");

    public void Append(ChatHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var masked = entry with
        {
            Prompt = CardNumberMasker.Mask(entry.Prompt),
            Response = CardNumberMasker.Mask(entry.Response),
        };
        var line = JsonSerializer.Serialize(masked, JsonOptions) + Environment.NewLine;
        var path = PathFor(entry.Time);

        lock (_lock)
        {
            Directory.CreateDirectory(folder);
            File.AppendAllText(path, line);
        }
    }

    public static bool IsExpired(DateTimeOffset time, DateTimeOffset now) => now - time > RetentionWindow;

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
}

