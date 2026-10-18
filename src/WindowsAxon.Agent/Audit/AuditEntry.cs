
namespace WindowsAxon.Agent.Audit;

public sealed record AuditEntry(DateTimeOffset Time,
                                string Kind,
                                string? Request,
                                string Tool,
                                string Arguments,
                                string Risk,
                                string? Decision,
                                bool? Success,
                                string? ScriptHash = null,
                                string? CallId = null,
                                string? Status = null,
                                double? DurationMs = null,
                                string? Result = null,
                                string? Error = null,
                                string? Agent = null);
