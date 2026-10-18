using System.Text.Json;
using WindowsAxon.Agent.Audit;
using WindowsAxon.Agent.Selection;
using WindowsAxon.Agent.Steps;
using WindowsAxon.Agent.SubAgents;
using WindowsAxon.Agent.Tools;
using Microsoft.Extensions.AI;

namespace WindowsAxon.Agent.Guard;

/// <summary>ToolGuard: checks, timeout, observation cut, masking, and audit around one tool.</summary>
public sealed class GuardedAIFunction(ToolEntry entry,
                                      ToolSelectionState state,
                                      CallTracker tracker,
                                      ApprovalLedger ledger,
                                      IAuditLog audit,
                                      SubAgentTrace? trace = null)
    : DelegatingAIFunction(entry.Function)
{
    public const int MaxObservationChars = 1500;
    public const int MaxErrorChars = 300;
    public const string NotEnabledMessage = "This tool is not enabled. Call load_skill or enable_tools first.";
    public const string RepeatMessage = "You already ran this. Use the result or select a different action.";
    private const string StoppedMessage = "Stopped: the same action was repeated 3 times.";

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments,
                                                                CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var context = FunctionInvokingChatClient.CurrentContext;
        var callId = context?.CallContent.CallId ?? "";
        var argumentsJson = JsonSerializer.Serialize(arguments.ToDictionary(a => a.Key, a => a.Value));
        var decision = entry.NeedsApproval ? ledger.Find(callId) == true ? "approved" : "missing" : "not needed";
        var call = new ToolCallAudit(audit,
                                     ledger.CurrentRequest,
                                     entry.Name,
                                     argumentsJson,
                                     entry.Risk.ToString(),
                                     decision,
                                     callId,
                                     trace?.Agent);

        if (!state.IsActive(entry.Name))
        {
            call.Complete("blocked", false, NotEnabledMessage);
            return NotEnabledMessage;
        }

        var repeat = tracker.RecordCall(entry.Name + argumentsJson);
        if (repeat == 2)
        {
            call.Complete("blocked", false, RepeatMessage);
            return RepeatMessage;
        }

        if (repeat >= 3)
        {
            if (context is not null)
            {
                context.Terminate = true;
            }

            call.Complete("blocked", false, StoppedMessage);
            return StoppedMessage;
        }

        if (entry.NeedsApproval)
        {
            if (decision == "missing")
            {
                ledger.CountUnapproved();
            }
        }

        state.MarkUsed(entry.Name);
        object? result;
        string text;
        bool success;
        string status;
        Exception? error;
        try
        {
            (result, text, success, status, error) = await RunAsync(arguments, cancellationToken);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped while the tool ran: the tool may already have changed something, so keep a record.
            call.Complete("cancelled", false, error: ex);
            throw;
        }

        if (tracker.RecordResult(success) >= 2)
        {
            state.MustFinish = true;
        }

        call.Complete(status, success, text, error);
        trace?.RecordCall(entry, argumentsJson, status, text);
        var observation = CardNumberMasker.Mask(text);
        if (error is not null && status == "failed")
        {
            var message = CardNumberMasker.Mask(error.Message);
            observation = "Error: " + (message.Length > MaxErrorChars ? message[..MaxErrorChars] : message);
        }
        observation = success && entry.Name == "browser_read" ? observation : Cut(observation);
        var images = TranscriptWriter.Images(result);
        return images.Count > 0 ? new List<AIContent>([new TextContent(observation), .. images]) : observation;
    }

    private async Task<(object? Result,
                        string Text,
                        bool Success,
                        string Status,
                        Exception? Error)> RunAsync(AIFunctionArguments arguments,
                                                    CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(entry.Timeout);
        try
        {
            var result = await base.InvokeCoreAsync(arguments, timeout.Token);
            return (result, TranscriptWriter.ObservationText(result), true, "succeeded", null);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, $"Error: the tool did not finish in {(int)entry.Timeout.TotalSeconds} seconds.", false, "timed_out", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, "Error: " + ex.Message, false, "failed", ex);
        }
    }

    private static string Cut(string text) =>
        text.Length <= MaxObservationChars
        ? text
        : $"{text[..MaxObservationChars]}... (cut: showing {MaxObservationChars} of {text.Length} characters)";
}
