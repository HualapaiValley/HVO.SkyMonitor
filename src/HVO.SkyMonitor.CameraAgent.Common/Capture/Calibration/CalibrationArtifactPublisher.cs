using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
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

    internal SiloArtifactStorage? Silo { get; init; }

    internal const string SiloEvidenceRevision = "calibration-evidence-1";
    internal const string SiloBundleRevision = "calibration-bundle-1";
    internal const string SiloProfileRevision = "calibration-profile-1";

    internal static Guid BundleArtifactId(CalibrationLibraryBundleV1 bundle) =>
        ProcessingIdentity.CreateArtifactId(SiloHash.Sha256(CalibrationLibraryContractJson.Serialize(bundle)));

    internal async ValueTask PublishSiloBundleAsync(CalibrationLibraryBundleV1 bundle, ReadOnlyMemory<byte> profile,
        CancellationToken cancellationToken)
    {
        if (Silo is null) return;
        var envelope = CalibrationLibraryContractJson.Serialize(bundle);
        var id = BundleArtifactId(bundle);
        var members = new[]
        {
            SiloArtifactStorage.Member(id, SiloBundleRevision, "calibration-bundle", SiloRetentionClass.Full,
                "application/json", envelope.LongLength, SiloHash.Sha256(envelope)),
            SiloArtifactStorage.Member(id, SiloProfileRevision, "calibration-profile", SiloRetentionClass.Full,
                "application/json", profile.Length, bundle.ProfileIdentitySha256)
        };
        var created = bundle.CreatedUtc.ToUniversalTime();
        var draft = new SiloGroupDraft("calibration-bundle", id.ToString("N"), "1", created, created, created,
            members, [], new([]), CameraId: bundle.Applicability.RigId);
        await Silo.PublishAsync(draft, (member, token) => SiloArtifactStorage.OpenMemoryAsync(
            member.ArtifactRevision == SiloBundleRevision ? envelope : profile, token), cancellationToken).ConfigureAwait(false);
    }

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
        if (Silo is not null)
        {
            var parsed = CaptureContractJson.ParseManifest(manifest);
            if (!parsed.IsValid || parsed.Document?.Manifest is not { } evidence)
                throw new InvalidDataException("Silo calibration publication requires a valid artifact manifest.");
            var descriptor = evidence.Descriptor;
            var id = descriptor.Artifact.ArtifactId;
            var revision = CaptureContractJson.ComputeDescriptorSha256(descriptor);
            var payloadMember = SiloArtifactStorage.Member(id, revision, "calibration-frame", SiloRetentionClass.Full,
                descriptor.Artifact.MediaType, payload.Length, descriptor.Artifact.ChecksumSha256);
            var evidenceMember = SiloArtifactStorage.Member(id, SiloEvidenceRevision, "calibration-evidence", SiloRetentionClass.Metadata,
                "application/json", manifest.Length, SiloHash.Sha256(manifest.Span));
            var draft = new SiloGroupDraft("calibration-artifact", id.ToString("N"), revision, descriptor.Artifact.CreatedUtc.ToUniversalTime(),
                descriptor.Timing.ExposureStartedUtc.ToUniversalTime(), descriptor.Timing.ExposureEndedUtc.ToUniversalTime(),
                [payloadMember, evidenceMember], [], new([]), CameraId: descriptor.Capture.RigId);
            await Silo.PublishAsync(draft, (member, token) => SiloArtifactStorage.OpenMemoryAsync(
                member.Role == "calibration-frame" ? payload : manifest, token), cancellationToken).ConfigureAwait(false);
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
        Directory.CreateDirectory(directory);
        EnsureSafePath(path);
        RawIngressFileStore.SyncDirectoryHierarchy(_root, directory);
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
                CameraAgentFileSystem.FlushFile(stream);
            }
            EnsureSafePath(directory);
            try
            {
                CameraAgentFileSystem.PublishPrepared(_root, temporaryPath, path, overwrite: false);
            }
            catch (IOException exception) when (!FileSystemFaultCompatibility.IsContainmentFailure(exception) && File.Exists(path))
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
                RetainedFileMutations.Delete(temporaryPath);
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
