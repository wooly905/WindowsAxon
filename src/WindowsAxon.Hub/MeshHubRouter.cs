using System.Collections.Concurrent;
using System.Net.WebSockets;
using WindowsAxon.Mesh;

namespace WindowsAxon.Hub;

/// <summary>Routes registration, peer presence, legacy tasks, generic RPCs, binary streams, and broadcasts.</summary>
public sealed class MeshHubRouter
{
    private readonly ConcurrentDictionary<string, ConnectedNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ActiveTask> _tasks = new();
    private readonly ConcurrentDictionary<string, ActiveRoute> _rpcs = new();
    private readonly ConcurrentDictionary<string, ActiveStreamRoute> _streams = new();

    // Orders peer-list messages: a new node gets Registered before any PeersUpdated, and snapshots go out in order.
    private readonly SemaphoreSlim _membership = new(1, 1);

    public IReadOnlyList<string> OnlineNodes =>
        [.. _nodes.Values.Select(n => n.Name).Order(StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<PeerInfo> OnlinePeerInfos =>
        [.. _nodes.Values
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Select(n => new PeerInfo(n.NodeId, n.Name, n.Capabilities, n.ConnectedUtc))];

    public async Task HandleConnectionAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var channel = new WebSocketJsonChannel(socket);
        string? registeredName = null;
        try
        {
            var first = await channel.ReceiveAsync(cancellationToken);
            if (first is null || first.Type != MeshMessageTypes.Register
                || first.DeserializePayload<RegisterPayload>() is not { } register
                || string.IsNullOrWhiteSpace(register.NodeName))
            {
                await TrySendAsync(channel,
                                   MeshEnvelope.Create(MeshMessageTypes.Error,
                                                       new ErrorPayload("invalid_register", "First message must be a valid register payload.")),
                                   cancellationToken);
                await channel.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Invalid registration", cancellationToken);
                return;
            }

            var nodeName = register.NodeName.Trim();
            var nodeId = string.IsNullOrWhiteSpace(register.NodeId) ? $"legacy-{Guid.NewGuid():N}" : register.NodeId.Trim();
            var capabilities = register.Capabilities ?? PeerCapabilities.Default;
            var node = new ConnectedNode(nodeName, nodeId, capabilities, DateTimeOffset.UtcNow, channel);
            bool added;
            await _membership.WaitAsync(cancellationToken);
            try
            {
                added = _nodes.TryAdd(nodeName, node);
                if (added)
                {
                    registeredName = nodeName;
                    await channel.SendAsync(MeshEnvelope.Create(MeshMessageTypes.Registered,
                                                                new RegisteredPayload(nodeName, OnlineNodes, OnlinePeerInfos)),
                                            cancellationToken);
                }
            }
            finally
            {
                _membership.Release();
            }

            if (!added)
            {
                await TrySendAsync(channel,
                                   MeshEnvelope.Create(MeshMessageTypes.Error,
                                                       new ErrorPayload("duplicate_node", $"Node name '{nodeName}' is already connected.")),
                                   cancellationToken);
                await channel.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Duplicate node name", cancellationToken);
                return;
            }

            await BroadcastPeersAsync(excludeNode: nodeName, cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                var envelope = await channel.ReceiveAsync(cancellationToken);
                if (envelope is null)
                {
                    break;
                }

                await RouteMessageAsync(node, envelope, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            if (registeredName is not null)
            {
                _nodes.TryRemove(registeredName, out _);
                await CleanupForDisconnectedNodeAsync(registeredName);
                await BroadcastPeersAsync(excludeNode: null, CancellationToken.None);
            }
        }
    }

    private async Task RouteMessageAsync(ConnectedNode sender, MeshEnvelope envelope, CancellationToken cancellationToken)
    {
        switch (envelope.Type)
        {
            case MeshMessageTypes.TaskDispatch:
                if (envelope.DeserializePayload<TaskDispatchPayload>() is { } dispatch)
                {
                    await HandleDispatchAsync(sender, dispatch, cancellationToken);
                }
                break;

            case MeshMessageTypes.TaskToolStarted:
                if (envelope.DeserializePayload<TaskToolStartedPayload>() is { } started
                    && _tasks.TryGetValue(started.TaskId, out var taskForTool)
                    && string.Equals(taskForTool.TargetNode, sender.Name, StringComparison.OrdinalIgnoreCase)
                    && _nodes.TryGetValue(taskForTool.CallerNode, out var callerForTool))
                {
                    await TrySendAsync(callerForTool.Channel, envelope, cancellationToken);
                }
                break;

            case MeshMessageTypes.TaskApprovalRequest:
                if (envelope.DeserializePayload<TaskApprovalRequestPayload>() is { } req
                    && _tasks.TryGetValue(req.TaskId, out var taskForApproval)
                    && string.Equals(taskForApproval.TargetNode, sender.Name, StringComparison.OrdinalIgnoreCase)
                    && _nodes.TryGetValue(taskForApproval.CallerNode, out var callerForApproval))
                {
                    await TrySendAsync(callerForApproval.Channel, envelope, cancellationToken);
                }
                break;

            case MeshMessageTypes.TaskApprovalResponse:
                if (envelope.DeserializePayload<TaskApprovalResponsePayload>() is { } resp
                    && _tasks.TryGetValue(resp.TaskId, out var taskForResp)
                    && string.Equals(taskForResp.CallerNode, sender.Name, StringComparison.OrdinalIgnoreCase)
                    && _nodes.TryGetValue(taskForResp.TargetNode, out var targetForResp))
                {
                    await TrySendAsync(targetForResp.Channel, envelope, cancellationToken);
                }
                break;

            case MeshMessageTypes.TaskCancel:
                if (envelope.DeserializePayload<TaskCancelPayload>() is { } cancel
                    && _tasks.TryGetValue(cancel.TaskId, out var taskForCancel)
                    && string.Equals(taskForCancel.CallerNode, sender.Name, StringComparison.OrdinalIgnoreCase)
                    && _nodes.TryGetValue(taskForCancel.TargetNode, out var targetForCancel))
                {
                    await TrySendAsync(targetForCancel.Channel, envelope, cancellationToken);
                }
                break;

            case MeshMessageTypes.TaskComplete:
                if (envelope.DeserializePayload<TaskCompletePayload>() is { } complete
                    && _tasks.TryRemove(complete.TaskId, out var taskForDone)
                    && string.Equals(taskForDone.TargetNode, sender.Name, StringComparison.OrdinalIgnoreCase)
                    && _nodes.TryGetValue(taskForDone.CallerNode, out var callerForDone))
                {
                    await TrySendAsync(callerForDone.Channel, envelope, cancellationToken);
                }
                break;

            case MeshMessageTypes.RpcRequest:
                if (envelope.DeserializePayload<RpcRequestPayload>() is { } rpcReq)
                {
                    await HandleRpcRequestAsync(sender, rpcReq, cancellationToken);
                }
                break;

            case MeshMessageTypes.RpcEvent:
                if (envelope.DeserializePayload<RpcEventPayload>() is { } rpcEvent
                    && _rpcs.TryGetValue(rpcEvent.CorrelationId, out var rpcForEvent)
                    && string.Equals(rpcForEvent.TargetNode, sender.Name, StringComparison.OrdinalIgnoreCase)
                    && _nodes.TryGetValue(rpcForEvent.CallerNode, out var callerForRpcEvent))
                {
                    await TrySendAsync(callerForRpcEvent.Channel, envelope, cancellationToken);
                }
                break;

            case MeshMessageTypes.RpcCancel:
                if (envelope.DeserializePayload<RpcCancelPayload>() is { } rpcCancel
                    && _rpcs.TryGetValue(rpcCancel.CorrelationId, out var rpcForCancel)
                    && string.Equals(rpcForCancel.CallerNode, sender.Name, StringComparison.OrdinalIgnoreCase)
                    && _nodes.TryGetValue(rpcForCancel.TargetNode, out var targetForRpcCancel))
                {
                    await TrySendAsync(targetForRpcCancel.Channel, envelope, cancellationToken);
                }
                break;

            case MeshMessageTypes.RpcResponse:
                if (envelope.DeserializePayload<RpcResponsePayload>() is { } rpcResp
                    && _rpcs.TryRemove(rpcResp.CorrelationId, out var rpcForResp)
                    && string.Equals(rpcForResp.TargetNode, sender.Name, StringComparison.OrdinalIgnoreCase)
                    && _nodes.TryGetValue(rpcForResp.CallerNode, out var callerForRpcResp))
                {
                    await TrySendAsync(callerForRpcResp.Channel, envelope, cancellationToken);
                }
                break;

            case MeshMessageTypes.StreamOpen:
                if (envelope.DeserializePayload<StreamOpenPayload>() is { } streamOpen)
                {
                    await HandleStreamOpenAsync(sender, streamOpen, cancellationToken);
                }
                break;

            case MeshMessageTypes.StreamAccept:
                if (envelope.DeserializePayload<StreamAcceptPayload>() is { } streamAccept
                    && _streams.TryGetValue(streamAccept.StreamId, out var streamForAccept)
                    && string.Equals(streamForAccept.TargetNode, sender.Name, StringComparison.OrdinalIgnoreCase))
                {
                    if (!streamAccept.Accepted)
                    {
                        _streams.TryRemove(streamAccept.StreamId, out _);
                    }

                    if (_nodes.TryGetValue(streamForAccept.CallerNode, out var callerForAccept))
                    {
                        await TrySendAsync(callerForAccept.Channel, envelope, cancellationToken);
                    }
                }
                break;

            case MeshMessageTypes.StreamChunk:
                if (envelope.DeserializePayload<StreamChunkPayload>() is { } streamChunk
                    && _streams.TryGetValue(streamChunk.StreamId, out var streamForChunk)
                    && TryGetStreamPeer(streamForChunk, sender.Name, out var peerForChunk))
                {
                    await TrySendAsync(peerForChunk.Channel, envelope, cancellationToken);
                }
                break;

            case MeshMessageTypes.StreamClose:
                if (envelope.DeserializePayload<StreamClosePayload>() is { } streamClose
                    && _streams.TryGetValue(streamClose.StreamId, out var streamForClose)
                    && TryGetStreamPeer(streamForClose, sender.Name, out var peerForClose))
                {
                    var isCaller = string.Equals(streamForClose.CallerNode, sender.Name, StringComparison.OrdinalIgnoreCase);
                    if (!streamClose.Success || streamForClose.MarkClosed(isCaller))
                    {
                        _streams.TryRemove(streamClose.StreamId, out _);
                    }

                    await TrySendAsync(peerForClose.Channel, envelope, cancellationToken);
                }
                break;

            case MeshMessageTypes.Broadcast:
                if (envelope.DeserializePayload<BroadcastPayload>() is { } broadcast)
                {
                    await HandleBroadcastAsync(sender, broadcast, cancellationToken);
                }
                break;
        }
    }

    private async Task HandleDispatchAsync(ConnectedNode caller, TaskDispatchPayload dispatch, CancellationToken cancellationToken)
    {
        if (!_nodes.TryGetValue(dispatch.TargetNode, out var target))
        {
            await TrySendAsync(caller.Channel,
                               MeshEnvelope.Create(MeshMessageTypes.TaskComplete,
                                                   new TaskCompletePayload(dispatch.TaskId,
                                                                           RemoteRunStatus.Failed,
                                                                           $"Peer '{dispatch.TargetNode}' is not online.",
                                                                           0)),
                               cancellationToken);
            return;
        }

        // Bind the task to the authenticated sender name rather than trusting the payload's CallerNode.
        var normalized = dispatch with { CallerNode = caller.Name, TargetNode = target.Name };
        var active = new ActiveTask(normalized.TaskId, caller.Name, target.Name);
        if (!_tasks.TryAdd(normalized.TaskId, active))
        {
            return;
        }

        var sent = await TrySendAsync(target.Channel,
                                      MeshEnvelope.Create(MeshMessageTypes.TaskDispatch, normalized),
                                      cancellationToken);
        if (!sent && _tasks.TryRemove(normalized.TaskId, out _))
        {
            await TrySendAsync(caller.Channel,
                               MeshEnvelope.Create(MeshMessageTypes.TaskComplete,
                                                   new TaskCompletePayload(normalized.TaskId,
                                                                           RemoteRunStatus.Failed,
                                                                           $"Peer '{target.Name}' disconnected before receiving the task.",
                                                                           0)),
                               cancellationToken);
        }
    }

    private async Task HandleRpcRequestAsync(ConnectedNode caller, RpcRequestPayload request, CancellationToken cancellationToken)
    {
        if (request.HopCount > MeshCallContext.DefaultMaxHops)
        {
            await TrySendAsync(caller.Channel,
                               MeshEnvelope.Create(MeshMessageTypes.RpcResponse,
                                                   new RpcResponsePayload(request.CorrelationId,
                                                                          false,
                                                                          MeshEnvelope.EmptyObject,
                                                                          $"Mesh hop limit ({MeshCallContext.DefaultMaxHops}) exceeded.")),
                               cancellationToken);
            return;
        }

        if (!_nodes.TryGetValue(request.TargetNode, out var target))
        {
            await TrySendAsync(caller.Channel,
                               MeshEnvelope.Create(MeshMessageTypes.RpcResponse,
                                                   new RpcResponsePayload(request.CorrelationId,
                                                                          false,
                                                                          MeshEnvelope.EmptyObject,
                                                                          $"Peer '{request.TargetNode}' is not online.")),
                               cancellationToken);
            return;
        }

        var normalized = request with { CallerNode = caller.Name, TargetNode = target.Name };
        if (!_rpcs.TryAdd(normalized.CorrelationId, new ActiveRoute(caller.Name, target.Name)))
        {
            return;
        }

        var sent = await TrySendAsync(target.Channel,
                                      MeshEnvelope.Create(MeshMessageTypes.RpcRequest, normalized),
                                      cancellationToken);
        if (!sent && _rpcs.TryRemove(normalized.CorrelationId, out _))
        {
            await TrySendAsync(caller.Channel,
                               MeshEnvelope.Create(MeshMessageTypes.RpcResponse,
                                                   new RpcResponsePayload(normalized.CorrelationId,
                                                                          false,
                                                                          MeshEnvelope.EmptyObject,
                                                                          $"Peer '{target.Name}' disconnected before receiving the request.")),
                               cancellationToken);
        }
    }

    private async Task HandleStreamOpenAsync(ConnectedNode caller, StreamOpenPayload open, CancellationToken cancellationToken)
    {
        if (open.HopCount > MeshCallContext.DefaultMaxHops)
        {
            await TrySendAsync(caller.Channel,
                               MeshEnvelope.Create(MeshMessageTypes.StreamAccept,
                                                   new StreamAcceptPayload(open.StreamId,
                                                                           false,
                                                                           MeshEnvelope.EmptyObject,
                                                                           $"Mesh hop limit ({MeshCallContext.DefaultMaxHops}) exceeded.")),
                               cancellationToken);
            return;
        }

        if (!_nodes.TryGetValue(open.TargetNode, out var target))
        {
            await TrySendAsync(caller.Channel,
                               MeshEnvelope.Create(MeshMessageTypes.StreamAccept,
                                                   new StreamAcceptPayload(open.StreamId,
                                                                           false,
                                                                           MeshEnvelope.EmptyObject,
                                                                           $"Peer '{open.TargetNode}' is not online.")),
                               cancellationToken);
            return;
        }

        var normalized = open with { CallerNode = caller.Name, TargetNode = target.Name };
        if (!_streams.TryAdd(normalized.StreamId, new ActiveStreamRoute(caller.Name, target.Name)))
        {
            return;
        }

        var sent = await TrySendAsync(target.Channel,
                                      MeshEnvelope.Create(MeshMessageTypes.StreamOpen, normalized),
                                      cancellationToken);
        if (!sent && _streams.TryRemove(normalized.StreamId, out _))
        {
            await TrySendAsync(caller.Channel,
                               MeshEnvelope.Create(MeshMessageTypes.StreamAccept,
                                                   new StreamAcceptPayload(normalized.StreamId,
                                                                           false,
                                                                           MeshEnvelope.EmptyObject,
                                                                           $"Peer '{target.Name}' disconnected before accepting the stream.")),
                               cancellationToken);
        }
    }

    private async Task HandleBroadcastAsync(ConnectedNode sender, BroadcastPayload broadcast, CancellationToken cancellationToken)
    {
        if (broadcast.HopCount > MeshCallContext.DefaultMaxHops)
        {
            return;
        }

        var normalized = broadcast with { CallerNode = sender.Name };
        var envelope = MeshEnvelope.Create(MeshMessageTypes.Broadcast, normalized);
        var recipients = _nodes.Values
            .Where(n => !string.Equals(n.Name, sender.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        await Task.WhenAll(recipients.Select(n => TrySendAsync(n.Channel, envelope, cancellationToken)));
    }

    private bool TryGetStreamPeer(ActiveStreamRoute route, string senderName, out ConnectedNode peer)
    {
        string peerName;
        if (string.Equals(route.CallerNode, senderName, StringComparison.OrdinalIgnoreCase))
        {
            peerName = route.TargetNode;
        }
        else if (string.Equals(route.TargetNode, senderName, StringComparison.OrdinalIgnoreCase))
        {
            peerName = route.CallerNode;
        }
        else
        {
            peer = null!;
            return false;
        }

        return _nodes.TryGetValue(peerName, out peer!);
    }

    private async Task CleanupForDisconnectedNodeAsync(string nodeName)
    {
        foreach (var (taskId, task) in _tasks)
        {
            if (string.Equals(task.TargetNode, nodeName, StringComparison.OrdinalIgnoreCase))
            {
                if (_tasks.TryRemove(taskId, out _) && _nodes.TryGetValue(task.CallerNode, out var caller))
                {
                    await TrySendAsync(caller.Channel,
                                       MeshEnvelope.Create(MeshMessageTypes.TaskComplete,
                                                           new TaskCompletePayload(taskId,
                                                                                   RemoteRunStatus.Failed,
                                                                                   $"Peer '{nodeName}' disconnected while running the request.",
                                                                                   0)),
                                       CancellationToken.None);
                }
            }
            else if (string.Equals(task.CallerNode, nodeName, StringComparison.OrdinalIgnoreCase))
            {
                if (_tasks.TryRemove(taskId, out _) && _nodes.TryGetValue(task.TargetNode, out var target))
                {
                    await TrySendAsync(target.Channel,
                                       MeshEnvelope.Create(MeshMessageTypes.TaskCancel, new TaskCancelPayload(taskId)),
                                       CancellationToken.None);
                }
            }
        }

        foreach (var (corrId, route) in _rpcs)
        {
            if (string.Equals(route.TargetNode, nodeName, StringComparison.OrdinalIgnoreCase))
            {
                if (_rpcs.TryRemove(corrId, out _) && _nodes.TryGetValue(route.CallerNode, out var caller))
                {
                    await TrySendAsync(caller.Channel,
                                       MeshEnvelope.Create(MeshMessageTypes.RpcResponse,
                                                           new RpcResponsePayload(corrId,
                                                                                  false,
                                                                                  MeshEnvelope.EmptyObject,
                                                                                  $"Peer '{nodeName}' disconnected.")),
                                       CancellationToken.None);
                }
            }
            else if (string.Equals(route.CallerNode, nodeName, StringComparison.OrdinalIgnoreCase))
            {
                if (_rpcs.TryRemove(corrId, out _) && _nodes.TryGetValue(route.TargetNode, out var target))
                {
                    await TrySendAsync(target.Channel,
                                       MeshEnvelope.Create(MeshMessageTypes.RpcCancel, new RpcCancelPayload(corrId)),
                                       CancellationToken.None);
                }
            }
        }

        foreach (var (streamId, route) in _streams)
        {
            var isCaller = string.Equals(route.CallerNode, nodeName, StringComparison.OrdinalIgnoreCase);
            var isTarget = string.Equals(route.TargetNode, nodeName, StringComparison.OrdinalIgnoreCase);
            if (!isCaller && !isTarget)
            {
                continue;
            }

            if (_streams.TryRemove(streamId, out _))
            {
                var otherName = isCaller ? route.TargetNode : route.CallerNode;
                if (_nodes.TryGetValue(otherName, out var other))
                {
                    await TrySendAsync(other.Channel,
                                       MeshEnvelope.Create(MeshMessageTypes.StreamClose,
                                                           new StreamClosePayload(streamId,
                                                                                  false,
                                                                                  MeshEnvelope.EmptyObject,
                                                                                  $"Peer '{nodeName}' disconnected during stream transfer.")),
                                       CancellationToken.None);
                }
            }
        }
    }

    private async Task BroadcastPeersAsync(string? excludeNode, CancellationToken cancellationToken)
    {
        await _membership.WaitAsync(cancellationToken);
        try
        {
            var envelope = MeshEnvelope.Create(MeshMessageTypes.PeersUpdated,
                                               new PeersUpdatedPayload(OnlineNodes, OnlinePeerInfos));
            var recipients = _nodes.Values
                .Where(n => excludeNode is null || !string.Equals(n.Name, excludeNode, StringComparison.OrdinalIgnoreCase))
                .ToList();
            await Task.WhenAll(recipients.Select(n => TrySendAsync(n.Channel, envelope, cancellationToken)));
        }
        finally
        {
            _membership.Release();
        }
    }

    private static async Task<bool> TrySendAsync(WebSocketJsonChannel channel,
                                                 MeshEnvelope envelope,
                                                 CancellationToken cancellationToken)
    {
        try
        {
            await channel.SendAsync(envelope, cancellationToken);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed record ConnectedNode(string Name,
                                        string NodeId,
                                        PeerCapabilities Capabilities,
                                        DateTimeOffset ConnectedUtc,
                                        WebSocketJsonChannel Channel);

    private sealed record ActiveTask(string TaskId, string CallerNode, string TargetNode);

    private sealed record ActiveRoute(string CallerNode, string TargetNode);

    private sealed class ActiveStreamRoute(string callerNode, string targetNode)
    {
        private int _callerClosed;
        private int _targetClosed;

        public string CallerNode { get; } = callerNode;

        public string TargetNode { get; } = targetNode;

        public bool MarkClosed(bool isCaller)
        {
            if (isCaller)
            {
                Volatile.Write(ref _callerClosed, 1);
            }
            else
            {
                Volatile.Write(ref _targetClosed, 1);
            }

            return Volatile.Read(ref _callerClosed) != 0 && Volatile.Read(ref _targetClosed) != 0;
        }
    }
}
