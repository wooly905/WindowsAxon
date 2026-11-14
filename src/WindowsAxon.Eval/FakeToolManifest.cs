using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsAxon.Agent.Tools;

namespace WindowsAxon.Eval;

public sealed record FakeToolSpec(string Name,
                                  string Description,
                                  string Group,
                                  RiskLevel Risk,
                                  bool PolicyDriven,
                                  JsonElement Parameters,
                                  string Output,
                                  bool AlwaysInclude = false,
                                  bool Outbound = false);

public sealed record ToolCallRecord(string Name, IReadOnlyDictionary<string, string> Args);

public static class FakeToolManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static IReadOnlyList<FakeToolSpec> Load(string path) =>
        JsonSerializer.Deserialize<List<FakeToolSpec>>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"{path} has no tools.");

    public static ToolCatalog BuildCatalog(IReadOnlyList<FakeToolSpec> specs,
                                           IReadOnlyDictionary<string, string> outputs,
                                           List<ToolCallRecord> calls)
    {
        var catalog = new ToolCatalog();
        foreach (var spec in specs)
        {
            var output = outputs.GetValueOrDefault(spec.Name, spec.Output);
            catalog.Add(new FakeAIFunction(spec, output, calls),
                        spec.Group,
                        spec.Risk,
                        spec.PolicyDriven,
                        alwaysInclude: spec.AlwaysInclude,
                        outbound: spec.Outbound);
        }

        return catalog;
    }
}
