using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using WindowsAxon.Mesh;

namespace WindowsAxon.Agent.Tests.Mesh;

public sealed class PeerMeshEndToEndTests
{
    private static int NextTestPort()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    private static PeerMeshOptions CreateTestOptions(int port) => new()
    {
        DiscoveryPort = port,
        BeaconInterval = TimeSpan.FromMilliseconds(250),
        PeerTimeout = TimeSpan.FromSeconds(3),
        LoopbackOnly = true,
    };

    [Fact]
    public async Task SamePairKeyDiscoversDispatchesStreamsToolAndForwardsApproval()
    {
        var port = NextTestPort();
        var options = CreateTestOptions(port);
        var toolsObserved = new List<string>();
        TaskApprovalRequestPayload? observedApproval = null;

        await using var nodeA = new PeerMeshNode("Computer A",
                                                 "team-secret-key",
                                                 (_, _) => Task.FromResult(new RemoteTaskResult(RemoteRunStatus.Answered, "unused", 0)),
                                                 options);

        await using var nodeB = new PeerMeshNode("Computer B",
                                                 "team-secret-key",
                                                 async (task, ct) =>
                                                 {
                                                     task.ReportToolStarted("disk_get_usage");
                                                     var answer = await task.RequestApprovalAsync(
                                                         new TaskApprovalRequestPayload(task.TaskId,
                                                                                        Guid.NewGuid().ToString("N"),
                                                                                        "fs_delete",
                                                                                        "{\"path\":\"C:\\\\temp\\\\old.log\"}",
                                                                                        "Delete file?",
                                                                                        false),
                                                         ct);
                                                     return new RemoteTaskResult(RemoteRunStatus.Answered,
                                                                                 $"Free space: 120 GB (caller={task.CallerNode}, approval={answer}, prompt={task.Prompt})",
                                                                                 2);
                                                 },
                                                 options);

        nodeA.Start();
        nodeB.Start();

        await WaitUntilAsync(() => nodeA.OnlinePeers.Contains("Computer B") && nodeB.OnlinePeers.Contains("Computer A"));

        var ct = TestContext.Current.CancellationToken;
        var result = await nodeA.RunRemoteTaskAsync("Computer B",
                                                    "show me your harddrive free space",
                                                    tool => toolsObserved.Add(tool),
                                                    (req, _) =>
                                                    {
                                                        observedApproval = req;
                                                        return Task.FromResult(RemoteApprovalAnswer.Allow);
                                                    },
                                                    ct);

        Assert.Equal(RemoteRunStatus.Answered, result.Status);
        Assert.Contains("Free space: 120 GB", result.Text);
        Assert.Contains("caller=Computer A", result.Text);
        Assert.Contains("approval=Allow", result.Text);
        Assert.Contains("disk_get_usage", toolsObserved);
        Assert.NotNull(observedApproval);
        Assert.Equal("fs_delete", observedApproval.ToolName);
    }

    [Fact]
    public async Task DifferentPairKeyDoesNotDiscoverAndRejectsDirectConnection()
    {
        var port = NextTestPort();
        var options = CreateTestOptions(port);

        await using var nodeA = new PeerMeshNode("Computer A",
                                                 "pair-key-alpha",
                                                 (_, _) => Task.FromResult(new RemoteTaskResult(RemoteRunStatus.Answered, "ok", 1)),
                                                 options);

        await using var nodeB = new PeerMeshNode("Computer B",
                                                 "pair-key-beta",
                                                 (_, _) => Task.FromResult(new RemoteTaskResult(RemoteRunStatus.Answered, "ok", 1)),
                                                 options);

        nodeA.Start();
        nodeB.Start();

        await Task.Delay(700, TestContext.Current.CancellationToken);

        Assert.Equal(["Computer A"], nodeA.OnlinePeers);
        Assert.Equal(["Computer B"], nodeB.OnlinePeers);

        // Even if an attacker connects directly to nodeB's TCP port with the wrong pair key, the handshake fails.
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, nodeB.ListeningPort, TestContext.Current.CancellationToken);
        using var wrongCrypto = new PairKeyCrypto("pair-key-alpha");
        var ex = await Record.ExceptionAsync(() => EncryptedJsonChannel.ConnectClientAsync(tcp.GetStream(),
                                                                                           wrongCrypto,
                                                                                           TestContext.Current.CancellationToken));
        Assert.NotNull(ex);
        Assert.True(ex is EndOfStreamException or IOException or CryptographicException);
    }

    [Fact]
    public async Task CancellingOnCallerCancelsExecutionOnTarget()
    {
        var port = NextTestPort();
        var options = CreateTestOptions(port);
        var targetStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var targetCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var nodeA = new PeerMeshNode("Computer A",
                                                 "cancel-key",
                                                 (_, _) => Task.FromResult(new RemoteTaskResult(RemoteRunStatus.Answered, "", 0)),
                                                 options);

        await using var nodeB = new PeerMeshNode("Computer B",
                                                 "cancel-key",
                                                 async (_, ct) =>
                                                 {
                                                     targetStarted.TrySetResult();
                                                     try
                                                     {
                                                         await Task.Delay(TimeSpan.FromSeconds(30), ct);
                                                         return new RemoteTaskResult(RemoteRunStatus.Answered, "done", 1);
                                                     }
                                                     catch (OperationCanceledException)
                                                     {
                                                         targetCancelled.TrySetResult();
                                                         throw;
                                                     }
                                                 },
                                                 options);

        nodeA.Start();
        nodeB.Start();
        await WaitUntilAsync(() => nodeA.OnlinePeers.Contains("Computer B"));

        using var cts = new CancellationTokenSource();
        var runTask = nodeA.RunRemoteTaskAsync("Computer B",
                                               "long task",
                                               _ => { },
                                               (_, _) => Task.FromResult(RemoteApprovalAnswer.Deny),
                                               cts.Token);

        var testCt = TestContext.Current.CancellationToken;
        await targetStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), testCt);
        cts.Cancel();

        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5), testCt);
        await targetCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), testCt);
        Assert.Equal(RemoteRunStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task FailsPromptlyWhenTargetDisconnectsMidTaskAndRemovesPeerOnLeave()
    {
        var port = NextTestPort();
        var options = CreateTestOptions(port);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var nodeA = new PeerMeshNode("Computer A",
                                                 "disconnect-key",
                                                 (_, _) => Task.FromResult(new RemoteTaskResult(RemoteRunStatus.Answered, "", 0)),
                                                 options);

        var nodeB = new PeerMeshNode("Computer B",
                                     "disconnect-key",
                                     async (_, ct) =>
                                     {
                                         started.TrySetResult();
                                         await Task.Delay(TimeSpan.FromSeconds(30), ct);
                                         return new RemoteTaskResult(RemoteRunStatus.Answered, "", 0);
                                     },
                                     options);

        nodeA.Start();
        nodeB.Start();
        await WaitUntilAsync(() => nodeA.OnlinePeers.Contains("Computer B"));

        var testCt = TestContext.Current.CancellationToken;
        var runTask = nodeA.RunRemoteTaskAsync("Computer B",
                                               "work",
                                               _ => { },
                                               (_, _) => Task.FromResult(RemoteApprovalAnswer.Deny),
                                               testCt);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), testCt);
        await nodeB.DisposeAsync();

        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5), testCt);
        Assert.Equal(RemoteRunStatus.Failed, result.Status);
        Assert.Contains("disconnected", result.Text, StringComparison.OrdinalIgnoreCase);

        await WaitUntilAsync(() => !nodeA.OnlinePeers.Contains("Computer B"));
    }

    [Fact]
    public async Task DuplicateNodeNamesAreDisambiguatedAndCallable()
    {
        var port = NextTestPort();
        var options = CreateTestOptions(port);

        await using var node1 = new PeerMeshNode("Workstation",
                                                 "dup-key",
                                                 (_, _) => Task.FromResult(new RemoteTaskResult(RemoteRunStatus.Answered, "from-node-1", 1)),
                                                 options);

        await using var node2 = new PeerMeshNode("Workstation",
                                                 "dup-key",
                                                 (_, _) => Task.FromResult(new RemoteTaskResult(RemoteRunStatus.Answered, "from-node-2", 1)),
                                                 options);

        node1.Start();
        node2.Start();

        await WaitUntilAsync(() => node1.OnlinePeers.Count == 2 && node2.OnlinePeers.Count == 2);

        Assert.NotEqual(node1.NodeName, node2.NodeName);
        Assert.StartsWith("Workstation (", node1.NodeName, StringComparison.Ordinal);
        Assert.StartsWith("Workstation (", node2.NodeName, StringComparison.Ordinal);

        var otherPeer = node1.OnlinePeers.Single(p => !string.Equals(p, node1.NodeName, StringComparison.OrdinalIgnoreCase));
        var result = await node1.RunRemoteTaskAsync(otherPeer,
                                                    "ping",
                                                    _ => { },
                                                    (_, _) => Task.FromResult(RemoteApprovalAnswer.Deny),
                                                    TestContext.Current.CancellationToken);

        Assert.Equal(RemoteRunStatus.Answered, result.Status);
        Assert.Equal("from-node-2", result.Text);
    }

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
}
