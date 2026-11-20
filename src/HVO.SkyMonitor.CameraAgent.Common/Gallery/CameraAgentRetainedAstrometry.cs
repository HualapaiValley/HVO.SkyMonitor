using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

/// <summary>Capture-bound evidence only. A previous fit is never projected onto a new capture by this read API.</summary>
public sealed record CameraAgentRetainedAstrometry(Guid CaptureId, string Availability, string Reason,
    Guid? ArtifactId = null, string? SolutionIdentitySha256 = null, string? GeometryBasis = null,
    MeasuredAstrometrySolutionV1? Solution = null)
{
    // In-process verified projections only: never serialize catalog rows, measured inputs or raw buffers to the UI.
    [JsonIgnore] public AstrometricMapping? Mapping { get; init; }
    [JsonIgnore] public AstrometricResidualDiagnostics? Diagnostics { get; init; }
    [JsonIgnore] public ReconstructionDescriptor? SourceDescriptor { get; init; }
    public string? CalibrationVersion { get; init; }
    public string? ProjectionModel { get; init; }
    public bool HorizontalFlip { get; init; }
    public int WidthPixels { get; init; }
    public int HeightPixels { get; init; }
    public Guid? CompatibleBaseArtifactId { get; init; }
    public string DiagnosticsReason { get; init; } = "Compatible measured diagnostics are unavailable.";
}

/// <summary>Summary only; cannot supply geometry to another capture.</summary>
public sealed record CameraAgentLatestAstrometry(Guid CaptureId, Guid ArtifactId, string SolutionIdentitySha256,
    DateTimeOffset MidpointUtc, double RightAscensionHours, double DeclinationDegrees, double RollDegrees,
    PixelPoint? ReferencePixel, AstrometricLocalPixelScale LocalScale)
{
    public string? ScaleReference { get; init; }

    public static CameraAgentLatestAstrometry? FromVerified(CameraAgentRetainedAstrometry value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Mapping is not { } mapping || value.Solution is not { Assessment.HasMeasuredMapping: true } solution ||
            value.ArtifactId is not { } artifact || value.SolutionIdentitySha256 != solution.SolutionIdentitySha256 ||
            value.CaptureId != solution.Assessment.Frame.CaptureId) return null;
        var fit = solution.Assessment.Parameters!;
        var reference = ScaleAt(mapping.SkyToPixel(new(fit.BoresightRightAscensionHours, fit.BoresightDeclinationDegrees)), "measured boresight")
            ?? ScaleAt(new PixelPoint((value.WidthPixels - 1) / 2d, (value.HeightPixels - 1) / 2d), "source image center");
        if (reference is null && value.Diagnostics is { } diagnostics)
        {
            foreach (var residual in diagnostics.Residuals)
            {
                reference = ScaleAt(residual.Measured, "measured star");
                if (reference is not null) break;
            }
        }

        return new(value.CaptureId, artifact, solution.SolutionIdentitySha256, solution.Assessment.Frame.MidpointUtc,
            fit.BoresightRightAscensionHours, fit.BoresightDeclinationDegrees, fit.RollDegrees,
            reference?.Pixel, reference?.Scale ?? new(null, null))
        { ScaleReference = reference?.Label };

        ScaleSample? ScaleAt(PixelPoint? point, string label)
        {
            if (point is not { } pixel || pixel.X < 0 || pixel.Y < 0 ||
                pixel.X >= value.WidthPixels || pixel.Y >= value.HeightPixels || mapping.PixelToSky(pixel) is null) return null;
            var scale = mapping.LocalPixelScale(pixel);
            return scale is { XArcsecondsPerPixel: not null, YArcsecondsPerPixel: not null } ? new(pixel, scale, label) : null;
        }
    }

    private sealed record ScaleSample(PixelPoint Pixel, AstrometricLocalPixelScale Scale, string Label);
}

public sealed record CameraAgentLatestAstrometryRead(bool Completed, CameraAgentLatestAstrometry? Summary = null)
{
    public bool PreviousSummaryQuarantined { get; init; }
}

public interface ICameraAgentRetainedAstrometryService
{
    ValueTask<CameraAgentLatestAstrometry?> GetLatestAcceptedAsync(CancellationToken cancellationToken) => ValueTask.FromResult<CameraAgentLatestAstrometry?>(null);
    async ValueTask<CameraAgentLatestAstrometryRead> ReadLatestAcceptedAsync(CancellationToken cancellationToken)
        => new(true, await GetLatestAcceptedAsync(cancellationToken).ConfigureAwait(false));
    ValueTask<CameraAgentLatestAstrometryRead> ReadLatestAcceptedAsync(CameraAgentLatestAstrometry? previousSummary,
        CancellationToken cancellationToken) => ReadLatestAcceptedAsync(cancellationToken);
    ValueTask<CameraAgentRetainedAstrometry> GetAsync(Guid captureId, CancellationToken cancellationToken);
}

internal sealed class CameraAgentRetainedAstrometryService(SqliteCaptureProcessingStore store,
    ICameraAgentArtifactService artifacts, int maximumReadBytes = CameraAgentRetainedAstrometryService.MaximumReadBytes) : ICameraAgentRetainedAstrometryService, IDisposable
{
    internal const int MaximumRawBytes = 32 * 1024 * 1024;
    internal const int MaximumReadBytes = 44 * 1024 * 1024;
    private const int LatestNewRejectionLimit = 4096;
    private const string TemporaryEvidenceUnavailable = "retained-evidence-temporarily-unavailable";
    private const string InvalidEvidence = "retained-evidence-invalid";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim readGate = new(1, 1);
    private readonly SemaphoreSlim latestGate = new(1, 1);
    private IReadOnlyList<AstrometryCaptureCandidate>? latestSnapshot;
    private LinkedList<AstrometryCaptureCandidate> latestCandidates = [];
    private HashSet<AstrometryOutputStamp> rejectedOutputs = [];

    public void Dispose()
    {
        latestGate.Dispose();
        readGate.Dispose();
    }

    public async ValueTask<CameraAgentRetainedAstrometry> GetAsync(Guid captureId, CancellationToken cancellationToken)
    {
        // Do not accumulate raw buffers behind concurrent Current Sky/archive requests.
        if (!await readGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new(captureId, "Unavailable", "read-capacity");
        try { return await ReadAsync(captureId, cancellationToken).ConfigureAwait(false); }
        finally { readGate.Release(); }
    }

    public async ValueTask<CameraAgentLatestAstrometry?> GetLatestAcceptedAsync(CancellationToken cancellationToken)
        => (await ReadLatestAcceptedAsync(cancellationToken).ConfigureAwait(false)).Summary;

    public async ValueTask<CameraAgentLatestAstrometryRead> ReadLatestAcceptedAsync(CameraAgentLatestAstrometry? previousSummary,
        CancellationToken cancellationToken)
    {
        if (previousSummary is null) return await ReadLatestAcceptedAsync(cancellationToken).ConfigureAwait(false);

        var quarantined = false;
        try
        {
            // An interrupted candidate search cannot undo a durable decision about the displayed assessment.
            quarantined = await store.IsAstrometryQuarantinedAsync(previousSummary.CaptureId, previousSummary.ArtifactId,
                previousSummary.SolutionIdentitySha256, cancellationToken).ConfigureAwait(false);
            var read = await ReadLatestAcceptedAsync(cancellationToken).ConfigureAwait(false);
            return quarantined ? read with { PreviousSummaryQuarantined = true } : read;
        }
        catch (OperationCanceledException) when (quarantined)
        {
            return new(false) { PreviousSummaryQuarantined = true };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            return new(false) { PreviousSummaryQuarantined = quarantined };
        }
    }

    public async ValueTask<CameraAgentLatestAstrometryRead> ReadLatestAcceptedAsync(CancellationToken cancellationToken)
    {
        if (!await latestGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return new(false);
        try
        {
            var captures = await store.ReadAstrometryCandidatesAsync(cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(latestSnapshot, captures)) RefreshLatestCandidates(captures, cancellationToken);
            var node = latestCandidates.First;
            var newRejections = 0;
            while (node is not null && newRejections < LatestNewRejectionLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = node.Value;
                var value = await ReadAsync(candidate.CaptureId, cancellationToken, acceptedOnly: true).ConfigureAwait(false);
                if (value.Reason is "read-capacity" or TemporaryEvidenceUnavailable) return new(false);
                if (CameraAgentLatestAstrometry.FromVerified(value) is { } summary)
                    return await IsCurrentSnapshotAsync(captures, cancellationToken).ConfigureAwait(false) ? new(true, summary) : new(false);

                var next = node.Next;
                // An exact immutable rejection cannot become an accepted fit. A pending, invalid, ambiguous or
                // temporarily unavailable output remains eligible. Removal happens only after successful validation,
                // so independently canceled refreshes retain progress without treating an interrupted probe as empty.
                if (candidate.SoleOutput is { } output && value.Reason == "no-accepted-mapping" &&
                    value.ArtifactId == output.ArtifactId &&
                    string.Equals(value.SolutionIdentitySha256, output.ContentIdentitySha256, StringComparison.OrdinalIgnoreCase))
                {
                    rejectedOutputs.Add(output);
                    latestCandidates.Remove(node);
                    newRejections++;
                }
                node = next;
            }
            if (node is not null) return new(false);
            return new(await IsCurrentSnapshotAsync(captures, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            return new(false);
        }
        finally { latestGate.Release(); }
    }

    private async ValueTask<bool> IsCurrentSnapshotAsync(IReadOnlyList<AstrometryCaptureCandidate> snapshot,
        CancellationToken cancellationToken)
        => ReferenceEquals(snapshot, await store.ReadAstrometryCandidatesAsync(cancellationToken).ConfigureAwait(false));

    private void RefreshLatestCandidates(IReadOnlyList<AstrometryCaptureCandidate> snapshot, CancellationToken cancellationToken)
    {
        var pending = new LinkedList<AstrometryCaptureCandidate>();
        var retained = new HashSet<AstrometryOutputStamp>();
        foreach (var candidate in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.SoleOutput is { } output && rejectedOutputs.Contains(output)) retained.Add(output);
            else pending.AddLast(candidate);
        }
        latestCandidates = pending;
        rejectedOutputs = retained;
        latestSnapshot = snapshot;
    }

    private async ValueTask<CameraAgentRetainedAstrometry> ReadAsync(Guid captureId, CancellationToken cancellationToken, bool acceptedOnly = false)
    {
        if (captureId == Guid.Empty) return new(captureId, "Unavailable", "invalid-capture");
        var acquiredRawRead = false;
        try
        {
            var candidates = await store.ReadCaptureProductsAsync(captureId,
                MeasuredAstrometrySolutionV1.CurrentSchemaVersion, 2, cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0) return new(captureId, "NotRetained", "assessment-not-retained");
            if (candidates.Count != 1) return new(captureId, "Unavailable", "assessment-ambiguous");
            var assessment = candidates[0];
            if (assessment.AvailabilityState == "Quarantined") return new(captureId, "Unavailable", InvalidEvidence);
            if (assessment.AvailabilityState != "Available") return new(captureId, "Unavailable", TemporaryEvidenceUnavailable);
            var remaining = Math.Min(maximumReadBytes, MaximumReadBytes);
            var (solutionArtifact, solutionProduct) = await ReadProductAsync(assessment.ArtifactId,
                MeasuredAstrometryJson.MaximumSolutionBytes).ConfigureAwait(false);
            var solution = MeasuredAstrometryJson.Parse(solutionArtifact.Payload);
            if (solution.Assessment.Frame.CaptureId != captureId || solutionProduct.SourceArtifactIds.Count != 4)
                throw new InvalidDataException("Retained assessment source mismatch.");
            if (acceptedOnly)
            {
                if (!solution.Assessment.HasMeasuredMapping)
                {
                    var identity = solution.SolutionIdentitySha256;
                    if (!string.Equals(identity, solutionProduct.ContentIdentitySha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Retained assessment content identity mismatch.");
                    return new(captureId, "NotRetained", "no-accepted-mapping", solutionArtifact.ArtifactId, identity);
                }
                // Rejection probes read one bounded metadata object. Only raw reconstruction contends with the
                // selected-frame reader for the larger raw-read allowance.
                acquiredRawRead = await readGate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
                if (!acquiredRawRead) return new(captureId, "Unavailable", "read-capacity");
            }
            var rawId = solution.Assessment.Frame.SourceArtifactId;
            var rawRead = await OpenArtifactAsync(rawId).ConfigureAwait(false);
            var rawContent = rawRead.Content ?? throw new InvalidDataException("The retained raw is absent.");
            await using var rawLease = rawContent.ConfigureAwait(false);
            var descriptor = rawContent.Descriptor ?? throw new InvalidDataException("Missing raw descriptor.");
            if (rawRead.Status != CameraAgentArtifactReadStatus.Found || rawContent.CaptureId != captureId ||
                rawContent.Role != FrameArtifactRole.Raw || descriptor.Artifact.ArtifactId != rawId ||
                CaptureContractJson.ComputeDescriptorSha256(descriptor) != solution.Assessment.Frame.SourceDescriptorSha256)
                throw new InvalidDataException("Raw descriptor mismatch.");
            var rawBytes = await ReadBytesAsync(rawContent, MaximumRawBytes).ConfigureAwait(false);
            var raw = new ProcessingArtifact(rawId, FrameArtifactRole.Raw, descriptor.Artifact.Variant,
                ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256,
                descriptor.Artifact.MediaType, descriptor.Layout, rawBytes, descriptor.Artifact.CreatedUtc,
                descriptor.Controls.EffectiveExposure, CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor),
                descriptor.Capture.CaptureSequence, descriptor.Artifact.SourceArtifactIds,
                descriptor.Timing.ExposureStartedUtc, descriptor.Timing.ExposureEndedUtc)
            { CaptureId = captureId, DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(descriptor) };
            var (measured, _) = await ReadProductAsync(solution.MeasuredArtifactId, StructuredProcessingProductDescriptorV1.MaximumPayloadBytes).ConfigureAwait(false);
            var (scene, _) = await ReadProductAsync(solutionProduct.SourceArtifactIds[2], StructuredProcessingProductDescriptorV1.MaximumPayloadBytes).ConfigureAwait(false);
            var (context, _) = await ReadProductAsync(solutionProduct.SourceArtifactIds[3], MeasuredAstrometryJson.MaximumContextBytes).ConfigureAwait(false);
            var options = solutionProduct.Recipe.Descriptor.Options.GetProperty("parameters").Deserialize<MeasuredAstrometryOptions>(Json)!;
            var request = RetainedAstrometryProducts.Request(raw, measured, scene, context, options, solutionProduct.Variant);
            solution = RetainedAstrometryProducts.Verify(request, solutionProduct, cancellationToken);
            var frozen = MeasuredAstrometryJson.ParseContext(context.Payload);
            var associations = MeasuredStellarAssociationJson.Parse(measured.Payload).Associations
                ?? throw new InvalidDataException("Invalid retained associations.");
            var calibration = new AstrometricCalibration(frozen.Projection, frozen.CalibrationVersion, frozen.ReadoutIdentitySha256);
            return new(captureId, "Retained", solution.Assessment.ReasonCode, solutionArtifact.ArtifactId,
                solution.SolutionIdentitySha256, solution.Assessment.HasMeasuredMapping ? "MeasuredFit" : "Unavailable", solution)
            {
                Mapping = solution.Assessment.HasMeasuredMapping ? MeasuredAstrometryJson.ReconstructMapping(solution, calibration, associations) : null,
                Diagnostics = solution.Assessment.HasMeasuredMapping
                    ? MeasuredAstrometryJson.ReconstructDiagnostics(solution, frozen, associations, options, cancellationToken) : null,
                SourceDescriptor = descriptor,
                CalibrationVersion = frozen.CalibrationVersion,
                ProjectionModel = frozen.Projection.Model.ToString(),
                HorizontalFlip = frozen.Projection.HorizontalFlip,
                WidthPixels = descriptor.Layout.Width,
                HeightPixels = descriptor.Layout.Height,
                DiagnosticsReason = solution.Assessment.HasMeasuredMapping ? "Source mapping to the displayed base has not been established." : "No accepted measured mapping for this capture."
            };

            async ValueTask<(ProcessingArtifact Artifact, ProcessingProduct Product)> ReadProductAsync(Guid id, int maximum)
            {
                var result = await OpenArtifactAsync(id).ConfigureAwait(false);
                var content = result.Content ?? throw new InvalidDataException("A retained input is absent.");
                await using var contentLease = content.ConfigureAwait(false);
                if (result.Status != CameraAgentArtifactReadStatus.Found || content.CaptureId != captureId || content.ProductManifest is not { } manifest)
                    throw new InvalidDataException("Invalid retained input envelope.");
                var bytes = await ReadBytesAsync(content, maximum).ConfigureAwait(false);
                var identity = ProcessingIdentity.CreateRecipeIdentity(manifest.Artifact.Recipe);
                var product = new ProcessingProduct(manifest.Artifact.Role, manifest.Artifact.Variant!, manifest.OutputIdentitySha256,
                    manifest.Artifact.MediaType, null, bytes, manifest.Artifact.ChecksumSha256, identity, manifest.Algorithms,
                    manifest.Artifact.SourceArtifactIds, TimeSpan.FromTicks(manifest.TotalIntegrationTicks), manifest.Compatibility)
                { Kind = manifest.Kind, SchemaVersion = manifest.ProductSchemaVersion, ContentIdentitySha256 = manifest.ContentIdentitySha256 };
                var artifact = new ProcessingArtifact(id, product.Role, product.Variant, identity.IdentitySha256, product.MediaType,
                    null, bytes, manifest.Artifact.CreatedUtc, product.TotalIntegration, product.Compatibility,
                    SourceArtifactIds: product.SourceArtifactIds)
                {
                    CaptureId = captureId,
                    ProductKind = product.Kind,
                    SchemaVersion = product.SchemaVersion,
                    ContentIdentitySha256 = product.ContentIdentitySha256,
                    RecipeDescriptor = identity.Descriptor
                };
                return (artifact, product);
            }

            async ValueTask<CameraAgentArtifactContentResult> OpenArtifactAsync(Guid id)
            {
                var result = await artifacts.OpenContentAsync(id, cancellationToken).ConfigureAwait(false);
                if (result.Status != CameraAgentArtifactReadStatus.Unavailable) return result;
                if (result.Content is { } content) await content.DisposeAsync().ConfigureAwait(false);
                // Unavailable can also mean reconciliation quarantined a required input or the assessment
                // changed after candidate discovery. Only a confirmed quarantine lets the search advance.
                if (await store.ReadOutputByArtifactIdAsync(id, cancellationToken).ConfigureAwait(false) is { AvailabilityState: "Quarantined" })
                    throw new InvalidDataException("The retained artifact is quarantined.");
                throw new IOException("The retained artifact is temporarily unavailable.");
            }

            async ValueTask<byte[]> ReadBytesAsync(CameraAgentArtifactContentStream content, int maximum)
            {
                if (content.ByteLength is <= 0 || content.ByteLength > maximum || content.ByteLength > remaining)
                    throw new InvalidDataException("Retained astrometry read exceeds its byte bound.");
                remaining -= checked((int)content.ByteLength);
                var bytes = new byte[(int)content.ByteLength];
                await content.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                return bytes;
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or InvalidOperationException or JsonException or OverflowException)
        {
            return new(captureId, "Unavailable", InvalidEvidence);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            return new(captureId, "Unavailable", TemporaryEvidenceUnavailable);
        }
        finally
        {
            if (acquiredRawRead) readGate.Release();
        }
    }
}
