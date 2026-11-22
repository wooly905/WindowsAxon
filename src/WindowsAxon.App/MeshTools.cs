using System.Text.Json;
using WindowsAxon.Agent.Tools;
using WindowsAxon.Mesh;

namespace WindowsAxon.App;

/// <summary>Agent tools exposing mesh peer discovery, file transfer, artifacts, and multi-computer orchestration.</summary>
internal sealed class MeshTools(Func<IMeshNode?> getNode,
                                Func<FileTransferService?> getFiles,
                                Func<ArtifactService?> getArtifacts,
                                Func<MeshOrchestrator?> getOrchestrator,
                                Func<string> getSelfName,
                                Func<MeshCallContext?> getCallContext,
                                Action<string> reportProgress)
{
    [Tool("mesh_list_peers",
          "utilities",
          "List online WindowsAxon computers on the mesh network along with their supported capabilities and services.",
          RiskLevel.Read,
          alwaysInclude: true)]
    public object ListPeers()
    {
        var node = getNode();
        if (node is null || node.State != MeshConnectionState.Connected)
        {
            return new
            {
                Connected = false,
                Status = node?.StatusMessage ?? "Disabled",
                Peers = Array.Empty<object>(),
            };
        }

        var self = getSelfName();
        return new
        {
            Connected = true,
            LocalNode = self,
            Peers = node.PeerInfos.Select(p => new
            {
                p.DisplayName,
                p.NodeId,
                IsSelf = string.Equals(p.DisplayName, self, StringComparison.OrdinalIgnoreCase),
                p.Capabilities.Services,
                Os = p.Capabilities.OsDescription,
            }).ToArray(),
        };
    }

    [Tool("mesh_send_file",
          "files",
          "Send a local file to another online WindowsAxon computer. Saved into the remote computer's Downloads\\WindowsAxon folder. Maximum size 1 GB.",
          RiskLevel.Change,
          timeoutSeconds: 600,
          alwaysInclude: true,
          outbound: true)]
    public async Task<object> SendFileAsync(string targetNode,
                                            string sourcePath,
                                            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetNode);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var node = RequireConnectedNode();
        var files = getFiles() ?? throw new InvalidOperationException("File transfer service is not available.");
        var resolved = ResolvePeerName(targetNode, node.OnlinePeers, getSelfName());
        var lastBucket = -1;
        var result = await files.SendFileAsync(resolved,
                                               sourcePath,
                                               getCallContext(),
                                               onProgress: (sent, total) =>
                                               {
                                                   if (total <= 0)
                                                   {
                                                       return;
                                                   }

                                                   var bucket = (int)(sent * 4 / total);
                                                   if (bucket > lastBucket && bucket < 4)
                                                   {
                                                       lastBucket = bucket;
                                                       reportProgress($"Sending {Path.GetFileName(sourcePath)} to {resolved} ({bucket * 25}%)");
                                                   }
                                               },
                                               cancellationToken: cancellationToken);

        return new
        {
            result.TargetNode,
            result.FileName,
            result.BytesTransferred,
            result.SavedPath,
            result.Sha256,
            result.TraceId,
        };
    }

    [Tool("mesh_run_on_peer",
          "utilities",
          "Delegate a task or workflow step to a specific online WindowsAxon computer and wait for its answer.",
          RiskLevel.Change,
          timeoutSeconds: 600,
          alwaysInclude: true,
          outbound: true)]
    public async Task<object> RunOnPeerAsync(string targetNode,
                                             string prompt,
                                             string? stepId = null,
                                             CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetNode);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var node = RequireConnectedNode();
        var orchestrator = getOrchestrator() ?? throw new InvalidOperationException("Mesh orchestrator is not available.");
        var resolved = ResolvePeerName(targetNode, node.OnlinePeers, getSelfName());
        var outcome = await orchestrator.RunStepAsync(resolved,
                                                      prompt,
                                                      stepId,
                                                      getCallContext(),
                                                      reportProgress,
                                                      cancellationToken);
        return outcome;
    }

    [Tool("mesh_run_parallel",
          "utilities",
          "Dispatch multiple independent tasks to online WindowsAxon computers in parallel and wait for all answers. tasksJson must be a JSON array of objects: [{\"targetNode\":\"PC-B\",\"prompt\":\"...\",\"stepId\":\"step1\"}].",
          RiskLevel.Change,
          timeoutSeconds: 600,
          alwaysInclude: true,
          outbound: true)]
    public async Task<object> RunParallelAsync(string tasksJson, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tasksJson);

        var node = RequireConnectedNode();
        var orchestrator = getOrchestrator() ?? throw new InvalidOperationException("Mesh orchestrator is not available.");
        var rawTasks = JsonSerializer.Deserialize<List<PeerTaskSpec>>(tasksJson, MeshEnvelope.JsonOptions);
        if (rawTasks is null || rawTasks.Count == 0)
        {
            throw new ArgumentException("tasksJson must be a non-empty JSON array of {targetNode, prompt, stepId?}.");
        }

        var self = getSelfName();
        var resolvedTasks = rawTasks.Select(t => t with
        {
            TargetNode = ResolvePeerName(t.TargetNode, node.OnlinePeers, self),
        }).ToList();

        var outcomes = await orchestrator.RunParallelAsync(resolvedTasks,
                                                           getCallContext(),
                                                           reportProgress,
                                                           cancellationToken);
        return new
        {
            Total = outcomes.Count,
            Succeeded = outcomes.Count(o => o.Success),
            Outcomes = outcomes,
        };
    }

    [Tool("mesh_map_reduce",
          "utilities",
          "Run a fault-tolerant MapReduce job across online worker computers. shardsJson is a JSON array of {\"shardId\":\"1\",\"prompt\":\"...\",\"preferredNode\":\"...\"}. Optional workerNodes (comma-separated) restricts workers; optional reducePrompt + reducerNode runs the Reduce phase on a remote computer (omit reducerNode to reduce locally).",
          RiskLevel.Change,
          timeoutSeconds: 900,
          alwaysInclude: true,
          outbound: true)]
    public async Task<object> MapReduceAsync(string shardsJson,
                                             string? workerNodes = null,
                                             string? reducePrompt = null,
                                             string? reducerNode = null,
                                             bool retryFailedShards = true,
                                             CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shardsJson);

        var node = RequireConnectedNode();
        var orchestrator = getOrchestrator() ?? throw new InvalidOperationException("Mesh orchestrator is not available.");
        var shards = JsonSerializer.Deserialize<List<MapTaskSpec>>(shardsJson, MeshEnvelope.JsonOptions);
        if (shards is null || shards.Count == 0)
        {
            throw new ArgumentException("shardsJson must be a non-empty JSON array of {shardId, prompt, preferredNode?}.");
        }

        var self = getSelfName();
        IReadOnlyList<string>? workers = null;
        if (!string.IsNullOrWhiteSpace(workerNodes))
        {
            workers = workerNodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(w => ResolvePeerName(w, node.OnlinePeers, self))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        string? resolvedReducer = null;
        if (!string.IsNullOrWhiteSpace(reducerNode))
        {
            resolvedReducer = ResolvePeerName(reducerNode, node.OnlinePeers, self);
        }

        var normalizedShards = shards.Select(s => string.IsNullOrWhiteSpace(s.PreferredNode)
            ? s
            : s with { PreferredNode = ResolvePeerName(s.PreferredNode, node.OnlinePeers, self) }).ToList();

        return await orchestrator.RunMapReduceAsync(normalizedShards,
                                                    reducePrompt,
                                                    resolvedReducer,
                                                    workers,
                                                    retryFailedShards,
                                                    getCallContext(),
                                                    reportProgress,
                                                    cancellationToken);
    }

    [Tool("mesh_artifact_publish",
          "files",
          "Publish a local file to this computer's artifact store (kept for 72 hours, max 1 GB) and return an artifact://<node>/<id> handle that other computers can fetch directly.",
          RiskLevel.Change)]
    public async Task<object> PublishArtifactAsync(string sourcePath,
                                                   string? fileName = null,
                                                   CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        _ = RequireConnectedNode();
        var artifacts = getArtifacts() ?? throw new InvalidOperationException("Artifact service is not available.");
        var handle = await artifacts.PublishFileAsync(sourcePath, fileName, cancellationToken);
        return new { Handle = handle };
    }

    [Tool("mesh_artifact_fetch",
          "files",
          "Fetch an artifact://<node>/<id> handle from another online computer directly to a local path (defaults to Downloads\\WindowsAxon).",
          RiskLevel.Change,
          timeoutSeconds: 600,
          outbound: true)]
    public async Task<object> FetchArtifactAsync(string handleUri,
                                                 string? destinationPath = null,
                                                 CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handleUri);

        _ = RequireConnectedNode();
        var artifacts = getArtifacts() ?? throw new InvalidOperationException("Artifact service is not available.");
        var targetPath = string.IsNullOrWhiteSpace(destinationPath)
            ? Path.Combine(FileTransferService.DefaultReceiveFolder, $"artifact-{Guid.NewGuid():N}.bin")
            : Path.GetFullPath(destinationPath);
        var meta = await artifacts.FetchAsync(handleUri, targetPath, getCallContext(), cancellationToken);
        return new
        {
            meta.ArtifactId,
            meta.FileName,
            meta.SizeBytes,
            meta.Sha256,
            SavedPath = targetPath,
        };
    }

    private IMeshNode RequireConnectedNode()
    {
        var node = getNode();
        if (node is null || node.State != MeshConnectionState.Connected)
        {
            throw new InvalidOperationException("Not connected to the mesh network. Open Network settings first.");
        }

        return node;
    }

    internal static string ResolvePeerName(string requested, IReadOnlyList<string> onlinePeers, string selfName)
    {
        var query = requested.Trim().TrimStart('@');
        var exact = onlinePeers.FirstOrDefault(p => string.Equals(p, query, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            EnsureNotSelf(exact, selfName);
            return exact;
        }

        static string Normalize(string value) =>
            new([.. value.Where(char.IsLetterOrDigit)]);

        var normalizedQuery = Normalize(query);
        var matches = onlinePeers
            .Where(p => !string.Equals(p, selfName, StringComparison.OrdinalIgnoreCase))
            .Where(p => string.Equals(Normalize(p), normalizedQuery, StringComparison.OrdinalIgnoreCase)
                        || p.StartsWith(query, StringComparison.OrdinalIgnoreCase)
                        || (!string.IsNullOrEmpty(normalizedQuery)
                            && Normalize(p).StartsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count > 1)
        {
            throw new ArgumentException($"Multiple online computers match '{requested}': {string.Join(", ", matches)}. Specify the full computer name.");
        }

        var others = onlinePeers.Where(p => !string.Equals(p, selfName, StringComparison.OrdinalIgnoreCase)).ToList();
        var available = others.Count == 0 ? "none" : string.Join(", ", others);
        throw new InvalidOperationException($"Computer '{requested}' is not online. Online computers: {available}.");
    }

    private static void EnsureNotSelf(string peer, string selfName)
    {
        if (string.Equals(peer, selfName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{peer}' is this computer. Choose another online computer.");
        }
    }
}
