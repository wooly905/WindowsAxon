using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WindowsAxon.Hub;
using WindowsAxon.Mesh;

namespace WindowsAxon.Agent.Tests.Mesh;

public sealed class HubWebUiTests
{
    [Fact]
    public async Task DualPortHubSeparatesAdminUiAndNodeEndpointsAndListsOnlineNodes()
    {
        var ct = TestContext.Current.CancellationToken;
        const string hubToken = "secret-hub-token";

        int? uiPort = null;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.Listen(IPAddress.Loopback, 0);
        });
        builder.Services.AddSingleton<MeshHubRouter>();

        await using var app = builder.Build();
        HubEndpoints.Map(app, hubToken, () => uiPort);
        await app.StartAsync(ct);

        try
        {
            var addresses = app.Services
                .GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses
                .Select(a => new Uri(a))
                .ToList();
            Assert.Equal(2, addresses.Count);

            var hubUri = addresses[0];
            var uiUri = addresses[1];
            uiPort = uiUri.Port;

            using var http = new HttpClient();

            // UI port serves HTML page and /api/nodes without authentication, and does not expose /ws.
            var indexResponse = await http.GetAsync(uiUri, ct);
            Assert.Equal(HttpStatusCode.OK, indexResponse.StatusCode);
            Assert.Equal("text/html", indexResponse.Content.Headers.ContentType?.MediaType);
            var html = await indexResponse.Content.ReadAsStringAsync(ct);
            Assert.Contains("WindowsAxon Hub", html, StringComparison.Ordinal);

            var wsOnUiPort = await http.GetAsync(new Uri(uiUri, "/ws"), ct);
            Assert.Equal(HttpStatusCode.NotFound, wsOnUiPort.StatusCode);

            // Hub port serves /health, does not expose / or /api/nodes.
            var healthOnHubPort = await http.GetAsync(new Uri(hubUri, "/health"), ct);
            Assert.Equal(HttpStatusCode.OK, healthOnHubPort.StatusCode);

            var uiOnHubPort = await http.GetAsync(hubUri, ct);
            Assert.Equal(HttpStatusCode.NotFound, uiOnHubPort.StatusCode);

            var apiOnHubPort = await http.GetAsync(new Uri(hubUri, "/api/nodes"), ct);
            Assert.Equal(HttpStatusCode.NotFound, apiOnHubPort.StatusCode);

            // Initially no online nodes.
            var emptyPayload = await http.GetFromJsonAsync<JsonElement>(new Uri(uiUri, "/api/nodes"), ct);
            Assert.Equal(0, emptyPayload.GetProperty("count").GetInt32());
            Assert.Empty(emptyPayload.GetProperty("nodes").EnumerateArray());

            // Connect a node on the hub port using Bearer token auth.
            using (var ws = new ClientWebSocket())
            {
                ws.Options.SetRequestHeader("Authorization", $"Bearer {hubToken}");
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{hubUri.Port}/ws"), ct);
                using var channel = new WebSocketJsonChannel(ws);

                var caps = new PeerCapabilities([MeshServiceNames.AgentTask], OsDescription: "Windows 11 Pro");
                await channel.SendAsync(MeshEnvelope.Create(MeshMessageTypes.Register,
                                                            new RegisterPayload("Workstation-01", "node-1", caps)),
                                        ct);
                var registered = await channel.ReceiveAsync(ct);
                Assert.NotNull(registered);
                Assert.Equal(MeshMessageTypes.Registered, registered.Type);

                var onlinePayload = await http.GetFromJsonAsync<JsonElement>(new Uri(uiUri, "/api/nodes"), ct);
                Assert.Equal(1, onlinePayload.GetProperty("count").GetInt32());
                var node = onlinePayload.GetProperty("nodes").EnumerateArray().Single();
                Assert.Equal("Workstation-01", node.GetProperty("name").GetString());
                Assert.Equal("Windows 11 Pro", node.GetProperty("osDescription").GetString());
                Assert.True(node.TryGetProperty("onlineSinceUtc", out var sinceProp)
                            && sinceProp.ValueKind == JsonValueKind.String
                            && DateTimeOffset.TryParse(sinceProp.GetString(), out _));

                await channel.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
            }

            // After disconnect, node is removed from the online list.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            int remaining;
            do
            {
                var afterPayload = await http.GetFromJsonAsync<JsonElement>(new Uri(uiUri, "/api/nodes"), ct);
                remaining = afterPayload.GetProperty("count").GetInt32();
                if (remaining == 0)
                {
                    break;
                }

                await Task.Delay(25, ct);
            }
            while (DateTime.UtcNow < deadline);

            Assert.Equal(0, remaining);
        }
        finally
        {
            await app.StopAsync(ct);
        }
    }
}

