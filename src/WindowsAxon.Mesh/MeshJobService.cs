using System.Collections.Concurrent;
using System.Text.Json;

namespace WindowsAxon.Mesh;

public enum MeshJobState
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

public sealed record MeshJobStatus(string JobId,
                                   string Kind,
                                   MeshJobState State,
                                   string? Progress,
                                   JsonElement? Result,
                                   string? Error,
                                   string TraceId,
                                   DateTimeOffset CreatedUtc,
                                   DateTimeOffset UpdatedUtc)
{
    public bool IsTerminal => State is MeshJobState.Completed or MeshJobState.Failed or MeshJobState.Cancelled;
}

public sealed record MeshJobContext(string JobId,
                                    string Kind,
                                    JsonElement Payload,
                                    MeshCallContext CallContext,
                                    Action<string> ReportProgress);

/// <summary>Pluggable async job lifecycle service (start, poll status, cancel) built on IMeshTransport RPC.</summary>
public sealed class MeshJobService : IDisposable
{
    public const string StartTopic = "job.start";
    public const string StatusTopic = "job.status";
    public const string CancelTopic = "job.cancel";

    private static readonly TimeSpan DefaultCompletedRetention = TimeSpan.FromHours(1);

    private readonly IMeshTransport _transport;
    private readonly TimeSpan _completedRetention;
    private readonly ConcurrentDictionary<string, Func<MeshJobContext, CancellationToken, Task<JsonElement>>> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, JobEntry> _jobs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();

    public MeshJobService(IMeshTransport transport, TimeSpan? completedRetention = null)
    {
        ArgumentNullException.ThrowIfNull(transport);

        _transport = transport;
        _completedRetention = completedRetention ?? DefaultCompletedRetention;

        _transport.RegisterRpcHandler(StartTopic, HandleStartAsync);
        _transport.RegisterRpcHandler(StatusTopic, HandleStatusAsync);
        _transport.RegisterRpcHandler(CancelTopic, HandleCancelAsync);
    }

    public void RegisterWorker(string kind, Func<MeshJobContext, CancellationToken, Task<JsonElement>> worker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(worker);
        _workers[kind.Trim()] = worker;
    }

    public async Task<string> StartJobAsync(string targetNode,
                                            string kind,
                                            JsonElement payload,
                                            MeshCallContext? context = null,
                                            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetNode);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        var req = JsonSerializer.SerializeToElement(new StartJobRequest(kind.Trim(), payload), MeshEnvelope.JsonOptions);
        var rpc = await _transport.InvokeRpcAsync(targetNode, StartTopic, req, context: context, cancellationToken: cancellationToken);
        if (!rpc.Success)
        {
            throw new InvalidOperationException(rpc.Error ?? $"Failed to start job '{kind}' on '{targetNode}'.");
        }

        var response = rpc.Payload.Deserialize<StartJobResponse>(MeshEnvelope.JsonOptions)
            ?? throw new InvalidDataException("Invalid job.start response.");
        return response.JobId;
    }

    public async Task<MeshJobStatus> GetJobStatusAsync(string targetNode,
                                                       string jobId,
                                                       MeshCallContext? context = null,
                                                       CancellationToken cancellationToken = default)
    {
        var req = JsonSerializer.SerializeToElement(new JobIdRequest(jobId), MeshEnvelope.JsonOptions);
        var rpc = await _transport.InvokeRpcAsync(targetNode, StatusTopic, req, context: context, cancellationToken: cancellationToken);
        if (!rpc.Success)
        {
            throw new InvalidOperationException(rpc.Error ?? $"Could not query job '{jobId}' on '{targetNode}'.");
        }

        return rpc.Payload.Deserialize<MeshJobStatus>(MeshEnvelope.JsonOptions)
            ?? throw new InvalidDataException("Invalid job.status response.");
    }

    public async Task CancelJobAsync(string targetNode,
                                     string jobId,
                                     MeshCallContext? context = null,
                                     CancellationToken cancellationToken = default)
    {
        var req = JsonSerializer.SerializeToElement(new JobIdRequest(jobId), MeshEnvelope.JsonOptions);
        await _transport.InvokeRpcAsync(targetNode, CancelTopic, req, context: context, cancellationToken: cancellationToken);
    }

    public async Task<MeshJobStatus> WaitForJobAsync(string targetNode,
                                                     string jobId,
                                                     TimeSpan? pollInterval = null,
                                                     Action<string>? onProgress = null,
                                                     MeshCallContext? context = null,
                                                     CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        string? lastProgress = null;

        using var cancelReg = cancellationToken.Register(() => _ = CancelJobAsync(targetNode, jobId, context, CancellationToken.None));

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var status = await GetJobStatusAsync(targetNode, jobId, context, cancellationToken);
                if (!string.IsNullOrEmpty(status.Progress) && status.Progress != lastProgress)
                {
                    lastProgress = status.Progress;
                    onProgress?.Invoke(status.Progress);
                }

                if (status.IsTerminal)
                {
                    return status;
                }
            }
            catch (InvalidOperationException) when (_transport.State != MeshConnectionState.Connected)
            {
                // Transient disconnect while polling; wait for reconnect and re-poll the job ID.
            }

            await Task.Delay(interval, cancellationToken);
        }
    }

    private Task<RpcResult> HandleStartAsync(IncomingRpc rpc, CancellationToken cancellationToken)
    {
        PruneExpiredJobs();
        var req = rpc.Payload.Deserialize<StartJobRequest>(MeshEnvelope.JsonOptions);
        if (req is null || string.IsNullOrWhiteSpace(req.Kind))
        {
            return Task.FromResult(RpcResult.Fail("Invalid job.start payload."));
        }

        if (!_workers.TryGetValue(req.Kind, out var worker))
        {
            return Task.FromResult(RpcResult.Fail($"No job worker registered for kind '{req.Kind}'."));
        }

        var jobId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var entry = new JobEntry(new MeshJobStatus(jobId,
                                                   req.Kind,
                                                   MeshJobState.Queued,
                                                   null,
                                                   null,
                                                   null,
                                                   rpc.Context.TraceId,
                                                   now,
                                                   now),
                                 cts);
        _jobs[jobId] = entry;

        _ = Task.Run(() => RunWorkerAsync(entry, worker, req.Payload, rpc.Context), CancellationToken.None);
        return Task.FromResult(RpcResult.Ok(new StartJobResponse(jobId)));
    }

    private Task<RpcResult> HandleStatusAsync(IncomingRpc rpc, CancellationToken cancellationToken)
    {
        var req = rpc.Payload.Deserialize<JobIdRequest>(MeshEnvelope.JsonOptions);
        if (req is null || string.IsNullOrWhiteSpace(req.JobId))
        {
            return Task.FromResult(RpcResult.Fail("Invalid job.status payload."));
        }

        if (!_jobs.TryGetValue(req.JobId, out var entry))
        {
            return Task.FromResult(RpcResult.Fail($"Job '{req.JobId}' was not found."));
        }

        return Task.FromResult(RpcResult.Ok(entry.Status));
    }

    private Task<RpcResult> HandleCancelAsync(IncomingRpc rpc, CancellationToken cancellationToken)
    {
        var req = rpc.Payload.Deserialize<JobIdRequest>(MeshEnvelope.JsonOptions);
        if (req is not null && !string.IsNullOrWhiteSpace(req.JobId) && _jobs.TryGetValue(req.JobId, out var entry))
        {
            entry.Cts.Cancel();
        }

        return Task.FromResult(RpcResult.Ok());
    }

    private static async Task RunWorkerAsync(JobEntry entry,
                                             Func<MeshJobContext, CancellationToken, Task<JsonElement>> worker,
                                             JsonElement payload,
                                             MeshCallContext callContext)
    {
        entry.Update(s => s with { State = MeshJobState.Running, UpdatedUtc = DateTimeOffset.UtcNow });
        try
        {
            var jobCtx = new MeshJobContext(entry.Status.JobId,
                                            entry.Status.Kind,
                                            payload,
                                            callContext,
                                            progress => entry.Update(s => s with { Progress = progress, UpdatedUtc = DateTimeOffset.UtcNow }));
            var result = await worker(jobCtx, entry.Cts.Token);
            entry.Update(s => s with
            {
                State = MeshJobState.Completed,
                Result = result,
                UpdatedUtc = DateTimeOffset.UtcNow,
            });
        }
        catch (OperationCanceledException)
        {
            entry.Update(s => s with
            {
                State = MeshJobState.Cancelled,
                Error = "Cancelled.",
                UpdatedUtc = DateTimeOffset.UtcNow,
            });
        }
        catch (Exception ex)
        {
            entry.Update(s => s with
            {
                State = MeshJobState.Failed,
                Error = ex.Message,
                UpdatedUtc = DateTimeOffset.UtcNow,
            });
        }
        finally
        {
            entry.Cts.Dispose();
        }
    }

    private void PruneExpiredJobs()
    {
        var cutoff = DateTimeOffset.UtcNow - _completedRetention;
        foreach (var (id, entry) in _jobs)
        {
            var status = entry.Status;
            if (status.IsTerminal && status.UpdatedUtc < cutoff)
            {
                _jobs.TryRemove(id, out _);
            }
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        foreach (var (_, entry) in _jobs)
        {
            try
            {
                entry.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _lifetime.Dispose();
    }

    private sealed class JobEntry(MeshJobStatus initial, CancellationTokenSource cts)
    {
        private readonly Lock _lock = new();
        private MeshJobStatus _status = initial;

        public CancellationTokenSource Cts { get; } = cts;

        public MeshJobStatus Status
        {
            get
            {
                lock (_lock)
                {
                    return _status;
                }
            }
        }

        public void Update(Func<MeshJobStatus, MeshJobStatus> transform)
        {
            lock (_lock)
            {
                _status = transform(_status);
            }
        }
    }

    private sealed record StartJobRequest(string Kind, JsonElement Payload);

    private sealed record StartJobResponse(string JobId);

    private sealed record JobIdRequest(string JobId);
}
