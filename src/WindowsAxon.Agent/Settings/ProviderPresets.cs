namespace WindowsAxon.Agent.Settings;

/// <summary>Default endpoints for hosted OpenAI-compatible providers.</summary>
public static class ProviderPresets
{
    public static IReadOnlyDictionary<string, EndpointSettings> All { get; } = new Dictionary<string, EndpointSettings>
    {
        ["Nvidia"] = new()
        {
            BaseUrl = "https://integrate.api.nvidia.com/v1",
            Models = ["openai/gpt-oss-20b", "nvidia/nemotron-3.5-bolt-e4b"],
        },
        ["Google"] = new()
        {
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai",
            Models = ["gemini-3.8-flash", "gemini-flash-latest"],
        },
        ["Groq"] = new()
        {
            BaseUrl = "https://api.groq.com/openai/v1",
            Models = ["openai/gpt-oss-120b", "openai/gpt-oss-20b", "qwen/qwen3.8-27b"],
        },
    };

    /// <summary>Returns the configured endpoints, plus each preset that is not configured.</summary>
    public static Dictionary<string, EndpointSettings> MergeWith(IReadOnlyDictionary<string, EndpointSettings> endpoints)
    {
        var merged = new Dictionary<string, EndpointSettings>(endpoints, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, preset) in All)
        {
            merged.TryAdd(name, preset);
        }

        return merged;
    }
}
