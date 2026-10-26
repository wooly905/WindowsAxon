using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;
using ChatCompletionOptions = OpenAI.Chat.ChatCompletionOptions;
using ChatReasoningEffortLevel = OpenAI.Chat.ChatReasoningEffortLevel;

namespace WindowsAxon.Agent.Settings;

public sealed class EndpointFactory(ModelSettings settings, ISecretStore secrets)
{
    private const string CredentialPrefix = "credential:";

    public ModelSettings Settings { get; } = settings;

    public IChatClient CreateChatClient()
    {
        var endpoint = Settings.ChatEndpoint;
        var client = new OpenAIClient(new ApiKeyCredential(ResolveKey(endpoint) ?? "none"), // the SDK needs a value for servers without a key
            new OpenAIClientOptions { Endpoint = new Uri(endpoint.BaseUrl), NetworkTimeout = TimeSpan.FromMinutes(5) });
        var useResponses = Settings.UsesResponsesApi;
        IChatClient chat = useResponses
            ? client.GetResponsesClient().AsIChatClient(Settings.Chat.Model)
            : client.GetChatClient(Settings.Chat.Model).AsIChatClient();
        if (useResponses)
        {
            chat = new ConfigureOptionsChatClient(chat,
                                                  options =>
                                                  {
                                                      var previous = options.RawRepresentationFactory;
                                                      options.RawRepresentationFactory = model =>
                                                      {
                                                          var raw = previous?.Invoke(model) as CreateResponseOptions ?? new CreateResponseOptions();
                                                          raw.StoredOutputEnabled = false; // Routers need full history rather than previous_response_id.
                                                          return raw;
                                                      };
                                                  });
        }

        return Settings.Chat.Thinking || Settings.Chat.ReasoningEffortWhenThinkingOff is null
            ? chat
            : new ReasoningEffortChatClient(chat, Settings.Chat.ReasoningEffortWhenThinkingOff);
    }

    internal string? ResolveKey(EndpointSettings endpoint)
    {
        if (string.IsNullOrEmpty(endpoint.ApiKey))
        {
            return null;
        }

        if (!endpoint.ApiKey.StartsWith(CredentialPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ApiKey must be a credential reference (credential:<name>), not a key.");
        }

        if (!EndpointRules.MaySendApiKey(endpoint.BaseUrl))
        {
            throw new InvalidOperationException("WindowsAxon does not send an API key over http to a non-loopback address. Use https.");
        }

        return secrets.Read(endpoint.ApiKey[CredentialPrefix.Length..]);
    }
}

/// <summary>Sets reasoning_effort on every request, to turn off the thinking mode of the model.</summary>
public sealed class ReasoningEffortChatClient(IChatClient inner, string effort) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
                                                        ChatOptions? options = null,
                                                        CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, Apply(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
                                                                                   ChatOptions? options = null,
                                                                                   CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, Apply(options), cancellationToken);

    private ChatOptions Apply(ChatOptions? options)
    {
        var result = options?.Clone() ?? new ChatOptions();
        var previous = result.RawRepresentationFactory;
        result.RawRepresentationFactory = client =>
        {
            if (client.GetService<ResponsesClient>() is not null)
            {
                var responseOptions = previous?.Invoke(client) as CreateResponseOptions ?? new CreateResponseOptions();
                responseOptions.ReasoningOptions = new ResponseReasoningOptions
                {
                    ReasoningEffortLevel = new ResponseReasoningEffortLevel(effort),
                };
                return responseOptions;
            }
            var raw = previous?.Invoke(client) as ChatCompletionOptions ?? new ChatCompletionOptions();
            raw.ReasoningEffortLevel = new ChatReasoningEffortLevel(effort);
            return raw;
        };
        return result;
    }
}
