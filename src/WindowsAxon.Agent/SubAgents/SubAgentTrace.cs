using System.Text.Json;
using WindowsAxon.Agent.Guard;
using WindowsAxon.Agent.Tools;
using Microsoft.Extensions.AI;

namespace WindowsAxon.Agent.SubAgents;

/// <summary>
/// What one sub-agent run passes back to the main agent besides its answer: every change action and the results of
/// artifact tools. Read results stay in the sub-agent; the audit log keeps everything.
/// </summary>
public sealed class SubAgentTrace(string agent)
{
    public const int MaxArgumentChars = 300;
    public const int MaxArtifactChars = 500;

    private readonly List<string> _actions = [];
    private readonly List<string> _artifacts = [];

    public string Agent => agent;

    public IReadOnlyList<string> Actions => _actions;

    public IReadOnlyList<string> Artifacts => _artifacts;

    public void RecordCall(ToolEntry entry, string argumentsJson, string status, string result)
    {
        if (entry.Risk >= RiskLevel.Change)
        {
            _actions.Add($"{entry.Name} {Cut(argumentsJson, MaxArgumentChars)} {status}");
        }

        if (entry.Artifact && status == "succeeded")
        {
            _artifacts.Add($"{entry.Name}: {Cut(result, MaxArtifactChars)}");
        }
    }

    public void RecordDenied(FunctionCallContent call) =>
        _actions.Add($"{call.Name} {Cut(JsonSerializer.Serialize(call.Arguments), MaxArgumentChars)} denied");

    private static string Cut(string text, int max)
    {
        text = CardNumberMasker.Mask(text);
        return text.Length <= max ? text : text[..max] + "...";
    }
}
