using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace WindowsAxon.Hub;

internal static class HubEndpoints
{
    private const string IndexResourceName = "WindowsAxon.Hub.wwwroot.index.html";
    private static readonly Lazy<string> IndexHtml = new(LoadIndexHtml);

    public static void Map(WebApplication app, string? configuredToken, Func<int?> getUiPort)
    {
        app.MapWhen(
            context => getUiPort() is { } uiPort && context.Connection.LocalPort == uiPort,
            uiApp =>
            {
                // Clear any endpoint matched by WebApplication's implicit top-level UseRouting()
                // so hub-port routes (/ws, /health) cannot leak onto the UI port.
                uiApp.Use((context, next) =>
                {
                    context.SetEndpoint(null);
                    return next(context);
                });
                uiApp.UseRouting();
                uiApp.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet("/", () => Results.Content(IndexHtml.Value, "text/html; charset=utf-8"));
                    endpoints.MapGet("/api/nodes", (MeshHubRouter router) => Results.Ok(new
                    {
                        serverTimeUtc = DateTimeOffset.UtcNow,
                        count = router.OnlinePeerInfos.Count,
                        nodes = router.OnlinePeerInfos.Select(p => new
                        {
                            name = p.DisplayName,
                            onlineSinceUtc = p.OnlineSinceUtc,
                            osDescription = p.Capabilities.OsDescription,
                        }),
                    }));
                });
            });

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
    }

    internal static bool IsAuthorized(string? authorizationHeader, string? expectedToken)
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

    private static string LoadIndexHtml()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(IndexResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{IndexResourceName}' not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

