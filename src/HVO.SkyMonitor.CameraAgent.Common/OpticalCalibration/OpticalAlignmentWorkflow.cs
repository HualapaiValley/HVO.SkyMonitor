using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;

namespace HVO.SkyMonitor.CameraAgent.Common.OpticalCalibration;

public sealed record OpticalAlignmentCaptureEvidence(CameraAgentAstrometryAssessmentIdentity Assessment,
    string RigIdentitySha256, OpticalCalibrationFrame Frame, AstrometricCatalogData Catalog, AstrometricClockFacts Clock,
    AstrometricSolverOptions Solver, AstrometricAssessmentStatus Status, double? VerificationRmsPixels);

public sealed record OpticalAlignmentCaptureProvenance(CameraAgentAstrometryAssessmentIdentity Assessment,
    AstrometricFrameContext Frame, string ReadoutIdentitySha256, AstrometricClockFacts Clock);

/// <summary>Compact evidence retained atomically with the accepted named rig draft.</summary>
public sealed record OpticalAlignmentProvenance(string FitIdentitySha256, string InputIdentitySha256, string FitterVersion,
    string FitterSettingsIdentitySha256, string SolverVersion, string SolverSettingsIdentitySha256,
    string CatalogIdentitySha256, string CatalogSelectionIdentitySha256, int TrainingCount,
    IReadOnlyList<OpticalAlignmentCaptureProvenance> Captures, OpticalCalibrationDiagnostics Diagnostics,
    OpticalCalibrationCovariance? SharedCovariance);

public sealed class OpticalAlignmentProposal
{
    internal OpticalAlignmentProposal(VirtualOpticalCalibrationReview review, OpticalCalibrationResult result,
        IReadOnlyList<OpticalAlignmentCaptureEvidence> captures, int trainingCount)
    {
        Review = review;
        Result = result;
        Captures = captures;
        TrainingCount = trainingCount;
    }

    public VirtualOpticalCalibrationReview Review { get; }
    public OpticalCalibrationResult Result { get; }
    public IReadOnlyList<OpticalAlignmentCaptureEvidence> Captures { get; }
    public int TrainingCount { get; }
}

public sealed class OpticalAlignmentFitInProgressException : InvalidOperationException
{
    public OpticalAlignmentFitInProgressException() : base("An optical alignment fit is already running.") { }
    public OpticalAlignmentFitInProgressException(string message) : base(message) { }
    public OpticalAlignmentFitInProgressException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Bounded operator-requested fitting over verified archive evidence for the active named rig.</summary>
public sealed class OpticalAlignmentWorkflow(SqliteNamedRigProfileStore store,
    ICameraAgentRetainedAstrometryService astrometry, VirtualOpticalCalibrationReviewService reviewService) : IDisposable
{
    public const int MinimumTrainingCaptures = 3;
    public const int MaximumTrainingCaptures = 8;
    public const int MaximumValidationCaptures = 4;
    private readonly SemaphoreSlim _fitSlot = new(1, 1);

    public async Task<OpticalAlignmentProposal> FitAsync(IReadOnlyList<Guid> training, IReadOnlyList<Guid> validation,
        bool fitRadialDistortion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(training);
        ArgumentNullException.ThrowIfNull(validation);
        if (training.Count is < MinimumTrainingCaptures or > MaximumTrainingCaptures ||
            validation.Count is < 1 or > MaximumValidationCaptures)
            throw new ArgumentException("Choose three to eight training captures and one to four held-out captures.");
        var trainingCount = training.Count;
        var ids = training.Concat(validation).ToArray();
        if (ids.Contains(Guid.Empty) || ids.Distinct().Count() != ids.Length)
            throw new ArgumentException("Training and held-out capture identities must be nonempty and distinct.");
        if (!await _fitSlot.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new OpticalAlignmentFitInProgressException();

        try
        {
            var catalog = await store.GetAsync(cancellationToken).ConfigureAwait(false);
            var active = catalog.Revisions.SingleOrDefault(r => r.RevisionId == catalog.Selection.ActiveRevisionId)
                ?? throw new ArgumentException("Select an active named rig first.");
            if (catalog.Selection.PendingRevisionId is not null)
                throw new CaptureScheduleStoreConflictException("Resolve the pending rig revision before fitting.");
            var rigIdentity = CameraRigProfileIdentity.ComputeSha256(active.Rig);
            var captures = new OpticalAlignmentCaptureEvidence[ids.Length];
            for (var index = 0; index < captures.Length; index++)
            {
                var evidence = await astrometry.GetOpticalAlignmentInputAsync(ids[index], cancellationToken).ConfigureAwait(false);
                if (evidence is null || evidence.Frame.Frame.CaptureId != ids[index] ||
                    !string.Equals(evidence.RigIdentitySha256, rigIdentity, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Every capture needs verified associations and readout geometry from the active rig.");
                captures[index] = evidence;
            }

            var first = captures[0];
            if (captures.Any(capture => capture.Catalog.IdentitySha256 != first.Catalog.IdentitySha256 ||
                capture.Catalog.SelectionIdentitySha256 != first.Catalog.SelectionIdentitySha256 ||
                capture.Frame.Frame.ObserverIdentitySha256 != first.Frame.Frame.ObserverIdentitySha256 ||
                capture.Frame.Frame.Observer != first.Frame.Frame.Observer || capture.Solver != first.Solver))
                throw new ArgumentException("All captures must use the same site, catalog selection and solver settings.");

            var nominal = RigProjectionContextFactory.CreateNative(active.Rig);
            var frames = captures.Select(capture => capture.Frame).ToArray();
            var result = await Task.Run(() => OpticalCalibrationSession.Fit(nominal, frames[..trainingCount], frames[trainingCount..],
                first.Catalog, new(FitRadialDistortion: fitRadialDistortion, MaximumFrames: MaximumTrainingCaptures),
                first.Solver, cancellationToken), cancellationToken).ConfigureAwait(false);
            var review = await reviewService.ReviewAsync(result, cancellationToken).ConfigureAwait(false);
            if (review.RigRevisionId != active.RevisionId || review.SelectionVersion != catalog.Selection.Version)
                throw new CaptureScheduleStoreConflictException("The active rig changed during the fit.");
            return new(review, result, Array.AsReadOnly(captures), trainingCount);
        }
        finally
        {
            _fitSlot.Release();
        }
    }

    public async Task<VirtualOpticalCalibrationDecision> AcceptAsync(OpticalAlignmentProposal proposal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        foreach (var capture in proposal.Captures)
        {
            if (await astrometry.ReadPublicationStateAsync(capture.Assessment, cancellationToken).ConfigureAwait(false) !=
                CameraAgentAstrometryPublicationState.Available)
                throw new CaptureScheduleStoreConflictException("A source capture is no longer available for acceptance.");
        }
        var result = proposal.Result;
        var provenance = new OpticalAlignmentProvenance(result.IdentitySha256, result.InputIdentitySha256,
            OpticalCalibrationOptions.FitterVersion, result.OptionsIdentitySha256, AstrometricConventions.SolverVersion,
            result.SolverSettingsIdentitySha256, result.CatalogIdentitySha256, result.CatalogSelectionIdentitySha256,
            proposal.TrainingCount, proposal.Captures.Select(capture => new OpticalAlignmentCaptureProvenance(capture.Assessment,
                capture.Frame.Frame, capture.Frame.ReadoutIdentitySha256, capture.Clock)).ToArray(), result.Diagnostics, result.SharedCovariance);
        return await reviewService.AcceptAsync(proposal.Review, result, provenance, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _fitSlot.Dispose();
}
