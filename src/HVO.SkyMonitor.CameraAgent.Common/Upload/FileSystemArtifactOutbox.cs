using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;

namespace HVO.SkyMonitor.CameraAgent.Common.Upload;

/// <summary>Filesystem-backed durable outbox with atomic manifest commits.</summary>
public sealed class FileSystemArtifactOutbox : IArtifactOutbox
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public async ValueTask EnqueueAsync(string root, ArtifactUploadManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();
        root = Path.GetFullPath(root);
        var directory = Path.Combine(root, "outbox");
        var directoryExisted = Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        RawIngressFileStore.EnsureNoSymbolicLinks(root, directory);
        if (!directoryExisted)
        {
            RawIngressFileStore.SyncDirectoryHierarchy(root, directory);
        }
        var path = Path.Combine(directory, string.Concat(manifest.IdempotencyKey, ".json"));
        var content = JsonSerializer.SerializeToUtf8Bytes(manifest, SerializerOptions);
        if (File.Exists(path))
        {
            ValidateExisting(root, directory, path, content);
            return;
        }

        var temporaryPath = string.Concat(path, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // FlushAsync does not provide a flush-to-disk contract.
                stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
            }
            RawIngressFileStore.EnsureNoSymbolicLinks(root, directory);
            try
            {
                File.Move(temporaryPath, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                ValidateExisting(root, directory, path, content);
                return;
            }
            RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
            RawIngressFileStore.SyncDirectory(directory);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public IReadOnlyList<ArtifactUploadManifest> List(
        string root,
        int maximumResults,
        IReadOnlySet<string>? excludedIdempotencyKeys = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (maximumResults is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResults));
        }

        var manifests = new List<ArtifactUploadManifest>(maximumResults);
        foreach (var path in EnumerateManifestPaths(root, CancellationToken.None))
        {
            var idempotencyKey = Path.GetFileNameWithoutExtension(path);
            if (excludedIdempotencyKeys?.Contains(idempotencyKey) == true)
            {
                continue;
            }

            manifests.Add(ReadManifest(path));
            if (manifests.Count == maximumResults)
            {
                break;
            }
        }

        return manifests;
    }

    public IEnumerable<ArtifactUploadManifest> EnumeratePending(string root, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        foreach (var path in EnumerateManifestPaths(root, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ReadManifest(path);
        }
    }

    public ValueTask AcknowledgeAsync(string root, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(Path.GetFullPath(root), "outbox", string.Concat(idempotencyKey, ".json"));
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return ValueTask.CompletedTask;
    }

    private static List<string> EnumerateManifestPaths(string root, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetFullPath(root), "outbox");
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var paths = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            paths.Add(path);
        }
        paths.Sort(StringComparer.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();
        return paths;
    }

    private static ArtifactUploadManifest ReadManifest(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ArtifactUploadManifest>(File.ReadAllBytes(path), SerializerOptions)
                ?? throw new InvalidDataException($"Outbox manifest '{path}' is invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Outbox manifest '{path}' is invalid.", exception);
        }
    }

    private static void ValidateExisting(
        string root,
        string directory,
        string path,
        ReadOnlySpan<byte> expected)
    {
        RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
        var existing = File.ReadAllBytes(path);
        if (!existing.AsSpan().SequenceEqual(expected))
        {
            throw new InvalidDataException($"Outbox manifest '{path}' conflicts with the requested upload.");
        }
        RawIngressFileStore.SyncFile(root, path);
        RawIngressFileStore.SyncDirectory(directory);
    }
}
