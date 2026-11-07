
namespace WindowsAxon.Agent.Settings;

public sealed record EndpointSettings
{
    public EndpointApi Api { get; init; } = EndpointApi.Automatic;

    public required string BaseUrl
    {
        get; init;
    }

    /// <summary>A reference "credential:&lt;name&gt;" to Windows Credential Manager, or null.</summary>
    public string? ApiKey
    {
        get; init;
    }

    /// <summary>Model names that the settings form offers for this endpoint.</summary>
    public List<string> Models { get; init; } = [];

    public bool Equals(EndpointSettings? other) =>
        other is not null && Api == other.Api && BaseUrl == other.BaseUrl && ApiKey == other.ApiKey
        && Models.SequenceEqual(other.Models);

    public override int GetHashCode() => HashCode.Combine(Api, BaseUrl, ApiKey, Models.Count);
}
