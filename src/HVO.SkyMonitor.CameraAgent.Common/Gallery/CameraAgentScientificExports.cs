using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.SiteProfile;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Storage.FileSystem;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

public interface ICameraAgentScientificExports
{
    ValueTask<CameraAgentScientificExportResult> PrepareAsync(
        Guid captureId, Guid artifactId, ScientificExportFormat format, CancellationToken cancellationToken,
        bool reuseRecent = false);
}

public sealed record CameraAgentScientificExportResult(
    CameraAgentArtifactReadStatus Status, string? ReasonCode, CameraAgentScientificDownload? Download = null);

public sealed class CameraAgentScientificDownload : IAsyncDisposable
{
    private Action? _release;

    internal CameraAgentScientificDownload(Stream content, ScientificExportReceipt receipt, Action release)
    {
        Content = content;
        Receipt = receipt;
        _release = release;
    }

    public Stream Content { get; }
    public ScientificExportReceipt Receipt { get; }

    public async ValueTask DisposeAsync()
    {
        var release = Interlocked.Exchange(ref _release, null);
        if (release is null) return;
        try { await Content.DisposeAsync().ConfigureAwait(false); }
        finally { release(); }
    }
}

internal sealed partial class CameraAgentScientificExports(
    ICameraAgentArtifactService artifacts, ICameraAgentRetainedAstrometryService astrometry,
    IDeploymentLocationStore deployment, ISiteProfileStore siteProfile,
    IOptions<CameraAgentHostOptions> options, IStorageCapacityProvider capacityProvider, RawIngressState ingress,
    ILogger<CameraAgentScientificExports> logger) : ICameraAgentScientificExports, IDisposable
{
    private const long MinimumIngressReserveBytes = 64L * 1024 * 1024;
    private const int MaximumCachedBytes = 8 * 1024 * 1024;
    private const string Capacity = "scientific-export.capacity";
    private const string IoUnavailable = "scientific-export.io-unavailable";
    private const string AstrometryUnavailable = "scientific-export.astrometry-unavailable";
    private readonly SemaphoreSlim _slots = new(2, 2);
    private readonly Lock _capacityGate = new();
    private long _pendingSpoolBytes;
    private RecentExport? _recent;

    private sealed record ExportKey(ScientificExportFormat Format, string DescriptorSha256, ScientificExportObservation Observation);
    private sealed record RecentExport(ExportKey Key, ScientificExportReceipt Receipt, byte[] Bytes);

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The result transfers the staged download and its slot to the response handler.")]
    public async ValueTask<CameraAgentScientificExportResult> PrepareAsync(
        Guid captureId, Guid artifactId, ScientificExportFormat format, CancellationToken cancellationToken,
        bool reuseRecent = false)
    {
        if (captureId == Guid.Empty || artifactId == Guid.Empty)
            return Missing();
        if (!await _slots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return Busy();

        var handedOff = false;
        Stream? output = null;
        long reservedBytes = 0;
        var operation = "source-open";
        RecentExport? staged = null;
        try
        {
            var opened = await artifacts.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
            ScientificExportReceipt receipt;
            var source = opened.Content;
            if (source is null) return SourceFailure(opened.Status);
            await using (source.ConfigureAwait(false))
            {
                if (opened.Status != CameraAgentArtifactReadStatus.Found)
                    return SourceFailure(opened.Status);
                if (source.CaptureId != captureId || source.ArtifactId != artifactId)
                    return Missing();
                if (source.Role != FrameArtifactRole.Raw || source.Descriptor is not { } descriptor ||
                    !ScientificCaptureExport.Supports(descriptor.Layout.PixelFormat))
                    return new(CameraAgentArtifactReadStatus.UnsupportedMediaType, ScientificCaptureExport.UnsupportedSource);
                if (descriptor.Capture.CaptureId != captureId || descriptor.Artifact.ArtifactId != artifactId ||
                    descriptor.Layout.ByteLength != source.ByteLength ||
                    !string.Equals(descriptor.Artifact.ChecksumSha256, source.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
                    return new(CameraAgentArtifactReadStatus.Conflict, ScientificCaptureExport.CorruptSource);
                if (source.ByteLength > ScientificCaptureExport.MaximumSourceBytes)
                    return new(CameraAgentArtifactReadStatus.TooLarge, ScientificCaptureExport.SourceTooLarge);
                if (!descriptor.Validate().IsValid)
                    return new(CameraAgentArtifactReadStatus.Conflict, ScientificCaptureExport.CorruptSource);

                CameraAgentRetainedAstrometry retained;
                try
                {
                    retained = await astrometry.GetAsync(captureId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DbException)
                {
                    LogIoFailure(format, AstrometryUnavailable, "astrometry-read", exception);
                    return new(CameraAgentArtifactReadStatus.Unavailable, AstrometryUnavailable);
                }
                if (retained.Reason == "read-capacity")
                    return Busy();
                if (retained.HasMissingEvidence)
                    return new(CameraAgentArtifactReadStatus.Conflict, "scientific-export.astrometry-missing");
                if (retained.Reason == CameraAgentRetainedAstrometryService.TemporaryEvidenceUnavailable)
                    return new(CameraAgentArtifactReadStatus.Unavailable, AstrometryUnavailable);

                operation = "observation-metadata";
                var descriptorSha256 = CaptureContractJson.ComputeDescriptorSha256(descriptor);
                var solved = retained is { Solution.Assessment.HasMeasuredMapping: true, Mapping: not null } &&
                    retained.Solution.Assessment.Frame.SourceArtifactId == artifactId &&
                    string.Equals(retained.Solution.Assessment.Frame.SourcePayloadSha256, source.ChecksumSha256, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(retained.Solution.Assessment.Frame.SourceDescriptorSha256,
                        descriptorSha256, StringComparison.OrdinalIgnoreCase);
                var location = CameraAgentCaptureLocationProjector.Project(descriptor, deployment);
                // Descriptive names come from the export-time profile. Only coordinates are captured provenance.
                var site = (await siteProfile.GetAsync(cancellationToken).ConfigureAwait(false)).Profile;
                var wcs = solved ? retained.Mapping!.ToCelestialWcs() : null;
                var observation = new ScientificExportObservation(location.LatitudeDegrees, location.LongitudeDegrees, location.ElevationMeters,
                    site.ObservatoryName, site.OwnerName, wcs, wcs is not null ? retained.SolutionIdentitySha256 : null);

                var key = new ExportKey(format, descriptorSha256, observation);
                var recent = reuseRecent ? Volatile.Read(ref _recent) : null;
                if (recent?.Key == key)
                {
                    operation = "source-verify";
                    await ScientificCaptureExport.VerifySourceAsync(source, source.ByteLength, source.ChecksumSha256, cancellationToken)
                        .ConfigureAwait(false);
                    output = new MemoryStream(recent.Bytes, writable: false);
                    receipt = recent.Receipt;
                }
                else
                {
                    var estimate = ScientificCaptureExport.MaximumOutputBytes(descriptor, format, observation);
                    operation = "capacity-probe";
                    if (!TryReserveSpool(estimate, source.ByteLength))
                        return Busy();
                    reservedBytes = estimate;

                    // Stage privately. On Linux the unlinked file is reclaimed even if the host exits.
                    operation = "spool-open";
                    output = CreateSpool(options.Value.RawIngressRoot);
                    operation = "encode";
                    receipt = await ScientificCaptureExport.WriteAsync(source, output, descriptor, format, observation, cancellationToken)
                        .ConfigureAwait(false);

                    if (receipt.ByteLength <= MaximumCachedBytes)
                    {
                        operation = "spool-read";
                        var bytes = new byte[(int)receipt.ByteLength];
                        await output.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                        output.Position = 0;
                        staged = new(key, receipt, bytes);
                    }
                }
                operation = "source-close";
            }
            cancellationToken.ThrowIfCancellationRequested();
            var download = new CameraAgentScientificDownload(output, receipt, () => _slots.Release());
            if (staged is not null)
                Volatile.Write(ref _recent, staged);
            handedOff = true;
            return new(CameraAgentArtifactReadStatus.Found, null, download);
        }
        catch (ScientificExportException exception)
        {
            return new(exception.ReasonCode switch
            {
                ScientificCaptureExport.UnsupportedSource => CameraAgentArtifactReadStatus.UnsupportedMediaType,
                ScientificCaptureExport.SourceTooLarge => CameraAgentArtifactReadStatus.TooLarge,
                _ => CameraAgentArtifactReadStatus.Conflict
            }, exception.ReasonCode);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var reason = operation == "capacity-probe" ? Capacity : IoUnavailable;
            LogIoFailure(format, reason, operation, exception);
            return new(CameraAgentArtifactReadStatus.Unavailable, reason);
        }
        finally
        {
            try
            {
                if (!handedOff && output is not null)
                {
                    try { await output.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        LogIoFailure(format, IoUnavailable, "spool-close", exception);
                    }
                }
            }
            finally
            {
                if (reservedBytes != 0)
                {
                    lock (_capacityGate) _pendingSpoolBytes -= reservedBytes;
                }
                if (!handedOff) _slots.Release();
            }
        }
    }

    private bool TryReserveSpool(long estimate, long sourceBytes)
    {
        lock (_capacityGate)
        {
            var available = capacityProvider.GetCapacity(options.Value.RawIngressRoot).AvailableBytes;
            var reserve = Math.Max(MinimumIngressReserveBytes, options.Value.RawIngressReserveBytes);
            // The latest live ingress attempt can be larger than the archived source being exported.
            // Use the source as a floor, including before the first capture after startup.
            var nextCaptureHeadroom = Math.Max(sourceBytes, ingress.LastCapturePayloadBytes);
            // The filesystem may not yet account for concurrent writers that passed the capacity check.
            if (available < reserve || available - reserve < nextCaptureHeadroom ||
                available - reserve - nextCaptureHeadroom - _pendingSpoolBytes < estimate)
                return false;
            _pendingSpoolBytes += estimate;
            return true;
        }
    }

    private static FileStream CreateSpool(string rawIngressRoot)
    {
        var root = Path.GetFullPath(rawIngressRoot);
        var directory = Path.Combine(root, ".scientific-exports");
        PhysicalRoot.VerifyPath(root, directory, "scientific-export-staging");
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            const UnixFileMode privateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(directory, privateDirectory);
            if (File.GetUnixFileMode(directory) != privateDirectory)
                throw new FileSystemFaultException(FileSystemFaultKind.PermissionDenied, "staging-directory-mode", directory);
        }
        PhysicalRoot.VerifyPath(root, directory, "scientific-export-staging");
        var path = Path.Combine(directory, $"hvo-science-{Guid.NewGuid():N}");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = OperatingSystem.IsLinux() ? FileOptions.Asynchronous : FileOptions.Asynchronous | FileOptions.DeleteOnClose,
            BufferSize = 64 * 1024
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var stream = new FileStream(path, options);
        try
        {
            // No payload is written while the spool still has a directory entry.
            if (OperatingSystem.IsLinux()) File.Delete(path);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static CameraAgentScientificExportResult SourceFailure(CameraAgentArtifactReadStatus status) => status switch
    {
        CameraAgentArtifactReadStatus.NotFound => Missing(),
        CameraAgentArtifactReadStatus.Gone => new(status, "scientific-export.source-gone"),
        CameraAgentArtifactReadStatus.Conflict => new(status, ScientificCaptureExport.CorruptSource),
        CameraAgentArtifactReadStatus.TooLarge => new(status, ScientificCaptureExport.SourceTooLarge),
        CameraAgentArtifactReadStatus.UnsupportedMediaType => new(status, ScientificCaptureExport.UnsupportedSource),
        _ => new(CameraAgentArtifactReadStatus.Unavailable, "scientific-export.source-unavailable")
    };

    private static CameraAgentScientificExportResult Missing()
        => new(CameraAgentArtifactReadStatus.NotFound, "scientific-export.source-not-found");

    private static CameraAgentScientificExportResult Busy()
        => new(CameraAgentArtifactReadStatus.Unavailable, Capacity);

    private void LogIoFailure(ScientificExportFormat format, string reason, string operation, Exception exception)
    {
        // Exception messages, paths and inner exceptions can disclose the filesystem layout.
        var fault = exception as FileSystemFaultException;
        IoFailure(logger, format, reason, operation, fault?.Kind.ToString() ?? exception.GetType().Name, exception.HResult);
    }

    [LoggerMessage(EventId = 14722, Level = LogLevel.Warning,
        Message = "Scientific export {Format} failed with {ReasonCode} during {Operation}: {Cause} ({ErrorCode}).")]
    private static partial void IoFailure(ILogger logger, ScientificExportFormat format, string reasonCode,
        string operation, string cause, int errorCode);

    public void Dispose() => _slots.Dispose();
}
