namespace WindowsAxon.Agent.Settings;

public sealed record MeshSettings
{
    public const string CredentialPrefix = "credential:";

    public bool Enabled { get; init; }

    /// <summary>WebSocket URL of WindowsAxon.Hub, for example wss://hub.example.com/ws or ws://192.168.1.10:5250/ws.</summary>
    public string HubUrl { get; init; } = "";

    /// <summary>Display name used by other computers to target this machine with @NodeName. Defaults to the machine name.</summary>
    public string NodeName { get; init; } = "";

    /// <summary>Credential reference in Windows Credential Manager (credential:WindowsAxon/mesh/...).</summary>
    public string? Token { get; init; }

    public string EffectiveNodeName =>
        string.IsNullOrWhiteSpace(NodeName) ? Environment.MachineName : NodeName.Trim();

    public void Validate()
    {
        if (!Enabled && string.IsNullOrWhiteSpace(HubUrl))
        {
            return;
        }

        if (!Uri.TryCreate(HubUrl.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("ws" or "wss")
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("Enter a ws:// or wss:// Hub URL without embedded credentials.");
        }

        if (!string.IsNullOrEmpty(Token) && !Token.StartsWith(CredentialPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("The mesh hub token must be a credential:<name> reference.");
        }
    }
}

