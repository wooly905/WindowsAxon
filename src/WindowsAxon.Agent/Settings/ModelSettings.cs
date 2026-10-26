using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsAxon.Agent.Settings;

public sealed record ModelSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public Dictionary<string, EndpointSettings> Endpoints
    {
        get; init;
    } = new()
    {
        ["local"] = new EndpointSettings { BaseUrl = "http://localhost:11434/v1" },
    };

    public ChatSettings Chat { get; init; } = new();

    public EndpointSettings ChatEndpoint =>
        Endpoints.TryGetValue(Chat.Endpoint, out var endpoint)
        ? endpoint
        : throw new InvalidOperationException($"Model endpoint '{Chat.Endpoint}' is not defined in Models.Endpoints.");

    public bool IsChatRemote => EndpointRules.IsRemote(ChatEndpoint.BaseUrl);

    public bool UsesResponsesApi => ChatEndpoint.Api == EndpointApi.Responses
        || (ChatEndpoint.Api == EndpointApi.Automatic
            && IsChatRemote
            && string.Equals(Chat.Model, "auto", StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads the "Models" section of a configuration JSON document.</summary>
    public static ModelSettings Load(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("Models", out var models))
        {
            return new ModelSettings();
        }

        var loaded = models.Deserialize<ModelSettings>(JsonOptions) ?? new ModelSettings();
        return loaded with
        {
            Endpoints = loaded.Endpoints.Count == 0 ? new ModelSettings().Endpoints : loaded.Endpoints,
            Chat = loaded.Chat ?? new ChatSettings(),
        };
    }
}
