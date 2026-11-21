using System.Security.Cryptography;
using System.Text;
using WindowsAxon.Hub;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<MeshHubRouter>();

var app = builder.Build();
var configuredToken = app.Configuration["Hub:Token"];
if (string.IsNullOrWhiteSpace(configuredToken))
{
    configuredToken = Environment.GetEnvironmentVariable("WINDOWSAXON_HUB_TOKEN");
}

if (string.IsNullOrWhiteSpace(configuredToken))
{
    app.Logger.LogWarning("Hub:Token / WINDOWSAXON_HUB_TOKEN is not set. Accepting unauthenticated WebSocket connections.");
}

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapGet("/health", (MeshHubRouter router) => Results.Ok(new
{
    status = "ok",
    onlineNodes = router.OnlineNodes,
}));

app.Map("/ws", async (HttpContext context, MeshHubRouter router) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    if (!IsAuthorized(context.Request.Headers.Authorization.ToString(), configuredToken))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await router.HandleConnectionAsync(socket, context.RequestAborted);
});

app.Run();

static bool IsAuthorized(string? authorizationHeader, string? expectedToken)
{
    if (string.IsNullOrWhiteSpace(expectedToken))
    {
        return true;
    }

    const string prefix = "Bearer ";
    if (string.IsNullOrEmpty(authorizationHeader) || !authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var actualBytes = Encoding.UTF8.GetBytes(authorizationHeader[prefix.Length..].Trim());
    var expectedBytes = Encoding.UTF8.GetBytes(expectedToken.Trim());
    return CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
}

