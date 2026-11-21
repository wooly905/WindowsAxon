using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsAxon.Mesh;

public enum RemoteRunStatus
{
    Answered,
    AskedUser,
    Cancelled,
    Failed,
}

public enum RemoteApprovalAnswer
{
    Deny,
    Allow,
    AllowForConversation,
}

public static class MeshMessageTypes
{
    public const string Register = "register";
    public const string Registered = "registered";
    public const string PeersUpdated = "peers_updated";
    public const string TaskDispatch = "task_dispatch";
    public const string TaskToolStarted = "task_tool_started";
    public const string TaskApprovalRequest = "task_approval_request";
    public const string TaskApprovalResponse = "task_approval_response";
    public const string TaskCancel = "task_cancel";
    public const string TaskComplete = "task_complete";
    public const string Error = "error";
}

public sealed record MeshEnvelope(string Type, JsonElement Payload)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static MeshEnvelope Create<T>(string type, T payload) =>
        new(type, JsonSerializer.SerializeToElement(payload, JsonOptions));

    public T? DeserializePayload<T>() => Payload.Deserialize<T>(JsonOptions);
}

public sealed record RegisterPayload(string NodeName);

public sealed record RegisteredPayload(string NodeName, IReadOnlyList<string> Peers);

public sealed record PeersUpdatedPayload(IReadOnlyList<string> Peers);

public sealed record TaskDispatchPayload(string TaskId, string CallerNode, string TargetNode, string Prompt);

public sealed record TaskToolStartedPayload(string TaskId, string ToolName);

public sealed record TaskApprovalRequestPayload(string TaskId,
                                                string ApprovalId,
                                                string ToolName,
                                                string ArgumentsJson,
                                                string? Note,
                                                bool AllowForConversation);

public sealed record TaskApprovalResponsePayload(string TaskId, string ApprovalId, RemoteApprovalAnswer Answer);

public sealed record TaskCancelPayload(string TaskId);

public sealed record TaskCompletePayload(string TaskId, RemoteRunStatus Status, string Text, int Steps);

public sealed record ErrorPayload(string Code, string Message);

