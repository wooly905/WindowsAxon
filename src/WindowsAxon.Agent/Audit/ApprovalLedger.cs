using System.Collections.Concurrent;
using WindowsAxon.Agent.Tools;
using Microsoft.Extensions.AI;

namespace WindowsAxon.Agent.Audit;

/// <summary>Single-use approvals bound to the request, tool, and exact arguments.</summary>
public sealed class ApprovalLedger
{
    // Providers can send empty or repeated call IDs in one turn, so the key also holds the fingerprint.
    private readonly ConcurrentDictionary<(string CallId, string Fingerprint), bool> _decisions = new();

    /// <summary>The text of the request that is running, for the audit log.</summary>
    public string? CurrentRequest
    {
        get; set;
    }

    public void Record(FunctionCallContent call, bool approved) =>
        _decisions[(call.CallId ?? "", ToolCallIdentity.Fingerprint(call.Name, call.Arguments))] = approved;

    public bool Consume(string callId, string tool, IDictionary<string, object?> arguments) =>
        _decisions.TryRemove((callId, ToolCallIdentity.Fingerprint(tool, arguments)), out var approved) && approved;

    public void Reset() => _decisions.Clear();
}
