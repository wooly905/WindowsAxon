using System.Text.Json.Serialization;

namespace WindowsAxon.Agent.Settings;

public enum MeshMode
{
    Disabled,
    CentralServer,
    PeerToPeer,
}

public sealed record MeshSettings
{
    public const string CredentialPrefix = "credential:";

    /// <summary>Network mode: Disabled (standalone), CentralServer (WindowsAxon.Hub), or PeerToPeer (same LAN).</summary>
    public MeshMode? Mode { get; init; }

    /// <summary>Legacy flag kept for backward compatibility with older settings.json files.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Enabled { get; init; }

    /// <summary>WebSocket URL of WindowsAxon.Hub, for example wss://hub.example.com/ws or ws://192.168.1.10:5250/ws.</summary>
    public string HubUrl { get; init; } = "";

    /// <summary>Display name used by other computers to target this machine with @NodeName. Defaults to the machine name.</summary>
    public string NodeName { get; init; } = "";

    /// <summary>Hub token credential reference in Windows Credential Manager (credential:WindowsAxon/mesh/...).</summary>
    public string? Token { get; init; }

    /// <summary>Peer-to-peer pair key credential reference in Windows Credential Manager (credential:WindowsAxon/mesh/...).</summary>
    public string? PairKey { get; init; }

    [JsonIgnore]
    public MeshMode EffectiveMode => Mode ?? (Enabled ? MeshMode.CentralServer : MeshMode.Disabled);

    [JsonIgnore]
    public bool IsEnabled => EffectiveMode != MeshMode.Disabled;

    [JsonIgnore]
    public string EffectiveNodeName =>
        string.IsNullOrWhiteSpace(NodeName) ? Environment.MachineName : NodeName.Trim();

    public void Validate()
    {
        if (!string.IsNullOrEmpty(Token) && !Token.StartsWith(CredentialPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("The mesh hub token must be a credential:<name> reference.");
        }

        if (!string.IsNullOrEmpty(PairKey) && !PairKey.StartsWith(CredentialPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("The peer-to-peer pair key must be a credential:<name> reference.");
        }

        if (EffectiveMode == MeshMode.CentralServer || !string.IsNullOrWhiteSpace(HubUrl))
        {
            if (!Uri.TryCreate(HubUrl.Trim(), UriKind.Absolute, out var uri)
                || uri.Scheme is not ("ws" or "wss")
                || !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new ArgumentException("Enter a ws:// or wss:// Hub URL without embedded credentials.");
            }
        }

        if (EffectiveMode == MeshMode.PeerToPeer && string.IsNullOrWhiteSpace(PairKey))
        {
            throw new ArgumentException("Enter a pair key to enable peer-to-peer mode.");
        }
    }
}

