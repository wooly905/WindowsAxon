using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;

namespace WindowsAxon.Mesh;

/// <summary>Thread-safe JSON envelope framing over a WebSocket.</summary>
public sealed class WebSocketJsonChannel(WebSocket socket) : IDisposable
{
    private const int MaxMessageBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public WebSocketState State => socket.State;

    public async Task SendAsync(MeshEnvelope envelope, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, MeshEnvelope.JsonOptions);
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                throw new WebSocketException("WebSocket is not open.");
            }

            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<MeshEnvelope?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var stream = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                if (stream.Length + result.Count > MaxMessageBytes)
                {
                    throw new InvalidOperationException("Mesh message exceeded maximum allowed size.");
                }

                stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    break;
                }
            }

            if (stream.Length == 0)
            {
                return null;
            }

            stream.Position = 0;
            return await JsonSerializer.DeserializeAsync<MeshEnvelope>(stream, MeshEnvelope.JsonOptions, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken cancellationToken)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(status, description, cancellationToken);
            }
            catch (WebSocketException)
            {
            }
            catch (OperationCanceledException)
            {
            }
            // The connection loop can dispose the socket after the state check.
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose() => _sendLock.Dispose();
}

