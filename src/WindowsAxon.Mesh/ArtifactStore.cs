using System.Security.Cryptography;
using System.Text.Json;

namespace WindowsAxon.Mesh;

public sealed record ArtifactMetadata(string ArtifactId,
                                      string FileName,
                                      long SizeBytes,
                                      string Sha256,
                                      DateTimeOffset CreatedUtc);

/// <summary>URI handle identifying an artifact hosted on a specific mesh node: artifact://&lt;node&gt;/&lt;id&gt;.</summary>
public static class ArtifactHandle
{
    public const string Scheme = "artifact://";

    public static string Format(string nodeName, string artifactId) =>
        $"{Scheme}{Uri.EscapeDataString(nodeName)}/{Uri.EscapeDataString(artifactId)}";

    public static bool TryParse(string? handle, out string nodeName, out string artifactId)
    {
        nodeName = "";
        artifactId = "";
        if (string.IsNullOrWhiteSpace(handle) || !handle.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = handle[Scheme.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1)
        {
            return false;
        }

        nodeName = Uri.UnescapeDataString(rest[..slash]);
        artifactId = Uri.UnescapeDataString(rest[(slash + 1)..]);
        return !string.IsNullOrWhiteSpace(nodeName)
               && !string.IsNullOrWhiteSpace(artifactId)
               && artifactId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    }
}

/// <summary>Local artifact store with 1 GB per-file limit and 72-hour automatic expiration.</summary>
public sealed class ArtifactStore
{
    public const long MaxArtifactBytes = 1024L * 1024 * 1024; // 1 GB
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(72);

    private readonly string _folder;
    private readonly TimeSpan _retention;

    public ArtifactStore(string? folder = null, TimeSpan? retention = null)
    {
        _folder = string.IsNullOrWhiteSpace(folder) ? DefaultFolder : Path.GetFullPath(folder);
        _retention = retention ?? DefaultRetention;
    }

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                       "WindowsAxon",
                                                       "artifacts");

    public string Folder => _folder;

    public async Task<ArtifactMetadata> PutFileAsync(string sourcePath,
                                                     string? fileName = null,
                                                     CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var fullSource = Path.GetFullPath(sourcePath);
        var info = new FileInfo(fullSource);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Source file does not exist.", fullSource);
        }

        if (info.Length > MaxArtifactBytes)
        {
            throw new ArgumentException($"Artifact exceeds the maximum size of {MaxArtifactBytes / (1024 * 1024)} MB.");
        }

        Directory.CreateDirectory(_folder);
        var id = Guid.NewGuid().ToString("N");
        var dataPath = DataPath(id);
        var tempPath = $"{dataPath}.part";
        var safeName = FileTransferService.SanitizeFileName(fileName ?? info.Name);

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        try
        {
            await using (var input = new FileStream(fullSource, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, useAsync: true))
            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, useAsync: true))
            {
                var buffer = new byte[256 * 1024];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > MaxArtifactBytes)
                    {
                        throw new InvalidOperationException("Artifact exceeded maximum allowed size.");
                    }

                    hasher.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            File.Move(tempPath, dataPath, overwrite: true);
            var metadata = new ArtifactMetadata(id,
                                                safeName,
                                                total,
                                                Convert.ToHexStringLower(hasher.GetHashAndReset()),
                                                DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(MetaPath(id),
                                         JsonSerializer.Serialize(metadata, MeshEnvelope.JsonOptions),
                                         cancellationToken);
            return metadata;
        }
        catch
        {
            TryDelete(tempPath);
            TryDelete(dataPath);
            throw;
        }
    }

    public bool TryGet(string artifactId, out ArtifactMetadata metadata, out string dataPath, DateTimeOffset? now = null)
    {
        metadata = null!;
        dataPath = "";
        if (string.IsNullOrWhiteSpace(artifactId) || !artifactId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return false;
        }

        var metaPath = MetaPath(artifactId);
        var binPath = DataPath(artifactId);
        if (!File.Exists(metaPath) || !File.Exists(binPath))
        {
            return false;
        }

        ArtifactMetadata? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ArtifactMetadata>(File.ReadAllText(metaPath), MeshEnvelope.JsonOptions);
        }
        catch (Exception)
        {
            return false;
        }

        if (parsed is null)
        {
            return false;
        }

        var current = now ?? DateTimeOffset.UtcNow;
        if (current - parsed.CreatedUtc > _retention)
        {
            TryDelete(metaPath);
            TryDelete(binPath);
            return false;
        }

        metadata = parsed;
        dataPath = binPath;
        return true;
    }

    public int PruneExpired(DateTimeOffset? now = null)
    {
        if (!Directory.Exists(_folder))
        {
            return 0;
        }

        var current = now ?? DateTimeOffset.UtcNow;
        var pruned = 0;

        foreach (var metaPath in Directory.EnumerateFiles(_folder, "*.meta.json"))
        {
            var expired = false;
            try
            {
                var meta = JsonSerializer.Deserialize<ArtifactMetadata>(File.ReadAllText(metaPath), MeshEnvelope.JsonOptions);
                expired = meta is null || current - meta.CreatedUtc > _retention;
            }
            catch (Exception)
            {
                expired = true;
            }

            if (expired)
            {
                var id = Path.GetFileName(metaPath)[..^".meta.json".Length];
                TryDelete(metaPath);
                TryDelete(DataPath(id));
                pruned++;
            }
        }

        foreach (var partPath in Directory.EnumerateFiles(_folder, "*.part"))
        {
            try
            {
                if (current - File.GetLastWriteTimeUtc(partPath) > TimeSpan.FromHours(1))
                {
                    TryDelete(partPath);
                }
            }
            catch (IOException)
            {
            }
        }

        return pruned;
    }

    private string DataPath(string id) => Path.Combine(_folder, $"{id}.bin");

    private string MetaPath(string id) => Path.Combine(_folder, $"{id}.meta.json");

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
}

/// <summary>Pluggable cross-node artifact fetch service built on IMeshTransport binary streams.</summary>
public sealed class ArtifactService
{
    public const string FetchTopic = MeshServiceNames.ArtifactFetch;
    private const int ChunkBytes = 256 * 1024;

    private readonly IMeshTransport _transport;
    private readonly ArtifactStore _store;

    public ArtifactService(IMeshTransport transport, ArtifactStore store)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(store);

        _transport = transport;
        _store = store;
        _transport.RegisterStreamHandler(FetchTopic, HandleIncomingFetchAsync);
    }

    public ArtifactStore Store => _store;

    public async Task<string> PublishFileAsync(string sourcePath, string? fileName = null, CancellationToken cancellationToken = default)
    {
        var meta = await _store.PutFileAsync(sourcePath, fileName, cancellationToken);
        return ArtifactHandle.Format(_transport.NodeName, meta.ArtifactId);
    }

    public async Task<ArtifactMetadata> FetchAsync(string handleUri,
                                                   string destinationPath,
                                                   MeshCallContext? context = null,
                                                   CancellationToken cancellationToken = default)
    {
        if (!ArtifactHandle.TryParse(handleUri, out var hostNode, out var artifactId))
        {
            throw new ArgumentException($"Invalid artifact handle '{handleUri}'.", nameof(handleUri));
        }

        var fullDest = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDest)!);

        var openMeta = JsonSerializer.SerializeToElement(new ArtifactFetchRequest(artifactId), MeshEnvelope.JsonOptions);
        await using var stream = await _transport.OpenStreamAsync(hostNode, FetchTopic, openMeta, context, cancellationToken);
        var meta = stream.AcceptMetadata.Deserialize<ArtifactMetadata>(MeshEnvelope.JsonOptions)
            ?? throw new InvalidDataException("Missing artifact metadata in stream accept.");
        if (meta.SizeBytes is < 0 or > ArtifactStore.MaxArtifactBytes)
        {
            await stream.FailAsync("Artifact exceeds maximum allowed size.", CancellationToken.None);
            throw new InvalidOperationException("Artifact exceeds maximum allowed size.");
        }

        var tempPath = $"{fullDest}.{Guid.NewGuid():N}.part";
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long received = 0;
        try
        {
            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkBytes, useAsync: true))
            {
                await foreach (var chunk in stream.ReadChunksAsync(cancellationToken))
                {
                    received += chunk.Length;
                    if (received > ArtifactStore.MaxArtifactBytes || received > meta.SizeBytes)
                    {
                        throw new InvalidDataException("Artifact stream exceeded declared size.");
                    }

                    hasher.AppendData(chunk);
                    await output.WriteAsync(chunk, cancellationToken);
                }
            }

            var actualSha = Convert.ToHexStringLower(hasher.GetHashAndReset());
            if (received != meta.SizeBytes || !string.Equals(actualSha, meta.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Artifact integrity check (size or SHA-256) failed.");
            }

            File.Move(tempPath, fullDest, overwrite: true);
            await stream.CompleteWriteAsync(MeshEnvelope.EmptyObject, cancellationToken);
            return meta;
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (IOException)
            {
            }

            await stream.FailAsync(ex.Message, CancellationToken.None);
            throw;
        }
    }

    private async Task HandleIncomingFetchAsync(IncomingStream incoming, CancellationToken cancellationToken)
    {
        var req = incoming.Metadata.Deserialize<ArtifactFetchRequest>(MeshEnvelope.JsonOptions);
        if (req is null || !_store.TryGet(req.ArtifactId, out var meta, out var dataPath))
        {
            await incoming.RejectAsync("Artifact not found or expired.", cancellationToken);
            return;
        }

        await using var stream = await incoming.AcceptAsync(JsonSerializer.SerializeToElement(meta, MeshEnvelope.JsonOptions),
                                                            cancellationToken);
        try
        {
            await using var input = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes, useAsync: true);
            var buffer = new byte[ChunkBytes];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await stream.WriteChunkAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            await stream.CompleteWriteAsync(MeshEnvelope.EmptyObject, cancellationToken);
        }
        catch (Exception ex)
        {
            await stream.FailAsync(ex.Message, CancellationToken.None);
        }
    }

    private sealed record ArtifactFetchRequest(string ArtifactId);
}
