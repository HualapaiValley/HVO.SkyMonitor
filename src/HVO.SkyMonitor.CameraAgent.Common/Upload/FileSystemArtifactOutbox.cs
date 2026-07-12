using System.Text.Json;
using HVO.SkyMonitor.AgentCore;

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
        var directory = Path.Combine(Path.GetFullPath(root), "outbox");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, string.Concat(manifest.IdempotencyKey, ".json"));
        if (File.Exists(path))
        {
            return;
        }

        var temporaryPath = string.Concat(path, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(manifest, SerializerOptions), cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public IReadOnlyList<ArtifactUploadManifest> List(string root, int maximumResults)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (maximumResults is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResults));
        }

        return EnumeratePending(root, CancellationToken.None).Take(maximumResults).ToArray();
    }

    public IEnumerable<ArtifactUploadManifest> EnumeratePending(string root, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var directory = Path.Combine(Path.GetFullPath(root), "outbox");
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        var paths = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            paths.Add(path);
        }
        cancellationToken.ThrowIfCancellationRequested();
        paths.Sort(StringComparer.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArtifactUploadManifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<ArtifactUploadManifest>(File.ReadAllBytes(path), SerializerOptions)
                    ?? throw new InvalidDataException($"Outbox manifest '{path}' is invalid.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"Outbox manifest '{path}' is invalid.", exception);
            }
            yield return manifest;
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
}
