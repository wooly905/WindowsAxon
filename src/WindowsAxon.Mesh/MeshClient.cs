using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace WindowsAxon.Mesh;

public sealed record RemoteTaskResult(RemoteRunStatus Status, string Text, int Steps);

public sealed record IncomingRemoteTask(string TaskId,
                                        string CallerNode,
                                        string Prompt,
                                        string ConversationId,
                                        bool ForceNewRequest,
                                        Action<string> ReportToolStarted,
                                        Func<TaskApprovalRequestPayload, CancellationToken, Task<RemoteApprovalAnswer>> RequestApprovalAsync);

public enum MeshConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error,
}

/// <summary>Persistent WebSocket client connecting a WindowsAxon node to WindowsAxon.Hub.</summary>
public sealed class MeshClient : IMeshNode
{
    private static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    ];

    private readonly Uri _hubUri;
    private readonly string _nodeId;
    private readonly string _nodeName;
    private readonly string? _token;
    private readonly PeerCapabilities _capabilities;
    private readonly Func<IncomingRemoteTask, CancellationToken, Task<RemoteTaskResult>> _incomingHandler;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, OutgoingTaskState> _outgoing = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _incoming = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemoteApprovalAnswer>> _pendingApprovals = new();
    private readonly ConcurrentDictionary<string, Func<IncomingRpc, CancellationToken, Task<RpcResult>>> _rpcHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Func<IncomingStream, CancellationToken, Task>> _streamHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Func<IncomingBroadcast, CancellationToken, Task>> _broadcastHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, OutgoingRpcState> _outgoingRpcs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _incomingRpcs = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<StreamAcceptPayload>> _pendingStreamOpens = new();
    private readonly ConcurrentDictionary<string, MeshStream> _activeStreams = new();

    private WebSocketJsonChannel? _channel;
    private Task? _loopTask;
    private volatile IReadOnlyList<string> _peers = [];
    private volatile IReadOnlyList<PeerInfo> _peerInfos = [];
    private MeshConnectionState _state = MeshConnectionState.Disconnected;
    private string _statusMessage = "Disconnected";

    public MeshClient(Uri hubUri,
                      string nodeName,
                      string? token,
                      Func<IncomingRemoteTask, CancellationToken, Task<RemoteTaskResult>> incomingHandler,
                      PeerCapabilities? capabilities = null,
                      string? nodeId = null)
    {
        ArgumentNullException.ThrowIfNull(hubUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        ArgumentNullException.ThrowIfNull(incomingHandler);

        _hubUri = hubUri;
        _nodeId = string.IsNullOrWhiteSpace(nodeId) ? Guid.NewGuid().ToString("N") : nodeId.Trim();
        _nodeName = nodeName.Trim();
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        _capabilities = capabilities ?? PeerCapabilities.Default;
        _incomingHandler = incomingHandler;
    }

    public string NodeId => _nodeId;

    public string NodeName => _nodeName;

    public PeerCapabilities LocalCapabilities => _capabilities;

    public MeshConnectionState State => _state;

    public string StatusMessage => _statusMessage;

    public IReadOnlyList<string> OnlinePeers => _peers;

    public IReadOnlyList<PeerInfo> PeerInfos => _peerInfos;

    public event Action<IReadOnlyList<string>>? PeersChanged;

    public event Action<IReadOnlyList<PeerInfo>>? PeerInfosChanged;

    public event Action<MeshConnectionState, string>? ConnectionStateChanged;

    public void Start()
    {
        _loopTask ??= Task.Run(() => RunConnectionLoopAsync(_lifetime.Token));
    }

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

    public async Task<RemoteTaskResult> RunRemoteTaskAsync(string targetNode,
                                                           string prompt,
                                                           string conversationId,
                                                           bool forceNewRequest,
                                                           Action<string> onToolStarted,
                                                           Func<TaskApprovalRequestPayload, CancellationToken, Task<RemoteApprovalAnswer>> onApprovalRequested,
                                                           CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var channel = _channel;
        if (_state != MeshConnectionState.Connected || channel is null || channel.State != WebSocketState.Open)
        {
            return new RemoteTaskResult(RemoteRunStatus.Failed, "Not connected to the mesh hub.", 0);
        }

        var taskId = Guid.NewGuid().ToString("N");
        var state = new OutgoingTaskState(onToolStarted, onApprovalRequested, cancellationToken);
        _outgoing[taskId] = state;

        using var cancelRegistration = cancellationToken.Register(() =>
        {
            _ = SendCancelAsync(taskId);
            state.Completion.TrySetResult(new RemoteTaskResult(RemoteRunStatus.Cancelled, "Stopped.", 0));
        });

        try
        {
            await channel.SendAsync(MeshEnvelope.Create(MeshMessageTypes.TaskDispatch,
                                                        new TaskDispatchPayload(taskId, _nodeName, targetNode, prompt, conversationId, forceNewRequest)),
                                    cancellationToken);
            return await state.Completion.Task;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RemoteTaskResult(RemoteRunStatus.Cancelled, "Stopped.", 0);
        }
        catch (Exception ex)
        {
            return new RemoteTaskResult(RemoteRunStatus.Failed, $"Could not reach peer '{targetNode}': {ex.Message}", 0);
        }
        finally
        {
            _outgoing.TryRemove(taskId, out _);
        }
    }

    public async Task<RpcResult> InvokeRpcAsync(string targetNode,
                                                string topic,
                                                JsonElement payload,
                                                Action<string, JsonElement>? onEvent = null,
                                                MeshCallContext? context = null,
                                                CancellationToken cancellationToken = default)
    {
        var channel = _channel;
        if (_state != MeshConnectionState.Connected || channel is null || channel.State != WebSocketState.Open)
        {
            return RpcResult.Fail("Not connected to the mesh hub.");
        }

        var callCtx = context is null ? MeshCallContext.CreateRoot(_nodeName) : context.NextHop(_nodeName);
        var corrId = Guid.NewGuid().ToString("N");
        var state = new OutgoingRpcState(onEvent);
        _outgoingRpcs[corrId] = state;

        using var cancelReg = cancellationToken.Register(() =>
        {
            _ = TrySendAsync(channel,
                             MeshEnvelope.Create(MeshMessageTypes.RpcCancel, new RpcCancelPayload(corrId)),
                             CancellationToken.None);
            state.Completion.TrySetCanceled(cancellationToken);
        });

        try
        {
            await channel.SendAsync(MeshEnvelope.Create(MeshMessageTypes.RpcRequest,
                                                        new RpcRequestPayload(corrId,
                                                                              _nodeName,
                                                                              targetNode,
                                                                              topic,
                                                                              payload,
                                                                              callCtx.TraceId,
                                                                              callCtx.HopCount)),
                                    cancellationToken);
            return await state.Completion.Task;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return RpcResult.Fail($"RPC '{topic}' to '{targetNode}' failed: {ex.Message}");
        }
        finally
        {
            _outgoingRpcs.TryRemove(corrId, out _);
        }
    }

    public async Task<IMeshStream> OpenStreamAsync(string targetNode,
                                                   string topic,
                                                   JsonElement metadata,
                                                   MeshCallContext? context = null,
                                                   CancellationToken cancellationToken = default)
    {
        var channel = _channel;
        if (_state != MeshConnectionState.Connected || channel is null || channel.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Not connected to the mesh hub.");
        }

        var callCtx = context is null ? MeshCallContext.CreateRoot(_nodeName) : context.NextHop(_nodeName);
        var streamId = Guid.NewGuid().ToString("N");
        var acceptTcs = new TaskCompletionSource<StreamAcceptPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingStreamOpens[streamId] = acceptTcs;

        using var cancelReg = cancellationToken.Register(() => acceptTcs.TrySetCanceled(cancellationToken));
        try
        {
            await channel.SendAsync(MeshEnvelope.Create(MeshMessageTypes.StreamOpen,
                                                        new StreamOpenPayload(streamId,
                                                                              _nodeName,
                                                                              targetNode,
                                                                              topic,
                                                                              metadata,
                                                                              callCtx.TraceId,
                                                                              callCtx.HopCount)),
                                    cancellationToken);
            var accept = await acceptTcs.Task;
            if (!accept.Accepted)
            {
                throw new InvalidOperationException(accept.Error ?? $"Peer '{targetNode}' rejected stream '{topic}'.");
            }

            var stream = new MeshStream(streamId,
                                        topic,
                                        targetNode,
                                        callCtx,
                                        metadata,
                                        accept.Metadata,
                                        (env, ct) => channel.SendAsync(env, ct),
                                        () =>
                                        {
                                            _activeStreams.TryRemove(streamId, out _);
                                            return ValueTask.CompletedTask;
                                        });
            _activeStreams[streamId] = stream;
            return stream;
        }
        finally
        {
            _pendingStreamOpens.TryRemove(streamId, out _);
        }
    }

    public async Task BroadcastAsync(string topic,
                                     JsonElement payload,
                                     MeshCallContext? context = null,
                                     CancellationToken cancellationToken = default)
    {
        var channel = _channel;
        if (_state != MeshConnectionState.Connected || channel is null || channel.State != WebSocketState.Open)
        {
            return;
        }

        var callCtx = context is null ? MeshCallContext.CreateRoot(_nodeName) : context.NextHop(_nodeName);
        await channel.SendAsync(MeshEnvelope.Create(MeshMessageTypes.Broadcast,
                                                    new BroadcastPayload(_nodeName, topic, payload, callCtx.TraceId, callCtx.HopCount)),
                                cancellationToken);
    }

    private async Task RunConnectionLoopAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            UpdateState(MeshConnectionState.Connecting, $"Connecting to {_hubUri}...");
            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            if (_token is not null)
            {
                socket.Options.SetRequestHeader("Authorization", $"Bearer {_token}");
            }

            try
            {
                await socket.ConnectAsync(_hubUri, cancellationToken);
                using var channel = new WebSocketJsonChannel(socket);
                _channel = channel;

                await channel.SendAsync(MeshEnvelope.Create(MeshMessageTypes.Register,
                                                            new RegisterPayload(_nodeName, _nodeId, _capabilities)),
                                        cancellationToken);
                var first = await channel.ReceiveAsync(cancellationToken);
                if (first is null)
                {
                    throw new WebSocketException("Hub closed the connection during registration.");
                }

                if (first.Type == MeshMessageTypes.Error)
                {
                    var err = first.DeserializePayload<ErrorPayload>();
                    UpdateState(MeshConnectionState.Error, err?.Message ?? "Registration rejected by hub.");
                    if (err?.Code is "duplicate_node" or "unauthorized")
                    {
                        return;
                    }

                    throw new InvalidOperationException(err?.Message ?? "Registration failed.");
                }

                if (first.Type != MeshMessageTypes.Registered || first.DeserializePayload<RegisteredPayload>() is not { } reg)
                {
                    throw new InvalidOperationException($"Unexpected handshake message '{first.Type}'.");
                }

                attempt = 0;
                UpdatePeers(reg.Peers, reg.PeerInfos);
                UpdateState(MeshConnectionState.Connected, $"Connected as '{reg.NodeName}' ({reg.Peers.Count} online)");

                await ReceiveLoopAsync(channel, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                UpdateState(MeshConnectionState.Error, $"Disconnected: {ex.Message}");
            }
            finally
            {
                _channel = null;
                FailAllInFlight("Connection to mesh hub was lost.");
                UpdatePeers([], []);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var delay = ReconnectDelays[Math.Min(attempt++, ReconnectDelays.Length - 1)];
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        UpdateState(MeshConnectionState.Disconnected, "Disconnected");
    }

    private async Task ReceiveLoopAsync(WebSocketJsonChannel channel, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var envelope = await channel.ReceiveAsync(cancellationToken);
            if (envelope is null)
            {
                return;
            }

            switch (envelope.Type)
            {
                case MeshMessageTypes.PeersUpdated:
                    if (envelope.DeserializePayload<PeersUpdatedPayload>() is { } peers)
                    {
                        UpdatePeers(peers.Peers, peers.PeerInfos);
                        UpdateState(MeshConnectionState.Connected, $"Connected as '{_nodeName}' ({peers.Peers.Count} online)");
                    }
                    break;

                case MeshMessageTypes.TaskDispatch:
                    if (envelope.DeserializePayload<TaskDispatchPayload>() is { } dispatch)
                    {
                        _ = Task.Run(() => HandleIncomingTaskAsync(channel, dispatch, cancellationToken), cancellationToken);
                    }
                    break;

                case MeshMessageTypes.TaskCancel:
                    if (envelope.DeserializePayload<TaskCancelPayload>() is { } cancel
                        && _incoming.TryGetValue(cancel.TaskId, out var cts))
                    {
                        cts.Cancel();
                    }
                    break;

                case MeshMessageTypes.TaskToolStarted:
                    if (envelope.DeserializePayload<TaskToolStartedPayload>() is { } started
                        && _outgoing.TryGetValue(started.TaskId, out var outForTool))
                    {
                        outForTool.OnToolStarted(started.ToolName);
                    }
                    break;

                case MeshMessageTypes.TaskApprovalRequest:
                    if (envelope.DeserializePayload<TaskApprovalRequestPayload>() is { } req
                        && _outgoing.TryGetValue(req.TaskId, out var outForApproval))
                    {
                        _ = Task.Run(() => HandleApprovalRequestAsync(channel, req, outForApproval), cancellationToken);
                    }
                    break;

                case MeshMessageTypes.TaskApprovalResponse:
                    if (envelope.DeserializePayload<TaskApprovalResponsePayload>() is { } resp
                        && _pendingApprovals.TryRemove(resp.ApprovalId, out var pending))
                    {
                        pending.TrySetResult(resp.Answer);
                    }
                    break;

                case MeshMessageTypes.TaskComplete:
                    if (envelope.DeserializePayload<TaskCompletePayload>() is { } done
                        && _outgoing.TryRemove(done.TaskId, out var outForDone))
                    {
                        outForDone.Completion.TrySetResult(new RemoteTaskResult(done.Status, done.Text, done.Steps));
                    }
                    break;

                case MeshMessageTypes.RpcRequest:
                    if (envelope.DeserializePayload<RpcRequestPayload>() is { } rpcReq)
                    {
                        _ = Task.Run(() => HandleIncomingRpcAsync(channel, rpcReq, cancellationToken), cancellationToken);
                    }
                    break;

                case MeshMessageTypes.RpcEvent:
                    if (envelope.DeserializePayload<RpcEventPayload>() is { } rpcEvent
                        && _outgoingRpcs.TryGetValue(rpcEvent.CorrelationId, out var outRpcForEvent))
                    {
                        outRpcForEvent.OnEvent?.Invoke(rpcEvent.EventName, rpcEvent.Payload);
                    }
                    break;

                case MeshMessageTypes.RpcCancel:
                    if (envelope.DeserializePayload<RpcCancelPayload>() is { } rpcCancel
                        && _incomingRpcs.TryGetValue(rpcCancel.CorrelationId, out var rpcCts))
                    {
                        rpcCts.Cancel();
                    }
                    break;

                case MeshMessageTypes.RpcResponse:
                    if (envelope.DeserializePayload<RpcResponsePayload>() is { } rpcResp
                        && _outgoingRpcs.TryRemove(rpcResp.CorrelationId, out var outRpcForResp))
                    {
                        outRpcForResp.Completion.TrySetResult(new RpcResult(rpcResp.Success, rpcResp.Payload, rpcResp.Error));
                    }
                    break;

                case MeshMessageTypes.StreamOpen:
                    if (envelope.DeserializePayload<StreamOpenPayload>() is { } streamOpen)
                    {
                        _ = Task.Run(() => HandleIncomingStreamOpenAsync(channel, streamOpen, cancellationToken), cancellationToken);
                    }
                    break;

                case MeshMessageTypes.StreamAccept:
                    if (envelope.DeserializePayload<StreamAcceptPayload>() is { } streamAccept
                        && _pendingStreamOpens.TryRemove(streamAccept.StreamId, out var pendingOpen))
                    {
                        pendingOpen.TrySetResult(streamAccept);
                    }
                    break;

                case MeshMessageTypes.StreamChunk:
                    if (envelope.DeserializePayload<StreamChunkPayload>() is { } streamChunk
                        && _activeStreams.TryGetValue(streamChunk.StreamId, out var streamForChunk))
                    {
                        await streamForChunk.DeliverChunkAsync(streamChunk, cancellationToken);
                    }
                    break;

                case MeshMessageTypes.StreamClose:
                    if (envelope.DeserializePayload<StreamClosePayload>() is { } streamClose
                        && _activeStreams.TryGetValue(streamClose.StreamId, out var streamForClose))
                    {
                        streamForClose.DeliverClose(streamClose);
                    }
                    break;

                case MeshMessageTypes.Broadcast:
                    if (envelope.DeserializePayload<BroadcastPayload>() is { } broadcast)
                    {
                        _ = Task.Run(() => HandleIncomingBroadcastAsync(broadcast, cancellationToken), cancellationToken);
                    }
                    break;
            }
        }
    }

    private async Task HandleIncomingRpcAsync(WebSocketJsonChannel channel,
                                              RpcRequestPayload request,
                                              CancellationToken connectionToken)
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

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
        _incomingRpcs[request.CorrelationId] = cts;
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
            result = await handler(incoming, cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = RpcResult.Fail("Cancelled.");
        }
        catch (Exception ex)
        {
            result = RpcResult.Fail(ex.Message);
        }
        finally
        {
            _incomingRpcs.TryRemove(request.CorrelationId, out _);
        }

        await TrySendAsync(channel,
                           MeshEnvelope.Create(MeshMessageTypes.RpcResponse,
                                               new RpcResponsePayload(request.CorrelationId, result.Success, result.Payload, result.Error)),
                           CancellationToken.None);
    }

    private async Task HandleIncomingStreamOpenAsync(WebSocketJsonChannel channel,
                                                     StreamOpenPayload open,
                                                     CancellationToken connectionToken)
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
            return;
        }

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

                var stream = new MeshStream(open.StreamId,
                                            open.Topic,
                                            open.CallerNode,
                                            context,
                                            open.Metadata,
                                            acceptMeta,
                                            (env, sendCt) => channel.SendAsync(env, sendCt),
                                            () =>
                                            {
                                                _activeStreams.TryRemove(open.StreamId, out _);
                                                return ValueTask.CompletedTask;
                                            });
                _activeStreams[open.StreamId] = stream;
                await channel.SendAsync(MeshEnvelope.Create(MeshMessageTypes.StreamAccept,
                                                            new StreamAcceptPayload(open.StreamId, true, acceptMeta)),
                                        ct);
                return stream;
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
            await handler(incoming, connectionToken);
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
            var incoming = new IncomingBroadcast(broadcast.Topic,
                                                 broadcast.Payload,
                                                 new MeshCallContext(broadcast.CallerNode, broadcast.TraceId, broadcast.HopCount));
            await handler(incoming, cancellationToken);
        }
        catch (Exception)
        {
        }
    }

    private async Task HandleIncomingTaskAsync(WebSocketJsonChannel channel,
                                               TaskDispatchPayload dispatch,
                                               CancellationToken connectionToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
        _incoming[dispatch.TaskId] = cts;
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
                                                                               cts.Token),
                                                      (req, ct) => RequestApprovalFromCallerAsync(channel, req, ct));
            result = await _incomingHandler(incomingTask, cts.Token);
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
            _incoming.TryRemove(dispatch.TaskId, out _);
        }

        await TrySendAsync(channel,
                           MeshEnvelope.Create(MeshMessageTypes.TaskComplete,
                                               new TaskCompletePayload(dispatch.TaskId, result.Status, result.Text, result.Steps)),
                           CancellationToken.None);
    }

    private async Task<RemoteApprovalAnswer> RequestApprovalFromCallerAsync(WebSocketJsonChannel channel,
                                                                            TaskApprovalRequestPayload request,
                                                                            CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<RemoteApprovalAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingApprovals[request.ApprovalId] = tcs;
        using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        try
        {
            await channel.SendAsync(MeshEnvelope.Create(MeshMessageTypes.TaskApprovalRequest, request), cancellationToken);
            return await tcs.Task;
        }
        finally
        {
            _pendingApprovals.TryRemove(request.ApprovalId, out _);
        }
    }

    private static async Task HandleApprovalRequestAsync(WebSocketJsonChannel channel,
                                                         TaskApprovalRequestPayload request,
                                                         OutgoingTaskState state)
    {
        RemoteApprovalAnswer answer;
        try
        {
            answer = await state.OnApprovalRequested(request, state.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            answer = RemoteApprovalAnswer.Deny;
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

    private async Task SendCancelAsync(string taskId)
    {
        if (_channel is { } channel)
        {
            await TrySendAsync(channel,
                               MeshEnvelope.Create(MeshMessageTypes.TaskCancel, new TaskCancelPayload(taskId)),
                               CancellationToken.None);
        }
    }

    private static async Task TrySendAsync(WebSocketJsonChannel channel,
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

    private void FailAllInFlight(string reason)
    {
        foreach (var (_, cts) in _incoming)
        {
            cts.Cancel();
        }

        _incoming.Clear();

        foreach (var (_, cts) in _incomingRpcs)
        {
            cts.Cancel();
        }

        _incomingRpcs.Clear();

        foreach (var (_, pending) in _pendingApprovals)
        {
            pending.TrySetCanceled();
        }

        _pendingApprovals.Clear();

        foreach (var (id, state) in _outgoing)
        {
            if (_outgoing.TryRemove(id, out _))
            {
                state.Completion.TrySetResult(new RemoteTaskResult(RemoteRunStatus.Failed, reason, 0));
            }
        }

        foreach (var (id, rpc) in _outgoingRpcs)
        {
            if (_outgoingRpcs.TryRemove(id, out _))
            {
                rpc.Completion.TrySetResult(RpcResult.Fail(reason));
            }
        }

        foreach (var (id, pendingOpen) in _pendingStreamOpens)
        {
            if (_pendingStreamOpens.TryRemove(id, out _))
            {
                pendingOpen.TrySetResult(new StreamAcceptPayload(id, false, MeshEnvelope.EmptyObject, reason));
            }
        }

        foreach (var (id, stream) in _activeStreams)
        {
            if (_activeStreams.TryRemove(id, out _))
            {
                stream.OnTransportFailure(reason);
            }
        }
    }

    private void UpdatePeers(IReadOnlyList<string> peers, IReadOnlyList<PeerInfo>? peerInfos)
    {
        _peers = peers;
        _peerInfos = peerInfos is { Count: > 0 }
            ? peerInfos
            : [.. peers.Select(p => new PeerInfo(p, p, PeerCapabilities.Default))];
        PeersChanged?.Invoke(peers);
        PeerInfosChanged?.Invoke(_peerInfos);
    }

    private void UpdateState(MeshConnectionState state, string message)
    {
        _state = state;
        _statusMessage = message;
        ConnectionStateChanged?.Invoke(state, message);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        if (_channel is { } channel)
        {
            await channel.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client shutting down", CancellationToken.None);
        }

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _lifetime.Dispose();
    }

    private sealed class OutgoingTaskState(Action<string> onToolStarted,
                                           Func<TaskApprovalRequestPayload, CancellationToken, Task<RemoteApprovalAnswer>> onApprovalRequested,
                                           CancellationToken cancellationToken)
    {
        public Action<string> OnToolStarted { get; } = onToolStarted;

        public Func<TaskApprovalRequestPayload, CancellationToken, Task<RemoteApprovalAnswer>> OnApprovalRequested { get; } = onApprovalRequested;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public TaskCompletionSource<RemoteTaskResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class OutgoingRpcState(Action<string, JsonElement>? onEvent)
    {
        public Action<string, JsonElement>? OnEvent { get; } = onEvent;

        public TaskCompletionSource<RpcResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
