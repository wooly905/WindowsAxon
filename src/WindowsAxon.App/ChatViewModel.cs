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

    public bool IsMarkdown => Speaker == "WindowsAxon" || Speaker.StartsWith("Reply to ", StringComparison.Ordinal);

    public Visibility SpeakerVisibility => Speaker is "You" or "WindowsAxon" or "" ? Visibility.Collapsed : Visibility.Visible;

    public HorizontalAlignment Alignment => IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public Thickness Margin => IsUser ? new Thickness(80, 0, 0, 10) : new Thickness(0, 0, 80, 10);

    public Thickness BorderThickness => IsUser ? new Thickness(0) : new Thickness(1);

    public Brush Background => Resource(IsUser ? "AccentFillColorDefaultBrush"
        : Speaker == "Error" ? "SystemFillColorCriticalBackgroundBrush" : "WindowsAxonMessageBackgroundBrush");

    public Brush BorderBrush => Resource(Speaker == "WindowsAxon" ? "WindowsAxonMessageBorderBrush" : "CardStrokeColorDefaultBrush");

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

public partial class ChatViewModel : ObservableObject
{
    private readonly DesktopChatService service;
    private readonly Func<DateTimeOffset> clock;
    private DateTimeOffset? lastMessageTime;

    public ChatViewModel(DesktopChatService service, Func<DateTimeOffset>? clock = null)
    {
        this.service = service;
        this.clock = clock ?? (() => DateTimeOffset.Now);
        Input = "";
        Status = "";
    }

    public ObservableCollection<ChatMessageView> Messages
    {
        get;
    } =
    [
        new("WindowsAxon",
            "Try an English request such as 'show disk usage', 'check my network', 'open Paint', or 'close Paint'. WindowsAxon offers Windows, file, browser, desktop, Paint, and script tools."),
    ];

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
        var text = Input.Trim();
        Input = "";
        AddDividerIfNeeded();
        Add("You", text);
        IsBusy = true;
        Status = "";
        try
        {
            var result = await service.RunAsync(text, cancellationToken);
            AwaitingReply = result.Status == RunStatus.AskedUser;
            Add(result.Status == RunStatus.Failed ? "Error" : "WindowsAxon", result.Text);
            Status = result.Status switch
            {
                RunStatus.Cancelled => "Stopped. You can enter a new request.",
                RunStatus.Failed => "Could not complete the request. Check the model service in Settings.",
                _ => "",
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AwaitingReply = false;
            Status = "Stopped. You can enter a new request.";
            Add("WindowsAxon", "Stopped.");
        }
        catch (Exception)
        {
            AwaitingReply = false;
            Add("Error", "Could not start the conversation. Check the model settings and try again.");
            Status = "Could not start the conversation.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void NewChat()
    {
        service.NewChat();
        Messages.Clear();
        lastMessageTime = null;
        Input = "";
        AwaitingReply = false;
        Status = "";
    }

    public void ShowTool(string tool)
    {
        if (IsBusy)
        {
            Status = tool;
            AddEvent($"Called {tool}");
        }
    }

    public void AddEvent(string text) => Add("", text, MessageKind.Event);

    public void ShowRemoteRequest(string callerNode, string prompt)
    {
        AddDividerIfNeeded();
        Add($"From {callerNode}", prompt);
    }

    public void ShowRemoteTool(string callerNode, string tool) => AddEvent($"[{callerNode}] Called {tool}");

    public void ShowRemoteResponse(string callerNode, RunStatus status, string text) =>
        Add(status == RunStatus.Failed ? $"Error (to {callerNode})" : $"Reply to {callerNode}", text);

    [RelayCommand(CanExecute = nameof(CanStartNewRequest))]
    private void StartNewRequest()
    {
        service.StartNewRequest();
        AwaitingReply = false;
        Status = "Enter a new request.";
    }

    public void SettingsChanged()
    {
        NewChat();
        Add("WindowsAxon", "Model settings updated. A new conversation has started.");
    }

    public void PruneExpired() => PruneExpired(clock());

    private void PruneExpired(DateTimeOffset now)
    {
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            if (Messages[i].Time is { } time && ChatHistoryLog.IsExpired(time, now))
            {
                Messages.RemoveAt(i);
            }
        }

        while (Messages.Count > 0 && Messages[0].Kind == MessageKind.Divider)
        {
            if (Messages.Count == 1 || Messages[1].Kind == MessageKind.Divider)
            {
                Messages.RemoveAt(0);
            }
            else
            {
                break;
            }
        }

        if (lastMessageTime is { } last && ChatHistoryLog.IsExpired(last, now))
        {
            lastMessageTime = Messages.LastOrDefault(m => m.Time is not null)?.Time;
        }
    }

    private void AddDividerIfNeeded()
    {
        var now = clock();
        PruneExpired(now);
        if (ChatHistoryLog.NeedsDivider(lastMessageTime, now))
        {
            Add("", ChatHistoryLog.DividerText(now, now), MessageKind.Divider);
        }
    }

    private void Add(string speaker, string text, MessageKind kind = MessageKind.Message)
    {
        var now = clock();
        PruneExpired(now);
        Messages.Add(new(speaker, text, kind, now));
        lastMessageTime = now;
    }
}
