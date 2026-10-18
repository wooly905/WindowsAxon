using System.Text.Json;
using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Audit;
using WindowsAxon.Agent.Guard;
using WindowsAxon.Agent.Selection;
using WindowsAxon.Agent.Settings;
using WindowsAxon.Agent.Skills;
using WindowsAxon.Agent.Steps;
using WindowsAxon.Agent.SubAgents;
using WindowsAxon.Agent.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace WindowsAxon.Agent;

public sealed record AgentDependencies(ChatSettings Chat,
                                       IChatClient Model,
                                       ToolCatalog Catalog,
                                       SkillCatalog Skills,
                                       IReadOnlyList<string> SkillRoots,
                                       IAuditLog Audit,
                                       IReadOnlyList<IApprovalRule> ApprovalRules,
                                       IStepLimitPrompter StepLimitPrompter,
                                       bool DocsAvailable);

public sealed class AgentFactory(AgentDependencies dependencies)
{
    public async Task<WindowsAxonSession> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        var d = dependencies;
        var groups = d.Chat.ToolSelection == ToolSelectionMode.Groups;
        var subAgents = d.Chat.ToolSelection == ToolSelectionMode.SubAgents;
        var selection = new ToolSelectionState(d.Catalog, d.Chat.MaxActiveTools) { AllToolsActive = !groups };
        var steps = new StepEvents();
        var calls = new CallTracker();
        var ledger = new ApprovalLedger();
        var policy = new ApprovalPolicy(d.Catalog, d.ApprovalRules, d.Chat.AutoApproveTools);
        var approvals = new ApprovalLoop(policy, ledger, d.Audit);
        var toolsByAgent = SubAgentCatalog.All.ToDictionary(a => a.Name,
                                                            a => (IReadOnlyList<string>)d.Catalog.Entries
                                                                .Where(a.Includes)
                                                                .Select(e => e.Name)
                                                                .ToList());
        var agents = SubAgentCatalog.All.Where(a => toolsByAgent[a.Name].Count > 0).ToList();
        var agentList = DelegateFunction.DescribeAgents(agents, toolsByAgent);
        var fallbackPrompt = subAgents
            ? Prompts.Orchestrator(d.DocsAvailable, agentList, StepMode.Structured)
            : Prompts.System(d.DocsAvailable, StepMode.Structured);

        var tools = d.Catalog.BuildAgentTools(entry => new GuardedAIFunction(entry, selection, calls, ledger, d.Audit),
                                              entry => !subAgents || SubAgentCatalog.MainGroups.Contains(entry.Group))
            .ToList();

        if (groups)
        {
            tools.Add(new EnableToolsFunction(d.Catalog, selection));
        }
        else if (subAgents && agents.Count > 0)
        {
            tools.Add(new DelegateFunction(agents,
                                           (definition, trace) => CreateSubAgent(definition,
                                                                                 trace,
                                                                                 selection.Budget,
                                                                                 ledger),
                                           approvals,
                                           selection));
        }

        var skillsProvider = new AgentSkillsProvider(new CachingAgentSkillsSource(new DeduplicatingAgentSkillsSource(
                new AgentFileSkillsSource(d.SkillRoots,
                                          options: new AgentFileSkillsSourceOptions { AllowedScriptExtensions = [] }))),
                                                     options: new AgentSkillsProviderOptions
                                                     {
                                                         DisableLoadSkillApproval = true,
                                                         DisableReadSkillResourceApproval = true,
                                                         SkillsInstructionPrompt = Prompts.SkillsInstruction,
                                                     },
                                                     ownsSource: true);

        var agent = BuildChatClient(selection, steps, ledger, scopeTools: groups, fallbackPrompt)
            .AsAIAgent(new ChatClientAgentOptions
            {
                Name = "WindowsAxon",
                ChatOptions = new ChatOptions
                {
                    Instructions = subAgents
                        ? Prompts.Orchestrator(d.DocsAvailable, agentList, d.Chat.StepMode)
                        : Prompts.System(d.DocsAvailable, d.Chat.StepMode),
                    Temperature = Temperature,
                    Tools = tools,
                },
                AIContextProviders = [skillsProvider],
                UseProvidedChatClientAsIs = true,
            });

        return new WindowsAxonSession
        {
            Agent = agent,
            Session = await agent.CreateSessionAsync(cancellationToken: cancellationToken),
            Selection = selection,
            Steps = steps,
            Calls = calls,
            Ledger = ledger,
            Policy = policy,
            Approvals = approvals,
            Tools = tools,
        };
    }

    private float? Temperature =>
        string.Equals(dependencies.Chat.Model, "auto", StringComparison.OrdinalIgnoreCase) ? null : 0.1f;

    /// <summary>The model pipeline shared by the main agent and the sub-agents.</summary>
    private IChatClient BuildChatClient(ToolSelectionState selection,
                                        StepEvents steps,
                                        ApprovalLedger ledger,
                                        bool scopeTools,
                                        string structuredFallbackPrompt)
    {
        var d = dependencies;
        var builder = d.Model.AsBuilder()
            .UseFunctionInvocation(configure: f =>
            {
                f.MaximumIterationsPerRequest = 60; // backstop; ToolScopeChatClient owns the step limit
                f.MaximumConsecutiveErrorsPerRequest = 2;
                f.IncludeDetailedErrors = true;
                // Catalog tools log inside their guard, before results are trimmed. Include provider tools too.
                f.FunctionInvoker = (context, token) => d.Catalog.Find(context.Function.Name) is not null
                    ? context.Function.InvokeAsync(context.Arguments, token)
                    : ToolCallAudit.InvokeAsync(context, d.Audit, ledger, token);
            })
            .Use(inner => new ToolScopeChatClient(inner, selection, d.Skills, d.StepLimitPrompter, scopeTools));
        if (d.Chat.StepMode == StepMode.Structured)
        {
            builder.Use(inner => new ReActStepChatClient(inner,
                                                         d.Chat.ArgsSchema,
                                                         new TranscriptWriter(d.Chat.ContextWindow),
                                                         steps));
        }
        else
        {
            builder.Use(inner => new NativeFallbackChatClient(new ToolImageChatClient(inner),
                                                               new ReActStepChatClient(inner,
                                                                                       d.Chat.ArgsSchema,
                                                                                       new TranscriptWriter(d.Chat.ContextWindow),
                                                                                       steps),
                                                               structuredFallbackPrompt,
                                                               response => LogNativeFallback(d.Audit, ledger, response)));
        }

        builder.Use(inner => new ImageFallbackChatClient(inner, error => LogImageFallback(d.Audit, ledger, error)));
        return builder.Build();
    }

    private SubAgentInstance CreateSubAgent(SubAgentDefinition definition,
                                            SubAgentTrace trace,
                                            StepBudget budget,
                                            ApprovalLedger ledger)
    {
        var d = dependencies;
        var selection = new ToolSelectionState(d.Catalog, d.Chat.MaxActiveTools, budget) { AllToolsActive = true };
        var steps = new StepEvents();
        var calls = new CallTracker();
        var tools = d.Catalog.BuildAgentTools(entry => new GuardedAIFunction(entry,
                                                                             selection,
                                                                             calls,
                                                                             ledger,
                                                                             d.Audit,
                                                                             trace),
                                              definition.Includes);
        var agent = BuildChatClient(selection,
                                    steps,
                                    ledger,
                                    scopeTools: false,
                                    Prompts.SubAgent(definition, StepMode.Structured))
            .AsAIAgent(new ChatClientAgentOptions
            {
                Name = definition.Name,
                ChatOptions = new ChatOptions
                {
                    Instructions = Prompts.SubAgent(definition, d.Chat.StepMode),
                    Temperature = Temperature,
                    Tools = tools,
                },
                UseProvidedChatClientAsIs = true,
            });

        return new SubAgentInstance(agent, steps);
    }

    private static void LogImageFallback(IAuditLog audit, ApprovalLedger ledger, Exception error) =>
        audit.Write(new AuditEntry(DateTimeOffset.Now,
                                   "model_fallback",
                                   ledger.CurrentRequest,
                                   "",
                                   "{}",
                                   "None",
                                   null,
                                   null,
                                   Status: "no_images",
                                   Error: error.ToString()));

    private static void LogNativeFallback(IAuditLog audit, ApprovalLedger ledger, ChatResponse response)
    {
        var metadata = JsonSerializer.Serialize(new
        {
            response.ModelId,
            FinishReason = response.FinishReason?.Value
        });
        var reasoning = string.Join("\n", response.Messages.SelectMany(m => m.Contents)
            .OfType<TextReasoningContent>().Select(c => c.Text));
        audit.Write(new AuditEntry(DateTimeOffset.Now,
                                   "model_fallback",
                                   ledger.CurrentRequest,
                                   "",
                                   metadata,
                                   "None",
                                   null,
                                   null,
                                   Status: "structured",
                                   Result: reasoning,
                                   Error: "Native response contained reasoning but no answer or tool call."));
    }
}
