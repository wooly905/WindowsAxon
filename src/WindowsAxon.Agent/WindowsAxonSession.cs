using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Audit;
using WindowsAxon.Agent.Guard;
using WindowsAxon.Agent.Selection;
using WindowsAxon.Agent.Steps;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace WindowsAxon.Agent;

/// <summary>Everything that belongs to one conversation.</summary>
public sealed class WindowsAxonSession
{
    public required AIAgent Agent
    {
        get; init;
    }

    public required AgentSession Session
    {
        get; init;
    }

    public required ToolSelectionState Selection
    {
        get; init;
    }

    public required StepEvents Steps
    {
        get; init;
    }

    public required CallTracker Calls
    {
        get; init;
    }

    public required ApprovalLedger Ledger
    {
        get; init;
    }

    public required ApprovalPolicy Policy
    {
        get; init;
    }

    public required ApprovalLoop Approvals
    {
        get; init;
    }

    public required IReadOnlyList<AITool> Tools
    {
        get; init;
    }
}
