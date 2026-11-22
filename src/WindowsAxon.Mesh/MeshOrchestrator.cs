using System.Text;
using System.Text.Json;

namespace WindowsAxon.Mesh;

public sealed record PeerTaskSpec(string TargetNode, string Prompt, string? StepId = null);

public sealed record PeerTaskOutcome(string StepId,
                                     string TargetNode,
                                     bool Success,
                                     RemoteRunStatus Status,
                                     string Text,
                                     int Steps,
                                     string? Error = null,
                                     string? JobId = null,
                                     string? TraceId = null);

public sealed record MapTaskSpec(string ShardId, string Prompt, string? PreferredNode = null);

public sealed record MapReduceResult(IReadOnlyList<PeerTaskOutcome> MapOutcomes,
                                     PeerTaskOutcome? ReduceOutcome,
                                     string TraceId);

public sealed record IncomingOrchestratedTask(string JobId,
                                              string CallerNode,
                                              string Prompt,
                                              MeshCallContext CallContext,
                                              Action<string> ReportProgress,
                                              Func<TaskApprovalRequestPayload, CancellationToken, Task<RemoteApprovalAnswer>> RequestApprovalAsync);

/// <summary>
/// Distributed orchestrator built on IMeshTransport + MeshJobService supporting single-step delegation,
/// parallel fan-out, and fault-tolerant MapReduce across online WindowsAxon peers.
/// </summary>
public sealed class MeshOrchestrator
{
    public const string AgentPromptJobKind = "agent.prompt";
    public const string ApprovalRpcTopic = "orchestrator.approval";

    private readonly IMeshTransport _transport;
    private readonly MeshJobService _jobs;
    private readonly Func<IncomingOrchestratedTask, CancellationToken, Task<RemoteTaskResult>> _taskHandler;
    private readonly Func<string, TaskApprovalRequestPayload, CancellationToken, Task<RemoteApprovalAnswer>>? _approvalHandler;
    private readonly TimeSpan _pollInterval;

    public MeshOrchestrator(IMeshTransport transport,
                            MeshJobService jobs,
                            Func<IncomingOrchestratedTask, CancellationToken, Task<RemoteTaskResult>> taskHandler,
                            Func<string, TaskApprovalRequestPayload, CancellationToken, Task<RemoteApprovalAnswer>>? approvalHandler = null,
                            TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(taskHandler);

        _transport = transport;
        _jobs = jobs;
        _taskHandler = taskHandler;
        _approvalHandler = approvalHandler;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(200);

        _jobs.RegisterWorker(AgentPromptJobKind, HandleAgentJobAsync);
        _transport.RegisterRpcHandler(ApprovalRpcTopic, HandleApprovalRpcAsync);
    }

    public async Task<PeerTaskOutcome> RunStepAsync(string targetNode,
                                                    string prompt,
                                                    string? stepId = null,
                                                    MeshCallContext? context = null,
                                                    Action<string>? onProgress = null,
                                                    CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetNode);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var id = string.IsNullOrWhiteSpace(stepId) ? Guid.NewGuid().ToString("N")[..8] : stepId.Trim();
        var callCtx = context ?? MeshCallContext.CreateRoot(_transport.NodeName);
        var payload = JsonSerializer.SerializeToElement(new AgentJobRequest(prompt, _transport.NodeName), MeshEnvelope.JsonOptions);

        string jobId;
        try
        {
            jobId = await _jobs.StartJobAsync(targetNode, AgentPromptJobKind, payload, callCtx, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new PeerTaskOutcome(id, targetNode, false, RemoteRunStatus.Cancelled, "Stopped.", 0, "Cancelled.", null, callCtx.TraceId);
        }
        catch (Exception ex)
        {
            return new PeerTaskOutcome(id, targetNode, false, RemoteRunStatus.Failed, ex.Message, 0, ex.Message, null, callCtx.TraceId);
        }

        try
        {
            var final = await _jobs.WaitForJobAsync(targetNode,
                                                    jobId,
                                                    _pollInterval,
                                                    progress => onProgress?.Invoke($"[{targetNode}] {progress}"),
                                                    callCtx,
                                                    cancellationToken);
            if (final.State == MeshJobState.Completed && final.Result is { } resJson)
            {
                var parsed = resJson.Deserialize<AgentJobResponse>(MeshEnvelope.JsonOptions);
                if (parsed is not null)
                {
                    var ok = parsed.Status is RemoteRunStatus.Answered or RemoteRunStatus.AskedUser;
                    return new PeerTaskOutcome(id,
                                               targetNode,
                                               ok,
                                               parsed.Status,
                                               parsed.Text,
                                               parsed.Steps,
                                               ok ? null : parsed.Text,
                                               jobId,
                                               callCtx.TraceId);
                }
            }

            if (final.State == MeshJobState.Cancelled)
            {
                return new PeerTaskOutcome(id, targetNode, false, RemoteRunStatus.Cancelled, "Stopped.", 0, final.Error, jobId, callCtx.TraceId);
            }

            var err = final.Error ?? $"Remote job '{jobId}' ended in state {final.State}.";
            return new PeerTaskOutcome(id, targetNode, false, RemoteRunStatus.Failed, err, 0, err, jobId, callCtx.TraceId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new PeerTaskOutcome(id, targetNode, false, RemoteRunStatus.Cancelled, "Stopped.", 0, "Cancelled.", jobId, callCtx.TraceId);
        }
        catch (Exception ex)
        {
            return new PeerTaskOutcome(id, targetNode, false, RemoteRunStatus.Failed, ex.Message, 0, ex.Message, jobId, callCtx.TraceId);
        }
    }

    public async Task<IReadOnlyList<PeerTaskOutcome>> RunParallelAsync(IReadOnlyList<PeerTaskSpec> tasks,
                                                                       MeshCallContext? context = null,
                                                                       Action<string>? onProgress = null,
                                                                       CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        if (tasks.Count == 0)
        {
            return [];
        }

        var rootCtx = context ?? MeshCallContext.CreateRoot(_transport.NodeName);
        var running = tasks.Select((spec, index) =>
        {
            var id = string.IsNullOrWhiteSpace(spec.StepId) ? $"step-{index + 1}" : spec.StepId.Trim();
            return RunStepAsync(spec.TargetNode, spec.Prompt, id, rootCtx, onProgress, cancellationToken);
        }).ToArray();

        return await Task.WhenAll(running);
    }

    public async Task<MapReduceResult> RunMapReduceAsync(IReadOnlyList<MapTaskSpec> shards,
                                                         string? reducePromptTemplate = null,
                                                         string? reducerNode = null,
                                                         IReadOnlyList<string>? workerNodes = null,
                                                         bool retryFailedShards = true,
                                                         MeshCallContext? context = null,
                                                         Action<string>? onProgress = null,
                                                         CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shards);
        if (shards.Count == 0)
        {
            throw new ArgumentException("At least one shard is required for MapReduce.", nameof(shards));
        }

        var rootCtx = context ?? MeshCallContext.CreateRoot(_transport.NodeName);
        var initialWorkers = ResolveWorkers(workerNodes);
        if (initialWorkers.Count == 0)
        {
            throw new InvalidOperationException("No online remote worker computers are available for MapReduce.");
        }

        var mapTasks = shards.Select((shard, index) => RunShardWithFailoverAsync(shard,
                                                                                 index,
                                                                                 initialWorkers,
                                                                                 workerNodes,
                                                                                 retryFailedShards,
                                                                                 rootCtx,
                                                                                 onProgress,
                                                                                 cancellationToken)).ToArray();
        var mapOutcomes = await Task.WhenAll(mapTasks);

        PeerTaskOutcome? reduceOutcome = null;
        if (!string.IsNullOrWhiteSpace(reducePromptTemplate) && !string.IsNullOrWhiteSpace(reducerNode))
        {
            var reducePrompt = BuildReducePrompt(reducePromptTemplate, mapOutcomes);
            reduceOutcome = await RunStepAsync(reducerNode,
                                               reducePrompt,
                                               "reduce",
                                               rootCtx,
                                               onProgress,
                                               cancellationToken);
        }

        return new MapReduceResult(mapOutcomes, reduceOutcome, rootCtx.TraceId);
    }

    private async Task<PeerTaskOutcome> RunShardWithFailoverAsync(MapTaskSpec shard,
                                                                  int index,
                                                                  IReadOnlyList<string> initialWorkers,
                                                                  IReadOnlyList<string>? requestedWorkers,
                                                                  bool retryFailedShards,
                                                                  MeshCallContext rootCtx,
                                                                  Action<string>? onProgress,
                                                                  CancellationToken cancellationToken)
    {
        var shardId = string.IsNullOrWhiteSpace(shard.ShardId) ? $"shard-{index + 1}" : shard.ShardId.Trim();
        var primary = !string.IsNullOrWhiteSpace(shard.PreferredNode)
                      && initialWorkers.Any(w => string.Equals(w, shard.PreferredNode, StringComparison.OrdinalIgnoreCase))
            ? initialWorkers.First(w => string.Equals(w, shard.PreferredNode, StringComparison.OrdinalIgnoreCase))
            : initialWorkers[index % initialWorkers.Count];

        var outcome = await RunStepAsync(primary, shard.Prompt, shardId, rootCtx, onProgress, cancellationToken);
        if (outcome.Success || !retryFailedShards || cancellationToken.IsCancellationRequested)
        {
            return outcome;
        }

        // Re-check currently online workers and failover to a different healthy node.
        var fallback = ResolveWorkers(requestedWorkers)
            .FirstOrDefault(w => !string.Equals(w, primary, StringComparison.OrdinalIgnoreCase));
        if (fallback is null)
        {
            return outcome;
        }

        onProgress?.Invoke($"Retrying shard '{shardId}' on '{fallback}' after '{primary}' failed.");
        return await RunStepAsync(fallback, shard.Prompt, shardId, rootCtx, onProgress, cancellationToken);
    }

    private List<string> ResolveWorkers(IReadOnlyList<string>? requestedWorkers)
    {
        var self = _transport.NodeName;
        var online = _transport.PeerInfos
            .Where(p => !string.Equals(p.DisplayName, self, StringComparison.OrdinalIgnoreCase))
            .Where(p => p.Capabilities.Services.Contains(MeshServiceNames.Jobs, StringComparer.OrdinalIgnoreCase))
            .Select(p => p.DisplayName)
            .ToList();

        if (online.Count == 0)
        {
            online = _transport.OnlinePeers
                .Where(p => !string.Equals(p, self, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (requestedWorkers is null || requestedWorkers.Count == 0)
        {
            return online;
        }

        var selected = new List<string>();
        foreach (var req in requestedWorkers)
        {
            var match = online.FirstOrDefault(p => string.Equals(p, req.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null && !selected.Contains(match, StringComparer.OrdinalIgnoreCase))
            {
                selected.Add(match);
            }
        }

        return selected;
    }

    private static string BuildReducePrompt(string reducePromptTemplate, IReadOnlyList<PeerTaskOutcome> mapOutcomes)
    {
        var sb = new StringBuilder();
        sb.AppendLine(reducePromptTemplate.Trim());
        sb.AppendLine();
        sb.AppendLine("Map shard results:");
        foreach (var outcome in mapOutcomes)
        {
            sb.AppendLine($"- Shard '{outcome.StepId}' (worker: {outcome.TargetNode}, status: {outcome.Status}):");
            sb.AppendLine(outcome.Text);
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<JsonElement> HandleAgentJobAsync(MeshJobContext job, CancellationToken cancellationToken)
    {
        var req = job.Payload.Deserialize<AgentJobRequest>(MeshEnvelope.JsonOptions)
            ?? throw new InvalidDataException("Invalid agent.prompt job payload.");

        var incoming = new IncomingOrchestratedTask(
            job.JobId,
            job.CallContext.CallerNode,
            req.Prompt,
            job.CallContext,
            job.ReportProgress,
            (approvalReq, ct) => ForwardApprovalToCallerAsync(job.CallContext.CallerNode, job.CallContext.TraceId, approvalReq, ct));

        var result = await _taskHandler(incoming, cancellationToken);
        return JsonSerializer.SerializeToElement(new AgentJobResponse(result.Status, result.Text, result.Steps),
                                                 MeshEnvelope.JsonOptions);
    }

    private async Task<RemoteApprovalAnswer> ForwardApprovalToCallerAsync(string callerNode,
                                                                          string traceId,
                                                                          TaskApprovalRequestPayload request,
                                                                          CancellationToken cancellationToken)
    {
        // Use hop 0 for the reply-channel approval prompt back to the orchestrator so max-hop workers can still ask.
        var approvalCtx = new MeshCallContext(_transport.NodeName, traceId, 0);
        var payload = JsonSerializer.SerializeToElement(request, MeshEnvelope.JsonOptions);
        var rpc = await _transport.InvokeRpcAsync(callerNode, ApprovalRpcTopic, payload, context: approvalCtx, cancellationToken: cancellationToken);
        if (!rpc.Success)
        {
            return RemoteApprovalAnswer.Deny;
        }

        var response = rpc.Payload.Deserialize<ApprovalRpcResponse>(MeshEnvelope.JsonOptions);
        return response?.Answer ?? RemoteApprovalAnswer.Deny;
    }

    private async Task<RpcResult> HandleApprovalRpcAsync(IncomingRpc rpc, CancellationToken cancellationToken)
    {
        if (_approvalHandler is null)
        {
            return RpcResult.Ok(new ApprovalRpcResponse(RemoteApprovalAnswer.Deny));
        }

        var req = rpc.Payload.Deserialize<TaskApprovalRequestPayload>(MeshEnvelope.JsonOptions);
        if (req is null)
        {
            return RpcResult.Fail("Invalid approval request payload.");
        }

        var answer = await _approvalHandler(rpc.Context.CallerNode, req, cancellationToken);
        return RpcResult.Ok(new ApprovalRpcResponse(answer));
    }

    private sealed record AgentJobRequest(string Prompt, string CallerNode);

    private sealed record AgentJobResponse(RemoteRunStatus Status, string Text, int Steps);

    private sealed record ApprovalRpcResponse(RemoteApprovalAnswer Answer);
}
