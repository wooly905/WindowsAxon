using System.Text.Json;
using WindowsAxon.Agent.Settings;
using WindowsAxon.Eval;

// Usage: WindowsAxon.Eval --config <settings.json> [--cases eval/cases] [--tools eval/tools.json] [--skills skills]
//        [--args-schema PerTool|Free] [--tool-selection Groups|All|SubAgents] [--docs] [--small-model]
//        [--out eval/results]
var options = ParseArgs(args);
if (!options.TryGetValue("config", out var configPath))
{
    Console.Error.WriteLine("Missing --config <settings.json> (a file with a \"Models\" section).");
    return 2;
}

var settings = ModelSettings.Load(File.ReadAllText(configPath));
if (options.TryGetValue("args-schema", out var argsSchema))
{
    settings = settings with
    {
        Chat = settings.Chat with
        {
            ArgsSchema = Enum.Parse<ArgsSchemaMode>(argsSchema, ignoreCase: true)
        }
    };
}

if (options.TryGetValue("tool-selection", out var toolSelection))
{
    settings = settings with
    {
        Chat = settings.Chat with
        {
            ToolSelection = Enum.Parse<ToolSelectionMode>(toolSelection, ignoreCase: true)
        }
    };
}

var evalRoot = Path.GetDirectoryName(Path.GetFullPath(options.GetValueOrDefault("tools", "eval/tools.json")))!;
var factory = new EndpointFactory(settings, new EnvironmentSecretStore());
var runner = new EvalRunner(factory.CreateChatClient,
                            settings.Chat,
                            FakeToolManifest.Load(options.GetValueOrDefault("tools", "eval/tools.json")),
                            Path.GetFullPath(options.GetValueOrDefault("skills", "skills")),
                            evalRoot,
                            docsAvailable: options.ContainsKey("docs"));

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var cases = EvalCase.LoadFolder(options.GetValueOrDefault("cases", "eval/cases"));
var summary = await runner.RunAllAsync(cases, cts.Token);

foreach (var r in summary.Results)
{
    Console.WriteLine($"{(r.Success ? "PASS" : "FAIL")} {r.Id,-12} steps={r.Steps,2} invalid={r.InvalidSteps} {r.Seconds,6:F1}s {string.Join("; ", r.Failures)}");
}

var smallModel = options.ContainsKey("small-model");
var passed = summary.PassesM1Gate(smallModel);
Console.WriteLine();
Console.WriteLine($"Model {settings.Chat.Model} ({settings.Chat.StepMode}, args {settings.Chat.ArgsSchema}, tools {settings.Chat.ToolSelection}), {summary.Cases} cases");
Console.WriteLine($"Selection {summary.SelectionAccuracy:P0}  First tool {summary.FirstToolAccuracy:P0}  Success {summary.TaskSuccess:P0}  Invalid steps {summary.InvalidStepRate:P1}  Unapproved {summary.Unapproved}");
Console.WriteLine($"Average steps {summary.AverageSteps:F1}  Average time {summary.AverageSeconds:F1}s  Average main context {summary.AverageMainContextTokens:F0} tokens (recorded, not a gate)");
Console.WriteLine($"M1 gate: {(passed ? "PASS" : "FAIL")}");

var outFolder = options.GetValueOrDefault("out", "eval/results");
Directory.CreateDirectory(outFolder);
var outFile = Path.Combine(outFolder, ReportFileName.For(settings.Chat.Model, DateTime.Now));
File.WriteAllText(outFile, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Report: {outFile}");
return passed ? 0 : 1;

static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var key = args[i][2..];
        var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
        result[key] = hasValue ? args[++i] : "true";
    }

    return result;
}

/// <summary>For the evaluation CLI only: reads WINDOWSAXON_KEY_&lt;NAME&gt; (name upper case, non-letters as _). The app uses Credential Manager.</summary>
internal sealed class EnvironmentSecretStore : ISecretStore
{
    public string? Read(string name) =>
        Environment.GetEnvironmentVariable("WINDOWSAXON_KEY_" + new string(name.ToUpperInvariant()
        .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')
        .ToArray()));
}
