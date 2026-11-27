using HVO.SkyMonitor.CameraAgent.Common.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.LocalFiles;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

internal sealed class RawIngressFileStore(
    string root,
    IRawIngressFaultInjector faultInjector,
    Action? fileFlushRecorder = null,
    Action? directorySyncRecorder = null)
{
    internal const string AdmissionStageDirectory = "raw-admissions";

    private static readonly string[] TopLevelDirectories = ["journal", "quarantine"];
    private readonly string _root = Path.GetFullPath(root);
    private readonly IRawIngressFaultInjector _faultInjector = faultInjector;
    private readonly Action? _fileFlushRecorder = fileFlushRecorder;
    private readonly Action? _directorySyncRecorder = directorySyncRecorder;

    internal RawIngressPaths GetPaths(DateTimeOffset timestampUtc, Guid artifactId)
        => GetPaths(_root, timestampUtc, artifactId);

    internal static RawIngressPaths GetPaths(string root, DateTimeOffset timestampUtc, Guid artifactId)
    {
        root = Path.GetFullPath(root);
        var payload = Path.Combine(root, ".silo", AdmissionStagePath(artifactId, ".bin"));
        var sidecar = Path.Combine(root, ".silo", AdmissionStagePath(artifactId, ".json"));
        return new RawIngressPaths(
            NormalizeRelative(root, payload),
            NormalizeRelative(root, sidecar),
            payload,
            sidecar);
    }

    internal static string AdmissionStagePath(Guid artifactId, string extension)
        => Path.Combine(AdmissionStageDirectory, artifactId.ToString("N") + extension);

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
        var started = CameraAgentWorkloadTelemetry.Start();
        var outcome = CameraAgentWorkloadTelemetry.Outcome.Failure;
        try
        {
            using var integrity = DeploymentLocation.RetainedIntegrityLedger.BeginWrite(finalPath, content);
            var directory = Path.GetDirectoryName(finalPath)!;
            var directoryExisted = Directory.Exists(directory);
            Directory.CreateDirectory(directory);
            EnsureNoSymbolicLinks(_root, directory);
            if (!directoryExisted)
            {
                FlushDirectoryHierarchy(directory);
            }
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
            outcome = CameraAgentWorkloadTelemetry.Outcome.Success;
        }
        catch (OperationCanceledException)
        {
            outcome = CameraAgentWorkloadTelemetry.Outcome.Cancelled;
            throw;
        }
        catch (FileSystemFaultException exception) when (exception.InnerException is OperationCanceledException)
        {
            outcome = CameraAgentWorkloadTelemetry.Outcome.Cancelled;
            ExceptionDispatchInfo.Throw(exception.InnerException);
            throw;
        }
        finally
        {
            CameraAgentWorkloadTelemetry.Record(started, CameraAgentWorkloadTelemetry.Operation.IngressFilePublication, outcome);
        }
    }

    internal void EnsureRootIsPhysical()
    {
        CameraAgentFileSystem.EnsureStorageRootDurable(_root, _directorySyncRecorder);
        EnsureNoSymbolicLinks(_root, Path.Combine(_root, ".silo", AdmissionStageDirectory));
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
        PhysicalRoot.VerifyPath(rootPath, path, "inspect-raw-path");
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
        DurableSync.File(handle, path);
    }

    private static string NormalizeRelative(string root, string path)
        => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static void FlushDirectory(string directory)
    {
        DurableSync.Directory(directory);
    }

    internal static int GetLinuxDirectoryOnlyFlag(Architecture architecture)
        => HVO.SkyMonitor.LocalFiles.LinuxOpenFlags.GetDirectoryFlag(architecture);

    internal static int GetLinuxNoFollowFlag(Architecture architecture)
        => HVO.SkyMonitor.LocalFiles.LinuxOpenFlags.GetNoFollowFlag(architecture);

    private void FlushDirectoryTracked(string directory)
    {
        FlushDirectory(directory);
        if (OperatingSystem.IsLinux())
        {
            _directorySyncRecorder?.Invoke();
        }
    }
}
