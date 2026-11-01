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
    private readonly bool _detected;

    private EndpointFactory(ModelSettings settings, ISecretStore secrets, bool detected) : this(settings, secrets)
    {
        _detected = detected;
    }

    public ModelSettings Settings { get; } = settings;

    public static void ClearApiDetectionCache() => EndpointApiDetector.Shared.Invalidate();

    public async Task<EndpointFactory> ResolveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResolveKey(Settings.ChatEndpoint);
        var automatic = Settings.ChatEndpoint.Api == EndpointApi.Automatic;
        var api = automatic
            ? await EndpointApiDetector.Shared.DetectAsync(Settings,
                                                           candidate => CreateProtocolClient(candidate, automatic: true),
                                                           cancellationToken)
            : Settings.ChatEndpoint.Api;
        var resolved = Settings with
        {
            Endpoints = new Dictionary<string, EndpointSettings>(Settings.Endpoints)
            {
                [Settings.Chat.Endpoint] = Settings.ChatEndpoint with { Api = api }
            },
            Chat = EffectiveChat(api, automatic)
        };

        return new EndpointFactory(resolved, secrets, automatic);
    }

    public IChatClient CreateChatClient()
    {
        if (Settings.ChatEndpoint.Api == EndpointApi.Automatic)
        {
            throw new InvalidOperationException("Resolve the automatic API with ResolveAsync before creating a client.");
        }

        return CreateProtocolClient(Settings.ChatEndpoint.Api, _detected);
    }

    private ChatSettings EffectiveChat(EndpointApi api, bool automatic) => Settings.Chat with
    {
        StepMode = automatic || api == EndpointApi.Responses ? StepMode.Native : Settings.Chat.StepMode,
        ReasoningEffortWhenThinkingOff = (automatic || Settings.IsChatRemote || api == EndpointApi.Responses)
            && Settings.Chat.ReasoningEffortWhenThinkingOff == "none"
            ? null : Settings.Chat.ReasoningEffortWhenThinkingOff
    };

    private IChatClient CreateProtocolClient(EndpointApi api, bool automatic)
    {
        var endpoint = Settings.ChatEndpoint;
        var client = new OpenAIClient(new ApiKeyCredential(ResolveKey(endpoint) ?? "none"), // the SDK needs a value for servers without a key
            new OpenAIClientOptions { Endpoint = new Uri(endpoint.BaseUrl), NetworkTimeout = TimeSpan.FromMinutes(5) });
        var useResponses = api == EndpointApi.Responses;
        IChatClient chat = useResponses
            ? client.GetResponsesClient().AsIChatClient(Settings.Chat.Model)
            : client.GetChatClient(Settings.Chat.Model).AsIChatClient();
        if (useResponses || automatic)
        {
            chat = new ConfigureOptionsChatClient(chat,
                                                  options =>
                                                  {
                                                      options.Temperature = null;

                                                      if (!useResponses)
                                                      {
                                                          return;
                                                      }

                                                      var previous = options.RawRepresentationFactory;
                                                      options.RawRepresentationFactory = model =>
                                                      {
                                                          var raw = previous?.Invoke(model) as CreateResponseOptions ?? new CreateResponseOptions();
                                                          raw.StoredOutputEnabled = false; // Routers need full history rather than previous_response_id.
                                                          return raw;
                                                      };
                                                  });
        }

        var chatSettings = EffectiveChat(api, automatic);

        return chatSettings.Thinking || chatSettings.ReasoningEffortWhenThinkingOff is null
            ? chat
            : new ReasoningEffortChatClient(chat, chatSettings.ReasoningEffortWhenThinkingOff);
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
