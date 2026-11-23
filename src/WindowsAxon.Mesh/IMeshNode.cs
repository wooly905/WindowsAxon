namespace WindowsAxon.Mesh;

/// <summary>Common contract for a distributed WindowsAxon node (central hub or LAN peer-to-peer).</summary>
public interface IMeshNode : IMeshTransport
{
    void Start();

    Task<RemoteTaskResult> RunRemoteTaskAsync(string targetNode,
                                              string prompt,
                                              string conversationId,
                                              bool forceNewRequest,
                                              Action<string> onToolStarted,
                                              Func<TaskApprovalRequestPayload, CancellationToken, Task<RemoteApprovalAnswer>> onApprovalRequested,
                                              CancellationToken cancellationToken);
}
