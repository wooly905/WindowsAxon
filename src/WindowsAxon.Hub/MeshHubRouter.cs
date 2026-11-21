using System.Collections.Concurrent;
using System.Net.WebSockets;
using WindowsAxon.Mesh;

namespace WindowsAxon.Hub;

/// <summary>Routes registration, peer presence, tasks, approvals, and cancellations between connected WindowsAxon nodes.</summary>
public sealed class MeshHubRouter
{
    private readonly ConcurrentDictionary<string, ConnectedNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ActiveTask> _tasks = new();

    public IReadOnlyList<string> OnlineNodes => [.. _nodes.Values.Select(n => n.Name).Order(StringComparer.OrdinalIgnoreCase)];

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
                                   MeshEnvelope.Create(MeshMessageTypes.Error, new ErrorPayload("invalid_register", "First message must be a valid register payload.")),
                                   cancellationToken);
                await channel.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Invalid registration", cancellationToken);
                return;
            }

            var nodeName = register.NodeName.Trim();
            var node = new ConnectedNode(nodeName, channel);
            if (!_nodes.TryAdd(nodeName, node))
            {
                await TrySendAsync(channel,
                                   MeshEnvelope.Create(MeshMessageTypes.Error, new ErrorPayload("duplicate_node", $"Node name '{nodeName}' is already connected.")),
                                   cancellationToken);
                await channel.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Duplicate node name", cancellationToken);
                return;
            }

            registeredName = nodeName;
            await channel.SendAsync(MeshEnvelope.Create(MeshMessageTypes.Registered, new RegisteredPayload(nodeName, OnlineNodes)),
                                    cancellationToken);
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
                await CleanupTasksForDisconnectedNodeAsync(registeredName);
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

    private async Task CleanupTasksForDisconnectedNodeAsync(string nodeName)
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
    }

    private async Task BroadcastPeersAsync(string? excludeNode, CancellationToken cancellationToken)
    {
        var envelope = MeshEnvelope.Create(MeshMessageTypes.PeersUpdated, new PeersUpdatedPayload(OnlineNodes));
        var recipients = _nodes.Values
            .Where(n => excludeNode is null || !string.Equals(n.Name, excludeNode, StringComparison.OrdinalIgnoreCase))
            .ToList();
        await Task.WhenAll(recipients.Select(n => TrySendAsync(n.Channel, envelope, cancellationToken)));
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

    private sealed record ConnectedNode(string Name, WebSocketJsonChannel Channel);

    private sealed record ActiveTask(string TaskId, string CallerNode, string TargetNode);
}

