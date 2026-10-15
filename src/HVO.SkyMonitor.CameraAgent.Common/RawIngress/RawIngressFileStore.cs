using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

internal sealed class RawIngressFileStore(
    string root,
    IRawIngressFaultInjector faultInjector,
    Action? fileFlushRecorder = null,
    Action? directorySyncRecorder = null)
{
    private static readonly string[] TopLevelDirectories = ["frames", "index", "journal", "quarantine"];
    private readonly string _root = Path.GetFullPath(root);
    private readonly IRawIngressFaultInjector _faultInjector = faultInjector;
    private readonly Action? _fileFlushRecorder = fileFlushRecorder;
    private readonly Action? _directorySyncRecorder = directorySyncRecorder;

    internal RawIngressPaths GetPaths(DateTimeOffset timestampUtc, Guid artifactId)
        => GetPaths(_root, timestampUtc, artifactId);

    internal static RawIngressPaths GetPaths(string root, DateTimeOffset timestampUtc, Guid artifactId)
    {
        root = Path.GetFullPath(root);
        var timestamp = timestampUtc.ToUniversalTime();
        var directory = Path.Combine(
            root,
            "frames",
            timestamp.Year.ToString("D4", CultureInfo.InvariantCulture),
            timestamp.Month.ToString("D2", CultureInfo.InvariantCulture),
            timestamp.Day.ToString("D2", CultureInfo.InvariantCulture),
            FrameArtifactRole.Raw.ToString());
        var stem = string.Concat(timestamp.ToString("yyyy-MM-dd_HH-mm-ss.fff'Z'", CultureInfo.InvariantCulture), "-", artifactId.ToString("N"));
        var payload = Path.Combine(directory, string.Concat(stem, ".bin"));
        var sidecar = Path.Combine(directory, string.Concat(stem, ".json"));
        return new RawIngressPaths(
            NormalizeRelative(root, payload),
            NormalizeRelative(root, sidecar),
            payload,
            sidecar);
    }

    internal async Task PublishPayloadAsync(
        RawIngressPaths paths,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
        => await PublishAsync(
            paths.PayloadAbsolutePath,
            payload,
            RawIngressFaultPoint.PayloadPartiallyWritten,
            RawIngressFaultPoint.PayloadWritten,
            RawIngressFaultPoint.PayloadFlushed,
            RawIngressFaultPoint.PayloadPublished,
            RawIngressFaultPoint.PayloadDirectorySynced,
            cancellationToken).ConfigureAwait(false);

    internal async Task PublishSidecarAsync(
        RawIngressPaths paths,
        ReadOnlyMemory<byte> sidecar,
        CancellationToken cancellationToken)
        => await PublishAsync(
            paths.SidecarAbsolutePath,
            sidecar,
            null,
            RawIngressFaultPoint.SidecarWritten,
            RawIngressFaultPoint.SidecarFlushed,
            RawIngressFaultPoint.SidecarPublished,
            RawIngressFaultPoint.SidecarDirectorySynced,
            cancellationToken).ConfigureAwait(false);

    internal static async Task<ArtifactManifestV2> ReadAndValidateExistingAsync(
        RawIngressPaths paths,
        ReadOnlyMemory<byte> payload,
        RawCaptureIdentity identity,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.PayloadAbsolutePath) || !File.Exists(paths.SidecarAbsolutePath))
        {
            throw new RawIngressConflictException("Only part of the immutable raw evidence already exists.");
        }
        var sidecar = await File.ReadAllBytesAsync(paths.SidecarAbsolutePath, cancellationToken).ConfigureAwait(false);
        var parsed = CaptureContractJson.ParseManifest(sidecar);
        var manifest = parsed.Document?.Manifest;
        if (!parsed.IsValid || manifest is null ||
            manifest.Descriptor.Capture.AgentId != identity.AgentId ||
            manifest.Descriptor.Capture.CaptureSequence != identity.CaptureSequence ||
            manifest.Descriptor.Capture.CaptureId != identity.CaptureId ||
            manifest.Descriptor.Artifact.ArtifactId != identity.ArtifactId ||
            manifest.RelativeArtifactPath != paths.PayloadRelativePath)
        {
            throw new RawIngressConflictException("Existing immutable sidecar differs from the retry identity.");
        }
        var reconstruction = FrameReconstructor.TryReconstruct(manifest.Descriptor, payload, out _);
        using var existingPayload = new FileStream(
            paths.PayloadAbsolutePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var existingChecksum = await PayloadChecksum.ComputeSha256Async(existingPayload, cancellationToken).ConfigureAwait(false);
        if (!reconstruction.IsValid || new FileInfo(paths.PayloadAbsolutePath).Length != payload.Length ||
            !string.Equals(existingChecksum, manifest.Descriptor.Artifact.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new RawIngressConflictException("Existing immutable payload differs from the retry.");
        }
        return manifest;
    }

    internal static async Task AppendCompatibilityIndexAsync(
        string root,
        RawCaptureReceipt receipt,
        CancellationToken cancellationToken,
        Action? fileFlushRecorder = null)
    {
        var descriptor = receipt.Manifest.Descriptor;
        var indexDirectory = Path.Combine(Path.GetFullPath(root), "index");
        Directory.CreateDirectory(indexDirectory);
        var indexPath = Path.Combine(indexDirectory, $"frames_{descriptor.Timing.ExposureStartedUtc:yyyy-MM-dd}.jsonl");
        var gate = FrameIndexLock.ForRoot(root);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var stream = new FileStream(
                indexPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            if (stream.Length > 0)
            {
                stream.Seek(-1, SeekOrigin.End);
                if (stream.ReadByte() != '\n')
                {
                    stream.Seek(0, SeekOrigin.End);
                    await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                }
            }
            stream.Seek(0, SeekOrigin.End);
            var line = JsonSerializer.SerializeToUtf8Bytes(new CompatibilityIndexEntry(
                descriptor.Artifact.ArtifactId,
                descriptor.Artifact.Role,
                descriptor.Artifact.SourceArtifactIds,
                descriptor.Artifact.Recipe.ImplementationVersion,
                descriptor.Timing.ExposureStartedUtc,
                descriptor.Layout.Width,
                descriptor.Layout.Height,
                descriptor.Layout.PixelFormat,
                null), SerializerOptions);
            await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            CameraAgentFileSystem.FlushFile(stream);
            fileFlushRecorder?.Invoke();
        }
        finally
        {
            gate.Release();
        }
    }

    internal static async Task AppendCompatibilityIndexAsync(
        string root,
        ReadOnlyMemory<byte> manifestJson,
        CancellationToken cancellationToken,
        Action? fileFlushRecorder = null)
    {
        var parsed = CaptureContractJson.ParseManifest(manifestJson);
        var manifest = parsed.Document?.Manifest;
        if (!parsed.IsValid || manifest is null)
        {
            throw new InvalidDataException("Cannot append an invalid raw ingress manifest to the compatibility index.");
        }
        var descriptor = manifest.Descriptor;
        var receipt = new RawCaptureReceipt(
            RawIngressOutcome.Existing,
            manifest,
            new StoredFrameReference(
                manifest.RelativeArtifactPath,
                Path.GetFullPath(Path.Combine(root, manifest.RelativeArtifactPath)),
                descriptor.Timing.ExposureStartedUtc,
                FrameArtifactRole.Raw),
            CaptureContractJson.ComputeManifestSha256(manifestJson.Span));
        await AppendCompatibilityIndexAsync(root, receipt, cancellationToken, fileFlushRecorder).ConfigureAwait(false);
    }

    internal static async Task EnsureCompatibilityIndexAsync(
        string root,
        ReadOnlyMemory<byte> manifestJson,
        CancellationToken cancellationToken,
        Action? fileFlushRecorder = null)
    {
        var parsed = CaptureContractJson.ParseManifest(manifestJson);
        var manifest = parsed.Document?.Manifest;
        if (!parsed.IsValid || manifest is null)
        {
            throw new InvalidDataException("Cannot repair an invalid raw ingress manifest index projection.");
        }
        var descriptor = manifest.Descriptor;
        var indexPath = Path.Combine(
            Path.GetFullPath(root), "index", $"frames_{descriptor.Timing.ExposureStartedUtc:yyyy-MM-dd}.jsonl");
        if (File.Exists(indexPath))
        {
            foreach (var line in await File.ReadAllLinesAsync(indexPath, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    if (MatchesIndexEntry(document.RootElement, descriptor))
                    {
                        return;
                    }
                }
                catch (JsonException)
                {
                }
            }
        }
        await AppendCompatibilityIndexAsync(root, manifestJson, cancellationToken, fileFlushRecorder).ConfigureAwait(false);
    }

    internal static bool MatchesIndexEntry(JsonElement element, ReconstructionDescriptor descriptor)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty("artifactId", out var artifactId) &&
           artifactId.ValueKind == JsonValueKind.String &&
           Guid.TryParse(artifactId.GetString(), out var parsedId) &&
           parsedId == descriptor.Artifact.ArtifactId &&
           element.TryGetProperty("role", out var role) &&
           role.ValueKind == JsonValueKind.String &&
           string.Equals(role.GetString(), descriptor.Artifact.Role.ToString(), StringComparison.Ordinal) &&
           element.TryGetProperty("timestampUtc", out var timestamp) &&
           timestamp.ValueKind == JsonValueKind.String &&
           timestamp.TryGetDateTimeOffset(out var parsedTimestamp) &&
           parsedTimestamp == descriptor.Timing.ExposureStartedUtc &&
           element.TryGetProperty("width", out var width) &&
           width.ValueKind == JsonValueKind.Number &&
           width.TryGetInt32(out var parsedWidth) &&
           parsedWidth == descriptor.Layout.Width &&
           element.TryGetProperty("height", out var height) &&
           height.ValueKind == JsonValueKind.Number &&
           height.TryGetInt32(out var parsedHeight) &&
           parsedHeight == descriptor.Layout.Height &&
           element.TryGetProperty("pixelFormat", out var pixelFormat) &&
           pixelFormat.ValueKind == JsonValueKind.String &&
           string.Equals(pixelFormat.GetString(), descriptor.Layout.PixelFormat.ToString(), StringComparison.Ordinal);

    private async Task PublishAsync(
        string finalPath,
        ReadOnlyMemory<byte> content,
        RawIngressFaultPoint? partiallyWritten,
        RawIngressFaultPoint written,
        RawIngressFaultPoint flushed,
        RawIngressFaultPoint published,
        RawIngressFaultPoint directorySynced,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(finalPath)!;
        var directoryExisted = Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        EnsureNoSymbolicLinks(_root, directory);
        if (!directoryExisted)
        {
            FlushDirectoryHierarchy(directory);
        }
        try
        {
            await AtomicPublisher.PublishAsync(
                PhysicalRoot.Open(_root),
                NormalizeRelative(_root, finalPath),
                PublishMode.CreateNew,
                async (stream, token) =>
                {
                    // Acceptance holds the root's exclusive process lock; only this invocation
                    // owns the temporary, and every original crash boundary remains observable.
                    if (partiallyWritten is { } partialPoint && content.Length > 1 && _faultInjector.IsEnabled(partialPoint))
                    {
                        var prefixLength = Math.Min(64 * 1024, Math.Max(1, content.Length / 2));
                        await stream.WriteAsync(content[..prefixLength], token).ConfigureAwait(false);
                        DurableSync.File((FileStream)stream);
                        _faultInjector.Inject(partialPoint);
                        await stream.WriteAsync(content[prefixLength..], token).ConfigureAwait(false);
                    }
                    else
                    {
                        await stream.WriteAsync(content, token).ConfigureAwait(false);
                    }
                    _faultInjector.Inject(written);
                },
                phase =>
                {
                    switch (phase)
                    {
                        case PublicationPhase.FileFlushed:
                            _fileFlushRecorder?.Invoke();
                            _faultInjector.Inject(flushed);
                            break;
                        case PublicationPhase.Renamed:
                            _faultInjector.Inject(published);
                            break;
                        case PublicationPhase.DirectorySynced:
                            if (OperatingSystem.IsLinux()) _directorySyncRecorder?.Invoke();
                            _faultInjector.Inject(directorySynced);
                            break;
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (FileSystemFaultException exception)
        {
            FileSystemFaultCompatibility.Rethrow(exception);
        }
    }

    internal void EnsureRootIsPhysical()
    {
        CameraAgentFileSystem.EnsureStorageRootDurable(_root, _directorySyncRecorder);
        foreach (var child in TopLevelDirectories)
        {
            var childPath = Path.Combine(_root, child);
            Directory.CreateDirectory(childPath);
            EnsureNoSymbolicLinks(_root, childPath);
            if (OperatingSystem.IsLinux())
            {
                FlushDirectoryTracked(childPath);
                FlushDirectoryTracked(_root);
            }
        }
    }

    private void FlushDirectoryHierarchy(string directory)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            FlushDirectoryTracked(current.FullName);
            if (string.Equals(
                    Path.TrimEndingDirectorySeparator(current.FullName),
                    Path.TrimEndingDirectorySeparator(_root),
                    StringComparison.Ordinal))
            {
                return;
            }
            current = current.Parent;
        }
        throw new IOException("Raw ingress directory hierarchy escaped the configured root.");
    }

    internal static void EnsureNoSymbolicLinks(string rootPath, string path)
    {
        try
        {
            PhysicalRoot.VerifyPath(rootPath, path, "inspect-raw-path");
        }
        catch (FileSystemFaultException exception)
        {
            FileSystemFaultCompatibility.Rethrow(exception);
        }
    }

    internal static void SyncDirectory(string directory) => FlushDirectory(directory);

    internal static int SyncDirectoryHierarchy(string root, string directory)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var current = new DirectoryInfo(Path.GetFullPath(directory));
        var count = 0;
        while (current is not null)
        {
            FlushDirectory(current.FullName);
            count++;
            if (string.Equals(
                    Path.TrimEndingDirectorySeparator(current.FullName),
                    root,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return count;
            }
            current = current.Parent;
        }
        throw new IOException("Raw ingress directory synchronization escaped the configured root.");
    }

    internal static void SyncFile(string root, string path)
    {
        EnsureNoSymbolicLinks(root, path);
        using var handle = File.OpenHandle(path, FileMode.Open,
                OperatingSystem.IsWindows() ? FileAccess.ReadWrite : FileAccess.Read, FileShare.Read);
        try
        {
            DurableSync.File(handle, path);
        }
        catch (FileSystemFaultException exception)
        {
            FileSystemFaultCompatibility.Rethrow(exception);
        }
    }

    private static string NormalizeRelative(string root, string path)
        => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static void FlushDirectory(string directory)
    {
        try
        {
            DurableSync.Directory(directory);
        }
        catch (FileSystemFaultException exception)
        {
            FileSystemFaultCompatibility.Rethrow(exception);
        }
    }

    internal static int GetLinuxDirectoryOnlyFlag(Architecture architecture)
        => HVO.SkyMonitor.Storage.FileSystem.LinuxOpenFlags.GetDirectoryFlag(architecture);

    internal static int GetLinuxNoFollowFlag(Architecture architecture)
        => HVO.SkyMonitor.Storage.FileSystem.LinuxOpenFlags.GetNoFollowFlag(architecture);

    private void FlushDirectoryTracked(string directory)
    {
        FlushDirectory(directory);
        if (OperatingSystem.IsLinux())
        {
            _directorySyncRecorder?.Invoke();
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record CompatibilityIndexEntry(
        Guid ArtifactId,
        FrameArtifactRole Role,
        IReadOnlyList<Guid> SourceArtifactIds,
        string RecipeVersion,
        DateTimeOffset TimestampUtc,
        int Width,
        int Height,
        CameraPixelFormat PixelFormat,
        object? Metadata);
}
