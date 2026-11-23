using WindowsAxon.Hub;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration[WebHostDefaults.ServerUrlsKey]))
{
    builder.WebHost.UseUrls("http://0.0.0.0:5250", "http://0.0.0.0:80");
}

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

var uiPortSection = app.Configuration.GetSection("Hub:UiPort");
int? uiPort = uiPortSection.Exists() ? uiPortSection.Get<int?>() : 80;
HubEndpoints.Map(app, configuredToken, () => uiPort);

app.Run();

