using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.SiteProfile;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

public interface ICameraAgentScientificExports
{
    ValueTask<CameraAgentScientificExportResult> PrepareAsync(
        Guid captureId, Guid artifactId, ScientificExportFormat format, CancellationToken cancellationToken);
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

internal sealed class CameraAgentScientificExports(
    ICameraAgentArtifactService artifacts, ICameraAgentRetainedAstrometryService astrometry,
    IDeploymentLocationStore deployment, ISiteProfileStore siteProfile) : ICameraAgentScientificExports, IDisposable
{
    private readonly SemaphoreSlim _slots = new(2, 2);

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The result transfers the staged download and its slot to the response handler.")]
    public async ValueTask<CameraAgentScientificExportResult> PrepareAsync(
        Guid captureId, Guid artifactId, ScientificExportFormat format, CancellationToken cancellationToken)
    {
        if (captureId == Guid.Empty || artifactId == Guid.Empty)
            return Missing();
        if (!await _slots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return Busy();

        var handedOff = false;
        FileStream? output = null;
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

                var retained = await astrometry.GetAsync(captureId, cancellationToken).ConfigureAwait(false);
                if (retained.Reason == "read-capacity")
                    return Busy();
                var solved = retained is { Solution.Assessment.HasMeasuredMapping: true, Mapping: not null } &&
                    retained.Solution.Assessment.Frame.SourceArtifactId == artifactId &&
                    string.Equals(retained.Solution.Assessment.Frame.SourcePayloadSha256, source.ChecksumSha256, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(retained.Solution.Assessment.Frame.SourceDescriptorSha256,
                        CaptureContractJson.ComputeDescriptorSha256(descriptor), StringComparison.OrdinalIgnoreCase);
                var location = CameraAgentCaptureLocationProjector.Project(descriptor, deployment);
                var site = (await siteProfile.GetAsync(cancellationToken).ConfigureAwait(false)).Profile;
                var observation = new ScientificExportObservation(location.LatitudeDegrees, location.LongitudeDegrees, location.ElevationMeters,
                    site.ObservatoryName, site.OwnerName, solved ? retained.Mapping!.ToCelestialWcs() : null,
                    solved ? retained.SolutionIdentitySha256 : null);

                // Stage privately before any response bytes are committed, including on checksum failure or cancellation.
                // An unlinked Linux file is reclaimed even if the host exits during preparation or download.
                output = CreateSpool();
                receipt = await ScientificCaptureExport.WriteAsync(source, output, descriptor, format, observation, cancellationToken)
                    .ConfigureAwait(false);
            }
            var download = new CameraAgentScientificDownload(output, receipt, () => _slots.Release());
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
            return new(CameraAgentArtifactReadStatus.Unavailable, "scientific-export.io-unavailable");
        }
        finally
        {
            if (!handedOff)
            {
                try
                {
                    if (output is not null) await output.DisposeAsync().ConfigureAwait(false);
                }
                finally { _slots.Release(); }
            }
        }
    }

    private static FileStream CreateSpool()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hvo-science-{Guid.NewGuid():N}");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose,
            BufferSize = 64 * 1024
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var stream = new FileStream(path, options);
        try
        {
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
        => new(CameraAgentArtifactReadStatus.Unavailable, "scientific-export.capacity");

    public void Dispose() => _slots.Dispose();
}
