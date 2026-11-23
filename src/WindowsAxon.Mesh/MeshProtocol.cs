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
    public const string RpcRequest = "rpc_request";
    public const string RpcEvent = "rpc_event";
    public const string RpcResponse = "rpc_response";
    public const string RpcCancel = "rpc_cancel";
    public const string StreamOpen = "stream_open";
    public const string StreamAccept = "stream_accept";
    public const string StreamChunk = "stream_chunk";
    public const string StreamClose = "stream_close";
    public const string Broadcast = "broadcast";
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

    public static readonly JsonElement EmptyObject = JsonSerializer.SerializeToElement(new { }, JsonOptions);

    public static MeshEnvelope Create<T>(string type, T payload) =>
        new(type, JsonSerializer.SerializeToElement(payload, JsonOptions));

    public T? DeserializePayload<T>() => Payload.Deserialize<T>(JsonOptions);
}

public sealed record RegisterPayload(string NodeName,
                                     string? NodeId = null,
                                     PeerCapabilities? Capabilities = null);

public sealed record RegisteredPayload(string NodeName,
                                       IReadOnlyList<string> Peers,
                                       IReadOnlyList<PeerInfo>? PeerInfos = null);

public sealed record PeersUpdatedPayload(IReadOnlyList<string> Peers,
                                         IReadOnlyList<PeerInfo>? PeerInfos = null);

public sealed record TaskDispatchPayload(string TaskId,
                                         string CallerNode,
                                         string TargetNode,
                                         string Prompt,
                                         string ConversationId,
                                         bool ForceNewRequest = false);

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

public sealed record RpcRequestPayload(string CorrelationId,
                                       string CallerNode,
                                       string TargetNode,
                                       string Topic,
                                       JsonElement Payload,
                                       string TraceId,
                                       int HopCount);

public sealed record RpcEventPayload(string CorrelationId, string EventName, JsonElement Payload);

public sealed record RpcResponsePayload(string CorrelationId,
                                        bool Success,
                                        JsonElement Payload,
                                        string? Error = null);

public sealed record RpcCancelPayload(string CorrelationId);

public sealed record StreamOpenPayload(string StreamId,
                                       string CallerNode,
                                       string TargetNode,
                                       string Topic,
                                       JsonElement Metadata,
                                       string TraceId,
                                       int HopCount);

public sealed record StreamAcceptPayload(string StreamId,
                                         bool Accepted,
                                         JsonElement Metadata,
                                         string? Error = null);

public sealed record StreamChunkPayload(string StreamId, long Sequence, string DataBase64);

public sealed record StreamClosePayload(string StreamId,
                                        bool Success,
                                        JsonElement Metadata,
                                        string? Error = null);

public sealed record BroadcastPayload(string CallerNode,
                                      string Topic,
                                      JsonElement Payload,
                                      string TraceId,
                                      int HopCount);

public sealed record ErrorPayload(string Code, string Message);
