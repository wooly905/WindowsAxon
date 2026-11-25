using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsAxon.Agent;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WindowsAxon.App;

public sealed record ChatMessageView(string Speaker,
                                     string Text,
                                     MessageKind Kind = MessageKind.Message,
                                     DateTimeOffset? Time = null)
{
    private bool IsUser => Speaker == "You";

    public bool IsMarkdown => Speaker == "WindowsAxon"
        || Speaker.StartsWith("Reply to ", StringComparison.Ordinal)
        || Speaker.StartsWith("Replied from ", StringComparison.Ordinal);

    public Visibility SpeakerVisibility => Speaker is "You" or "WindowsAxon" or "" ? Visibility.Collapsed : Visibility.Visible;

    public HorizontalAlignment Alignment => IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public Thickness Margin => IsUser ? new Thickness(80, 0, 0, 10) : new Thickness(0, 0, 80, 10);

    public Thickness BorderThickness => IsUser ? new Thickness(0) : new Thickness(1);

    public Brush Background => Resource(IsUser ? "AccentFillColorDefaultBrush"
        : Speaker.StartsWith("Error", StringComparison.Ordinal) ? "SystemFillColorCriticalBackgroundBrush" : "WindowsAxonMessageBackgroundBrush");

    public Brush BorderBrush => Resource(Speaker == "WindowsAxon" || Speaker.StartsWith("Replied from ", StringComparison.Ordinal)
        ? "WindowsAxonMessageBorderBrush" : "CardStrokeColorDefaultBrush");

    public Brush Foreground => Resource(IsUser ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
}

public sealed partial class ChatTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Message { get; set; }

    public DataTemplate? Event { get; set; }

    public DataTemplate? Divider { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => (item as ChatMessageView)?.Kind switch
    {
        MessageKind.Event => Event,
        MessageKind.Divider => Divider,
        _ => Message,
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>One chat area: the local computer (<see cref="Peer"/> is null) or one remote computer.</summary>
public sealed class ChatConversation(string? peer)
{
    public string? Peer { get; } = peer;

    public ObservableCollection<ChatMessageView> Messages { get; } = [];

    public DateTimeOffset? LastMessageTime { get; set; }

    public int Unread { get; set; }

    /// <summary>The remote computer keeps one agent session per ID. A new ID starts a new remote conversation.</summary>
    public string ConversationId { get; set; } = Guid.NewGuid().ToString("N");
}

public partial class ChatViewModel : ObservableObject
{
    private readonly DesktopChatService service;
    private readonly Func<DateTimeOffset> clock;
    private readonly ChatConversation local = new(null);
    private readonly Dictionary<string, ChatConversation> peers = new(StringComparer.OrdinalIgnoreCase);
    private ChatConversation? running;
    private bool localAwaitingReply;

    public ChatViewModel(DesktopChatService service, Func<DateTimeOffset>? clock = null)
    {
        this.service = service;
        this.clock = clock ?? (() => DateTimeOffset.Now);
        Input = "";
        Status = "";
        local.Messages.Add(new("WindowsAxon",
                               "Try an English request such as 'show disk usage', 'check my network', 'open Paint', or 'close Paint'. WindowsAxon offers Windows, file, browser, desktop, Paint, and script tools."));
        Active = local;
    }

    /// <summary>Raised when a conversation's unread count changes.</summary>
    public event Action? UnreadChanged;

    public ChatConversation Active
    {
        get; private set;
    }

    public ObservableCollection<ChatMessageView> Messages => Active.Messages;

    public string? ActivePeer => Active.Peer;

    public string Header => Active.Peer is { } peer
        ? $"Chat with {peer}. Requests run on that computer."
        : "Enter what you want to do in Windows.";

    /// <summary>Remote computers that have a conversation, online or not.</summary>
    public IEnumerable<string> PeersWithConversation => peers.Values.Where(c => c.Messages.Count > 0).Select(c => c.Peer!);

    public int UnreadFor(string peer) => peers.TryGetValue(peer, out var conversation) ? conversation.Unread : 0;

    /// <summary>Shows the conversation with <paramref name="peer"/>, or the local one when it is null.</summary>
    public void SelectConversation(string? peer)
    {
        var next = peer is null ? local : For(peer);
        var hadUnread = next.Unread > 0;
        next.Unread = 0;
        if (!ReferenceEquals(next, Active))
        {
            Active = next;
            AwaitingReply = ReferenceEquals(next, local) && localAwaitingReply;
            Status = "";
            OnPropertyChanged(nameof(Active));
            OnPropertyChanged(nameof(Messages));
            OnPropertyChanged(nameof(ActivePeer));
            OnPropertyChanged(nameof(Header));
        }

        if (hadUnread)
        {
            UnreadChanged?.Invoke();
        }
    }

    private ChatConversation For(string peer)
    {
        if (!peers.TryGetValue(peer, out var conversation))
        {
            conversation = new ChatConversation(peer);
            peers[peer] = conversation;
        }

        return conversation;
    }

    private void MarkUnread(ChatConversation conversation)
    {
        if (!ReferenceEquals(conversation, Active))
        {
            conversation.Unread++;
            UnreadChanged?.Invoke();
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string Input
    {
        get; set;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(NewChatCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartNewRequestCommand))]
    public partial bool IsBusy
    {
        get; set;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartNewRequestCommand))]
    public partial bool AwaitingReply
    {
        get; set;
    }

    [ObservableProperty]
    public partial string Status
    {
        get; set;
    }

    public bool CanEdit => !IsBusy;

    private bool CanSend => CanEdit && !string.IsNullOrWhiteSpace(Input);

    private bool CanStartNewRequest => CanEdit && AwaitingReply;

    [RelayCommand(CanExecute = nameof(CanSend), IncludeCancelCommand = true)]
    private async Task SendAsync(CancellationToken cancellationToken)
    {
        // The peer list is disabled while busy, but the result still goes to the conversation that sent it.
        var conversation = Active;
        var isLocal = ReferenceEquals(conversation, local);
        var text = Input.Trim();
        Input = "";
        AddDividerIfNeeded(conversation);
        Add(conversation, "You", text);
        IsBusy = true;
        running = conversation;
        Status = "";
        try
        {
            var result = await service.RunAsync(text, conversation.Peer, conversation.ConversationId, cancellationToken);
            if (isLocal)
            {
                SetLocalAwaitingReply(result.Status == RunStatus.AskedUser);
            }
            var speaker = result.Status == RunStatus.Failed
                ? (result.RemotePeer is { } failedPeer ? $"Error (from {failedPeer})" : "Error")
                : (result.RemotePeer is { } remotePeer ? $"Replied from {remotePeer}" : "WindowsAxon");
            Add(conversation, speaker, result.Text);
            Status = result.Status switch
            {
                RunStatus.Cancelled => "Stopped. You can enter a new request.",
                RunStatus.Failed => result.RemotePeer is null
                    ? "Could not complete the request. Check the model service in Settings."
                    : $"Could not complete the request on {result.RemotePeer}.",
                _ => "",
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (isLocal)
            {
                SetLocalAwaitingReply(false);
            }
            Status = "Stopped. You can enter a new request.";
            Add(conversation, "WindowsAxon", "Stopped.");
        }
        catch (Exception)
        {
            if (isLocal)
            {
                SetLocalAwaitingReply(false);
            }
            Add(conversation, "Error", "Could not start the conversation. Check the model settings and try again.");
            Status = "Could not start the conversation.";
        }
        finally
        {
            running = null;
            IsBusy = false;
        }
    }

    private void SetLocalAwaitingReply(bool value)
    {
        localAwaitingReply = value;
        AwaitingReply = ReferenceEquals(Active, local) && value;
    }

    /// <summary>Clears the active conversation. For the local one, it also starts a new agent session.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void NewChat()
    {
        if (ReferenceEquals(Active, local))
        {
            ResetLocal();
        }
        else
        {
            Active.Messages.Clear();
            Active.LastMessageTime = null;
            Active.ConversationId = Guid.NewGuid().ToString("N");
        }

        Input = "";
        Status = "";
    }

    private void ResetLocal()
    {
        service.NewChat();
        local.Messages.Clear();
        local.LastMessageTime = null;
        SetLocalAwaitingReply(false);
    }

    public void ShowTool(string tool)
    {
        if (IsBusy && running is { } conversation)
        {
            Status = tool;
            Add(conversation, "", $"Called {tool}", MessageKind.Event);
        }
    }

    public void AddEvent(string text) => Add(Active, "", text, MessageKind.Event);

    public void ShowRemoteRequest(string callerNode, string prompt)
    {
        var conversation = For(callerNode);
        AddDividerIfNeeded(conversation);
        Add(conversation, $"From {callerNode}", prompt);
        MarkUnread(conversation);
    }

    public void ShowRemoteTool(string callerNode, string tool) =>
        Add(For(callerNode), "", $"[{callerNode}] Called {tool}", MessageKind.Event);

    public void ShowRemoteResponse(string callerNode, RunStatus status, string text)
    {
        var conversation = For(callerNode);
        Add(conversation, status == RunStatus.Failed ? $"Error (to {callerNode})" : $"Reply to {callerNode}", text);
        MarkUnread(conversation);
    }

    public void ShowRemoteFileReceived(string callerNode, string fileName, long sizeBytes, string savedPath)
    {
        var conversation = For(callerNode);
        AddDividerIfNeeded(conversation);
        Add(conversation, $"From {callerNode}", $"Received file {fileName} ({FormatByteSize(sizeBytes)})\nSaved to {savedPath}");
        MarkUnread(conversation);
    }

    private static string FormatByteSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):0.##} MB",
        >= 1024L => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };

    [RelayCommand(CanExecute = nameof(CanStartNewRequest))]
    private void StartNewRequest()
    {
        service.StartNewRequest();
        AwaitingReply = false;
        Status = "Enter a new request.";
    }

    public void SettingsChanged()
    {
        ResetLocal();
        Input = "";
        Status = "";
        Add(local, "WindowsAxon", "Model settings updated. A new conversation has started.");
    }

    public void PruneExpired()
    {
        var now = clock();
        PruneExpired(local, now);
        foreach (var conversation in peers.Values)
        {
            PruneExpired(conversation, now);
        }
    }

    private static void PruneExpired(ChatConversation conversation, DateTimeOffset now)
    {
        var messages = conversation.Messages;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Time is { } time && ChatHistoryLog.IsExpired(time, now))
            {
                messages.RemoveAt(i);
            }
        }

        while (messages.Count > 0 && messages[0].Kind == MessageKind.Divider)
        {
            if (messages.Count == 1 || messages[1].Kind == MessageKind.Divider)
            {
                messages.RemoveAt(0);
            }
            else
            {
                break;
            }
        }

        if (conversation.LastMessageTime is { } last && ChatHistoryLog.IsExpired(last, now))
        {
            conversation.LastMessageTime = messages.LastOrDefault(m => m.Time is not null)?.Time;
        }
    }

    private void AddDividerIfNeeded(ChatConversation conversation)
    {
        var now = clock();
        PruneExpired(conversation, now);
        if (ChatHistoryLog.NeedsDivider(conversation.LastMessageTime, now))
        {
            Add(conversation, "", ChatHistoryLog.DividerText(now, now), MessageKind.Divider);
        }
    }

    private void Add(ChatConversation conversation, string speaker, string text, MessageKind kind = MessageKind.Message)
    {
        var now = clock();
        PruneExpired(conversation, now);
        conversation.Messages.Add(new(speaker, text, kind, now));
        conversation.LastMessageTime = now;
    }
}
