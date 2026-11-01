using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace WindowsAxon.Agent.Settings;

internal sealed class EndpointApiDetector(TimeProvider? timeProvider = null)
{
    private const string ProbeName = "windowsaxon_api_probe";
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(string Url, string Model, string? Credential), CacheEntry> _cache = new();
    private int _generation;

    internal static EndpointApiDetector Shared { get; } = new();

    internal void Invalidate() => Interlocked.Increment(ref _generation);

    internal async Task<EndpointApi> DetectAsync(ModelSettings settings,
                                                 Func<EndpointApi, IChatClient> createClient,
                                                 CancellationToken cancellationToken)
    {
        var endpoint = settings.ChatEndpoint;
        var key = (new Uri(endpoint.BaseUrl).AbsoluteUri.TrimEnd('/'), settings.Chat.Model, endpoint.ApiKey);
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var generation = Volatile.Read(ref _generation);

            if (_cache.TryGetValue(key, out var cached)
                && cached.Generation == generation && cached.Expires > _time.GetUtcNow())
            {
                return cached.Api;
            }

            var failures = new List<Exception>();

            foreach (var api in new[] { EndpointApi.Responses, EndpointApi.ChatCompletions })
            {
                try
                {
                    using var client = createClient(api);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(60));
                    await ProbeAsync(client, timeout.Token);
                    _cache[key] = new CacheEntry(api, _time.GetUtcNow().AddHours(1), generation);

                    return api;
                }
                catch (Exception error) when (error is CapabilityException
                    || error is ClientResultException requestError && IsCompatibilityError(requestError))
                {
                    failures.Add(error);
                }
            }

            throw new NotSupportedException("Neither Responses nor Chat Completions supports the required tool-calling flow for this endpoint and model.",
                                             new AggregateException(failures));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task ProbeAsync(IChatClient client, CancellationToken cancellationToken)
    {
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "", ProbeName, "Return a capability test token.")],
            ToolMode = ChatToolMode.RequireSpecific(ProbeName)
        };
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, $"Call {ProbeName} once with no arguments. Then reply with only its result.")
        };
        var response = await client.GetResponseAsync(messages, options, cancellationToken);
        var calls = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToArray();

        if (calls.Length != 1 || calls[0].Name != ProbeName || string.IsNullOrEmpty(calls[0].CallId)
            || calls[0].Arguments is { Count: > 0 })
        {
            throw new CapabilityException("The API did not return the requested probe tool call.");
        }

        var token = Guid.NewGuid().ToString("N");
        messages.AddRange(response.Messages);
        messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(calls[0].CallId, token)]));
        options.ToolMode = ChatToolMode.None;
        var result = await client.GetResponseAsync(messages, options, cancellationToken);

        if (!result.Text.Contains(token, StringComparison.Ordinal)
            || result.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Any())
        {
            throw new CapabilityException("The API did not complete the probe tool-result round trip.");
        }
    }

    internal static bool IsCompatibilityError(ClientResultException error)
    {
        if (error.Status is not (400 or 404 or 405 or 422 or 501))
        {
            return false;
        }

        var body = error.GetRawResponse()?.Content?.ToString();
        var message = error.Message;
        string? code = null;
        string? parameter = null;

        if (!string.IsNullOrEmpty(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);

                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("error", out var detail) && detail.ValueKind == JsonValueKind.Object)
                {
                    code = StringProperty(detail, "code");
                    parameter = StringProperty(detail, "param");
                    message = StringProperty(detail, "message") ?? message;
                }
            }
            catch (JsonException)
            {
                // An unsupported route may return an HTML error page.
            }
        }

        var unsupported = message.Contains("not supported", StringComparison.OrdinalIgnoreCase)
            || message.Contains("does not support", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
            || message.Contains("only supported", StringComparison.OrdinalIgnoreCase);
        var capability = unsupported && (parameter is "tools" or "tool_choice"
            || new[] { "endpoint", "responses", "chat/completions", "tool calling", "function calling" }
                .Any(term => message.Contains(term, StringComparison.OrdinalIgnoreCase)));

        if (code is "model_not_found" or "model_not_available" || parameter == "model" && !capability)
        {
            return false;
        }

        if (error.Status is 405 or 501)
        {
            return true;
        }

        if (code is "unsupported_endpoint" or "unsupported_api" || capability)
        {
            return true;
        }

        return error.Status == 404 && !message.Contains("model", StringComparison.OrdinalIgnoreCase);
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record CacheEntry(EndpointApi Api, DateTimeOffset Expires, int Generation);

    private sealed class CapabilityException(string message) : Exception(message);
}
