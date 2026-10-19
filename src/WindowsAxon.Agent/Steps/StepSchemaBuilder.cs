using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsAxon.Agent.Settings;
using Microsoft.Extensions.AI;

namespace WindowsAxon.Agent.Steps;

/// <summary>Builds the JSON schema of one step and the compact tool list for the system message.</summary>
public static class StepSchemaBuilder
{
    public const string FinalAnswer = "final_answer";
    public const string AskUser = "ask_user";

    public static JsonElement Build(IReadOnlyList<AIFunctionDeclaration> tools, ArgsSchemaMode mode)
    {
        JsonObject schema = mode == ArgsSchemaMode.PerTool ? BuildPerTool(tools) : BuildFree(tools);
        return JsonSerializer.SerializeToElement(schema);
    }

    public static string DescribeTools(IReadOnlyList<AIFunctionDeclaration> tools)
    {
        var text = new StringBuilder();

        foreach (var tool in tools)
        {
            var parameters = new List<string>();
            var schema = tool.JsonSchema;
            var required = schema.TryGetProperty("required", out var r)
                ? r.EnumerateArray().Select(e => e.GetString()).ToHashSet()
                : [];

            if (schema.TryGetProperty("properties", out var properties))
            {
                foreach (var property in properties.EnumerateObject())
                {
                    var type = property.Value.TryGetProperty("type", out var t)
                        && t.ValueKind == JsonValueKind.String ? t.GetString() : "any";
                    var optional = required.Contains(property.Name) ? "" : "?";
                    parameters.Add($"{property.Name}{optional}: {type}");
                }
            }

            // enable_tools lists its groups after the first sentence.
            var description = tool.Name == "enable_tools"
                ? tool.Description?.Trim() ?? ""
                : FirstSentence(tool.Description);
            text.Append("- ").Append(tool.Name).Append('(').Append(string.Join(", ", parameters)).Append("): ")
                .Append(description).Append('\n');
        }

        return text.ToString();
    }

    private static JsonObject BuildPerTool(IReadOnlyList<AIFunctionDeclaration> tools)
    {
        var branches = new JsonArray();
        foreach (var tool in tools)
        {
            branches.Add(new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["thought"] = new JsonObject { ["type"] = "string" },
                    ["action"] = new JsonObject { ["const"] = tool.Name },
                    ["args"] = ArgsSchema(tool),
                },
                ["required"] = new JsonArray("thought", "action", "args"),
                ["additionalProperties"] = false,
            });
        }

        branches.Add(new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["thought"] = new JsonObject { ["type"] = "string" },
                ["action"] = new JsonObject { ["enum"] = new JsonArray(FinalAnswer, AskUser) },
                ["answer"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray("thought", "action", "answer"),
            ["additionalProperties"] = false,
        });

        return new JsonObject { ["anyOf"] = branches };
    }

    private static JsonObject BuildFree(IReadOnlyList<AIFunctionDeclaration> tools)
    {
        var actions = new JsonArray();
        foreach (var tool in tools)
        {
            actions.Add(tool.Name);
        }

        actions.Add(FinalAnswer);
        actions.Add(AskUser);

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["thought"] = new JsonObject { ["type"] = "string" },
                ["action"] = new JsonObject { ["type"] = "string", ["enum"] = actions },
                ["args"] = new JsonObject { ["type"] = "object" },
                ["answer"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray("thought", "action"),
        };
    }

    private static JsonObject ArgsSchema(AIFunctionDeclaration tool)
    {
        var node = JsonNode.Parse(tool.JsonSchema.GetRawText())?.AsObject() ?? [];
        node.Remove("title");
        node.Remove("description");
        node.Remove("$schema");
        node["type"] = "object";

        if (!node.ContainsKey("properties"))
        {
            node["properties"] = new JsonObject();
        }

        return node;
    }

    private static string FirstSentence(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return "";
        }

        var end = description.IndexOf(". ", StringComparison.Ordinal);
        return end < 0 ? description.Trim() : description[..(end + 1)].Trim();
    }
}
