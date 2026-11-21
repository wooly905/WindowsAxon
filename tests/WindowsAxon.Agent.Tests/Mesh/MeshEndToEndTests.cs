using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WindowsAxon.Hub;
using WindowsAxon.Mesh;

namespace WindowsAxon.Agent.Tests.Mesh;

public sealed class MeshEndToEndTests
{
    [Fact]
    public async Task DispatchesTaskStreamsToolAndForwardsApprovalToCaller()
    {
        await using var hub = await TestHubServer.StartAsync(token: "secret-token");
        var toolsObserved = new List<string>();
        TaskApprovalRequestPayload? observedApproval = null;

        await using var clientA = new MeshClient(hub.WebSocketUri,
                                                 "Computer A",
                                                 "secret-token",
                                                 (_, _) => Task.FromResult(new RemoteTaskResult(RemoteRunStatus.Answered, "unused", 0)));

        await using var clientB = new MeshClient(hub.WebSocketUri,
                                                 "Computer B",
                                                 "secret-token",
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
                                                                                 $"Free space: 120 GB (approval={answer}, prompt={task.Prompt})",
                                                                                 2);
                                                 });

        clientA.Start();
        clientB.Start();

        await WaitUntilAsync(() => clientA.OnlinePeers.Contains("Computer B") && clientB.OnlinePeers.Contains("Computer A"));

        var ct = TestContext.Current.CancellationToken;
        var result = await clientA.RunRemoteTaskAsync("Computer B",
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
        Assert.Contains("approval=Allow", result.Text);
        Assert.Contains("disk_get_usage", toolsObserved);
        Assert.NotNull(observedApproval);
        Assert.Equal("fs_delete", observedApproval.ToolName);
    }

    [Fact]
    public async Task CancellingOnCallerCancelsExecutionOnTarget()
    {
        await using var hub = await TestHubServer.StartAsync(token: null);
        var targetStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var targetCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var clientA = new MeshClient(hub.WebSocketUri,
                                                 "Computer A",
                                                 null,
                                                 (_, _) => Task.FromResult(new RemoteTaskResult(RemoteRunStatus.Answered, "", 0)));

        await using var clientB = new MeshClient(hub.WebSocketUri,
                                                 "Computer B",
                                                 null,
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
                                                 });

        clientA.Start();
        clientB.Start();
        await WaitUntilAsync(() => clientA.OnlinePeers.Contains("Computer B"));

        using var cts = new CancellationTokenSource();
        var runTask = clientA.RunRemoteTaskAsync("Computer B",
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
    public async Task RejectsDuplicateNodeName()
    {
        await using var hub = await TestHubServer.StartAsync(token: null);

        await using var first = new MeshClient(hub.WebSocketUri,
                                               "Computer A",
                                               null,
                                               (_, _) => Task.FromResult(new RemoteTaskResult(RemoteRunStatus.Answered, "", 0)));
        first.Start();
        await WaitUntilAsync(() => first.State == MeshConnectionState.Connected);

        await using var duplicate = new MeshClient(hub.WebSocketUri,
                                                   "Computer A",
                                                   null,
                                                   (_, _) => Task.FromResult(new RemoteTaskResult(RemoteRunStatus.Answered, "", 0)));
        duplicate.Start();
        await WaitUntilAsync(() => duplicate.State == MeshConnectionState.Error);

        Assert.Contains("already connected", duplicate.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailsPromptlyWhenTargetDisconnectsMidTask()
    {
        await using var hub = await TestHubServer.StartAsync(token: null);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var clientA = new MeshClient(hub.WebSocketUri,
                                                 "Computer A",
                                                 null,
                                                 (_, _) => Task.FromResult(new RemoteTaskResult(RemoteRunStatus.Answered, "", 0)));

        var clientB = new MeshClient(hub.WebSocketUri,
                                     "Computer B",
                                     null,
                                     async (_, ct) =>
                                     {
                                         started.TrySetResult();
                                         await Task.Delay(TimeSpan.FromSeconds(30), ct);
                                         return new RemoteTaskResult(RemoteRunStatus.Answered, "", 0);
                                     });

        clientA.Start();
        clientB.Start();
        await WaitUntilAsync(() => clientA.OnlinePeers.Contains("Computer B"));

        var testCt = TestContext.Current.CancellationToken;
        var runTask = clientA.RunRemoteTaskAsync("Computer B",
                                                 "work",
                                                 _ => { },
                                                 (_, _) => Task.FromResult(RemoteApprovalAnswer.Deny),
                                                 testCt);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), testCt);
        await clientB.DisposeAsync();

        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5), testCt);
        Assert.Equal(RemoteRunStatus.Failed, result.Status);
        Assert.Contains("disconnected", result.Text, StringComparison.OrdinalIgnoreCase);
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

    private sealed class TestHubServer(WebApplication app, Uri webSocketUri) : IAsyncDisposable
    {
        public Uri WebSocketUri { get; } = webSocketUri;

        public static async Task<TestHubServer> StartAsync(string? token)
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

                if (!string.IsNullOrWhiteSpace(token))
                {
                    var header = context.Request.Headers.Authorization.ToString();
                    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        || !CryptographicOperations.FixedTimeEquals(
                            Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim()),
                            Encoding.UTF8.GetBytes(token)))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                }

                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await router.HandleConnectionAsync(socket, context.RequestAborted);
            });

            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            var httpUri = new Uri(addresses.Addresses.First());
            var wsUri = new Uri($"ws://127.0.0.1:{httpUri.Port}/ws");
            return new TestHubServer(app, wsUri);
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}

