namespace WindowsAxon.Mesh;

/// <summary>Capabilities advertised by a WindowsAxon node for discovery and capability-based routing.</summary>
public sealed record PeerCapabilities(IReadOnlyList<string> Services,
                                      IReadOnlyList<string>? Tools = null,
                                      IReadOnlyList<string>? Skills = null,
                                      string? OsDescription = null)
{
    public static PeerCapabilities Default { get; } = new([MeshServiceNames.AgentTask]);
}

/// <summary>Well-known distributed service identifiers advertised in PeerCapabilities.Services.</summary>
public static class MeshServiceNames
{
    public const string AgentTask = "agent.task";
    public const string FileTransfer = "file.transfer";
    public const string ArtifactFetch = "artifact.fetch";
    public const string Jobs = "jobs";
}

/// <summary>Presence and capability snapshot of a connected node.</summary>
public sealed record PeerInfo(string NodeId,
                              string DisplayName,
                              PeerCapabilities Capabilities,
                              DateTimeOffset? OnlineSinceUtc = null);

/// <summary>Distributed call metadata propagated across hops for auditing and loop prevention.</summary>
public sealed record MeshCallContext(string CallerNode,
                                     string TraceId,
                                     int HopCount)
{
    public const int DefaultMaxHops = 5;

    public static MeshCallContext CreateRoot(string localNode) =>
        new(localNode, Guid.NewGuid().ToString("N"), 0);

    public MeshCallContext NextHop(string localNode, int maxHops = DefaultMaxHops)
    {
        var next = HopCount + 1;
        if (next > maxHops)
        {
            throw new InvalidOperationException($"Mesh hop limit ({maxHops}) exceeded for trace '{TraceId}'.");
        }

        return new(localNode, TraceId, next);
    }
}
