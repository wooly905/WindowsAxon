using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsAxon.Agent;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WindowsAxon.App;

public sealed record ChatMessageView(string Speaker, string Text, MessageKind Kind = MessageKind.Message)
{
    private bool IsUser => Speaker == "You";

    public bool IsMarkdown => Speaker == "WindowsAxon";

    public Visibility SpeakerVisibility => Speaker == "Error" ? Visibility.Visible : Visibility.Collapsed;

    public HorizontalAlignment Alignment => IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public Thickness Margin => IsUser ? new Thickness(80, 0, 0, 10) : new Thickness(0, 0, 80, 10);

    public Thickness BorderThickness => IsUser ? new Thickness(0) : new Thickness(1);

    public Brush Background => Resource(IsUser ? "AccentFillColorDefaultBrush"
        : Speaker == "Error" ? "SystemFillColorCriticalBackgroundBrush" : "WindowsAxonMessageBackgroundBrush");

    public Brush BorderBrush => Resource(Speaker == "WindowsAxon" ? "WindowsAxonMessageBorderBrush" : "CardStrokeColorDefaultBrush");

    public Brush Foreground => Resource(IsUser ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
}

public sealed record ConversationSummary(string Id, string Title, string Preview);

public sealed class ConversationGroup(string key, IEnumerable<ConversationSummary> items)
    : ObservableCollection<ConversationSummary>(items)
{
    public string Key { get; } = key;
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
    private const int TitleLength = 60;
    private readonly DesktopChatService service;
    private readonly ConversationStore store;
    private readonly List<Conversation> conversations;
    private Conversation? current;

    public ChatViewModel(DesktopChatService service, ConversationStore store)
    {
        this.service = service;
        this.store = store;
        conversations = [.. store.LoadAll()];
        Input = "";
        Status = "";
        RefreshConversationGroups();
    }

    public ObservableCollection<ChatMessageView> Messages
    {
        get;
    } =
    [
        new("WindowsAxon",
            "Try an English request such as 'show disk usage', 'check my network', 'open Paint', or 'close Paint'. WindowsAxon offers Windows, file, browser, desktop, Paint, and script tools."),
    ];

    public ObservableCollection<ConversationGroup> ConversationGroups { get; } = [];

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
        if (current is null)
        {
            current = new Conversation(ConversationStore.NewId(), Title(text), DateTimeOffset.Now, []);
            conversations.Insert(0, current);
        }

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
        current = null;
        Messages.Clear();
        Input = "";
        AwaitingReply = false;
        Status = "";
    }

    public void OpenConversation(string id)
    {
        if (!CanEdit || conversations.Find(conversation => conversation.Id == id) is not { } conversation)
        {
            return;
        }

        NewChat();
        current = conversation;
        var now = DateTimeOffset.Now;
        foreach (var message in conversation.Messages)
        {
            var text = message.Kind == MessageKind.Divider ? ConversationStore.DividerText(message.Time, now) : message.Text;
            Messages.Add(new(message.Speaker, text, message.Kind));
        }

        // The agent session is not saved, so the model starts without this history.
        Messages.Add(new("", "Earlier messages are not sent to the model.", MessageKind.Event));
    }

    public void DeleteConversation(string id)
    {
        if (!CanEdit)
        {
            return;
        }

        try
        {
            store.Delete(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Could not delete the conversation.";

            return;
        }

        conversations.RemoveAll(conversation => conversation.Id == id);
        if (current?.Id == id)
        {
            NewChat();
        }

        RefreshConversationGroups();
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
        Messages.Add(new("WindowsAxon", "Model settings updated. A new conversation has started."));
    }

    private void AddDividerIfNeeded()
    {
        var now = DateTimeOffset.Now;
        var last = current?.Messages.Count > 0 ? current.Messages[^1].Time : (DateTimeOffset?)null;
        if (ConversationStore.NeedsDivider(last, now))
        {
            Add("", ConversationStore.DividerText(now, now), MessageKind.Divider);
        }
    }

    private void Add(string speaker, string text, MessageKind kind = MessageKind.Message)
    {
        Messages.Add(new(speaker, text, kind));
        if (current is null)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        current.Messages.Add(new(speaker, text, now, kind));
        current = current with { Updated = now };
        var index = conversations.FindIndex(conversation => conversation.Id == current.Id);
        conversations.RemoveAt(index);
        conversations.Insert(0, current);
        try
        {
            store.Save(current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Could not save the conversation.";
        }

        RefreshConversationGroups();
    }

    private void RefreshConversationGroups()
    {
        var now = DateTimeOffset.Now;
        ConversationGroups.Clear();
        foreach (var group in conversations.GroupBy(conversation => ConversationStore.GroupName(conversation.Updated, now)))
        {
            ConversationGroups.Add(new ConversationGroup(group.Key, group.Select(Summary)));
        }
    }

    private static ConversationSummary Summary(Conversation conversation)
    {
        var last = conversation.Messages.LastOrDefault(message => message.Kind == MessageKind.Message);

        return new(conversation.Id, conversation.Title, FirstLine(last?.Text ?? ""));
    }

    private static string Title(string text) => FirstLine(text) is { Length: > TitleLength } line
        ? $"{line[..TitleLength]}…" : FirstLine(text);

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();
}
