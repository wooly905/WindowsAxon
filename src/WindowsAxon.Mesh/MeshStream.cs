using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace WindowsAxon.Mesh;

/// <summary>Bounded-memory chunked binary stream backed by MeshEnvelope framing.</summary>
internal sealed class MeshStream : IMeshStream
{
    private const int ChannelCapacity = 16;

    private readonly Func<MeshEnvelope, CancellationToken, Task> _sendEnvelope;
    private readonly Func<ValueTask>? _onDispose;
    private readonly Channel<byte[]> _incomingChunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(ChannelCapacity)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly TaskCompletionSource<StreamCloseResult> _remoteClose = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private long _sendSequence;
    private long _expectedRecvSequence;
    private int _writeCompleted;
    private int _disposed;
    private JsonElement _remoteCloseMetadata = MeshEnvelope.EmptyObject;

    public MeshStream(string streamId,
                      string topic,
                      string remoteNode,
                      MeshCallContext context,
                      JsonElement openMetadata,
                      JsonElement acceptMetadata,
                      Func<MeshEnvelope, CancellationToken, Task> sendEnvelope,
                      Func<ValueTask>? onDispose = null)
    {
        StreamId = streamId;
        Topic = topic;
        RemoteNode = remoteNode;
        Context = context;
        OpenMetadata = openMetadata;
        AcceptMetadata = acceptMetadata;
        _sendEnvelope = sendEnvelope;
        _onDispose = onDispose;
    }

    public string StreamId { get; }

    public string Topic { get; }

    public string RemoteNode { get; }

    public MeshCallContext Context { get; }

    public JsonElement OpenMetadata { get; }

    public JsonElement AcceptMetadata { get; }

    public JsonElement RemoteCloseMetadata => _remoteCloseMetadata;

    public async Task WriteChunkAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _writeCompleted) != 0)
        {
            throw new InvalidOperationException("Cannot write after the stream has been closed.");
        }

        var seq = Interlocked.Increment(ref _sendSequence) - 1;
        var base64 = Convert.ToBase64String(data.Span);
        await _sendEnvelope(MeshEnvelope.Create(MeshMessageTypes.StreamChunk,
                                                new StreamChunkPayload(StreamId, seq, base64)),
                            cancellationToken);
    }

    public async IAsyncEnumerable<byte[]> ReadChunksAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in _incomingChunks.Reader.ReadAllAsync(cancellationToken))
        {
            yield return chunk;
        }
    }

    public async Task<StreamCloseResult> CompleteWriteAsync(JsonElement metadata, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _writeCompleted, 1) == 0)
        {
            await _sendEnvelope(MeshEnvelope.Create(MeshMessageTypes.StreamClose,
                                                    new StreamClosePayload(StreamId, true, metadata)),
                                cancellationToken);
        }

        return await _remoteClose.Task.WaitAsync(cancellationToken);
    }

    public async Task FailAsync(string error, CancellationToken cancellationToken = default)
    {
        var message = string.IsNullOrWhiteSpace(error) ? "Stream failed." : error;
        if (Interlocked.Exchange(ref _writeCompleted, 1) == 0)
        {
            try
            {
                await _sendEnvelope(MeshEnvelope.Create(MeshMessageTypes.StreamClose,
                                                        new StreamClosePayload(StreamId, false, MeshEnvelope.EmptyObject, message)),
                                    cancellationToken);
            }
            catch (Exception)
            {
            }
        }

        var failure = new StreamCloseResult(false, MeshEnvelope.EmptyObject, message);
        _remoteClose.TrySetResult(failure);
        _incomingChunks.Writer.TryComplete(new IOException(message));
    }

    public async Task DeliverChunkAsync(StreamChunkPayload chunk, CancellationToken cancellationToken)
    {
        if (chunk.Sequence != _expectedRecvSequence)
        {
            OnTransportFailure($"Out-of-order stream chunk (expected {_expectedRecvSequence}, got {chunk.Sequence}).");
            return;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(chunk.DataBase64);
        }
        catch (FormatException)
        {
            OnTransportFailure("Invalid base64 payload in stream chunk.");
            return;
        }

        _expectedRecvSequence++;
        try
        {
            await _incomingChunks.Writer.WriteAsync(bytes, cancellationToken);
        }
        catch (ChannelClosedException)
        {
        }
    }

    public void DeliverClose(StreamClosePayload close)
    {
        _remoteCloseMetadata = close.Metadata;
        var result = new StreamCloseResult(close.Success, close.Metadata, close.Error);
        _remoteClose.TrySetResult(result);
        if (close.Success)
        {
            _incomingChunks.Writer.TryComplete();
        }
        else
        {
            _incomingChunks.Writer.TryComplete(new IOException(close.Error ?? "Remote peer aborted the stream."));
        }
    }

    public void OnTransportFailure(string reason)
    {
        _remoteClose.TrySetResult(new StreamCloseResult(false, MeshEnvelope.EmptyObject, reason));
        _incomingChunks.Writer.TryComplete(new IOException(reason));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _writeCompleted) == 0)
        {
            await FailAsync("Stream disposed before completion.", CancellationToken.None);
        }

        _incomingChunks.Writer.TryComplete();
        if (_onDispose is not null)
        {
            await _onDispose();
        }
    }
}
