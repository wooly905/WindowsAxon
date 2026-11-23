using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace WindowsAxon.Mesh;

public sealed record PeerMeshOptions
{
    public const int DefaultDiscoveryPort = 41520;
    public static readonly IPAddress DefaultMulticastAddress = IPAddress.Parse("239.255.41.52");

    public int DiscoveryPort { get; init; } = DefaultDiscoveryPort;

    public TimeSpan BeaconInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan PeerTimeout { get; init; } = TimeSpan.FromSeconds(8);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public bool LoopbackOnly { get; init; }
}

/// <summary>LAN peer-to-peer WindowsAxon mesh node using pair-key authenticated discovery and encrypted TCP channels.</summary>
public sealed class PeerMeshNode : IMeshNode
{
    private const int BeaconVersion = 1;

    private readonly string _rawNodeName;
    private readonly string _instanceId;
    private readonly PeerCapabilities _capabilities;
    private readonly DateTimeOffset _onlineSinceUtc = DateTimeOffset.UtcNow;
    private readonly PairKeyCrypto _crypto;
    private readonly Func<IncomingRemoteTask, CancellationToken, Task<RemoteTaskResult>> _incomingHandler;
    private readonly PeerMeshOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, DiscoveredPeer> _discovered = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<IncomingRpc, CancellationToken, Task<RpcResult>>> _rpcHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Func<IncomingStream, CancellationToken, Task>> _streamHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Func<IncomingBroadcast, CancellationToken, Task>> _broadcastHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _peersLock = new();

    private TcpListener? _tcpListener;
    private UdpClient? _udpReceiver;
    private UdpClient? _udpSender;
    private int _tcpPort;
    private IPAddress _localAddress = IPAddress.Loopback;
    private Task? _acceptLoopTask;
    private Task? _discoveryRecvTask;
    private Task? _discoverySendTask;
    private Task? _reaperTask;
    private long _lastReplyTicks;

    private volatile string _effectiveNodeName;
    private volatile IReadOnlyList<string> _peers = [];
    private volatile IReadOnlyList<PeerInfo> _peerInfos = [];
    private volatile IReadOnlyDictionary<string, DiscoveredPeer> _peersByDisplayName = new Dictionary<string, DiscoveredPeer>(StringComparer.OrdinalIgnoreCase);
    private MeshConnectionState _state = MeshConnectionState.Disconnected;
    private string _statusMessage = "Disconnected";
    private bool _started;

    public PeerMeshNode(string nodeName,
                        string pairKey,
                        Func<IncomingRemoteTask, CancellationToken, Task<RemoteTaskResult>> incomingHandler,
                        PeerMeshOptions? options = null,
                        PeerCapabilities? capabilities = null,
                        string? nodeId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pairKey);
        ArgumentNullException.ThrowIfNull(incomingHandler);

        _rawNodeName = nodeName.Trim();
        _effectiveNodeName = _rawNodeName;
        _instanceId = string.IsNullOrWhiteSpace(nodeId) ? Guid.NewGuid().ToString("N") : nodeId.Trim();
        _capabilities = capabilities ?? PeerCapabilities.Default;
        _crypto = new PairKeyCrypto(pairKey);
        _incomingHandler = incomingHandler;
        _options = options ?? new PeerMeshOptions();
    }

    public string NodeId => _instanceId;

    public string NodeName => _effectiveNodeName;

    public PeerCapabilities LocalCapabilities => _capabilities;

    public MeshConnectionState State => _state;

    public string StatusMessage => _statusMessage;

    public IReadOnlyList<string> OnlinePeers => _peers;

    public IReadOnlyList<PeerInfo> PeerInfos => _peerInfos;

    internal int ListeningPort => _tcpPort;

    public event Action<IReadOnlyList<string>>? PeersChanged;

    public event Action<IReadOnlyList<PeerInfo>>? PeerInfosChanged;

    public event Action<MeshConnectionState, string>? ConnectionStateChanged;

    public void RegisterRpcHandler(string topic, Func<IncomingRpc, CancellationToken, Task<RpcResult>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(handler);
        _rpcHandlers[topic.Trim()] = handler;
    }

    public void RegisterStreamHandler(string topic, Func<IncomingStream, CancellationToken, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(handler);
        _streamHandlers[topic.Trim()] = handler;
    }

    public void RegisterBroadcastHandler(string topic, Func<IncomingBroadcast, CancellationToken, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(handler);
        _broadcastHandlers[topic.Trim()] = handler;
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        try
        {
            _localAddress = _options.LoopbackOnly ? IPAddress.Loopback : GetPrimaryLocalAddress();

            _tcpListener = new TcpListener(_options.LoopbackOnly ? IPAddress.Loopback : IPAddress.Any, 0);
            _tcpListener.Start();
            _tcpPort = ((IPEndPoint)_tcpListener.LocalEndpoint).Port;

            _udpReceiver = CreateDiscoveryReceiver();
            _udpSender = CreateDiscoverySender();

            RebuildPeerSnapshot(notifyAlways: true);

            _acceptLoopTask = Task.Run(() => AcceptTcpLoopAsync(_lifetime.Token));
            _discoveryRecvTask = Task.Run(() => ReceiveBeaconsLoopAsync(_lifetime.Token));
            _discoverySendTask = Task.Run(() => SendBeaconsLoopAsync(_lifetime.Token));
            _reaperTask = Task.Run(() => ReapStalePeersLoopAsync(_lifetime.Token));
        }
        catch (Exception ex)
        {
            CleanupSockets();
            UpdateState(MeshConnectionState.Error, $"P2P failed to start: {ex.Message}");
        }
    }

    public async Task<RemoteTaskResult> RunRemoteTaskAsync(string targetNode,
                                                           string prompt,
                                                           string conversationId,
                                                           bool forceNewRequest,
                                                           Action<string> onToolStarted,
                                                           Func<TaskApprovalRequestPayload, CancellationToken, Task<RemoteApprovalAnswer>> onApprovalRequested,
                                                           CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        if (_state != MeshConnectionState.Connected)
        {
            return new RemoteTaskResult(RemoteRunStatus.Failed, "Peer-to-peer network is not active.", 0);
        }

        if (!_peersByDisplayName.TryGetValue(targetNode, out var peer))
        {
            return new RemoteTaskResult(RemoteRunStatus.Failed, $"Peer '{targetNode}' is not online.", 0);
        }

        TcpClient tcp;
        EncryptedJsonChannel channel;
        try
        {
            (tcp, channel) = await ConnectToPeerAsync(peer, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RemoteTaskResult(RemoteRunStatus.Cancelled, "Stopped.", 0);
        }
        catch (Exception ex)
        {
            return new RemoteTaskResult(RemoteRunStatus.Failed, $"Could not reach peer '{targetNode}': {ex.Message}", 0);
        }

        using (tcp)
        using (channel)
        {
            var taskId = Guid.NewGuid().ToString("N");
            using var cancelReg = cancellationToken.Register(() =>
                _ = TrySendAsync(channel,
                                 MeshEnvelope.Create(MeshMessageTypes.TaskCancel, new TaskCancelPayload(taskId)),
                                 CancellationToken.None));

            try
            {
                await channel.SendAsync(MeshEnvelope.Create(MeshMessageTypes.TaskDispatch,
                                                            new TaskDispatchPayload(taskId, _effectiveNodeName, targetNode, prompt, conversationId, forceNewRequest)),
                                        cancellationToken);

                while (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                {
                    MeshEnvelope? envelope;
                    try
                    {
                        envelope = await channel.ReceiveAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return new RemoteTaskResult(RemoteRunStatus.Cancelled, "Stopped.", 0);
                    }
                    catch (Exception)
                    {
                        return new RemoteTaskResult(RemoteRunStatus.Failed,
                                                    $"Peer '{targetNode}' disconnected while running the request.",
                                                    0);
                    }

                    if (envelope is null)
                    {
                        return new RemoteTaskResult(RemoteRunStatus.Failed,
                                                    $"Peer '{targetNode}' disconnected while running the request.",
                                                    0);
                    }

                    switch (envelope.Type)
                    {
                        case MeshMessageTypes.TaskToolStarted:
                            if (envelope.DeserializePayload<TaskToolStartedPayload>() is { } started
                                && string.Equals(started.TaskId, taskId, StringComparison.Ordinal))
                            {
                                onToolStarted(started.ToolName);
                            }
                            break;

                        case MeshMessageTypes.TaskApprovalRequest:
                            if (envelope.DeserializePayload<TaskApprovalRequestPayload>() is { } req
                                && string.Equals(req.TaskId, taskId, StringComparison.Ordinal))
                            {
                                _ = Task.Run(() => HandleCallerApprovalAsync(channel, req, onApprovalRequested, cancellationToken),
                                             CancellationToken.None);
                            }
                            break;

                        case MeshMessageTypes.TaskComplete:
                            if (envelope.DeserializePayload<TaskCompletePayload>() is { } done
                                && string.Equals(done.TaskId, taskId, StringComparison.Ordinal))
                            {
                                return new RemoteTaskResult(done.Status, done.Text, done.Steps);
                            }
                            break;
                    }
                }

                return new RemoteTaskResult(RemoteRunStatus.Cancelled, "Stopped.", 0);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new RemoteTaskResult(RemoteRunStatus.Cancelled, "Stopped.", 0);
            }
            catch (Exception ex)
            {
                return new RemoteTaskResult(RemoteRunStatus.Failed, $"Could not reach peer '{targetNode}': {ex.Message}", 0);
            }
        }
    }

    public async Task<RpcResult> InvokeRpcAsync(string targetNode,
                                                string topic,
                                                JsonElement payload,
                                                Action<string, JsonElement>? onEvent = null,
                                                MeshCallContext? context = null,
                                                CancellationToken cancellationToken = default)
    {
        if (_state != MeshConnectionState.Connected)
        {
            return RpcResult.Fail("Peer-to-peer network is not active.");
        }

        if (!_peersByDisplayName.TryGetValue(targetNode, out var peer))
        {
            return RpcResult.Fail($"Peer '{targetNode}' is not online.");
        }

        var callCtx = context is null ? MeshCallContext.CreateRoot(_effectiveNodeName) : context.NextHop(_effectiveNodeName);
        TcpClient tcp;
        EncryptedJsonChannel channel;
        try
        {
            (tcp, channel) = await ConnectToPeerAsync(peer, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return RpcResult.Fail($"Could not reach peer '{targetNode}': {ex.Message}");
        }

        using (tcp)
        using (channel)
        {
            var corrId = Guid.NewGuid().ToString("N");
            using var cancelReg = cancellationToken.Register(() =>
                _ = TrySendAsync(channel,
                                 MeshEnvelope.Create(MeshMessageTypes.RpcCancel, new RpcCancelPayload(corrId)),
                                 CancellationToken.None));

            try
            {
                await channel.SendAsync(MeshEnvelope.Create(MeshMessageTypes.RpcRequest,
                                                            new RpcRequestPayload(corrId,
                                                                                  _effectiveNodeName,
                                                                                  targetNode,
                                                                                  topic,
                                                                                  payload,
                                                                                  callCtx.TraceId,
                                                                                  callCtx.HopCount)),
                                        cancellationToken);

                while (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                {
                    var envelope = await channel.ReceiveAsync(cancellationToken);
                    if (envelope is null)
                    {
                        return RpcResult.Fail($"Peer '{targetNode}' disconnected.");
                    }

                    switch (envelope.Type)
                    {
                        case MeshMessageTypes.RpcEvent:
                            if (envelope.DeserializePayload<RpcEventPayload>() is { } ev
                                && string.Equals(ev.CorrelationId, corrId, StringComparison.Ordinal))
                            {
                                onEvent?.Invoke(ev.EventName, ev.Payload);
                            }
                            break;

                        case MeshMessageTypes.RpcResponse:
                            if (envelope.DeserializePayload<RpcResponsePayload>() is { } resp
                                && string.Equals(resp.CorrelationId, corrId, StringComparison.Ordinal))
                            {
                                return new RpcResult(resp.Success, resp.Payload, resp.Error);
                            }
                            break;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                return RpcResult.Fail("Node shutting down.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return RpcResult.Fail($"RPC '{topic}' to '{targetNode}' failed: {ex.Message}");
            }
        }
    }

    public async Task<IMeshStream> OpenStreamAsync(string targetNode,
                                                   string topic,
                                                   JsonElement metadata,
                                                   MeshCallContext? context = null,
                                                   CancellationToken cancellationToken = default)
    {
        if (_state != MeshConnectionState.Connected)
        {
            throw new InvalidOperationException("Peer-to-peer network is not active.");
        }

        if (!_peersByDisplayName.TryGetValue(targetNode, out var peer))
        {
            throw new InvalidOperationException($"Peer '{targetNode}' is not online.");
        }

        var callCtx = context is null ? MeshCallContext.CreateRoot(_effectiveNodeName) : context.NextHop(_effectiveNodeName);
        var (tcp, channel) = await ConnectToPeerAsync(peer, cancellationToken);
        try
        {
            var streamId = Guid.NewGuid().ToString("N");
            await channel.SendAsync(MeshEnvelope.Create(MeshMessageTypes.StreamOpen,
                                                        new StreamOpenPayload(streamId,
                                                                              _effectiveNodeName,
                                                                              targetNode,
                                                                              topic,
                                                                              metadata,
                                                                              callCtx.TraceId,
                                                                              callCtx.HopCount)),
                                    cancellationToken);

            var reply = await channel.ReceiveAsync(cancellationToken);
            if (reply is null
                || reply.Type != MeshMessageTypes.StreamAccept
                || reply.DeserializePayload<StreamAcceptPayload>() is not { } accept
                || !string.Equals(accept.StreamId, streamId, StringComparison.Ordinal))
            {
                throw new IOException($"Peer '{targetNode}' closed the connection before accepting stream '{topic}'.");
            }

            if (!accept.Accepted)
            {
                throw new InvalidOperationException(accept.Error ?? $"Peer '{targetNode}' rejected stream '{topic}'.");
            }

            return AttachDedicatedStream(tcp, channel, streamId, topic, targetNode, callCtx, metadata, accept.Metadata);
        }
        catch
        {
            channel.Dispose();
            tcp.Dispose();
            throw;
        }
    }

    public async Task BroadcastAsync(string topic,
                                     JsonElement payload,
                                     MeshCallContext? context = null,
                                     CancellationToken cancellationToken = default)
    {
        if (_state != MeshConnectionState.Connected)
        {
            return;
        }

        var callCtx = context is null ? MeshCallContext.CreateRoot(_effectiveNodeName) : context.NextHop(_effectiveNodeName);
        var envelope = MeshEnvelope.Create(MeshMessageTypes.Broadcast,
                                           new BroadcastPayload(_effectiveNodeName, topic, payload, callCtx.TraceId, callCtx.HopCount));
        var peers = _discovered.Values.ToList();
        await Task.WhenAll(peers.Select(async peer =>
        {
            try
            {
                var (tcp, channel) = await ConnectToPeerAsync(peer, cancellationToken);
                using (tcp)
                using (channel)
                {
                    await channel.SendAsync(envelope, cancellationToken);
                }
            }
            catch (Exception)
            {
            }
        }));
    }

    private MeshStream AttachDedicatedStream(TcpClient tcp,
                                             EncryptedJsonChannel channel,
                                             string streamId,
                                             string topic,
                                             string remoteNode,
                                             MeshCallContext context,
                                             JsonElement openMetadata,
                                             JsonElement acceptMetadata)
    {
        var streamCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Task? pumpTask = null;

        var stream = new MeshStream(
            streamId,
            topic,
            remoteNode,
            context,
            openMetadata,
            acceptMetadata,
            (env, ct) => channel.SendAsync(env, ct),
            async () =>
            {
                await streamCts.CancelAsync();
                channel.Dispose();
                tcp.Dispose();
                if (pumpTask is not null)
                {
                    try
                    {
                        await pumpTask;
                    }
                    catch (Exception)
                    {
                    }
                }

                streamCts.Dispose();
            });

        pumpTask = Task.Run(async () =>
        {
            try
            {
                while (!streamCts.IsCancellationRequested)
                {
                    var env = await channel.ReceiveAsync(streamCts.Token);
                    if (env is null)
                    {
                        stream.OnTransportFailure($"Peer '{remoteNode}' closed the stream connection.");
                        return;
                    }

                    switch (env.Type)
                    {
                        case MeshMessageTypes.StreamChunk:
                            if (env.DeserializePayload<StreamChunkPayload>() is { } chunk
                                && string.Equals(chunk.StreamId, streamId, StringComparison.Ordinal))
                            {
                                await stream.DeliverChunkAsync(chunk, streamCts.Token);
                            }
                            break;

                        case MeshMessageTypes.StreamClose:
                            if (env.DeserializePayload<StreamClosePayload>() is { } close
                                && string.Equals(close.StreamId, streamId, StringComparison.Ordinal))
                            {
                                stream.DeliverClose(close);
                                return;
                            }
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                stream.OnTransportFailure(ex.Message);
            }
        }, CancellationToken.None);

        return stream;
    }

    private async Task<(TcpClient Tcp, EncryptedJsonChannel Channel)> ConnectToPeerAsync(DiscoveredPeer peer,
                                                                                         CancellationToken cancellationToken)
    {
        var tcp = new TcpClient();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            connectCts.CancelAfter(_options.ConnectTimeout);
            await tcp.ConnectAsync(peer.Address, peer.TcpPort, connectCts.Token);
            var channel = await EncryptedJsonChannel.ConnectClientAsync(tcp.GetStream(), _crypto, cancellationToken);
            return (tcp, channel);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private static async Task HandleCallerApprovalAsync(EncryptedJsonChannel channel,
                                                        TaskApprovalRequestPayload request,
                                                        Func<TaskApprovalRequestPayload, CancellationToken, Task<RemoteApprovalAnswer>> onApprovalRequested,
                                                        CancellationToken cancellationToken)
    {
        RemoteApprovalAnswer answer;
        try
        {
            answer = await onApprovalRequested(request, cancellationToken);
        }
        catch (Exception)
        {
            answer = RemoteApprovalAnswer.Deny;
        }

        await TrySendAsync(channel,
                           MeshEnvelope.Create(MeshMessageTypes.TaskApprovalResponse,
                                               new TaskApprovalResponsePayload(request.TaskId, request.ApprovalId, answer)),
                           CancellationToken.None);
    }

    private async Task AcceptTcpLoopAsync(CancellationToken cancellationToken)
    {
        var listener = _tcpListener;
        if (listener is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            _ = Task.Run(() => HandleIncomingConnectionAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleIncomingConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        EncryptedJsonChannel channel;
        try
        {
            channel = await EncryptedJsonChannel.AcceptServerAsync(client.GetStream(), _crypto, cancellationToken);
        }
        catch (Exception)
        {
            client.Dispose();
            return;
        }

        MeshEnvelope? first;
        try
        {
            first = await channel.ReceiveAsync(cancellationToken);
        }
        catch (Exception)
        {
            channel.Dispose();
            client.Dispose();
            return;
        }

        if (first is null)
        {
            channel.Dispose();
            client.Dispose();
            return;
        }

        if (first.Type == MeshMessageTypes.StreamOpen && first.DeserializePayload<StreamOpenPayload>() is { } streamOpen)
        {
            await HandleIncomingStreamAsync(client, channel, streamOpen, cancellationToken);
            return;
        }

        using (client)
        using (channel)
        {
            switch (first.Type)
            {
                case MeshMessageTypes.TaskDispatch:
                    if (first.DeserializePayload<TaskDispatchPayload>() is { } dispatch)
                    {
                        await HandleIncomingTaskAsync(channel, dispatch, cancellationToken);
                    }
                    break;

                case MeshMessageTypes.RpcRequest:
                    if (first.DeserializePayload<RpcRequestPayload>() is { } rpcReq)
                    {
                        await HandleIncomingRpcAsync(channel, rpcReq, cancellationToken);
                    }
                    break;

                case MeshMessageTypes.Broadcast:
                    if (first.DeserializePayload<BroadcastPayload>() is { } broadcast)
                    {
                        await HandleIncomingBroadcastAsync(broadcast, cancellationToken);
                    }
                    break;
            }
        }
    }

    private async Task HandleIncomingStreamAsync(TcpClient client,
                                                 EncryptedJsonChannel channel,
                                                 StreamOpenPayload open,
                                                 CancellationToken cancellationToken)
    {
        if (open.HopCount > MeshCallContext.DefaultMaxHops)
        {
            await TrySendAsync(channel,
                               MeshEnvelope.Create(MeshMessageTypes.StreamAccept,
                                                   new StreamAcceptPayload(open.StreamId,
                                                                           false,
                                                                           MeshEnvelope.EmptyObject,
                                                                           $"Mesh hop limit ({MeshCallContext.DefaultMaxHops}) exceeded.")),
                               CancellationToken.None);
            channel.Dispose();
            client.Dispose();
            return;
        }

        if (!_streamHandlers.TryGetValue(open.Topic, out var handler))
        {
            await TrySendAsync(channel,
                               MeshEnvelope.Create(MeshMessageTypes.StreamAccept,
                                                   new StreamAcceptPayload(open.StreamId,
                                                                           false,
                                                                           MeshEnvelope.EmptyObject,
                                                                           $"No stream handler registered for topic '{open.Topic}'.")),
                               CancellationToken.None);
            channel.Dispose();
            client.Dispose();
            return;
        }

        var acceptedStream = false;
        var responded = 0;
        var context = new MeshCallContext(open.CallerNode, open.TraceId, open.HopCount);
        var incoming = new IncomingStream(
            open.StreamId,
            open.Topic,
            open.Metadata,
            context,
            AcceptAsync: async (acceptMeta, ct) =>
            {
                if (Interlocked.Exchange(ref responded, 1) != 0)
                {
                    throw new InvalidOperationException("Stream has already been accepted or rejected.");
                }

                await channel.SendAsync(MeshEnvelope.Create(MeshMessageTypes.StreamAccept,
                                                            new StreamAcceptPayload(open.StreamId, true, acceptMeta)),
                                        ct);
                acceptedStream = true;
                return AttachDedicatedStream(client, channel, open.StreamId, open.Topic, open.CallerNode, context, open.Metadata, acceptMeta);
            },
            RejectAsync: async (error, ct) =>
            {
                if (Interlocked.Exchange(ref responded, 1) == 0)
                {
                    await TrySendAsync(channel,
                                       MeshEnvelope.Create(MeshMessageTypes.StreamAccept,
                                                           new StreamAcceptPayload(open.StreamId, false, MeshEnvelope.EmptyObject, error)),
                                       ct);
                }
            });

        try
        {
            await handler(incoming, cancellationToken);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref responded, 1) == 0)
            {
                await TrySendAsync(channel,
                                   MeshEnvelope.Create(MeshMessageTypes.StreamAccept,
                                                       new StreamAcceptPayload(open.StreamId, false, MeshEnvelope.EmptyObject, ex.Message)),
                                   CancellationToken.None);
            }
        }
        finally
        {
            if (!acceptedStream)
            {
                channel.Dispose();
                client.Dispose();
            }
        }
    }

    private async Task HandleIncomingRpcAsync(EncryptedJsonChannel channel,
                                              RpcRequestPayload request,
                                              CancellationToken cancellationToken)
    {
        if (request.HopCount > MeshCallContext.DefaultMaxHops)
        {
            await TrySendAsync(channel,
                               MeshEnvelope.Create(MeshMessageTypes.RpcResponse,
                                                   new RpcResponsePayload(request.CorrelationId,
                                                                          false,
                                                                          MeshEnvelope.EmptyObject,
                                                                          $"Mesh hop limit ({MeshCallContext.DefaultMaxHops}) exceeded.")),
                               CancellationToken.None);
            return;
        }

        if (!_rpcHandlers.TryGetValue(request.Topic, out var handler))
        {
            await TrySendAsync(channel,
                               MeshEnvelope.Create(MeshMessageTypes.RpcResponse,
                                                   new RpcResponsePayload(request.CorrelationId,
                                                                          false,
                                                                          MeshEnvelope.EmptyObject,
                                                                          $"No RPC handler registered for topic '{request.Topic}'.")),
                               CancellationToken.None);
            return;
        }

        using var rpcCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cancelWatch = Task.Run(async () =>
        {
            try
            {
                while (!rpcCts.IsCancellationRequested)
                {
                    var env = await channel.ReceiveAsync(rpcCts.Token);
                    if (env is null
                        || (env.Type == MeshMessageTypes.RpcCancel
                            && env.DeserializePayload<RpcCancelPayload>() is { } cancel
                            && string.Equals(cancel.CorrelationId, request.CorrelationId, StringComparison.Ordinal)))
                    {
                        await rpcCts.CancelAsync();
                        return;
                    }
                }
            }
            catch (Exception)
            {
                await rpcCts.CancelAsync();
            }
        }, CancellationToken.None);

        RpcResult result;
        try
        {
            var incoming = new IncomingRpc(request.CorrelationId,
                                           request.Topic,
                                           request.Payload,
                                           new MeshCallContext(request.CallerNode, request.TraceId, request.HopCount),
                                           (eventName, eventPayload, ct) =>
                                               channel.SendAsync(MeshEnvelope.Create(MeshMessageTypes.RpcEvent,
                                                                                     new RpcEventPayload(request.CorrelationId,
                                                                                                         eventName,
                                                                                                         eventPayload)),
                                                                 ct));
            result = await handler(incoming, rpcCts.Token);
        }
        catch (OperationCanceledException)
        {
            result = RpcResult.Fail("Cancelled.");
        }
        catch (Exception ex)
        {
            result = RpcResult.Fail(ex.Message);
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            await TrySendAsync(channel,
                               MeshEnvelope.Create(MeshMessageTypes.RpcResponse,
                                                   new RpcResponsePayload(request.CorrelationId, result.Success, result.Payload, result.Error)),
                               CancellationToken.None);
        }

        await rpcCts.CancelAsync();
        try
        {
            await cancelWatch;
        }
        catch (Exception)
        {
        }
    }

    private async Task HandleIncomingBroadcastAsync(BroadcastPayload broadcast, CancellationToken cancellationToken)
    {
        if (broadcast.HopCount > MeshCallContext.DefaultMaxHops
            || !_broadcastHandlers.TryGetValue(broadcast.Topic, out var handler))
        {
            return;
        }

        try
        {
            await handler(new IncomingBroadcast(broadcast.Topic,
                                                broadcast.Payload,
                                                new MeshCallContext(broadcast.CallerNode, broadcast.TraceId, broadcast.HopCount)),
                          cancellationToken);
        }
        catch (Exception)
        {
        }
    }

    private async Task HandleIncomingTaskAsync(EncryptedJsonChannel channel,
                                               TaskDispatchPayload dispatch,
                                               CancellationToken cancellationToken)
    {
        using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pendingApprovals = new ConcurrentDictionary<string, TaskCompletionSource<RemoteApprovalAnswer>>();

        var receiveLoop = Task.Run(async () =>
        {
            try
            {
                while (!taskCts.IsCancellationRequested)
                {
                    var envelope = await channel.ReceiveAsync(taskCts.Token);
                    if (envelope is null)
                    {
                        await taskCts.CancelAsync();
                        return;
                    }

                    switch (envelope.Type)
                    {
                        case MeshMessageTypes.TaskCancel:
                            if (envelope.DeserializePayload<TaskCancelPayload>() is { } cancel
                                && string.Equals(cancel.TaskId, dispatch.TaskId, StringComparison.Ordinal))
                            {
                                await taskCts.CancelAsync();
                                return;
                            }
                            break;

                        case MeshMessageTypes.TaskApprovalResponse:
                            if (envelope.DeserializePayload<TaskApprovalResponsePayload>() is { } resp
                                && pendingApprovals.TryRemove(resp.ApprovalId, out var pending))
                            {
                                pending.TrySetResult(resp.Answer);
                            }
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                await taskCts.CancelAsync();
            }
        }, CancellationToken.None);

        RemoteTaskResult result;
        try
        {
            var incomingTask = new IncomingRemoteTask(dispatch.TaskId,
                                                      dispatch.CallerNode,
                                                      dispatch.Prompt,
                                                      dispatch.ConversationId,
                                                      dispatch.ForceNewRequest,
                                                      tool => _ = TrySendAsync(channel,
                                                                               MeshEnvelope.Create(MeshMessageTypes.TaskToolStarted,
                                                                                                   new TaskToolStartedPayload(dispatch.TaskId, tool)),
                                                                               taskCts.Token),
                                                      (req, ct) => RequestApprovalAsync(channel, pendingApprovals, req, ct));
            result = await _incomingHandler(incomingTask, taskCts.Token);
        }
        catch (OperationCanceledException)
        {
            result = new RemoteTaskResult(RemoteRunStatus.Cancelled, "Stopped.", 0);
        }
        catch (Exception ex)
        {
            result = new RemoteTaskResult(RemoteRunStatus.Failed, ex.Message, 0);
        }
        finally
        {
            foreach (var (_, pending) in pendingApprovals)
            {
                pending.TrySetCanceled();
            }

            pendingApprovals.Clear();
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            await TrySendAsync(channel,
                               MeshEnvelope.Create(MeshMessageTypes.TaskComplete,
                                                   new TaskCompletePayload(dispatch.TaskId, result.Status, result.Text, result.Steps)),
                               CancellationToken.None);
        }

        await taskCts.CancelAsync();
        try
        {
            await receiveLoop;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<RemoteApprovalAnswer> RequestApprovalAsync(EncryptedJsonChannel channel,
                                                                         ConcurrentDictionary<string, TaskCompletionSource<RemoteApprovalAnswer>> pendingApprovals,
                                                                         TaskApprovalRequestPayload request,
                                                                         CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<RemoteApprovalAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingApprovals[request.ApprovalId] = tcs;
        using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        try
        {
            await channel.SendAsync(MeshEnvelope.Create(MeshMessageTypes.TaskApprovalRequest, request), cancellationToken);
            return await tcs.Task;
        }
        finally
        {
            pendingApprovals.TryRemove(request.ApprovalId, out _);
        }
    }

    private UdpClient CreateDiscoveryReceiver()
    {
        var udp = new UdpClient();
        udp.ExclusiveAddressUse = false;
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, _options.DiscoveryPort));
        udp.MulticastLoopback = true;

        if (_options.LoopbackOnly)
        {
            TryJoinMulticast(udp, IPAddress.Loopback);
            return udp;
        }

        var joinedAny = false;
        foreach (var (address, _) in GetActiveIPv4Interfaces())
        {
            if (TryJoinMulticast(udp, address))
            {
                joinedAny = true;
            }
        }

        TryJoinMulticast(udp, IPAddress.Loopback);
        if (!joinedAny)
        {
            TryJoinMulticast(udp, IPAddress.Any);
        }

        return udp;
    }

    private static bool TryJoinMulticast(UdpClient udp, IPAddress interfaceAddress)
    {
        try
        {
            udp.JoinMulticastGroup(PeerMeshOptions.DefaultMulticastAddress, interfaceAddress);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private UdpClient CreateDiscoverySender()
    {
        var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.EnableBroadcast = true;
        udp.MulticastLoopback = true;
        if (_options.LoopbackOnly)
        {
            udp.Client.SetSocketOption(SocketOptionLevel.IP,
                                       SocketOptionName.MulticastInterface,
                                       IPAddress.Loopback.GetAddressBytes());
        }

        return udp;
    }

    private async Task SendBeaconsLoopAsync(CancellationToken cancellationToken)
    {
        await SendBeaconAsync(leaving: false, cancellationToken);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
            await SendBeaconAsync(leaving: false, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.BeaconInterval, cancellationToken);
                await SendBeaconAsync(leaving: false, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SendBeaconAsync(bool leaving, CancellationToken cancellationToken)
    {
        var sender = _udpSender;
        if (sender is null || _tcpPort <= 0)
        {
            return;
        }

        var beacon = new DiscoveryBeacon(BeaconVersion,
                                         _crypto.GroupId,
                                         _instanceId,
                                         _rawNodeName,
                                         _tcpPort,
                                         leaving,
                                         _crypto.ComputeBeaconMac(BeaconVersion, _instanceId, _rawNodeName, _tcpPort, leaving),
                                         _capabilities.Services,
                                         _capabilities.OsDescription);
        var payload = JsonSerializer.SerializeToUtf8Bytes(beacon, MeshEnvelope.JsonOptions);
        var multicastEndpoint = new IPEndPoint(PeerMeshOptions.DefaultMulticastAddress, _options.DiscoveryPort);

        if (_options.LoopbackOnly)
        {
            await TrySendDatagramAsync(sender, payload, multicastEndpoint, cancellationToken);
            return;
        }

        var interfaces = GetActiveIPv4Interfaces();
        foreach (var (address, mask) in interfaces)
        {
            TrySetMulticastInterface(sender, address);
            await TrySendDatagramAsync(sender, payload, multicastEndpoint, cancellationToken);

            var broadcast = ComputeDirectedBroadcast(address, mask);
            if (broadcast is not null)
            {
                await TrySendDatagramAsync(sender, payload, new IPEndPoint(broadcast, _options.DiscoveryPort), cancellationToken);
            }
        }

        TrySetMulticastInterface(sender, IPAddress.Loopback);
        await TrySendDatagramAsync(sender, payload, multicastEndpoint, cancellationToken);
        await TrySendDatagramAsync(sender, payload, new IPEndPoint(IPAddress.Broadcast, _options.DiscoveryPort), cancellationToken);
    }

    private static void TrySetMulticastInterface(UdpClient sender, IPAddress address)
    {
        try
        {
            sender.Client.SetSocketOption(SocketOptionLevel.IP,
                                          SocketOptionName.MulticastInterface,
                                          address.GetAddressBytes());
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task TrySendDatagramAsync(UdpClient sender,
                                                   byte[] payload,
                                                   IPEndPoint endpoint,
                                                   CancellationToken cancellationToken)
    {
        try
        {
            await sender.SendAsync(payload, endpoint, cancellationToken);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReceiveBeaconsLoopAsync(CancellationToken cancellationToken)
    {
        var receiver = _udpReceiver;
        if (receiver is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await receiver.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            ProcessBeacon(packet.Buffer, packet.RemoteEndPoint.Address);
        }
    }

    private void ProcessBeacon(byte[] buffer, IPAddress remoteAddress)
    {
        DiscoveryBeacon? beacon;
        try
        {
            beacon = JsonSerializer.Deserialize<DiscoveryBeacon>(buffer, MeshEnvelope.JsonOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (beacon is null
            || beacon.Version != BeaconVersion
            || !string.Equals(beacon.GroupId, _crypto.GroupId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(beacon.InstanceId)
            || string.IsNullOrWhiteSpace(beacon.NodeName)
            || beacon.TcpPort is <= 0 or > 65535
            || string.Equals(beacon.InstanceId, _instanceId, StringComparison.Ordinal))
        {
            return;
        }

        var trimmedName = beacon.NodeName.Trim();
        if (!_crypto.VerifyBeaconMac(beacon.Version, beacon.InstanceId, trimmedName, beacon.TcpPort, beacon.Leaving, beacon.Mac))
        {
            return;
        }

        if (beacon.Leaving)
        {
            if (_discovered.TryRemove(beacon.InstanceId, out _))
            {
                RebuildPeerSnapshot();
            }

            return;
        }

        var normalizedAddress = remoteAddress.IsIPv4MappedToIPv6 ? remoteAddress.MapToIPv4() : remoteAddress;
        var caps = beacon.Services is { Count: > 0 }
            ? new PeerCapabilities(beacon.Services, OsDescription: beacon.OsDescription)
            : PeerCapabilities.Default;
        var now = DateTimeOffset.UtcNow;

        var isNew = !_discovered.TryGetValue(beacon.InstanceId, out var existing);
        var firstSeen = isNew ? now : existing!.FirstSeenUtc;
        var peer = new DiscoveredPeer(beacon.InstanceId, trimmedName, normalizedAddress, beacon.TcpPort, caps, firstSeen, now);

        var changed = isNew
                      || !string.Equals(existing!.RawNodeName, peer.RawNodeName, StringComparison.Ordinal)
                      || !existing.Address.Equals(peer.Address)
                      || existing.TcpPort != peer.TcpPort
                      || !existing.Capabilities.Services.SequenceEqual(peer.Capabilities.Services, StringComparer.OrdinalIgnoreCase);

        _discovered[beacon.InstanceId] = peer;

        if (changed)
        {
            RebuildPeerSnapshot();
        }

        if (isNew)
        {
            var nowTicks = Environment.TickCount64;
            var prevTicks = Interlocked.Read(ref _lastReplyTicks);
            if (nowTicks - prevTicks >= 500
                && Interlocked.CompareExchange(ref _lastReplyTicks, nowTicks, prevTicks) == prevTicks)
            {
                _ = SendBeaconAsync(leaving: false, _lifetime.Token);
            }
        }
    }

    private async Task ReapStalePeersLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var cutoff = DateTimeOffset.UtcNow - _options.PeerTimeout;
            var removedAny = false;
            foreach (var (id, peer) in _discovered)
            {
                if (peer.LastSeenUtc < cutoff && _discovered.TryRemove(id, out _))
                {
                    removedAny = true;
                }
            }

            if (removedAny)
            {
                RebuildPeerSnapshot();
            }
        }
    }

    private void RebuildPeerSnapshot(bool notifyAlways = false)
    {
        IReadOnlyList<string>? changedPeers = null;
        IReadOnlyList<PeerInfo>? changedInfos = null;
        string? newStatus = null;

        lock (_peersLock)
        {
            var selfEntry = new PeerSnapshotEntry(_instanceId, _rawNodeName, _localAddress, _capabilities, _onlineSinceUtc, IsSelf: true, Peer: null);
            var entries = new List<PeerSnapshotEntry>(_discovered.Count + 1) { selfEntry };
            foreach (var peer in _discovered.Values)
            {
                entries.Add(new PeerSnapshotEntry(peer.InstanceId, peer.RawNodeName, peer.Address, peer.Capabilities, peer.FirstSeenUtc, IsSelf: false, Peer: peer));
            }

            var displayByName = new Dictionary<string, DiscoveredPeer>(StringComparer.OrdinalIgnoreCase);
            var infos = new List<PeerInfo>(entries.Count);
            var selfDisplay = _rawNodeName;

            foreach (var group in entries.GroupBy(e => e.RawNodeName, StringComparer.OrdinalIgnoreCase))
            {
                var items = group.ToList();
                if (items.Count == 1)
                {
                    var only = items[0];
                    infos.Add(new PeerInfo(only.InstanceId, only.RawNodeName, only.Capabilities, only.OnlineSinceUtc));
                    if (only.IsSelf)
                    {
                        selfDisplay = only.RawNodeName;
                    }
                    else if (only.Peer is not null)
                    {
                        displayByName[only.RawNodeName] = only.Peer;
                    }

                    continue;
                }

                var ipCounts = items.GroupBy(i => i.Address.ToString(), StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
                foreach (var item in items)
                {
                    var ipText = item.Address.ToString();
                    var suffix = ipCounts[ipText] == 1 ? ipText : item.InstanceId[..Math.Min(4, item.InstanceId.Length)];
                    var disambiguated = $"{item.RawNodeName} ({suffix})";
                    infos.Add(new PeerInfo(item.InstanceId, disambiguated, item.Capabilities, item.OnlineSinceUtc));
                    if (item.IsSelf)
                    {
                        selfDisplay = disambiguated;
                    }
                    else if (item.Peer is not null)
                    {
                        displayByName[disambiguated] = item.Peer;
                    }
                }
            }

            infos.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName));
            var displayNames = infos.Select(i => i.DisplayName).ToList();

            var listChanged = notifyAlways || !_peers.SequenceEqual(displayNames, StringComparer.Ordinal);
            _effectiveNodeName = selfDisplay;
            _peersByDisplayName = displayByName;
            _peers = displayNames;
            _peerInfos = infos;

            if (listChanged)
            {
                changedPeers = displayNames;
                changedInfos = infos;
                newStatus = $"P2P active as '{selfDisplay}' ({displayNames.Count} online)";
            }
        }

        if (changedPeers is not null)
        {
            PeersChanged?.Invoke(changedPeers);
            PeerInfosChanged?.Invoke(changedInfos!);
            UpdateState(MeshConnectionState.Connected, newStatus!);
        }
    }

    private static List<(IPAddress Address, IPAddress Mask)> GetActiveIPv4Interfaces()
    {
        var result = new List<(IPAddress, IPAddress)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork
                        || IPAddress.IsLoopback(unicast.Address))
                    {
                        continue;
                    }

                    var bytes = unicast.Address.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254)
                    {
                        continue;
                    }

                    result.Add((unicast.Address, unicast.IPv4Mask));
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        return result;
    }

    private static IPAddress GetPrimaryLocalAddress()
    {
        var interfaces = GetActiveIPv4Interfaces();
        return interfaces.Count > 0 ? interfaces[0].Address : IPAddress.Loopback;
    }

    private static IPAddress? ComputeDirectedBroadcast(IPAddress address, IPAddress mask)
    {
        var addrBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        if (addrBytes.Length != 4 || maskBytes.Length != 4)
        {
            return null;
        }

        var broadcast = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            broadcast[i] = (byte)(addrBytes[i] | ~maskBytes[i]);
        }

        return new IPAddress(broadcast);
    }

    private static async Task TrySendAsync(EncryptedJsonChannel channel,
                                           MeshEnvelope envelope,
                                           CancellationToken cancellationToken)
    {
        try
        {
            await channel.SendAsync(envelope, cancellationToken);
        }
        catch (Exception)
        {
        }
    }

    private void UpdateState(MeshConnectionState state, string message)
    {
        _state = state;
        _statusMessage = message;
        ConnectionStateChanged?.Invoke(state, message);
    }

    private void CleanupSockets()
    {
        try
        {
            _tcpListener?.Stop();
        }
        catch (SocketException)
        {
        }

        _udpReceiver?.Dispose();
        _udpSender?.Dispose();
        _tcpListener = null;
        _udpReceiver = null;
        _udpSender = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_started && _udpSender is not null)
        {
            using var leaveCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await SendBeaconAsync(leaving: true, leaveCts.Token);
        }

        await _lifetime.CancelAsync();
        CleanupSockets();

        var tasks = new[] { _acceptLoopTask, _discoveryRecvTask, _discoverySendTask, _reaperTask }
            .Where(t => t is not null)
            .Cast<Task>()
            .ToArray();
        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _lifetime.Dispose();
        _crypto.Dispose();
        UpdateState(MeshConnectionState.Disconnected, "Disconnected");
    }

    private sealed record DiscoveryBeacon(int Version,
                                          string GroupId,
                                          string InstanceId,
                                          string NodeName,
                                          int TcpPort,
                                          bool Leaving,
                                          string Mac,
                                          IReadOnlyList<string>? Services = null,
                                          string? OsDescription = null);

    private sealed record DiscoveredPeer(string InstanceId,
                                         string RawNodeName,
                                         IPAddress Address,
                                         int TcpPort,
                                         PeerCapabilities Capabilities,
                                         DateTimeOffset FirstSeenUtc,
                                         DateTimeOffset LastSeenUtc);

    private sealed record PeerSnapshotEntry(string InstanceId,
                                            string RawNodeName,
                                            IPAddress Address,
                                            PeerCapabilities Capabilities,
                                            DateTimeOffset OnlineSinceUtc,
                                            bool IsSelf,
                                            DiscoveredPeer? Peer);
}
