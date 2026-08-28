using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;

internal static class CalibrationLibraryEvidenceNames
{
    internal const string BundleEnvelope = "calibration-library-bundle.json";
    internal const string ProfileMarker = "reference-calibration-profile.json";
}

public enum CalibrationPublicationFaultPoint
{
    BeforePayloadWrite,
    AfterPayloadPublished,
    BeforeManifestWrite,
    AfterManifestPublished,
    BeforeBundleWrite,
    AfterBundlePublished,
    BeforeProfileWrite,
    AfterProfilePublished,
    DirectorySynced,
    AfterSqlitePublication,
    AfterActivationCommitted
}

public interface ICalibrationPublicationFaultInjector
{
    void Inject(CalibrationPublicationFaultPoint point, string relativePath);
}

public sealed class NullCalibrationPublicationFaultInjector : ICalibrationPublicationFaultInjector
{
    public static NullCalibrationPublicationFaultInjector Instance { get; } = new();

    private NullCalibrationPublicationFaultInjector()
    {
    }

    public void Inject(CalibrationPublicationFaultPoint point, string relativePath)
    {
    }
}

public sealed class CalibrationPublicationConflictException : IOException
{
    public CalibrationPublicationConflictException()
    {
    }

    public CalibrationPublicationConflictException(string message) : base(message)
    {
    }

    public CalibrationPublicationConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class CalibrationArtifactPublisher(
    IOptions<CameraAgentHostOptions> options,
    ICalibrationPublicationFaultInjector faultInjector)
{
    private readonly string _root = Path.GetFullPath(options.Value.RawIngressRoot);
    private readonly ICalibrationPublicationFaultInjector _faultInjector = faultInjector;

    internal void InjectFault(CalibrationPublicationFaultPoint point, string relativePath)
        => _faultInjector.Inject(point, relativePath);

    public async Task PublishPairAsync(
        string payloadRelativePath,
        ReadOnlyMemory<byte> payload,
        string manifestRelativePath,
        ReadOnlyMemory<byte> manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payloadRelativePath);
        ArgumentNullException.ThrowIfNull(manifestRelativePath);
        ValidatePairPaths(payloadRelativePath, manifestRelativePath);
        var lifecycle = RawIngressLifecycleLock.ForRoot(_root);
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var payloadPath = Resolve(payloadRelativePath);
            var manifestPath = Resolve(manifestRelativePath);
            if (File.Exists(manifestPath) && !File.Exists(payloadPath))
            {
                throw new CalibrationPublicationConflictException(
                    "A committed calibration manifest references a missing immutable payload.");
            }
            _faultInjector.Inject(CalibrationPublicationFaultPoint.BeforePayloadWrite, payloadRelativePath);
            await WriteImmutableAsync(payloadPath, payload, cancellationToken).ConfigureAwait(false);
            _faultInjector.Inject(CalibrationPublicationFaultPoint.AfterPayloadPublished, payloadRelativePath);
            _faultInjector.Inject(CalibrationPublicationFaultPoint.BeforeManifestWrite, manifestRelativePath);
            await WriteImmutableAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            _faultInjector.Inject(CalibrationPublicationFaultPoint.AfterManifestPublished, manifestRelativePath);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task PublishProfileMarkerAsync(
        string relativePath,
        ReadOnlyMemory<byte> profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (!relativePath.EndsWith($"/{CalibrationLibraryEvidenceNames.ProfileMarker}", StringComparison.Ordinal))
        {
            throw new ArgumentException("The calibration profile marker path is invalid.", nameof(relativePath));
        }
        var lifecycle = RawIngressLifecycleLock.ForRoot(_root);
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _faultInjector.Inject(CalibrationPublicationFaultPoint.BeforeProfileWrite, relativePath);
            var path = Resolve(relativePath);
            await WriteImmutableAsync(path, profile, cancellationToken).ConfigureAwait(false);
            _faultInjector.Inject(CalibrationPublicationFaultPoint.AfterProfilePublished, relativePath);
            RawIngressFileStore.SyncDirectory(Path.GetDirectoryName(path)!);
            _faultInjector.Inject(CalibrationPublicationFaultPoint.DirectorySynced, relativePath);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task PublishBundleEnvelopeAsync(
        string relativePath,
        ReadOnlyMemory<byte> bundle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (!relativePath.EndsWith($"/{CalibrationLibraryEvidenceNames.BundleEnvelope}", StringComparison.Ordinal))
        {
            throw new ArgumentException("The calibration bundle envelope path is invalid.", nameof(relativePath));
        }
        var lifecycle = RawIngressLifecycleLock.ForRoot(_root);
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _faultInjector.Inject(CalibrationPublicationFaultPoint.BeforeBundleWrite, relativePath);
            await WriteImmutableAsync(Resolve(relativePath), bundle, cancellationToken).ConfigureAwait(false);
            _faultInjector.Inject(CalibrationPublicationFaultPoint.AfterBundlePublished, relativePath);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task<bool> EnsureCommittedEvidenceIsCompleteAsync(
        string profileRelativePath,
        IReadOnlyList<string> requiredRelativePaths,
        bool markerExpected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profileRelativePath);
        ArgumentNullException.ThrowIfNull(requiredRelativePaths);
        var lifecycle = RawIngressLifecycleLock.ForRoot(_root);
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(Resolve(profileRelativePath)))
            {
                if (markerExpected)
                {
                    throw new CalibrationPublicationConflictException(
                        "The committed calibration profile marker is missing.");
                }
                return false;
            }
            foreach (var relativePath in requiredRelativePaths)
            {
                if (!File.Exists(Resolve(relativePath)))
                {
                    throw new CalibrationPublicationConflictException(
                        "The committed calibration profile references missing immutable evidence.");
                }
            }
            return true;
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private async Task WriteImmutableAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        var existed = Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        EnsureSafePath(path);
        if (!existed)
        {
            RawIngressFileStore.SyncDirectoryHierarchy(_root, directory);
        }
        if (File.Exists(path))
        {
            if (!await FileMatchesAsync(path, bytes, cancellationToken).ConfigureAwait(false))
            {
                throw new CalibrationPublicationConflictException(
                    "Calibration publication conflicts with existing immutable bytes.");
            }
            RawIngressFileStore.SyncDirectory(directory);
            return;
        }

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // FlushAsync does not guarantee durable filesystem publication.
                stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
            }
            EnsureSafePath(directory);
            try
            {
                File.Move(temporaryPath, path, overwrite: false);
            }
            catch (IOException exception) when (File.Exists(path))
            {
                if (!await FileMatchesAsync(path, bytes, cancellationToken).ConfigureAwait(false))
                {
                    throw new CalibrationPublicationConflictException(
                        "Concurrent calibration publication produced different immutable bytes.", exception);
                }
            }
            EnsureSafePath(path);
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

    private static async Task<bool> FileMatchesAsync(
        string path,
        ReadOnlyMemory<byte> expected,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expected.Length)
        {
            return false;
        }

        var buffer = new byte[64 * 1024];
        var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            var offset = 0;
            while (offset < expected.Length)
            {
                var count = await stream.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, expected.Length - offset)), cancellationToken)
                    .ConfigureAwait(false);
                if (count == 0 || !buffer.AsSpan(0, count).SequenceEqual(expected.Span.Slice(offset, count)))
                {
                    return false;
                }
                offset += count;
            }
            return await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) == 0;
        }
    }

    private string Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
            relativePath.Contains('\\', StringComparison.Ordinal) ||
            relativePath.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("Calibration publication paths must be safe relative paths.", nameof(relativePath));
        }
        var path = Path.GetFullPath(Path.Combine(_root, relativePath));
        var prefix = string.Concat(Path.TrimEndingDirectorySeparator(_root), Path.DirectorySeparatorChar);
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("Calibration publication paths cannot escape storage.", nameof(relativePath));
        }
        EnsureSafePath(path);
        return path;
    }

    private void EnsureSafePath(string path)
    {
        try
        {
            RawIngressFileStore.EnsureNoSymbolicLinks(_root, path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CalibrationPublicationConflictException(
                "Calibration publication storage contains an unsafe path.", exception);
        }
    }

    private static void ValidatePairPaths(string payloadRelativePath, string manifestRelativePath)
    {
        var payloadStem = Path.ChangeExtension(payloadRelativePath, null);
        var manifestStem = Path.ChangeExtension(manifestRelativePath, null);
        if (!payloadRelativePath.EndsWith(".bin", StringComparison.Ordinal) ||
            !manifestRelativePath.EndsWith(".json", StringComparison.Ordinal) ||
            !string.Equals(payloadStem, manifestStem, StringComparison.Ordinal))
        {
            throw new ArgumentException("Calibration payload and manifest paths must be a matching .bin/.json pair.");
        }
    }
}
