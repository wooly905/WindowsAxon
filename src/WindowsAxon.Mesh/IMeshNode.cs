namespace WindowsAxon.Mesh;

/// <summary>Common contract for a distributed WindowsAxon node (central hub or LAN peer-to-peer).</summary>
public interface IMeshNode : IAsyncDisposable
{
    string NodeName { get; }

    MeshConnectionState State { get; }

    string StatusMessage { get; }

    IReadOnlyList<string> OnlinePeers { get; }

    event Action<IReadOnlyList<string>>? PeersChanged;

    event Action<MeshConnectionState, string>? ConnectionStateChanged;

    void Start();

    Task<RemoteTaskResult> RunRemoteTaskAsync(string targetNode,
                                              string prompt,
                                              Action<string> onToolStarted,
                                              Func<TaskApprovalRequestPayload, CancellationToken, Task<RemoteApprovalAnswer>> onApprovalRequested,
                                              CancellationToken cancellationToken);
}
