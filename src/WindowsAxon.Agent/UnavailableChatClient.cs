using Microsoft.Extensions.AI;

namespace WindowsAxon.Agent;

/// <summary>A chat client for code that needs one but never calls the model, such as skill loading.</summary>
public sealed class UnavailableChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
                                               ChatOptions? options = null,
                                               CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This chat client does not call a model.");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
                                                                          ChatOptions? options = null,
                                                                          CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This chat client does not call a model.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
