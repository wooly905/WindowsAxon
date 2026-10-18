using System.Diagnostics;
using System.Text.Json;
using WindowsAxon.Agent.Steps;
using Microsoft.Extensions.AI;

namespace WindowsAxon.Agent.Audit;

/// <summary>Records a tool's start and full result before observation trimming.</summary>
public sealed class ToolCallAudit
{
    private readonly IAuditLog _audit;
    private readonly AuditEntry _entry;
    private readonly long _started;

    public ToolCallAudit(IAuditLog audit,
                         string? request,
                         string tool,
                         string arguments,
                         string risk,
                         string decision,
                         string? callId,
                         string? agent = null)
    {
        _audit = audit;
        _entry = new AuditEntry(DateTimeOffset.Now,
                                "call_start",
                                request,
                                tool,
                                arguments,
                                risk,
                                decision,
                                null,
                                CallId: string.IsNullOrEmpty(callId) ? Guid.NewGuid().ToString("N") : callId,
                                Status: "started",
                                Agent: agent);
        _started = Stopwatch.GetTimestamp();
        _audit.Write(_entry);
    }

    public void Complete(string status, bool success, string? result = null, Exception? error = null) =>
        _audit.Write(_entry with
        {
            Time = DateTimeOffset.Now,
            Kind = "call",
            Status = status,
            Success = success,
            DurationMs = Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
            Result = result,
            Error = error?.ToString(),
        });

    public static async ValueTask<object?> InvokeAsync(FunctionInvocationContext context,
                                                       IAuditLog audit,
                                                       ApprovalLedger ledger,
                                                       CancellationToken cancellationToken)
    {
        var call = new ToolCallAudit(audit,
                                     ledger.CurrentRequest,
                                     context.Function.Name,
                                     JsonSerializer.Serialize(context.Arguments.ToDictionary(a => a.Key, a => a.Value)),
                                     "Read",
                                     "not needed",
                                     context.CallContent.CallId);
        try
        {
            var result = await context.Function.InvokeAsync(context.Arguments, cancellationToken);
            call.Complete("succeeded", true, TranscriptWriter.ObservationText(result));
            return result;
        }
        catch (OperationCanceledException ex)
        {
            call.Complete(cancellationToken.IsCancellationRequested ? "cancelled" : "timed_out", false, error: ex);
            throw;
        }
        catch (Exception ex)
        {
            call.Complete("failed", false, error: ex);
            throw;
        }
    }
}
