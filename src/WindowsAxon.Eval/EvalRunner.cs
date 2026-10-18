using System.Diagnostics;
using System.Text.Json;
using WindowsAxon.Agent;
using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Audit;
using WindowsAxon.Agent.Selection;
using WindowsAxon.Agent.Settings;
using WindowsAxon.Agent.Skills;
using WindowsAxon.Agent.Steps;
using WindowsAxon.Agent.SubAgents;
using Microsoft.Extensions.AI;

namespace WindowsAxon.Eval;

public sealed record EvalResult(string Id,
                                bool Success,
                                bool? SelectionCorrect,
                                bool? FirstToolCorrect,
                                int Steps,
                                int ValidSteps,
                                int InvalidSteps,
                                int Approvals,
                                int Unapproved,
                                double Seconds,
                                IReadOnlyList<string> Failures,
                                string Answer,
                                int MainContextTokens = 0);

public sealed record EvalSummary(int Cases,
                                 double SelectionAccuracy,
                                 double FirstToolAccuracy,
                                 double TaskSuccess,
                                 double InvalidStepRate,
                                 int Unapproved,
                                 double AverageSteps,
                                 double AverageSeconds,
                                 double AverageMainContextTokens,
                                 IReadOnlyList<EvalResult> Results)
{
    public static EvalSummary From(IReadOnlyList<EvalResult> results)
    {
        static double Rate(IEnumerable<bool?> values)
        {
            var known = values.Where(v => v.HasValue).ToList();
            return known.Count == 0 ? 1.0 : known.Count(v => v == true) / (double)known.Count;
        }

        var allSteps = results.Sum(r => r.ValidSteps + r.InvalidSteps);
        return new EvalSummary(results.Count,
                               Rate(results.Select(r => r.SelectionCorrect)),
                               Rate(results.Select(r => r.FirstToolCorrect)),
                               results.Count == 0 ? 0 : results.Count(r => r.Success) / (double)results.Count,
                               allSteps == 0 ? 0 : results.Sum(r => r.InvalidSteps) / (double)allSteps,
                               results.Sum(r => r.Unapproved),
                               results.Count == 0 ? 0 : results.Average(r => r.Steps),
                               results.Count == 0 ? 0 : results.Average(r => r.Seconds),
                               results.Count == 0 ? 0 : results.Average(r => r.MainContextTokens),
                               results);
    }

    /// <summary>Spec Section 14.1. Time is reported but is not a gate.</summary>
    public bool PassesM1Gate(bool smallModel) =>
        SelectionAccuracy >= 0.85 && TaskSuccess >= (smallModel ? 0.65 : 0.80) && InvalidStepRate <= 0.05
        && Unapproved == 0;
}

public sealed class EvalRunner(Func<IChatClient> modelFactory,
                               ChatSettings chat,
                               IReadOnlyList<FakeToolSpec> tools,
                               string skillsRoot,
                               string evalRoot,
                               bool docsAvailable)
{
    public async Task<EvalSummary> RunAllAsync(IEnumerable<EvalCase> cases, CancellationToken cancellationToken)
    {
        var results = new List<EvalResult>();
        foreach (var c in cases)
        {
            results.Add(await RunCaseAsync(c, cancellationToken));
        }

        return EvalSummary.From(results);
    }

    public async Task<EvalResult> RunCaseAsync(EvalCase c, CancellationToken cancellationToken)
    {
        var calls = new List<ToolCallRecord>();
        var catalog = FakeToolManifest.BuildCatalog(tools, c.ResolveMocks(evalRoot), calls);
        var audit = new NullAuditLog();
        var meter = new MainContextMeter(modelFactory());
        // Eval scores approval prompts, so it always uses the ask-first policy.
        var dependencies = new AgentDependencies(chat with
        {
            AutoApproveTools = false
        },
                                                 meter,
                                                 catalog,
                                                 SkillCatalog.Load([(skillsRoot, true)]),
                                                 [skillsRoot],
                                                 audit,
                                                 [],
                                                 new NoExtension(),
                                                 docsAvailable);
        var session = await new AgentFactory(dependencies).CreateSessionAsync(cancellationToken);
        var approver = new AutoApprover();
        var runner = new AgentRunner(session, approver, audit);

        var stopwatch = Stopwatch.StartNew();
        var result = await runner.RunAsync(c.Request, cancellationToken: cancellationToken);
        var replies = new Queue<string>(c.Replies);
        while (result.Status == RunStatus.AskedUser && replies.Count > 0)
        {
            result = await runner.RunAsync(replies.Dequeue(), cancellationToken: cancellationToken);
        }

        stopwatch.Stop();

        var names = calls.Select(call => call.Name).ToList();
        var failures = new List<string>();
        if (result.Status is RunStatus.Failed or RunStatus.Cancelled)
        {
            failures.Add($"run ended with {result.Status}: {result.Text}");
        }

        failures.AddRange(c.ExpectToolsAll.Where(t => !names.Contains(t)).Select(t => $"missing tool {t}"));
        if (c.ExpectToolsAny.Count > 0 && !c.ExpectToolsAny.Any(names.Contains))
        {
            failures.Add($"none of {string.Join(", ", c.ExpectToolsAny)} was called");
        }

        failures.AddRange(c.ForbidTools.Where(names.Contains).Select(t => $"forbidden tool {t}"));
        foreach (var (tool, expected) in c.ExpectArgs)
        {
            var matched = calls.Any(call => call.Name == tool && expected.All(e =>
                call.Args.TryGetValue(e.Key, out var actual) && string.Equals(actual.Trim(),
                                                                              e.Value.Trim(),
                                                                              StringComparison.OrdinalIgnoreCase)));
            if (!matched)
            {
                failures.Add($"{tool} was not called with {string.Join(", ", expected.Select(e => $"{e.Key}={e.Value}"))}");
            }
        }

        failures.AddRange(c.ExpectAnswerContains
            .Where(text => !result.Text.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Select(text => $"answer does not contain '{text}'"));

        bool? selection = null;
        var expectedKey = c.ExpectSkill is not null ? $"skill:{c.ExpectSkill}" : c.ExpectGroup is not null ? GroupKey(c.ExpectGroup) : null;
        if (expectedKey is not null)
        {
            selection = session.Selection.ActivationHistory.Contains(expectedKey);
            if (selection == false)
            {
                failures.Add($"{expectedKey} was not selected");
            }
        }

        var expectedTools = c.ExpectToolsAll.Concat(c.ExpectToolsAny).ToHashSet();
        bool? firstTool = expectedTools.Count == 0 ? null : names.FirstOrDefault() is { } first
            && expectedTools.Contains(first);

        if (session.Ledger.UnapprovedCalls > 0)
        {
            failures.Add($"{session.Ledger.UnapprovedCalls} change calls ran without approval");
        }

        return new EvalResult(c.Id,
                              failures.Count == 0,
                              selection,
                              firstTool,
                              result.Steps,
                              session.Steps.ValidSteps,
                              session.Steps.InvalidSteps,
                              approver.Count,
                              session.Ledger.UnapprovedCalls,
                              stopwatch.Elapsed.TotalSeconds,
                              failures,
                              result.Text,
                              meter.MaxTokens);
    }

    /// <summary>ToolSelection = SubAgents: a group is selected by delegating to the agent that owns it.</summary>
    private string? GroupKey(string group)
    {
        if (chat.ToolSelection != ToolSelectionMode.SubAgents)
        {
            return $"group:{group}";
        }

        return SubAgentCatalog.All.FirstOrDefault(a => a.Groups.Contains(group)) is { } agent
            ? $"agent:{agent.Name}"
            : null;
    }

    /// <summary>Measures the largest main-agent request, about four characters per token.</summary>
    private sealed class MainContextMeter(IChatClient inner) : DelegatingChatClient(inner)
    {
        private const string MainPrompt = "You are WindowsAxon,";

        public int MaxTokens
        {
            get; private set;
        }

        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
                                                            ChatOptions? options = null,
                                                            CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            var prompt = options?.Instructions ?? list.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? "";
            if (prompt.StartsWith(MainPrompt, StringComparison.Ordinal))
            {
                var chars = (options?.Instructions?.Length ?? 0) + list.SelectMany(m => m.Contents).Sum(Length);
                MaxTokens = Math.Max(MaxTokens, chars / 4);
            }

            return base.GetResponseAsync(list, options, cancellationToken);
        }

        private static int Length(AIContent content) => content switch
        {
            TextContent text => text.Text.Length,
            TextReasoningContent reasoning => reasoning.Text.Length,
            FunctionCallContent call => call.Name.Length + JsonSerializer.Serialize(call.Arguments).Length,
            FunctionResultContent result => TranscriptWriter.ObservationText(result.Result).Length,
            _ => 0,
        };
    }

    private sealed class AutoApprover : IApprovalPrompter
    {
        private int _count;

        public int Count => _count;

        public Task<ApprovalAnswer> AskAsync(FunctionCallContent call,
                                             ApprovalDecision decision,
                                             CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return Task.FromResult(ApprovalAnswer.Allow);
        }
    }

    private sealed class NoExtension : IStepLimitPrompter
    {
        public Task<bool> AskToContinueAsync(int stepsUsed, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class NullAuditLog : IAuditLog
    {
        public void Write(AuditEntry entry)
        {
        }
    }
}
