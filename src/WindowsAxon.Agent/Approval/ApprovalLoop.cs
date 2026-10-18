using System.Text.Json;
using WindowsAxon.Agent.Audit;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace WindowsAxon.Agent.Approval;

/// <summary>Answers the approval requests of an agent run until the run returns without one.</summary>
public sealed class ApprovalLoop(ApprovalPolicy policy, ApprovalLedger ledger, IAuditLog audit)
{
    /// <summary>Shows approval cards. AgentRunner sets it before the first run.</summary>
    public IApprovalPrompter? Prompter
    {
        get; set;
    }

    /// <param name="pending">Receives the requests being decided, then null after the agent accepted the answers.</param>
    /// <param name="decided">Receives each call and whether it was approved.</param>
    public async Task<AgentResponse> ResolveAsync(AIAgent agent,
                                                  AgentSession session,
                                                  AgentResponse response,
                                                  Action<List<ToolApprovalRequestContent>?>? pending,
                                                  Action<FunctionCallContent, bool>? decided,
                                                  CancellationToken cancellationToken)
    {
        while (response.Messages.SelectMany(m => m.Contents)
            .OfType<ToolApprovalRequestContent>()
            .ToList() is { Count: > 0 } requests)
        {
            pending?.Invoke(requests);
            var answers = new List<AIContent>();
            foreach (var request in requests)
            {
                var approved = await DecideAsync((FunctionCallContent)request.ToolCall, cancellationToken);
                decided?.Invoke((FunctionCallContent)request.ToolCall, approved);
                answers.Add(request.CreateResponse(approved, approved ? null : "The user denied this action."));
            }

            response = await agent.RunAsync(new ChatMessage(ChatRole.User, answers),
                                            session,
                                            cancellationToken: cancellationToken);
            pending?.Invoke(null);
        }

        return response;
    }

    private async Task<bool> DecideAsync(FunctionCallContent call, CancellationToken cancellationToken)
    {
        var decision = await Task.Run(() => policy.Decide(call), cancellationToken)
            .WaitAsync(cancellationToken);

        bool approved;
        string label;
        if (decision.Outcome == ApprovalOutcome.AutoApprove)
        {
            approved = true;
            label = "auto-approved";
        }
        else
        {
            var prompter = Prompter ?? throw new InvalidOperationException("No approval prompter is set.");
            var answer = await prompter.AskAsync(call, decision, cancellationToken);
            approved = answer != ApprovalAnswer.Deny;
            label = approved ? "approved" : "denied";
            if (answer == ApprovalAnswer.AllowForConversation && decision.AllowForConversation)
            {
                policy.AllowForConversation(call.Name);
            }
        }

        ledger.Record(call.CallId, approved);
        audit.Write(new AuditEntry(DateTimeOffset.Now,
                                   "approval",
                                   ledger.CurrentRequest,
                                   call.Name,
                                   JsonSerializer.Serialize(call.Arguments),
                                   policy.RiskOf(call.Name)?.ToString() ?? "Unknown",
                                   label,
                                   null,
                                   CallId: call.CallId,
                                   Status: label));

        return approved;
    }
}
