using System.Net;
using System.Net.Sockets;
using WindowsAxon.Mesh;

namespace WindowsAxon.Agent.Tests.Mesh;

public sealed class MeshOrchestratorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"windowsaxon-orch-{Guid.NewGuid():N}");

    public MeshOrchestratorTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    private static int NextTestPort()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    private static PeerMeshOptions CreateOptions(int port) => new()
    {
        DiscoveryPort = port,
        BeaconInterval = TimeSpan.FromMilliseconds(200),
        PeerTimeout = TimeSpan.FromSeconds(3),
        LoopbackOnly = true,
    };

    private static PeerCapabilities FullCapabilities() => new(
    [
        MeshServiceNames.AgentTask,
        MeshServiceNames.FileTransfer,
        MeshServiceNames.ArtifactFetch,
        MeshServiceNames.Jobs,
    ]);

    [Fact]
    public async Task OrchestratorRunsParallelStepsWithProgressAndApprovalAcrossMultiplePeers()
    {
        var port = NextTestPort();
        var options = CreateOptions(port);
        var ct = TestContext.Current.CancellationToken;

        await using var nodeA = new PeerMeshNode("Orchestrator", "orch-key", UnusedTask, options, FullCapabilities());
        await using var nodeB = new PeerMeshNode("WorkerB", "orch-key", UnusedTask, options, FullCapabilities());
        await using var nodeC = new PeerMeshNode("WorkerC", "orch-key", UnusedTask, options, FullCapabilities());

        using var jobsA = new MeshJobService(nodeA);
        using var jobsB = new MeshJobService(nodeB);
        using var jobsC = new MeshJobService(nodeC);

        var approvalsObserved = new List<string>();
        var orchA = new MeshOrchestrator(nodeA,
                                         jobsA,
                                         (_, _) => Task.FromResult(new RemoteTaskResult(RemoteRunStatus.Answered, "self", 0)),
                                         (peer, req, _) =>
                                         {
                                             lock (approvalsObserved)
                                             {
                                                 approvalsObserved.Add($"{peer}:{req.ToolName}");
                                             }

                                             return Task.FromResult(RemoteApprovalAnswer.Allow);
                                         },
                                         pollInterval: TimeSpan.FromMilliseconds(25));

        _ = new MeshOrchestrator(nodeB,
                                 jobsB,
                                 async (task, token) =>
                                 {
                                     task.ReportProgress("sys_info");
                                     var ans = await task.RequestApprovalAsync(
                                         new TaskApprovalRequestPayload(task.JobId, Guid.NewGuid().ToString("N"), "check_disk", "{}", null, false),
                                         token);
                                     return new RemoteTaskResult(RemoteRunStatus.Answered, $"B:{task.Prompt}:{ans}", 1);
                                 });

        _ = new MeshOrchestrator(nodeC,
                                 jobsC,
                                 (task, _) =>
                                 {
                                     task.ReportProgress("net_ping");
                                     return Task.FromResult(new RemoteTaskResult(RemoteRunStatus.Answered, $"C:{task.Prompt}", 1));
                                 });

        nodeA.Start();
        nodeB.Start();
        nodeC.Start();
        await WaitUntilAsync(() => nodeA.OnlinePeers.Contains("WorkerB") && nodeA.OnlinePeers.Contains("WorkerC"));

        var progress = new List<string>();
        var outcomes = await orchA.RunParallelAsync(
            [
                new PeerTaskSpec("WorkerB", "disk", "step-disk"),
                new PeerTaskSpec("WorkerC", "network", "step-net"),
            ],
            onProgress: p =>
            {
                lock (progress)
                {
                    progress.Add(p);
                }
            },
            cancellationToken: ct);

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o => Assert.True(o.Success));
        Assert.Equal(outcomes[0].TraceId, outcomes[1].TraceId);
        Assert.Contains(outcomes, o => o.StepId == "step-disk" && o.Text == "B:disk:Allow");
        Assert.Contains(outcomes, o => o.StepId == "step-net" && o.Text == "C:network");
        Assert.Contains("WorkerB:check_disk", approvalsObserved);
    }

    [Fact]
    public async Task MapReduceShufflesArtifactsBetweenWorkersAndReducesOnTarget()
    {
        var port = NextTestPort();
        var options = CreateOptions(port);
        var ct = TestContext.Current.CancellationToken;

        await using var master = new PeerMeshNode("Master", "mr-key", UnusedTask, options, FullCapabilities());
        await using var workerB = new PeerMeshNode("WorkerB", "mr-key", UnusedTask, options, FullCapabilities());
        await using var workerC = new PeerMeshNode("WorkerC", "mr-key", UnusedTask, options, FullCapabilities());

        using var jobsM = new MeshJobService(master);
        using var jobsB = new MeshJobService(workerB);
        using var jobsC = new MeshJobService(workerC);

        var artifactsB = new ArtifactService(workerB, new ArtifactStore(Path.Combine(_tempDir, "art-b")));
        var artifactsC = new ArtifactService(workerC, new ArtifactStore(Path.Combine(_tempDir, "art-c")));

        var orchM = new MeshOrchestrator(master,
                                         jobsM,
                                         (_, _) => Task.FromResult(new RemoteTaskResult(RemoteRunStatus.Answered, "", 0)),
                                         pollInterval: TimeSpan.FromMilliseconds(25));

        // WorkerB maps a shard and publishes an artifact handle.
        _ = new MeshOrchestrator(workerB,
                                 jobsB,
                                 async (task, token) =>
                                 {
                                     var outPath = Path.Combine(_tempDir, $"map-b-{Guid.NewGuid():N}.txt");
                                     await File.WriteAllTextAsync(outPath, $"mapped:{task.Prompt}", token);
                                     var handle = await artifactsB.PublishFileAsync(outPath, "shard-b.txt", token);
                                     return new RemoteTaskResult(RemoteRunStatus.Answered, handle, 1);
                                 });

        // WorkerC maps shards AND acts as Reducer, pulling any artifact:// handles directly from WorkerB!
        _ = new MeshOrchestrator(workerC,
                                 jobsC,
                                 async (task, token) =>
                                 {
                                     if (task.Prompt.StartsWith("REDUCE:", StringComparison.Ordinal))
                                     {
                                         var pulled = new List<string>();
                                         foreach (var line in task.Prompt.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                                         {
                                             if (ArtifactHandle.TryParse(line, out var host, out var id))
                                             {
                                                 if (host == "WorkerC" && artifactsC.Store.TryGet(id, out _, out var localPath))
                                                 {
                                                     pulled.Add(await File.ReadAllTextAsync(localPath, token));
                                                 }
                                                 else
                                                 {
                                                     var dest = Path.Combine(_tempDir, $"shuffle-{Guid.NewGuid():N}.txt");
                                                     await artifactsC.FetchAsync(line, dest, task.CallContext, token);
                                                     pulled.Add(await File.ReadAllTextAsync(dest, token));
                                                 }
                                             }
                                         }

                                         pulled.Sort(StringComparer.Ordinal);
                                         return new RemoteTaskResult(RemoteRunStatus.Answered, string.Join(" | ", pulled), 1);
                                     }

                                     var outPath = Path.Combine(_tempDir, $"map-c-{Guid.NewGuid():N}.txt");
                                     await File.WriteAllTextAsync(outPath, $"mapped:{task.Prompt}", token);
                                     var handle = await artifactsC.PublishFileAsync(outPath, "shard-c.txt", token);
                                     return new RemoteTaskResult(RemoteRunStatus.Answered, handle, 1);
                                 });

        master.Start();
        workerB.Start();
        workerC.Start();
        await WaitUntilAsync(() => master.OnlinePeers.Contains("WorkerB")
                                   && master.OnlinePeers.Contains("WorkerC")
                                   && workerC.OnlinePeers.Contains("WorkerB"));

        var mrResult = await orchM.RunMapReduceAsync(
            [
                new MapTaskSpec("s1", "part-1", PreferredNode: "WorkerB"),
                new MapTaskSpec("s2", "part-2", PreferredNode: "WorkerC"),
            ],
            reducePromptTemplate: "REDUCE: merge all shard artifacts",
            reducerNode: "WorkerC",
            cancellationToken: ct);

        Assert.Equal(2, mrResult.MapOutcomes.Count);
        Assert.All(mrResult.MapOutcomes, o => Assert.True(o.Success));
        Assert.NotNull(mrResult.ReduceOutcome);
        Assert.True(mrResult.ReduceOutcome.Success);
        Assert.Equal("mapped:part-1 | mapped:part-2", mrResult.ReduceOutcome.Text);
    }

    [Fact]
    public async Task MapReduceFailsOverShardToSurvivingWorkerWhenPrimaryFails()
    {
        var port = NextTestPort();
        var options = CreateOptions(port);
        var ct = TestContext.Current.CancellationToken;

        await using var master = new PeerMeshNode("Master", "failover-key", UnusedTask, options, FullCapabilities());
        await using var flaky = new PeerMeshNode("FlakyWorker", "failover-key", UnusedTask, options, FullCapabilities());
        await using var healthy = new PeerMeshNode("HealthyWorker", "failover-key", UnusedTask, options, FullCapabilities());

        using var jobsM = new MeshJobService(master);
        using var jobsF = new MeshJobService(flaky);
        using var jobsH = new MeshJobService(healthy);

        var orchM = new MeshOrchestrator(master,
                                         jobsM,
                                         (_, _) => Task.FromResult(new RemoteTaskResult(RemoteRunStatus.Answered, "", 0)),
                                         pollInterval: TimeSpan.FromMilliseconds(25));

        _ = new MeshOrchestrator(flaky,
                                 jobsF,
                                 (_, _) => throw new InvalidOperationException("Simulated worker crash"));

        _ = new MeshOrchestrator(healthy,
                                 jobsH,
                                 (task, _) => Task.FromResult(new RemoteTaskResult(RemoteRunStatus.Answered, $"ok:{task.Prompt}", 1)));

        master.Start();
        flaky.Start();
        healthy.Start();
        await WaitUntilAsync(() => master.OnlinePeers.Contains("FlakyWorker") && master.OnlinePeers.Contains("HealthyWorker"));

        var result = await orchM.RunMapReduceAsync(
            [new MapTaskSpec("s1", "compute", PreferredNode: "FlakyWorker")],
            retryFailedShards: true,
            cancellationToken: ct);

        var only = Assert.Single(result.MapOutcomes);
        Assert.True(only.Success);
        Assert.Equal("HealthyWorker", only.TargetNode);
        Assert.Equal("ok:compute", only.Text);
    }

    [Fact]
    public async Task MultiHopOrchestrationBlocksInfiniteRecursionAtHopLimit()
    {
        var port = NextTestPort();
        var options = CreateOptions(port);
        var ct = TestContext.Current.CancellationToken;

        await using var nodeA = new PeerMeshNode("NodeA", "hop-key", UnusedTask, options, FullCapabilities());
        await using var nodeB = new PeerMeshNode("NodeB", "hop-key", UnusedTask, options, FullCapabilities());

        using var jobsA = new MeshJobService(nodeA);
        using var jobsB = new MeshJobService(nodeB);

        MeshOrchestrator? orchA = null;
        MeshOrchestrator? orchB = null;

        orchA = new MeshOrchestrator(nodeA,
                                     jobsA,
                                     async (task, token) =>
                                     {
                                         var next = await orchA!.RunStepAsync("NodeB", "ping", context: task.CallContext, cancellationToken: token);
                                         return new RemoteTaskResult(next.Status, next.Text, 1);
                                     },
                                     pollInterval: TimeSpan.FromMilliseconds(20));

        orchB = new MeshOrchestrator(nodeB,
                                     jobsB,
                                     async (task, token) =>
                                     {
                                         var next = await orchB!.RunStepAsync("NodeA", "pong", context: task.CallContext, cancellationToken: token);
                                         return new RemoteTaskResult(next.Status, next.Text, 1);
                                     },
                                     pollInterval: TimeSpan.FromMilliseconds(20));

        nodeA.Start();
        nodeB.Start();
        await WaitUntilAsync(() => nodeA.OnlinePeers.Contains("NodeB") && nodeB.OnlinePeers.Contains("NodeA"));

        var outcome = await orchA.RunStepAsync("NodeB", "start-loop", cancellationToken: ct);
        Assert.False(outcome.Success);
        Assert.Contains("hop limit", outcome.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<RemoteTaskResult> UnusedTask(IncomingRemoteTask _, CancellationToken __) =>
        Task.FromResult(new RemoteTaskResult(RemoteRunStatus.Answered, "", 0));

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met within the timeout.");
            }

            await Task.Delay(25);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
