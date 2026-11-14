using System.Text;
using System.Text.Json;

namespace WindowsAxon.Agent.Tools;

internal static class ToolCallIdentity
{
    internal static string Fingerprint(string tool, IDictionary<string, object?>? arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(tool);
            WriteCanonical(writer, JsonSerializer.SerializeToElement(arguments ?? new Dictionary<string, object?>()));
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();

            foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }

            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();

            foreach (var item in element.EnumerateArray())
            {
                WriteCanonical(writer, item);
            }

            writer.WriteEndArray();
        }
        else
        {
            element.WriteTo(writer);
        }
    }
}
