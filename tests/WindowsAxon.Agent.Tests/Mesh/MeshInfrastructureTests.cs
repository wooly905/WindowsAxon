using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WindowsAxon.App;
using WindowsAxon.Hub;
using WindowsAxon.Mesh;

namespace WindowsAxon.Agent.Tests.Mesh;

public sealed class MeshInfrastructureTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"windowsaxon-mesh-infra-{Guid.NewGuid():N}");

    public MeshInfrastructureTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task PeerToPeerSupportsCapabilitiesRpcBroadcastJobsArtifactsAndMultiChunkFileTransfer()
    {
        var port = NextTestPort();
        var options = new PeerMeshOptions
        {
            DiscoveryPort = port,
            BeaconInterval = TimeSpan.FromMilliseconds(200),
            PeerTimeout = TimeSpan.FromSeconds(4),
            LoopbackOnly = true,
        };

        var capsA = new PeerCapabilities([MeshServiceNames.AgentTask, MeshServiceNames.FileTransfer], OsDescription: "Windows-A");
        var capsB = new PeerCapabilities([MeshServiceNames.AgentTask, MeshServiceNames.FileTransfer, MeshServiceNames.Jobs, MeshServiceNames.ArtifactFetch],
                                         OsDescription: "Windows-B");

        await using var nodeA = new PeerMeshNode("Computer A",
                                                 "infra-p2p-key",
                                                 (_, _) => Task.FromResult(new RemoteTaskResult(RemoteRunStatus.Answered, "", 0)),
                                                 options,
                                                 capsA);
        await using var nodeB = new PeerMeshNode("Computer B",
                                                 "infra-p2p-key",
                                                 (_, _) => Task.FromResult(new RemoteTaskResult(RemoteRunStatus.Answered, "", 0)),
                                                 options,
                                                 capsB);

        var ct = TestContext.Current.CancellationToken;
        var receiveFolderB = Path.Combine(_tempDir, "recv-b-p2p");
        var artifactFolderB = Path.Combine(_tempDir, "artifacts-b-p2p");
        var filesA = new FileTransferService(nodeA, Path.Combine(_tempDir, "recv-a-p2p"));
        var filesB = new FileTransferService(nodeB, receiveFolderB);
        var artifactsA = new ArtifactService(nodeA, new ArtifactStore(Path.Combine(_tempDir, "artifacts-a-p2p")));
        var artifactsB = new ArtifactService(nodeB, new ArtifactStore(artifactFolderB));
        using var jobsA = new MeshJobService(nodeA);
        using var jobsB = new MeshJobService(nodeB);

        // Register RPC, Broadcast, and Job worker on B.
        nodeB.RegisterRpcHandler("math.double", async (rpc, token) =>
        {
            var value = rpc.Payload.GetProperty("n").GetInt32();
            await rpc.SendEventAsync("progress", JsonSerializer.SerializeToElement(new { step = 1 }), token);
            return RpcResult.Ok(new { result = value * 2, trace = rpc.Context.TraceId, hops = rpc.Context.HopCount });
        });

        var broadcastReceived = new TaskCompletionSource<IncomingBroadcast>(TaskCreationOptions.RunContinuationsAsynchronously);
        nodeB.RegisterBroadcastHandler("ping.all", (b, _) =>
        {
            broadcastReceived.TrySetResult(b);
            return Task.CompletedTask;
        });

        jobsB.RegisterWorker("slow.sum", async (job, token) =>
        {
            job.ReportProgress("halfway");
            await Task.Delay(50, token);
            var a = job.Payload.GetProperty("a").GetInt32();
            var b = job.Payload.GetProperty("b").GetInt32();
            return JsonSerializer.SerializeToElement(new { sum = a + b });
        });

        var receivedFileTcs = new TaskCompletionSource<ReceivedFileInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        filesB.FileReceived += info => receivedFileTcs.TrySetResult(info);

        nodeA.Start();
        nodeB.Start();
        await WaitUntilAsync(() => nodeA.PeerInfos.Any(p => p.DisplayName == "Computer B")
                                   && nodeB.PeerInfos.Any(p => p.DisplayName == "Computer A"));

        // 1. Capabilities advertised in PeerInfo.
        var infoB = nodeA.PeerInfos.Single(p => p.DisplayName == "Computer B");
        Assert.Contains(MeshServiceNames.FileTransfer, infoB.Capabilities.Services);
        Assert.Equal("Windows-B", infoB.Capabilities.OsDescription);

        // 2. Generic RPC + progress event + TraceId + HopCount.
        var events = new List<string>();
        var rpcRes = await nodeA.InvokeRpcAsync("Computer B",
                                                "math.double",
                                                JsonSerializer.SerializeToElement(new { n = 21 }),
                                                onEvent: (name, _) => events.Add(name),
                                                cancellationToken: ct);
        Assert.True(rpcRes.Success);
        Assert.Equal(42, rpcRes.Payload.GetProperty("result").GetInt32());
        Assert.Equal(0, rpcRes.Payload.GetProperty("hops").GetInt32());
        Assert.Contains("progress", events);

        // 3. HopCount limit blocks infinite A->B->A recursion.
        var maxHopContext = new MeshCallContext("Computer A", "trace-loop", MeshCallContext.DefaultMaxHops);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            nodeA.InvokeRpcAsync("Computer B", "math.double", MeshEnvelope.EmptyObject, context: maxHopContext, cancellationToken: ct));

        // 4. Broadcast.
        await nodeA.BroadcastAsync("ping.all", JsonSerializer.SerializeToElement(new { msg = "hello" }), cancellationToken: ct);
        var broadcast = await broadcastReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal("Computer A", broadcast.Context.CallerNode);
        Assert.Equal("hello", broadcast.Payload.GetProperty("msg").GetString());

        // 5. Async Job lifecycle.
        var jobId = await jobsA.StartJobAsync("Computer B",
                                              "slow.sum",
                                              JsonSerializer.SerializeToElement(new { a = 17, b = 25 }),
                                              cancellationToken: ct);
        var progressSeen = new List<string>();
        var finalJob = await jobsA.WaitForJobAsync("Computer B",
                                                   jobId,
                                                   pollInterval: TimeSpan.FromMilliseconds(20),
                                                   onProgress: p => progressSeen.Add(p),
                                                   cancellationToken: ct);
        Assert.Equal(MeshJobState.Completed, finalJob.State);
        Assert.Equal(42, finalJob.Result!.Value.GetProperty("sum").GetInt32());

        // 6. Multi-chunk file transfer (> 2 chunks of 256 KB) + collision auto-numbering.
        var srcPath = Path.Combine(_tempDir, "duck.pdf");
        var payload = new byte[600 * 1024];
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(srcPath, payload, ct);

        var transfer1 = await filesA.SendFileAsync("Computer B", srcPath, cancellationToken: ct);
        var received1 = await receivedFileTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal("duck.pdf", transfer1.FileName);
        Assert.Equal(payload.Length, transfer1.BytesTransferred);
        Assert.Equal(received1.SavedPath, transfer1.SavedPath);
        Assert.Equal(payload, await File.ReadAllBytesAsync(transfer1.SavedPath, ct));

        // Second transfer of same filename auto-renames to duck (1).pdf without overwriting.
        var transfer2 = await filesA.SendFileAsync("Computer B", srcPath, cancellationToken: ct);
        Assert.EndsWith("duck (1).pdf", transfer2.SavedPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(transfer1.SavedPath));
        Assert.True(File.Exists(transfer2.SavedPath));

        // 7. Cross-node Artifact publish + pull over Stream.
        var handle = await artifactsB.PublishFileAsync(transfer1.SavedPath, "report.pdf", ct);
        Assert.True(ArtifactHandle.TryParse(handle, out var host, out _));
        Assert.Equal("Computer B", host);

        var pulledPath = Path.Combine(_tempDir, "pulled-report.pdf");
        var fetchedMeta = await artifactsA.FetchAsync(handle, pulledPath, cancellationToken: ct);
        Assert.Equal("report.pdf", fetchedMeta.FileName);
        Assert.Equal(payload, await File.ReadAllBytesAsync(pulledPath, ct));
    }

    [Fact]
    public async Task CentralServerRelaysRpcStreamFileTransferAndStaysBackwardCompatibleWithLegacyClient()
    {
        await using var hub = await TestHubServer.StartAsync();
        var ct = TestContext.Current.CancellationToken;

        // Legacy client sends old RegisterPayload with only NodeName (no NodeId, no Capabilities).
        using var legacySocket = new ClientWebSocket();
        await legacySocket.ConnectAsync(hub.WebSocketUri, ct);
        using var legacyChannel = new WebSocketJsonChannel(legacySocket);
        await legacyChannel.SendAsync(MeshEnvelope.Create(MeshMessageTypes.Register, new { nodeName = "LegacyPC" }), ct);
        var legacyReg = await legacyChannel.ReceiveAsync(ct);
        Assert.NotNull(legacyReg);
        Assert.Equal(MeshMessageTypes.Registered, legacyReg.Type);
        Assert.Contains("LegacyPC", legacyReg.DeserializePayload<RegisteredPayload>()!.Peers);

        // Two modern MeshClient nodes connected to the same Hub.
        await using var clientA = new MeshClient(hub.WebSocketUri,
                                                 "Computer A",
                                                 null,
                                                 (_, _) => Task.FromResult(new RemoteTaskResult(RemoteRunStatus.Answered, "", 0)),
                                                 new PeerCapabilities([MeshServiceNames.AgentTask, MeshServiceNames.FileTransfer]));
        await using var clientB = new MeshClient(hub.WebSocketUri,
                                                 "Computer B",
                                                 null,
                                                 (_, _) => Task.FromResult(new RemoteTaskResult(RemoteRunStatus.Answered, "", 0)),
                                                 new PeerCapabilities([MeshServiceNames.AgentTask, MeshServiceNames.FileTransfer]));

        var filesA = new FileTransferService(clientA, Path.Combine(_tempDir, "hub-recv-a"));
        var filesB = new FileTransferService(clientB, Path.Combine(_tempDir, "hub-recv-b"));
        var receivedTcs = new TaskCompletionSource<ReceivedFileInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        filesB.FileReceived += info => receivedTcs.TrySetResult(info);

        clientB.RegisterRpcHandler("echo", (rpc, _) => Task.FromResult(RpcResult.Ok(rpc.Payload)));

        clientA.Start();
        clientB.Start();
        await WaitUntilAsync(() => clientA.OnlinePeers.Contains("Computer B") && clientA.OnlinePeers.Contains("LegacyPC"));

        // Modern node sees LegacyPC with default capabilities.
        var legacyInfo = clientA.PeerInfos.Single(p => p.DisplayName == "LegacyPC");
        Assert.Contains(MeshServiceNames.AgentTask, legacyInfo.Capabilities.Services);

        // RPC over Hub.
        var echo = await clientA.InvokeRpcAsync("Computer B",
                                                "echo",
                                                JsonSerializer.SerializeToElement(new { text = "via-hub" }),
                                                cancellationToken: ct);
        Assert.True(echo.Success);
        Assert.Equal("via-hub", echo.Payload.GetProperty("text").GetString());

        // Multi-chunk file transfer over Hub.
        var srcPath = Path.Combine(_tempDir, "hub-doc.pdf");
        var bytes = new byte[550 * 1024];
        Random.Shared.NextBytes(bytes);
        await File.WriteAllBytesAsync(srcPath, bytes, ct);

        var sent = await filesA.SendFileAsync("Computer B", srcPath, cancellationToken: ct);
        var received = await receivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal("Computer A", received.CallerNode);
        Assert.Equal(bytes.Length, sent.BytesTransferred);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(received.SavedPath, ct));
    }

    [Fact]
    public async Task ArtifactStoreEnforces72HourRetentionAndSanitizesFileNames()
    {
        var store = new ArtifactStore(Path.Combine(_tempDir, "retention"));
        var src = Path.Combine(_tempDir, "sample.txt");
        await File.WriteAllTextAsync(src, "hello artifact", TestContext.Current.CancellationToken);

        var meta = await store.PutFileAsync(src, @"..\..\evil\sample.txt", TestContext.Current.CancellationToken);
        Assert.Equal("sample.txt", meta.FileName);
        Assert.True(store.TryGet(meta.ArtifactId, out _, out _, now: meta.CreatedUtc.AddHours(71)));
        Assert.Equal(0, store.PruneExpired(meta.CreatedUtc.AddHours(71)));

        // Past 72 hours -> pruned automatically.
        Assert.Equal(1, store.PruneExpired(meta.CreatedUtc.AddHours(73)));
        Assert.False(store.TryGet(meta.ArtifactId, out _, out _, now: meta.CreatedUtc.AddHours(73)));
    }

    [Fact]
    public void MeshToolsResolvesPeerNamesFlexiblyAndRejectsSelfOrAmbiguous()
    {
        IReadOnlyList<string> peers = ["Computer A", "Computer B", "Workstation (192.168.1.10)", "Workstation (192.168.1.11)"];

        Assert.Equal("Computer B", MeshTools.ResolvePeerName("computerb", peers, "Computer A"));
        Assert.Equal("Computer B", MeshTools.ResolvePeerName("@Computer B", peers, "Computer A"));
        Assert.Throws<ArgumentException>(() => MeshTools.ResolvePeerName("Computer A", peers, "Computer A"));
        Assert.Throws<ArgumentException>(() => MeshTools.ResolvePeerName("Workstation", peers, "Computer A"));
        Assert.Throws<InvalidOperationException>(() => MeshTools.ResolvePeerName("GhostPC", peers, "Computer A"));
    }

    private static int NextTestPort()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
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

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private sealed class TestHubServer(WebApplication app, Uri webSocketUri) : IAsyncDisposable
    {
        public Uri WebSocketUri { get; } = webSocketUri;

        public static async Task<TestHubServer> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddSingleton<MeshHubRouter>();
            var app = builder.Build();
            app.UseWebSockets();
            app.Map("/ws", async (HttpContext context, MeshHubRouter router) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await router.HandleConnectionAsync(socket, context.RequestAborted);
            });

            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            var httpUri = new Uri(addresses.Addresses.First());
            return new TestHubServer(app, new Uri($"ws://127.0.0.1:{httpUri.Port}/ws"));
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
