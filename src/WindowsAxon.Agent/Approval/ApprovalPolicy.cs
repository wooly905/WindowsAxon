using WindowsAxon.Agent.Settings;
using WindowsAxon.Agent.Tools;
using Microsoft.Extensions.AI;

namespace WindowsAxon.Agent.Approval;

/// <summary>The approval gate in code. A prompt, a skill, or an observation cannot change it.</summary>
public sealed class ApprovalPolicy(ToolCatalog catalog,
                                  IEnumerable<IApprovalRule> rules,
                                  ApprovalLevel level)
{
    private static readonly ApprovalDecision AskEachTime = new(ApprovalOutcome.Ask, false, null);
    private static readonly ApprovalDecision Auto = new(ApprovalOutcome.AutoApprove, false, null);

    private readonly IReadOnlyList<IApprovalRule> _rules = rules.ToList();
    private readonly HashSet<string> _allowed = [];

    public ApprovalPolicy(ToolCatalog catalog,
                          IEnumerable<IApprovalRule> rules,
                          bool autoApprove = false,
                          bool approveAll = false)
        : this(catalog, rules, approveAll ? ApprovalLevel.All : autoApprove ? ApprovalLevel.Read : ApprovalLevel.Ask)
    {
    }

    public ApprovalDecision Decide(FunctionCallContent call)
    {
        if (level == ApprovalLevel.All)
        {
            return Auto;
        }

        var entry = catalog.Find(call.Name);
        if (entry is null)
        {
            return AskEachTime;
        }

        var rule = _rules.FirstOrDefault(r => r.AppliesTo(call.Name));
        var alwaysAsks = rule?.AlwaysAsks(call) == true;

        if (level == ApprovalLevel.Read && !entry.Outbound && entry.Risk < RiskLevel.Change
            && !entry.PolicyDriven && !alwaysAsks)
        {
            return Auto;
        }

        // Local includes policy-driven reads: the user accepted that tool results go to the chosen endpoint.
        if (level == ApprovalLevel.Local && !entry.Outbound && entry.Risk <= RiskLevel.Change && !alwaysAsks)
        {
            return Auto;
        }

        var ruleDecision = rule?.Decide(call);

        if (entry.Outbound)
        {
            return new ApprovalDecision(ApprovalOutcome.Ask, false,
                ruleDecision?.Note ?? "This request sends its arguments to a network destination. Review the destination and complete arguments.");
        }

        if (entry.Risk >= RiskLevel.Change || alwaysAsks)
        {
            return new ApprovalDecision(ApprovalOutcome.Ask, false, ruleDecision?.Note);
        }

        var decision = ruleDecision
            ?? AskEachTime;

        if (decision is not { Outcome: ApprovalOutcome.Ask, AllowForConversation: true })
        {
            return decision;
        }

        if (_allowed.Contains(ToolCallIdentity.Fingerprint(call.Name, call.Arguments)))
        {
            return decision with
            {
                Outcome = ApprovalOutcome.AutoApprove
            };
        }

        // A grant matches exact arguments only, so the option helps only calls without arguments.
        return call.Arguments is { Count: > 0 }
            ? decision with
            {
                AllowForConversation = false
            }
            : decision;
    }

    /// <summary>The user allowed this exact read again in the conversation. Has no effect for tools that ask each time.</summary>
    public void AllowForConversation(FunctionCallContent call) =>
        _allowed.Add(ToolCallIdentity.Fingerprint(call.Name, call.Arguments));

    public void Reset() => _allowed.Clear();

    /// <summary>The catalog risk of a tool, for the audit log; null for an unknown tool.</summary>
    public RiskLevel? RiskOf(string toolName) => catalog.Find(toolName)?.Risk;
}
