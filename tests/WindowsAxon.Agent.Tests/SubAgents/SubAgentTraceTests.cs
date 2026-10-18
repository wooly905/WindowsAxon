using WindowsAxon.Agent.Audit;
using WindowsAxon.Agent.Guard;
using WindowsAxon.Agent.Selection;
using WindowsAxon.Agent.SubAgents;
using WindowsAxon.Agent.Tests.Fakes;
using WindowsAxon.Agent.Tools;

namespace WindowsAxon.Agent.Tests.SubAgents;

public class SubAgentTraceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(SubAgentTrace Trace, ListAuditLog Audit)> Run(string tool,
                                                                            RiskLevel risk,
                                                                            bool artifact,
                                                                            Func<string, string> body)
    {
        var catalog = new ToolCatalog();
        catalog.Add(AIFunctionFactory.Create((string x) => body(x), tool), "files", risk, artifact: artifact);
        var state = new ToolSelectionState(catalog, 14) { AllToolsActive = true };
        var trace = new SubAgentTrace("files");
        var audit = new ListAuditLog();
        var guard = new GuardedAIFunction(catalog.Find(tool)!,
                                          state,
                                          new CallTracker(),
                                          new ApprovalLedger(),
                                          audit,
                                          trace);

        await guard.InvokeAsync(new AIFunctionArguments { ["x"] = "C:\\a.txt" }, Ct);

        return (trace, audit);
    }

    [Fact]
    public async Task Read_results_stay_in_the_sub_agent()
    {
        var (trace, _) = await Run("fs_read_text", RiskLevel.Read, artifact: false, _ => "secret content");

        Assert.Empty(trace.Actions);
        Assert.Empty(trace.Artifacts);
    }

    [Fact]
    public async Task Change_calls_are_recorded_with_arguments_and_status()
    {
        var (trace, audit) = await Run("fs_move", RiskLevel.Change, artifact: false, _ => "moved");

        var action = Assert.Single(trace.Actions);
        Assert.StartsWith("fs_move ", action);
        Assert.Contains("a.txt", action);
        Assert.EndsWith(" succeeded", action);
        Assert.Empty(trace.Artifacts);
        Assert.All(audit.Entries, e => Assert.Equal("files", e.Agent));
    }

    [Fact]
    public async Task Failed_change_calls_are_recorded_as_failed()
    {
        var (trace, _) = await Run("fs_move",
                                   RiskLevel.Change,
                                   artifact: false,
                                   _ => throw new IOException("locked"));

        Assert.EndsWith(" failed", Assert.Single(trace.Actions));
    }

    [Fact]
    public async Task Artifact_results_are_kept_and_cut()
    {
        var (trace, _) = await Run("fs_find_files",
                                   RiskLevel.Read,
                                   artifact: true,
                                   _ => "C:\\found.txt" + new string('z', 2000));

        Assert.Empty(trace.Actions);
        var artifact = Assert.Single(trace.Artifacts);
        Assert.StartsWith("fs_find_files: C:\\found.txt", artifact);
        Assert.True(artifact.Length < SubAgentTrace.MaxArtifactChars + 40);
    }

    [Fact]
    public void Denied_calls_are_recorded()
    {
        var trace = new SubAgentTrace("files");

        trace.RecordDenied(new FunctionCallContent("1",
                                                   "fs_delete",
                                                   new Dictionary<string, object?> { ["path"] = "C:\\a.txt" }));

        Assert.EndsWith(" denied", Assert.Single(trace.Actions));
    }
}
