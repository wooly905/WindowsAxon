using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace WindowsAxon.Mesh;

public sealed record RemoteTaskResult(RemoteRunStatus Status, string Text, int Steps);

public sealed record IncomingRemoteTask(string TaskId,
                                        string CallerNode,
                                        string Prompt,
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
public sealed class MeshClient : IAsyncDisposable
{
    private static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    ];

    private readonly Uri _hubUri;
    private readonly string _nodeName;
    private readonly string? _token;
    private readonly Func<IncomingRemoteTask, CancellationToken, Task<RemoteTaskResult>> _incomingHandler;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, OutgoingTaskState> _outgoing = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _incoming = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemoteApprovalAnswer>> _pendingApprovals = new();

    private WebSocketJsonChannel? _channel;
    private Task? _loopTask;
    private volatile IReadOnlyList<string> _peers = [];
    private MeshConnectionState _state = MeshConnectionState.Disconnected;
    private string _statusMessage = "Disconnected";

    public MeshClient(Uri hubUri,
                      string nodeName,
                      string? token,
                      Func<IncomingRemoteTask, CancellationToken, Task<RemoteTaskResult>> incomingHandler)
    {
        ArgumentNullException.ThrowIfNull(hubUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        ArgumentNullException.ThrowIfNull(incomingHandler);

        _hubUri = hubUri;
        _nodeName = nodeName.Trim();
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        _incomingHandler = incomingHandler;
    }

    public string NodeName => _nodeName;

    public MeshConnectionState State => _state;

    public string StatusMessage => _statusMessage;

    public IReadOnlyList<string> OnlinePeers => _peers;

    public event Action<IReadOnlyList<string>>? PeersChanged;

    public event Action<MeshConnectionState, string>? ConnectionStateChanged;

    public void Start()
    {
        _loopTask ??= Task.Run(() => RunConnectionLoopAsync(_lifetime.Token));
    }

    public async Task<RemoteTaskResult> RunRemoteTaskAsync(string targetNode,
                                                           string prompt,
                                                           Action<string> onToolStarted,
                                                           Func<TaskApprovalRequestPayload, CancellationToken, Task<RemoteApprovalAnswer>> onApprovalRequested,
                                                           CancellationToken cancellationToken)
    {
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
                                                        new TaskDispatchPayload(taskId, _nodeName, targetNode, prompt)),
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

                await channel.SendAsync(MeshEnvelope.Create(MeshMessageTypes.Register, new RegisterPayload(_nodeName)),
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
                    // Stop retrying on explicit auth or duplicate-name errors until settings change.
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
                UpdatePeers(reg.Peers);
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
                UpdatePeers([]);
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
                        UpdatePeers(peers.Peers);
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
            }
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
            // Connection closed or cancelled; receive loop handles teardown.
        }
    }

    private void FailAllInFlight(string reason)
    {
        foreach (var (_, cts) in _incoming)
        {
            cts.Cancel();
        }

        _incoming.Clear();

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
    }

    private void UpdatePeers(IReadOnlyList<string> peers)
    {
        _peers = peers;
        PeersChanged?.Invoke(peers);
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
}

