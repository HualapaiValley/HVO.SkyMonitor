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
    internal AstrometryLineageInvalidation? LineageInvalidation { get; init; }
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

/// <summary>Exact identity of a previously verified retained fit.</summary>
public sealed record CameraAgentAstrometryAssessmentIdentity(Guid CaptureId, Guid ArtifactId, string SolutionIdentitySha256);

/// <summary>Publication eligibility of one exact retained measurement and its required inputs.</summary>
public enum CameraAgentAstrometryPublicationState
{
    Indeterminate,
    Available,
    Invalidated
}

internal sealed record AstrometryLineageInvalidation(AstrometryOutputStamp Assessment, RetainedAstrometryLineageState State);

public sealed record CameraAgentLatestAstrometryRead(bool Completed, CameraAgentLatestAstrometry? Summary = null)
{
    /// <summary>The read confirmed quarantine of the supplied previous assessment or one of its required inputs.</summary>
    public bool PreviousSummaryQuarantined { get; init; }
    public bool PreviousSummaryEvicted { get; init; }
    public bool PreviousSummaryInvalidated => PreviousSummaryQuarantined || PreviousSummaryEvicted;
    /// <summary>The read confirmed quarantine or eviction of the supplied selected fit or a required input.</summary>
    public bool SelectedAssessmentInvalidated { get; init; }
}

public interface ICameraAgentRetainedAstrometryService
{
    ValueTask<CameraAgentLatestAstrometry?> GetLatestAcceptedAsync(CancellationToken cancellationToken) => ValueTask.FromResult<CameraAgentLatestAstrometry?>(null);
    async ValueTask<CameraAgentLatestAstrometryRead> ReadLatestAcceptedAsync(CancellationToken cancellationToken)
        => new(true, await GetLatestAcceptedAsync(cancellationToken).ConfigureAwait(false));
    ValueTask<CameraAgentLatestAstrometryRead> ReadLatestAcceptedAsync(
        CameraAgentLatestAstrometry? previousSummary, CancellationToken cancellationToken)
        => ReadLatestAcceptedAsync(cancellationToken);
    ValueTask<CameraAgentLatestAstrometryRead> ReadLatestAcceptedAsync(CameraAgentLatestAstrometry? previousSummary,
        CameraAgentAstrometryAssessmentIdentity? selectedAssessment, CancellationToken cancellationToken)
        => ReadLatestAcceptedAsync(previousSummary, cancellationToken);
    ValueTask<CameraAgentAstrometryPublicationState> ReadPublicationStateAsync(
        CameraAgentAstrometryAssessmentIdentity identity, CancellationToken cancellationToken)
        => ValueTask.FromResult(CameraAgentAstrometryPublicationState.Indeterminate);
    ValueTask<CameraAgentRetainedAstrometry> GetAsync(Guid captureId, CancellationToken cancellationToken);

    ValueTask<CameraAgentRetainedAstrometry> GetExactAsync(Guid captureId, CenteredViewArtifact evidence,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new CameraAgentRetainedAstrometry(captureId, "Unavailable", "exact-assessment-unavailable"));
}

internal sealed class CameraAgentRetainedAstrometryService(SqliteCaptureProcessingStore store,
    ICameraAgentArtifactService artifacts, int maximumReadBytes = CameraAgentRetainedAstrometryService.MaximumReadBytes) : ICameraAgentRetainedAstrometryService, IDisposable
{
    internal const int MaximumRawBytes = 32 * 1024 * 1024;
    internal const int MaximumReadBytes = 44 * 1024 * 1024;
    private const int LatestNewSkipLimit = 4096;
    private const int LatestSkipRecheckLimit = 64;
    internal const string TemporaryEvidenceUnavailable = "retained-evidence-temporarily-unavailable";
    private const string InvalidEvidence = "retained-evidence-invalid";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim readGate = new(1, 1);
    private readonly SemaphoreSlim latestGate = new(1, 1);
    private IReadOnlyList<AstrometryCaptureCandidate>? latestSnapshot;
    private LinkedList<AstrometryCaptureCandidate> latestCandidates = [];
    // An incremental store refresh retains the object for each unchanged capture. A changed capture
    // receives a new object even if the assessment itself still has the same artifact identity.
    private Dictionary<Guid, CandidateSkip> skippedCandidates = [];
    private Queue<AstrometrySkipCheck> skipsToRecheck = new();

    private readonly record struct CandidateProbe(CameraAgentRetainedAstrometry Evidence,
        AstrometrySkipCheck? Skip = null, bool Retry = false);

    private sealed record CandidateSkip(AstrometryCaptureCandidate Candidate, AstrometryLineageInvalidation? Lineage);

    public void Dispose()
    {
        latestGate.Dispose();
        readGate.Dispose();
    }

    public async ValueTask<CameraAgentAstrometryPublicationState> ReadPublicationStateAsync(
        CameraAgentAstrometryAssessmentIdentity identity, CancellationToken cancellationToken)
    {
        var states = await ReadLineageAsync(null, null, identity, cancellationToken).ConfigureAwait(false);
        return states?.Result switch
        {
            RetainedAstrometryLineageState.Available => CameraAgentAstrometryPublicationState.Available,
            RetainedAstrometryLineageState.Quarantined or RetainedAstrometryLineageState.Evicted => CameraAgentAstrometryPublicationState.Invalidated,
            _ => CameraAgentAstrometryPublicationState.Indeterminate
        };
    }

    public ValueTask<CameraAgentRetainedAstrometry> GetAsync(Guid captureId, CancellationToken cancellationToken) =>
        GetBoundAsync(captureId, null, cancellationToken);

    public ValueTask<CameraAgentRetainedAstrometry> GetExactAsync(Guid captureId, CenteredViewArtifact evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return GetBoundAsync(captureId, evidence, cancellationToken);
    }

    private async ValueTask<CameraAgentRetainedAstrometry> GetBoundAsync(Guid captureId, CenteredViewArtifact? exact,
        CancellationToken cancellationToken)
    {
        // Do not accumulate raw buffers behind concurrent Current Sky/archive requests.
        if (!await readGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new(captureId, "Unavailable", "read-capacity");
        try
        {
            var view = (await ReadAsync(captureId, cancellationToken, exact: exact).ConfigureAwait(false)).Evidence;
            if (view.Solution is null || view.ArtifactId is not { } artifact || view.SolutionIdentitySha256 is not { } solution)
                return view;

            var lineage = await ReadLineageAsync(null, null, new(captureId, artifact, solution), cancellationToken).ConfigureAwait(false);
            var state = lineage?.Result ?? RetainedAstrometryLineageState.Unknown;
            return state == RetainedAstrometryLineageState.Available
                ? view : new(captureId, "Unavailable", IsInvalid(state) ? InvalidEvidence : TemporaryEvidenceUnavailable);
        }
        finally { readGate.Release(); }
    }

    public async ValueTask<CameraAgentLatestAstrometry?> GetLatestAcceptedAsync(CancellationToken cancellationToken)
        => (await ReadLatestAcceptedAsync(cancellationToken).ConfigureAwait(false)).Summary;

    public ValueTask<CameraAgentLatestAstrometryRead> ReadLatestAcceptedAsync(CancellationToken cancellationToken)
        => ReadLatestAcceptedAsync(null, null, cancellationToken);

    public ValueTask<CameraAgentLatestAstrometryRead> ReadLatestAcceptedAsync(CameraAgentLatestAstrometry? previousSummary,
        CancellationToken cancellationToken)
        => ReadLatestAcceptedAsync(previousSummary, null, cancellationToken);

    public async ValueTask<CameraAgentLatestAstrometryRead> ReadLatestAcceptedAsync(CameraAgentLatestAstrometry? previousSummary,
        CameraAgentAstrometryAssessmentIdentity? selectedAssessment, CancellationToken cancellationToken)
    {
        var previous = IdentityOf(previousSummary);
        AstrometryLineageStates initial = default;
        AstrometryLineageStates current = default;
        var read = new CameraAgentLatestAstrometryRead(false);
        try
        {
            initial = await ReadLineageAsync(previous, selectedAssessment, null, cancellationToken).ConfigureAwait(false) ?? default;
            read = await ReadLatestAcceptedCoreAsync(previous, selectedAssessment, cancellationToken).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested)
                current = await ReadLineageAsync(previous, selectedAssessment, IdentityOf(read.Summary), cancellationToken).ConfigureAwait(false) ?? default;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested &&
            (IsInvalid(initial.Previous) || IsInvalid(initial.Selected) ||
             read.PreviousSummaryInvalidated || read.SelectedAssessmentInvalidated))
        {
            read = read with { Completed = false, Summary = null };
        }

        var previousState = current.Previous != RetainedAstrometryLineageState.Unknown ? current.Previous :
            read.PreviousSummaryQuarantined ? RetainedAstrometryLineageState.Quarantined :
            read.PreviousSummaryEvicted ? RetainedAstrometryLineageState.Evicted : initial.Previous;
        var selectedInvalid = current.Selected != RetainedAstrometryLineageState.Unknown
            ? IsInvalid(current.Selected) : read.SelectedAssessmentInvalidated || IsInvalid(initial.Selected);
        read = read with
        {
            PreviousSummaryQuarantined = previousState == RetainedAstrometryLineageState.Quarantined,
            PreviousSummaryEvicted = previousState == RetainedAstrometryLineageState.Evicted,
            SelectedAssessmentInvalidated = selectedInvalid
        };
        return read.Summary is not null && current.Result != RetainedAstrometryLineageState.Available
            ? read with { Completed = false, Summary = null } : read;
    }

    private static CameraAgentAstrometryAssessmentIdentity? IdentityOf(CameraAgentLatestAstrometry? summary)
        => summary is null ? null : new(summary.CaptureId, summary.ArtifactId, summary.SolutionIdentitySha256);

    private static bool IsInvalid(RetainedAstrometryLineageState state)
        => state is RetainedAstrometryLineageState.Quarantined or RetainedAstrometryLineageState.Evicted;

    private async ValueTask<AstrometryLineageStates?> ReadLineageAsync(
        CameraAgentAstrometryAssessmentIdentity? previous, CameraAgentAstrometryAssessmentIdentity? selected,
        CameraAgentAstrometryAssessmentIdentity? result, CancellationToken cancellationToken)
    {
        try
        {
            return await store.ReadAstrometryLineageAsync(previous, selected, result, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            return null;
        }
    }

    private async ValueTask<CameraAgentLatestAstrometryRead> ReadLatestAcceptedCoreAsync(
        CameraAgentAstrometryAssessmentIdentity? previous, CameraAgentAstrometryAssessmentIdentity? selected, CancellationToken cancellationToken)
    {
        if (!await latestGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return new(false);
        try
        {
            var captures = await store.ReadAstrometryCandidatesAsync(cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(latestSnapshot, captures)) RefreshLatestCandidates(captures, cancellationToken);
            await RecheckSkipsAsync(captures, cancellationToken).ConfigureAwait(false);
            var node = latestCandidates.First;
            var newSkips = 0;
            while (node is not null && newSkips < LatestNewSkipLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = node.Value;
                var probe = await ReadAsync(candidate.CaptureId, cancellationToken, acceptedOnly: true).ConfigureAwait(false);
                if (probe.Retry)
                    return LatestRead(false, null, previous, selected);
                if (CameraAgentLatestAstrometry.FromVerified(probe.Evidence) is { } summary)
                    return await CompleteLatestReadAsync(captures, summary, previous, selected, cancellationToken).ConfigureAwait(false);

                var next = node.Next;
                // Commit a conclusive probe before the next cancellation check. Unverified
                // failures stay on the work list so a later request can observe recovery.
                if (probe.Skip is { } skip)
                {
                    skippedCandidates.Add(candidate.CaptureId, new(candidate, probe.Evidence.LineageInvalidation));
                    skipsToRecheck.Enqueue(skip);
                    latestCandidates.Remove(node);
                    newSkips++;
                }
                node = next;
            }
            return node is null
                ? await CompleteLatestReadAsync(captures, null, previous, selected, cancellationToken).ConfigureAwait(false)
                : LatestRead(false, null, previous, selected);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested &&
            (IsInvalid(CachedInvalidation(previous)) || IsInvalid(CachedInvalidation(selected))))
        {
            return LatestRead(false, null, previous, selected);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            return LatestRead(false, null, previous, selected);
        }
        finally { latestGate.Release(); }
    }

    private CameraAgentLatestAstrometryRead LatestRead(bool completed, CameraAgentLatestAstrometry? summary,
        CameraAgentAstrometryAssessmentIdentity? previous, CameraAgentAstrometryAssessmentIdentity? selected)
    {
        var previousState = CachedInvalidation(previous);
        return new(completed, summary)
        {
            PreviousSummaryQuarantined = previousState == RetainedAstrometryLineageState.Quarantined,
            PreviousSummaryEvicted = previousState == RetainedAstrometryLineageState.Evicted,
            SelectedAssessmentInvalidated = IsInvalid(CachedInvalidation(selected))
        };
    }

    private async ValueTask<CameraAgentLatestAstrometryRead> CompleteLatestReadAsync(
        IReadOnlyList<AstrometryCaptureCandidate> snapshot, CameraAgentLatestAstrometry? summary,
        CameraAgentAstrometryAssessmentIdentity? previous, CameraAgentAstrometryAssessmentIdentity? selected, CancellationToken cancellationToken)
    {
        var current = await store.ReadAstrometryCandidatesAsync(cancellationToken).ConfigureAwait(false);
        if (ReferenceEquals(snapshot, current)) return LatestRead(true, summary, previous, selected);

        RefreshLatestCandidates(current, cancellationToken);
        return LatestRead(false, null, previous, selected);
    }

    private RetainedAstrometryLineageState CachedInvalidation(CameraAgentAstrometryAssessmentIdentity? identity)
        => identity is not null && latestSnapshot is { } snapshot &&
           skippedCandidates.TryGetValue(identity.CaptureId, out var skip) && skip.Lineage is { } proof &&
           proof.Assessment.ArtifactId == identity.ArtifactId &&
           string.Equals(proof.Assessment.ContentIdentitySha256, identity.SolutionIdentitySha256, StringComparison.OrdinalIgnoreCase) &&
           store.IsCurrentAstrometrySnapshotForCapture(snapshot, identity.CaptureId)
               ? proof.State : RetainedAstrometryLineageState.Unknown;

    private void RefreshLatestCandidates(IReadOnlyList<AstrometryCaptureCandidate> snapshot, CancellationToken cancellationToken)
    {
        var pending = new LinkedList<AstrometryCaptureCandidate>();
        var retained = new Dictionary<Guid, CandidateSkip>();
        foreach (var candidate in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (skippedCandidates.TryGetValue(candidate.CaptureId, out var skip) && ReferenceEquals(skip.Candidate, candidate))
                retained.Add(candidate.CaptureId, skip);
            else pending.AddLast(candidate);
        }
        latestCandidates = pending;
        skippedCandidates = retained;
        skipsToRecheck = new(skipsToRecheck.Where(check => retained.ContainsKey(check.CaptureId)));
        latestSnapshot = snapshot;
    }

    private async ValueTask RecheckSkipsAsync(IReadOnlyList<AstrometryCaptureCandidate> snapshot, CancellationToken cancellationToken)
    {
        if (skipsToRecheck.Count == 0) return;

        // Another store can change public selection or restore its inputs without updating
        // our snapshot. Recheck only a bounded batch of metadata proofs, without opening artifacts.
        var batch = skipsToRecheck.Take(LatestSkipRecheckLimit).ToArray();
        var changed = await store.ReadAstrometrySkipChangesAsync(batch, cancellationToken).ConfigureAwait(false);
        if (changed.Count > 0)
        {
            var pending = new LinkedList<AstrometryCaptureCandidate>();
            foreach (var candidate in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!skippedCandidates.ContainsKey(candidate.CaptureId) || changed.Contains(candidate.CaptureId))
                    pending.AddLast(candidate);
            }
            latestCandidates = pending;
            foreach (var captureId in changed) skippedCandidates.Remove(captureId);
        }
        for (var i = 0; i < batch.Length; i++)
        {
            var check = skipsToRecheck.Dequeue();
            if (!changed.Contains(check.CaptureId)) skipsToRecheck.Enqueue(check);
        }
    }

    private async ValueTask<AstrometryAssessmentSelection> ReadPinnedAssessmentAsync(
        Guid captureId, CenteredViewArtifact pin, CancellationToken cancellationToken)
    {
        var output = await store.ReadOutputByArtifactIdAsync(pin.ArtifactId, cancellationToken).ConfigureAwait(false);
        if (output is null)
            return new(AstrometryAssessmentSelectionKind.Absent);
        if (output.Capture.CaptureId != captureId || output.ProductSchemaVersion != MeasuredAstrometrySolutionV1.CurrentSchemaVersion ||
            output.OutputIdentitySha256 != pin.IdentitySha256 || output.Artifact.ChecksumSha256 != pin.PayloadSha256 ||
            output.ContentIdentitySha256 is not { } contentIdentity)
            throw new InvalidDataException("The retained astrometry journal differs from the committed evidence pin.");
        return new(AstrometryAssessmentSelectionKind.Public, pin.ArtifactId, contentIdentity, output.AvailabilityState);
    }

    private async ValueTask<CandidateProbe> ReadAsync(Guid captureId, CancellationToken cancellationToken,
        bool acceptedOnly = false, CenteredViewArtifact? exact = null)
    {
        if (captureId == Guid.Empty) return new(new(captureId, "Unavailable", "invalid-capture"));
        var acquiredRawRead = false;
        AstrometryAssessmentSelection assessment = default;
        try
        {
            assessment = exact is null
                ? await store.ReadAstrometryAssessmentAsync(captureId, cancellationToken).ConfigureAwait(false)
                : await ReadPinnedAssessmentAsync(captureId, exact, cancellationToken).ConfigureAwait(false);
            switch (assessment.Kind)
            {
                case AstrometryAssessmentSelectionKind.Absent:
                    return new(new(captureId, "NotRetained", "assessment-not-retained"),
                        new(captureId, AstrometryAssessmentSelectionKind.Absent));
                case AstrometryAssessmentSelectionKind.Pending:
                    return Unavailable(temporary: true);
                case AstrometryAssessmentSelectionKind.Ambiguous:
                    return new(new(captureId, "Unavailable", "assessment-ambiguous"),
                        new(captureId, AstrometryAssessmentSelectionKind.Ambiguous));
            }
            if (assessment.AvailabilityState == "Quarantined")
                return Invalidated(assessment, RetainedAstrometryLineageState.Quarantined);
            if (assessment.AvailabilityState != "Available")
                return await ReadFailedEvidenceAsync(temporary: true).ConfigureAwait(false);
            var remaining = Math.Min(maximumReadBytes, MaximumReadBytes);
            var (solutionArtifact, solutionProduct) = await ReadProductAsync(assessment.ArtifactId,
                MeasuredAstrometryJson.MaximumSolutionBytes).ConfigureAwait(false);
            if (exact is not null && (exact.IdentitySha256 != solutionProduct.OutputIdentitySha256 ||
                exact.PayloadSha256 != solutionProduct.ChecksumSha256 ||
                solutionProduct.SchemaVersion != MeasuredAstrometrySolutionV1.CurrentSchemaVersion))
                throw new InvalidDataException("The astrometric solution differs from the committed evidence pin.");
            var solution = MeasuredAstrometryJson.Parse(solutionArtifact.Payload);
            if (solution.Assessment.Frame.CaptureId != captureId || solutionProduct.SourceArtifactIds.Count != 4 ||
                solutionProduct.SourceArtifactIds[0] != solution.Assessment.Frame.SourceArtifactId ||
                solutionProduct.SourceArtifactIds[1] != solution.MeasuredArtifactId)
                throw new InvalidDataException("Retained assessment source mismatch.");
            if (!string.Equals(solution.SolutionIdentitySha256, solutionProduct.ContentIdentitySha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(solution.SolutionIdentitySha256, assessment.ContentIdentitySha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Retained assessment content identity mismatch.");
            if (acceptedOnly)
            {
                if (!solution.Assessment.HasMeasuredMapping)
                    return new(new(captureId, "NotRetained", "no-accepted-mapping", solutionArtifact.ArtifactId,
                        solution.SolutionIdentitySha256),
                        new(captureId, AstrometryAssessmentSelectionKind.Public,
                            new(solutionArtifact.ArtifactId, solution.SolutionIdentitySha256)));
                // Rejection probes read one bounded metadata object. Only raw reconstruction contends with the
                // selected-frame reader for the larger raw-read allowance.
                acquiredRawRead = await readGate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
                if (!acquiredRawRead) return new(new(captureId, "Unavailable", "read-capacity"), Retry: true);
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
            var evidence = new CameraAgentRetainedAstrometry(captureId, "Retained", solution.Assessment.ReasonCode, solutionArtifact.ArtifactId,
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
            return new(evidence);

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
            return await ReadFailedEvidenceAsync(temporary: false).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            return await ReadFailedEvidenceAsync(temporary: true).ConfigureAwait(false);
        }
        finally
        {
            if (acquiredRawRead) readGate.Release();
        }

        async ValueTask<CandidateProbe> ReadFailedEvidenceAsync(bool temporary)
        {
            // The artifact reads and their leases have ended. The journal lookup needs no raw-read allowance.
            if (acquiredRawRead)
            {
                readGate.Release();
                acquiredRawRead = false;
            }
            if (assessment.ContentIdentitySha256 is not { } identity)
                return Unavailable(temporary);

            // The failed open describes one artifact. Read the complete durable lineage before
            // deciding whether this capture is a definite skip or a candidate that should retry.
            var lineage = await ReadLineageAsync(null, null, new(captureId, assessment.ArtifactId, identity),
                cancellationToken).ConfigureAwait(false);
            var state = lineage?.Result;
            if (state is null or RetainedAstrometryLineageState.Pending)
                return Unavailable(temporary: true);

            // Malformed lineage is insufficient to authorize emission or cache a skip. The
            // original artifact failure still describes whether this probe should retry.
            return IsInvalid(state.Value) ? Invalidated(assessment, state.Value) : Unavailable(temporary);
        }

        CandidateProbe Unavailable(bool temporary)
            => new(new(captureId, "Unavailable", temporary ? TemporaryEvidenceUnavailable : InvalidEvidence),
                Retry: temporary);

        CandidateProbe Invalidated(AstrometryAssessmentSelection product, RetainedAstrometryLineageState state)
        {
            if (product.ContentIdentitySha256 is not { } identity)
                return Unavailable(temporary: false);
            var stamp = new AstrometryOutputStamp(product.ArtifactId, identity);
            return new(new CameraAgentRetainedAstrometry(captureId, "Unavailable", InvalidEvidence)
            {
                LineageInvalidation = new(stamp, state)
            }, new(captureId, AstrometryAssessmentSelectionKind.Public, stamp, state));
        }
    }
}
