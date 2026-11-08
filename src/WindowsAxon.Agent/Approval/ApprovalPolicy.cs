using WindowsAxon.Agent.Tools;
using Microsoft.Extensions.AI;

namespace WindowsAxon.Agent.Approval;

/// <summary>The approval gate in code. A prompt, a skill, or an observation cannot change it.</summary>
public sealed class ApprovalPolicy(ToolCatalog catalog, IEnumerable<IApprovalRule> rules, bool autoApprove = false)
{
    private static readonly ApprovalDecision AskEachTime = new(ApprovalOutcome.Ask, false, null);
    private static readonly ApprovalDecision Auto = new(ApprovalOutcome.AutoApprove, false, null);

    private readonly IReadOnlyList<IApprovalRule> _rules = rules.ToList();
    private readonly HashSet<string> _allowed = [];

    public ApprovalDecision Decide(FunctionCallContent call)
    {
        var entry = catalog.Find(call.Name);
        if (entry is null)
        {
            return AskEachTime;
        }

        var rule = _rules.FirstOrDefault(r => r.AppliesTo(call.Name));
        if (autoApprove && rule?.AlwaysAsks(call) != true)
        {
            return Auto;
        }

        var decision = rule?.Decide(call)
            ?? new ApprovalDecision(ApprovalOutcome.Ask, entry.Risk == RiskLevel.Change, null);

        return decision is { Outcome: ApprovalOutcome.Ask, AllowForConversation: true } && _allowed.Contains(call.Name)
            ? decision with
            {
                Outcome = ApprovalOutcome.AutoApprove
            }
            : decision;
    }

    /// <summary>The user selected "Allow for this conversation". Has no effect for tools that ask each time.</summary>
    public void AllowForConversation(string toolName) => _allowed.Add(toolName);

    public void Reset() => _allowed.Clear();

    /// <summary>The catalog risk of a tool, for the audit log; null for an unknown tool.</summary>
    public RiskLevel? RiskOf(string toolName) => catalog.Find(toolName)?.Risk;
}
