using System.Text.Json;

namespace WindowsAxon.Mesh;

public sealed record RpcResult(bool Success, JsonElement Payload, string? Error = null)
{
    public static RpcResult Ok<T>(T value) =>
        new(true, JsonSerializer.SerializeToElement(value, MeshEnvelope.JsonOptions));

    public static RpcResult Ok() => new(true, MeshEnvelope.EmptyObject);

    public static RpcResult Fail(string error) => new(false, MeshEnvelope.EmptyObject, error);
}

public sealed record IncomingRpc(string CorrelationId,
                                 string Topic,
                                 JsonElement Payload,
                                 MeshCallContext Context,
                                 Func<string, JsonElement, CancellationToken, Task> SendEventAsync);

public sealed record StreamCloseResult(bool Success, JsonElement Metadata, string? Error = null);

/// <summary>Chunked binary stream between two mesh peers with exchange of open/accept/close metadata.</summary>
public interface IMeshStream : IAsyncDisposable
{
    string StreamId { get; }

    string Topic { get; }

    string RemoteNode { get; }

    MeshCallContext Context { get; }

    JsonElement OpenMetadata { get; }

    JsonElement AcceptMetadata { get; }

    /// <summary>Close metadata supplied by the remote peer once it completes writing.</summary>
    JsonElement RemoteCloseMetadata { get; }

    Task WriteChunkAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    IAsyncEnumerable<byte[]> ReadChunksAsync(CancellationToken cancellationToken = default);

    Task<StreamCloseResult> CompleteWriteAsync(JsonElement metadata, CancellationToken cancellationToken = default);

    Task FailAsync(string error, CancellationToken cancellationToken = default);
}

public sealed record IncomingStream(string StreamId,
                                    string Topic,
                                    JsonElement Metadata,
                                    MeshCallContext Context,
                                    Func<JsonElement, CancellationToken, Task<IMeshStream>> AcceptAsync,
                                    Func<string, CancellationToken, Task> RejectAsync);

public sealed record IncomingBroadcast(string Topic, JsonElement Payload, MeshCallContext Context);

/// <summary>Payload-agnostic distributed transport primitives shared by Central Server and LAN P2P.</summary>
public interface IMeshTransport : IAsyncDisposable
{
    string NodeId { get; }

    string NodeName { get; }

    PeerCapabilities LocalCapabilities { get; }

    MeshConnectionState State { get; }

    string StatusMessage { get; }

    IReadOnlyList<string> OnlinePeers { get; }

    IReadOnlyList<PeerInfo> PeerInfos { get; }

    event Action<IReadOnlyList<string>>? PeersChanged;

    event Action<IReadOnlyList<PeerInfo>>? PeerInfosChanged;

    event Action<MeshConnectionState, string>? ConnectionStateChanged;

    void RegisterRpcHandler(string topic, Func<IncomingRpc, CancellationToken, Task<RpcResult>> handler);

    void RegisterStreamHandler(string topic, Func<IncomingStream, CancellationToken, Task> handler);

    void RegisterBroadcastHandler(string topic, Func<IncomingBroadcast, CancellationToken, Task> handler);

    Task<RpcResult> InvokeRpcAsync(string targetNode,
                                   string topic,
                                   JsonElement payload,
                                   Action<string, JsonElement>? onEvent = null,
                                   MeshCallContext? context = null,
                                   CancellationToken cancellationToken = default);

    Task<IMeshStream> OpenStreamAsync(string targetNode,
                                      string topic,
                                      JsonElement metadata,
                                      MeshCallContext? context = null,
                                      CancellationToken cancellationToken = default);

    Task BroadcastAsync(string topic,
                        JsonElement payload,
                        MeshCallContext? context = null,
                        CancellationToken cancellationToken = default);
}
