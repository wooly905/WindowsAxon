using System.Security.Cryptography;
using System.Text.Json;

namespace WindowsAxon.Mesh;

public sealed record FileTransferResult(string TargetNode,
                                        string FileName,
                                        long BytesTransferred,
                                        string SavedPath,
                                        string Sha256,
                                        string TraceId);

public sealed record ReceivedFileInfo(string CallerNode,
                                      string FileName,
                                      long SizeBytes,
                                      string SavedPath,
                                      string Sha256,
                                      string TraceId);

/// <summary>Pluggable peer-to-peer / hub-relayed file transfer service built on IMeshTransport streams.</summary>
public sealed class FileTransferService
{
    public const string TransferTopic = MeshServiceNames.FileTransfer;
    public const long MaxFileSizeBytes = 1024L * 1024 * 1024; // 1 GB
    private const int ChunkBytes = 256 * 1024;

    private readonly IMeshTransport _transport;
    private readonly string _receiveFolder;
    private readonly long _maxFileSizeBytes;

    public FileTransferService(IMeshTransport transport,
                               string? receiveFolder = null,
                               long maxFileSizeBytes = MaxFileSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(transport);

        _transport = transport;
        _receiveFolder = string.IsNullOrWhiteSpace(receiveFolder) ? DefaultReceiveFolder : Path.GetFullPath(receiveFolder);
        _maxFileSizeBytes = maxFileSizeBytes;

        _transport.RegisterStreamHandler(TransferTopic, HandleIncomingFileAsync);
    }

    public static string DefaultReceiveFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "WindowsAxon");

    public string ReceiveFolder => _receiveFolder;

    public event Action<ReceivedFileInfo>? FileReceived;

    public async Task<FileTransferResult> SendFileAsync(string targetNode,
                                                        string sourcePath,
                                                        MeshCallContext? context = null,
                                                        Action<long, long>? onProgress = null,
                                                        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetNode);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var fullPath = Path.GetFullPath(sourcePath);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"File not found: '{fullPath}'.", fullPath);
        }

        if (info.Length > _maxFileSizeBytes)
        {
            throw new ArgumentException($"File '{info.Name}' is too large ({info.Length:N0} bytes). Maximum allowed size is {_maxFileSizeBytes:N0} bytes.");
        }

        var safeName = SanitizeFileName(info.Name);
        var openMeta = JsonSerializer.SerializeToElement(new FileOfferMetadata(safeName, info.Length), MeshEnvelope.JsonOptions);

        await using var stream = await _transport.OpenStreamAsync(targetNode, TransferTopic, openMeta, context, cancellationToken);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long sent = 0;

        try
        {
            await using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes, useAsync: true);
            var buffer = new byte[ChunkBytes];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                hasher.AppendData(buffer, 0, read);
                await stream.WriteChunkAsync(buffer.AsMemory(0, read), cancellationToken);
                sent += read;
                onProgress?.Invoke(sent, info.Length);
            }

            var sha256 = Convert.ToHexStringLower(hasher.GetHashAndReset());
            var closeMeta = JsonSerializer.SerializeToElement(new FileSenderCloseMetadata(sent, sha256), MeshEnvelope.JsonOptions);
            var remoteClose = await stream.CompleteWriteAsync(closeMeta, cancellationToken);
            if (!remoteClose.Success)
            {
                throw new IOException(remoteClose.Error ?? $"Peer '{targetNode}' failed to save '{safeName}'.");
            }

            var receipt = remoteClose.Metadata.Deserialize<FileReceiverCloseMetadata>(MeshEnvelope.JsonOptions);
            return new FileTransferResult(targetNode,
                                          safeName,
                                          sent,
                                          receipt?.SavedPath ?? safeName,
                                          sha256,
                                          stream.Context.TraceId);
        }
        catch (Exception ex)
        {
            await stream.FailAsync(ex.Message, CancellationToken.None);
            throw;
        }
    }

    private async Task HandleIncomingFileAsync(IncomingStream incoming, CancellationToken cancellationToken)
    {
        var offer = incoming.Metadata.Deserialize<FileOfferMetadata>(MeshEnvelope.JsonOptions);
        if (offer is null || string.IsNullOrWhiteSpace(offer.FileName))
        {
            await incoming.RejectAsync("Invalid file transfer offer.", cancellationToken);
            return;
        }

        if (offer.FileSize < 0 || offer.FileSize > _maxFileSizeBytes)
        {
            await incoming.RejectAsync($"File exceeds maximum allowed size ({_maxFileSizeBytes:N0} bytes).", cancellationToken);
            return;
        }

        var safeName = SanitizeFileName(offer.FileName);
        Directory.CreateDirectory(_receiveFolder);

        await using var stream = await incoming.AcceptAsync(MeshEnvelope.EmptyObject, cancellationToken);
        var partPath = Path.Combine(_receiveFolder, $".{Guid.NewGuid():N}.part");
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long received = 0;

        try
        {
            await using (var output = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkBytes, useAsync: true))
            {
                await foreach (var chunk in stream.ReadChunksAsync(cancellationToken))
                {
                    received += chunk.Length;
                    if (received > _maxFileSizeBytes || received > offer.FileSize)
                    {
                        throw new InvalidDataException("Incoming file exceeded declared size.");
                    }

                    hasher.AppendData(chunk);
                    await output.WriteAsync(chunk, cancellationToken);
                }
            }

            var senderClose = stream.RemoteCloseMetadata.Deserialize<FileSenderCloseMetadata>(MeshEnvelope.JsonOptions);
            var actualSha = Convert.ToHexStringLower(hasher.GetHashAndReset());
            if (received != offer.FileSize
                || senderClose is null
                || senderClose.BytesSent != received
                || !string.Equals(actualSha, senderClose.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Incoming file failed size or SHA-256 integrity verification.");
            }

            var finalPath = ReserveUniqueDestinationPath(_receiveFolder, safeName);
            File.Move(partPath, finalPath, overwrite: false);

            var receipt = JsonSerializer.SerializeToElement(new FileReceiverCloseMetadata(finalPath, received, actualSha),
                                                            MeshEnvelope.JsonOptions);
            await stream.CompleteWriteAsync(receipt, cancellationToken);

            FileReceived?.Invoke(new ReceivedFileInfo(incoming.Context.CallerNode,
                                                      Path.GetFileName(finalPath),
                                                      received,
                                                      finalPath,
                                                      actualSha,
                                                      incoming.Context.TraceId));
        }
        catch (Exception ex)
        {
            TryDelete(partPath);
            await stream.FailAsync(ex.Message, CancellationToken.None);
        }
    }

    public static string SanitizeFileName(string rawName)
    {
        var nameOnly = Path.GetFileName(rawName.Trim());
        if (string.IsNullOrWhiteSpace(nameOnly) || nameOnly is "." or "..")
        {
            return "received-file";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new char[nameOnly.Length];
        for (var i = 0; i < nameOnly.Length; i++)
        {
            var ch = nameOnly[i];
            cleaned[i] = Array.IndexOf(invalid, ch) >= 0 || char.IsControl(ch) ? '_' : ch;
        }

        var result = new string(cleaned).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(result) ? "received-file" : result;
    }

    private static string ReserveUniqueDestinationPath(string folder, string fileName)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; i < 10_000; i++)
        {
            var numbered = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(numbered))
            {
                return numbered;
            }
        }

        return Path.Combine(folder, $"{stem}-{Guid.NewGuid():N}{ext}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record FileOfferMetadata(string FileName, long FileSize);

    private sealed record FileSenderCloseMetadata(long BytesSent, string Sha256);

    private sealed record FileReceiverCloseMetadata(string SavedPath, long BytesReceived, string Sha256);
}
