using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace WindowsAxon.Mesh;

/// <summary>Mutually authenticated, AES-256-GCM encrypted JSON envelope channel over a TCP stream.</summary>
internal sealed class EncryptedJsonChannel : IDisposable
{
    private const int MaxMessageBytes = 4 * 1024 * 1024;
    private const byte ProtocolVersion = 1;
    private const int NonceBytes = 32;
    private const int HmacBytes = 32;
    private const int GcmNonceBytes = 12;
    private const int GcmTagBytes = 16;
    private const int HeaderBytes = 4;

    private static readonly byte[] Magic = [(byte)'W', (byte)'X', (byte)'N', (byte)'P'];
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly Stream _stream;
    private readonly AesGcm _sendCipher;
    private readonly AesGcm _recvCipher;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ulong _sendSeq;
    private ulong _recvSeq;
    private bool _disposed;

    private EncryptedJsonChannel(Stream stream, byte[] sendKey, byte[] recvKey)
    {
        _stream = stream;
        _sendCipher = new AesGcm(sendKey, GcmTagBytes);
        _recvCipher = new AesGcm(recvKey, GcmTagBytes);
    }

    public static async Task<EncryptedJsonChannel> ConnectClientAsync(Stream stream,
                                                                      PairKeyCrypto crypto,
                                                                      CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(HandshakeTimeout);
        var ct = timeoutCts.Token;

        var clientNonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var clientTag = crypto.ComputeClientHelloTag(clientNonce);

        var hello = new byte[Magic.Length + 1 + NonceBytes + HmacBytes];
        Magic.CopyTo(hello, 0);
        hello[Magic.Length] = ProtocolVersion;
        clientNonce.CopyTo(hello, Magic.Length + 1);
        clientTag.CopyTo(hello, Magic.Length + 1 + NonceBytes);

        await stream.WriteAsync(hello, ct);
        await stream.FlushAsync(ct);

        var reply = new byte[NonceBytes + HmacBytes];
        await stream.ReadExactlyAsync(reply, ct);

        var serverNonce = reply.AsSpan(0, NonceBytes);
        var serverTag = reply.AsSpan(NonceBytes, HmacBytes);
        var expectedServerTag = crypto.ComputeServerHelloTag(clientNonce, serverNonce);
        if (!CryptographicOperations.FixedTimeEquals(serverTag, expectedServerTag))
        {
            throw new CryptographicException("Peer failed pair-key authentication.");
        }

        var (c2sKey, s2cKey) = crypto.DeriveSessionKeys(clientNonce, serverNonce);
        try
        {
            return new EncryptedJsonChannel(stream, sendKey: c2sKey, recvKey: s2cKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(c2sKey);
            CryptographicOperations.ZeroMemory(s2cKey);
        }
    }

    public static async Task<EncryptedJsonChannel> AcceptServerAsync(Stream stream,
                                                                     PairKeyCrypto crypto,
                                                                     CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(HandshakeTimeout);
        var ct = timeoutCts.Token;

        var hello = new byte[Magic.Length + 1 + NonceBytes + HmacBytes];
        await stream.ReadExactlyAsync(hello, ct);

        if (!hello.AsSpan(0, Magic.Length).SequenceEqual(Magic) || hello[Magic.Length] != ProtocolVersion)
        {
            throw new InvalidDataException("Invalid P2P handshake header.");
        }

        var clientNonce = hello.AsSpan(Magic.Length + 1, NonceBytes);
        var clientTag = hello.AsSpan(Magic.Length + 1 + NonceBytes, HmacBytes);
        var expectedClientTag = crypto.ComputeClientHelloTag(clientNonce);
        if (!CryptographicOperations.FixedTimeEquals(clientTag, expectedClientTag))
        {
            throw new CryptographicException("Peer failed pair-key authentication.");
        }

        var serverNonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var serverTag = crypto.ComputeServerHelloTag(clientNonce, serverNonce);
        var reply = new byte[NonceBytes + HmacBytes];
        serverNonce.CopyTo(reply, 0);
        serverTag.CopyTo(reply, NonceBytes);

        var (c2sKey, s2cKey) = crypto.DeriveSessionKeys(clientNonce, serverNonce);
        try
        {
            await stream.WriteAsync(reply, ct);
            await stream.FlushAsync(ct);
            return new EncryptedJsonChannel(stream, sendKey: s2cKey, recvKey: c2sKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(c2sKey);
            CryptographicOperations.ZeroMemory(s2cKey);
        }
    }

    public async Task SendAsync(MeshEnvelope envelope, CancellationToken cancellationToken)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(envelope, MeshEnvelope.JsonOptions);
        if (plaintext.Length > MaxMessageBytes)
        {
            throw new InvalidOperationException("Mesh message exceeded maximum allowed size.");
        }

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sendSeq == ulong.MaxValue)
            {
                throw new InvalidOperationException("Channel sequence limit exceeded.");
            }

            var seq = _sendSeq++;
            var nonce = BuildNonce(seq);
            var aad = BuildAad(seq);

            var frame = new byte[HeaderBytes + GcmTagBytes + plaintext.Length];
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, HeaderBytes), plaintext.Length);
            var tagSpan = frame.AsSpan(HeaderBytes, GcmTagBytes);
            var cipherSpan = frame.AsSpan(HeaderBytes + GcmTagBytes, plaintext.Length);

            _sendCipher.Encrypt(nonce, plaintext, cipherSpan, tagSpan, aad);

            await _stream.WriteAsync(frame, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<MeshEnvelope?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var header = new byte[HeaderBytes];
        var read = await ReadHeaderOrEofAsync(header, cancellationToken);
        if (!read)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > MaxMessageBytes)
        {
            throw new InvalidDataException($"Invalid encrypted frame length: {length}.");
        }

        var body = new byte[GcmTagBytes + length];
        await _stream.ReadExactlyAsync(body, cancellationToken);

        if (_recvSeq == ulong.MaxValue)
        {
            throw new InvalidOperationException("Channel sequence limit exceeded.");
        }

        var seq = _recvSeq++;
        var nonce = BuildNonce(seq);
        var aad = BuildAad(seq);

        var plaintext = new byte[length];
        _recvCipher.Decrypt(nonce,
                            body.AsSpan(GcmTagBytes, length),
                            body.AsSpan(0, GcmTagBytes),
                            plaintext,
                            aad);

        return JsonSerializer.Deserialize<MeshEnvelope>(plaintext, MeshEnvelope.JsonOptions);
    }

    private async Task<bool> ReadHeaderOrEofAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await _stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (count == 0)
            {
                if (offset == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("Connection closed mid-frame.");
            }

            offset += count;
        }

        return true;
    }

    private static byte[] BuildNonce(ulong seq)
    {
        var nonce = new byte[GcmNonceBytes];
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4, 8), seq);
        return nonce;
    }

    private static byte[] BuildAad(ulong seq)
    {
        var aad = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(aad, seq);
        return aad;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sendCipher.Dispose();
        _recvCipher.Dispose();
        _sendLock.Dispose();
        _stream.Dispose();
    }
}
