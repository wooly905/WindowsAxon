using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Guard;
using WindowsAxon.Agent.Selection;
using WindowsAxon.Agent.Steps;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace WindowsAxon.Agent.SubAgents;

/// <summary>A sub-agent created for one delegate call, and its step results.</summary>
public sealed record SubAgentInstance(AIAgent Agent, StepEvents Steps);

/// <summary>
/// delegate: runs one task in a sub-agent with its own context. Only the task, the answer, the change actions and the
/// artifacts return to the main agent.
/// </summary>
public sealed class DelegateFunction : AIFunction
{
    public const string ToolName = "delegate";

    private readonly IReadOnlyList<SubAgentDefinition> _agents;
    private readonly Func<SubAgentDefinition, SubAgentTrace, SubAgentInstance> _create;
    private readonly ApprovalLoop _approvals;
    private readonly ToolSelectionState _selection;
    private readonly JsonElement _schema;

    public DelegateFunction(IReadOnlyList<SubAgentDefinition> agents,
                            Func<SubAgentDefinition, SubAgentTrace, SubAgentInstance> create,
                            ApprovalLoop approvals,
                            ToolSelectionState selection)
    {
        ArgumentOutOfRangeException.ThrowIfZero(agents.Count);

        _agents = agents;
        _create = create;
        _approvals = approvals;
        _selection = selection;
        var names = new JsonArray();
        foreach (var agent in agents)
        {
            names.Add(agent.Name);
        }

        _schema = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["agent"] = new JsonObject { ["type"] = "string", ["enum"] = names },
                ["task"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray("agent", "task"),
        });

        // Structured mode shows only the first sentence of a description, so the agent list is in the system prompt.
        Description = "Give one complete task to an agent from the agent list. The agent does not see the " +
            "conversation, so include every name, path, and value it needs. It returns its answer, the changes it " +
            "made, and important results.";
    }

    /// <summary>The agent list for the main agent's prompt: what each agent does and which tools it has.</summary>
    public static string DescribeAgents(IReadOnlyList<SubAgentDefinition> agents,
                                        IReadOnlyDictionary<string, IReadOnlyList<string>> toolsByAgent) =>
        string.Join("\n", agents.Select(a => $"- {a.Name}: {a.Description}. " +
            $"Tools: {string.Join(", ", toolsByAgent.GetValueOrDefault(a.Name, []))}."));

    public override string Name => ToolName;

    public override string Description
    {
        get;
    }

    public override JsonElement JsonSchema => _schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments,
                                                                CancellationToken cancellationToken)
    {
        var name = arguments.TryGetValue("agent", out var a) ? a?.ToString() : null;
        var task = arguments.TryGetValue("task", out var t) ? t?.ToString() : null;
        var definition = _agents.FirstOrDefault(d => d.Name == name);
        if (definition is null)
        {
            return $"Unknown agent '{name}'. Agents: {string.Join(", ", _agents.Select(d => d.Name))}.";
        }

        if (string.IsNullOrWhiteSpace(task))
        {
            return "Write the task for the agent.";
        }

        _selection.RecordDelegation(definition.Name);
        var trace = new SubAgentTrace(definition.Name);
        var instance = _create(definition, trace);
        var session = await instance.Agent.CreateSessionAsync(cancellationToken: cancellationToken);
        var response = await instance.Agent.RunAsync(TranscriptWriter.NewRequest(task),
                                                     session,
                                                     cancellationToken: cancellationToken);
        response = await _approvals.ResolveAsync(instance.Agent,
                                                 session,
                                                 response,
                                                 null,
                                                 (call, approved) =>
                                                 {
                                                     if (!approved)
                                                     {
                                                         trace.RecordDenied(call);
                                                     }
                                                 },
                                                 cancellationToken);

        var asked = instance.Steps.LastFinalAction == StepSchemaBuilder.AskUser;
        return Format(definition.Name, response.Text, asked, trace);
    }

    internal static string Format(string agent, string answer, bool asked, SubAgentTrace trace)
    {
        answer = answer.Trim();
        if (answer.Length > GuardedAIFunction.MaxObservationChars)
        {
            answer = answer[..GuardedAIFunction.MaxObservationChars] + "...";
        }

        var text = new StringBuilder();
        text.Append($"Agent {agent} ").Append(asked ? "needs information from the user.\nQuestion: " : "finished.\nAnswer: ")
            .Append(answer.Length > 0 ? answer : "(no answer)").Append('\n');
        if (trace.Actions.Count > 0)
        {
            text.Append("Actions:\n");
            foreach (var action in trace.Actions)
            {
                text.Append("- ").Append(action).Append('\n');
            }
        }

        if (trace.Artifacts.Count > 0)
        {
            text.Append("Results:\n");
            foreach (var artifact in trace.Artifacts)
            {
                text.Append("- ").Append(artifact).Append('\n');
            }
        }

        return text.ToString().TrimEnd();
    }
}
