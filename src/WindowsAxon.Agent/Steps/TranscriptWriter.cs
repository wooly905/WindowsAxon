using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace WindowsAxon.Agent.Steps;

/// <summary>Writes the history as text messages for Structured mode, within the context budget.</summary>
public sealed class TranscriptWriter(int contextWindow)
{
    public const string RequestMarker = "windowsaxon.request";
    public const string ActionKey = "windowsaxon.action";

    private const int PreviousRequestsBudget = 1000;
    private const int CharsPerToken = 4;

    private int ScratchpadBudget => Math.Max(2000, contextWindow - 6000);

    public static ChatMessage NewRequest(string text) => new(ChatRole.User, text)
    {
        AdditionalProperties = new()
        {
            [RequestMarker] = true
        }
    };

    public static string ObservationText(object? result) => result switch
    {
        null => "",
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? "",
        JsonElement e => e.GetRawText(),
        IEnumerable<AIContent> contents => string.Join("\n", contents.OfType<TextContent>().Select(t => t.Text)),
        _ => JsonSerializer.Serialize(result),
    };

    public static IReadOnlyList<DataContent> Images(object? result) =>
        result is IEnumerable<AIContent> contents
        ? contents.OfType<DataContent>().Where(d => d.HasTopLevelMediaType("image")).ToList()
        : [];

    public List<ChatMessage> Write(IEnumerable<ChatMessage> history,
                                   string? instructions,
                                   IReadOnlyList<AIFunctionDeclaration> tools)
    {
        var all = history.ToList();
        var systemText = new StringBuilder();
        foreach (var system in all.Where(m => m.Role == ChatRole.System))
        {
            systemText.Append(system.Text).Append("\n\n");
        }

        var nonSystem = all.Where(m => m.Role != ChatRole.System).ToList();
        var start = nonSystem.FindLastIndex(m => m.Role == ChatRole.User
            && m.AdditionalProperties?.ContainsKey(RequestMarker) == true);
        if (start < 0)
        {
            start = 0;
        }

        var result = new List<ChatMessage> { new(ChatRole.System,
                                                 BuildSystem(instructions,
                                                             systemText.ToString(),
                                                             tools)) };
        result.AddRange(PreviousRequests(nonSystem[..start]));
        result.AddRange(TrimScratchpad(CurrentRequest(nonSystem[start..])));
        return result;
    }

    private static string BuildSystem(string? instructions,
                                      string extraSystem,
                                      IReadOnlyList<AIFunctionDeclaration> tools)
    {
        var text = new StringBuilder();
        text.Append(instructions?.Trim()).Append("\n\n");
        if (extraSystem.Length > 0)
        {
            text.Append(extraSystem.Trim()).Append("\n\n");
        }

        text.Append("Actions:\n").Append(StepSchemaBuilder.DescribeTools(tools));
        text.Append("- final_answer(answer: string): Give the answer to the user. The run ends.\n");
        text.Append("- ask_user(answer: string): Ask the user one question. The run ends.\n");
        return text.ToString();
    }

    private static IEnumerable<ChatMessage> PreviousRequests(List<ChatMessage> earlier)
    {
        var kept = new List<ChatMessage>();
        var used = 0;
        for (var i = earlier.Count - 1; i >= 0; i--)
        {
            var message = earlier[i];
            var textOnly = message.Contents.All(c => c is TextContent or TextReasoningContent)
                && !string.IsNullOrWhiteSpace(message.Text);
            if (!textOnly || (message.Role != ChatRole.User && message.Role != ChatRole.Assistant))
            {
                continue;
            }

            used += Tokens(message.Text);
            if (used > PreviousRequestsBudget)
            {
                break;
            }

            kept.Insert(0, new ChatMessage(message.Role, message.Text));
        }

        return kept;
    }

    private static List<ChatMessage> CurrentRequest(List<ChatMessage> current)
    {
        var toolNames = new Dictionary<string, string>();
        var result = new List<ChatMessage>();
        // Only the newest image is sent; older ones keep their text description.
        var lastImage = current.SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .LastOrDefault(r => Images(r.Result).Count > 0);
        foreach (var message in current)
        {
            if (message.Role == ChatRole.Assistant)
            {
                var thought = string.Concat(message.Contents.OfType<TextReasoningContent>().Select(r => r.Text));
                var calls = message.Contents.OfType<FunctionCallContent>().ToList();
                foreach (var call in calls)
                {
                    toolNames[call.CallId] = call.Name;
                    result.Add(new ChatMessage(ChatRole.Assistant, StepJson(thought, call.Name, call.Arguments, null)));
                }

                if (calls.Count == 0 && !string.IsNullOrWhiteSpace(message.Text))
                {
                    var action = message.AdditionalProperties?.TryGetValue(ActionKey, out var a) == true ? a?.ToString() : null;
                    result.Add(new ChatMessage(ChatRole.Assistant,
                                               StepJson(thought,
                                                        action ?? StepSchemaBuilder.FinalAnswer,
                                                        null,
                                                        message.Text)));
                }
            }
            else if (message.Role == ChatRole.Tool || message.Contents.Any(c => c is FunctionResultContent))
            {
                foreach (var functionResult in message.Contents.OfType<FunctionResultContent>())
                {
                    var name = toolNames.GetValueOrDefault(functionResult.CallId, "unknown");
                    var observation = Observation(name, functionResult.CallId, ObservationText(functionResult.Result));
                    result.Add(functionResult == lastImage
                        ? new ChatMessage(ChatRole.User, [new TextContent(observation), .. Images(functionResult.Result)])
                        : new ChatMessage(ChatRole.User, observation));
                }
            }
            else if (message.Role == ChatRole.User && !string.IsNullOrWhiteSpace(message.Text))
            {
                result.Add(new ChatMessage(ChatRole.User, message.Text)); // approval responses have no text and are dropped
            }
        }

        return result;
    }

    private List<ChatMessage> TrimScratchpad(List<ChatMessage> scratchpad)
    {
        var observations = scratchpad.Select((m, i) => (m, i)).Where(x => x.m.Text.StartsWith("<observation",
                                                                                              StringComparison.Ordinal))
            .Select(x => x.i)
            .ToList();
        var total = scratchpad.Sum(m => Tokens(m.Text));
        foreach (var index in observations.Take(Math.Max(0, observations.Count - 1)))
        {
            if (total <= ScratchpadBudget)
            {
                break;
            }

            var text = scratchpad[index].Text;
            var nameEnd = text.IndexOf('>');
            var body = text[(nameEnd + 1)..^"</observation>".Length];
            var replacement = $"{text[..(nameEnd + 1)]}Removed to save space. Started with: {body[..Math.Min(80, body.Length)]}</observation>";
            total -= Tokens(text) - Tokens(replacement);
            scratchpad[index] = new ChatMessage(ChatRole.User, replacement);
        }

        return scratchpad;
    }

    private static string Observation(string tool, string callId, string body)
    {
        // Neutralize observation delimiters; ordinary source code remains literal.
        body = Regex.Replace(body, @"<(?=/?observation(?:[\s/>]|$))", "&lt;", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return $"<observation tool=\"{SecurityElement.Escape(tool)}\" source=\"{SecurityElement.Escape(callId)}\" trusted=\"false\">{body}</observation>";
    }

    private static string StepJson(string thought, string action, IDictionary<string, object?>? args, string? answer)
    {
        var step = new JsonObject { ["thought"] = thought, ["action"] = action };
        if (answer is null)
        {
            var argsNode = new JsonObject();
            foreach (var (key, value) in args ?? new Dictionary<string, object?>())
            {
                argsNode[key] = value is null ? null : JsonSerializer.SerializeToNode(value);
            }

            step["args"] = argsNode;
        }
        else
        {
            step["answer"] = answer;
        }

        return step.ToJsonString();
    }

    private static int Tokens(string text) => (text.Length + CharsPerToken - 1) / CharsPerToken;
}
