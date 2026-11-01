using System.Text.Json;
using WindowsAxon.Agent.Steps;
using Microsoft.Extensions.AI;

namespace WindowsAxon.Eval;

/// <summary>A tool with the real name and schema and a fixed output. Results do not depend on the computer.</summary>
public sealed class FakeAIFunction(FakeToolSpec spec, string output, List<ToolCallRecord> calls) : AIFunction
{
    public override string Name => spec.Name;

    public override string Description => spec.Description;

    public override JsonElement JsonSchema => spec.Parameters;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments,
                                                          CancellationToken cancellationToken)
    {
        lock (calls)
        {
            calls.Add(new ToolCallRecord(spec.Name,
                                         arguments.ToDictionary(a => a.Key,
                                                                a => TranscriptWriter.ObservationText(a.Value))));
        }

        return ValueTask.FromResult<object?>(output);
    }
}
