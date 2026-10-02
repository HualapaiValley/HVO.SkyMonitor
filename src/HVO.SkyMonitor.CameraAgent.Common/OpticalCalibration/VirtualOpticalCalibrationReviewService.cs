using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;

namespace HVO.SkyMonitor.CameraAgent.Common.OpticalCalibration;

/// <summary>
/// Operator-facing review of one session optical calibration against the active named rig. Only an accepted
/// calibration for an active virtual rig with no pending change can be accepted; every other case names its blocker.
/// </summary>
public sealed record VirtualOpticalCalibrationReview(
    string RigRevisionId,
    string ProfileId,
    long SelectionVersion,
    string OpticsRevisionId,
    string CalibrationIdentitySha256,
    OpticalCalibrationStatus Status,
    string ReasonCode,
    IReadOnlyList<string> Rejections,
    bool CanAccept,
    string? Blocker,
    OpticsProfile CurrentOptics,
    OpticsProfile? ProposedOptics,
    IReadOnlyList<OpticalCalibrationParameter> Parameters,
    OpticalCalibrationDiagnostics Diagnostics,
    string CatalogIdentitySha256,
    string OptionsIdentitySha256);

/// <summary>Outcome of an explicit review decision. The active rig revision is never changed by a decision.</summary>
public sealed record VirtualOpticalCalibrationDecision(
    string Decision,
    string CalibrationIdentitySha256,
    string ActiveRigRevisionId,
    string? CandidateRigRevisionId,
    string? CandidateOpticsRevisionId);

/// <summary>
/// Retains accepted virtual-rig calibrations as immutable optics and rig revisions. Accepting composes a draft
/// rig revision that activates only through the ordinary named-rig preview and restart path; rejecting or any
/// failure writes nothing, so the previously accepted calibration stays active and retrievable.
/// </summary>
public sealed class VirtualOpticalCalibrationReviewService(SqliteNamedRigProfileStore store)
{
    public const string VirtualModuleType = "VirtualSky";

    public static string CalibrationVersion(OpticalCalibrationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"{OpticalCalibrationOptions.FitterVersion}:{result.IdentitySha256}";
    }

    public async Task<VirtualOpticalCalibrationReview> ReviewAsync(OpticalCalibrationResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        var catalog = await store.GetAsync(cancellationToken).ConfigureAwait(false);
        var activeId = catalog.Selection.ActiveRevisionId
            ?? throw new InvalidOperationException("No named rig is active.");
        var active = catalog.Revisions.SingleOrDefault(revision => revision.RevisionId == activeId)
            ?? throw new InvalidDataException("The active named rig revision is missing.");
        OpticsProfile? proposed = null;
        string? blocker = null;
        if (!string.Equals(active.Module.Type, VirtualModuleType, StringComparison.OrdinalIgnoreCase))
            blocker = "physical-rig";
        else if (catalog.Selection.PendingRevisionId is not null)
            blocker = "rig-change-pending";
        else if (!result.IsAccepted)
            blocker = "calibration-not-accepted";
        else if (!SameNominal(active.Rig, result.NominalNative))
            blocker = "nominal-mismatch";
        else
        {
            try
            {
                proposed = RigProjectionContextFactory.CreateCalibratedOptics(active.Rig, result.CalibratedNative!.Value,
                    CalibrationVersion(result));
            }
            catch (ArgumentException)
            {
                blocker = "unrepresentable";
            }
        }
        return new(active.RevisionId, active.ProfileId, catalog.Selection.Version, active.OpticsRevisionId, result.IdentitySha256, result.Status,
            result.ReasonCode, result.Rejections, blocker is null, blocker, active.Rig.Optics, proposed, result.Parameters,
            result.Diagnostics, result.CatalogIdentitySha256, result.OptionsIdentitySha256);
    }

    /// <summary>
    /// Retains an accepted calibration as a new optics revision and a draft rig revision of the reviewed profile.
    /// The review must still describe the active rig at the reviewed selection version, and the retention is one store
    /// transaction; accepting the same calibration again returns the retained draft.
    /// </summary>
    public async Task<VirtualOpticalCalibrationDecision> AcceptAsync(VirtualOpticalCalibrationReview review,
        OpticalCalibrationResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(result);
        if (!string.Equals(review.CalibrationIdentitySha256, result.IdentitySha256, StringComparison.Ordinal))
            throw new ArgumentException("The review does not describe this calibration.", nameof(review));
        var current = await ReviewAsync(result, cancellationToken).ConfigureAwait(false);
        if (current.RigRevisionId != review.RigRevisionId || current.OpticsRevisionId != review.OpticsRevisionId ||
            current.SelectionVersion != review.SelectionVersion)
            throw new CaptureScheduleStoreConflictException("The active named rig changed after review.");
        if (!current.CanAccept || current.ProposedOptics is not { } optics)
            throw new InvalidOperationException($"The calibration cannot be accepted ({current.Blocker}).");

        // The store rechecks the reviewed selection and writes the optics and draft rig revisions atomically.
        var candidate = await store.RetainCalibratedOpticsAsync(current.RigRevisionId, current.SelectionVersion,
            $"Calibrated optics {result.IdentitySha256[..12]}", optics, cancellationToken).ConfigureAwait(false);
        return new("accepted", result.IdentitySha256, current.RigRevisionId, candidate.RevisionId, candidate.OpticsRevisionId);
    }

    /// <summary>Records an explicit rejection. Nothing is written and the active calibration is unchanged.</summary>
    public static VirtualOpticalCalibrationDecision Reject(VirtualOpticalCalibrationReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        return new("rejected", review.CalibrationIdentitySha256, review.RigRevisionId, null, null);
    }

    private static bool SameNominal(CameraRigConfig rig, ProjectionContext nominal)
    {
        try
        {
            return RigProjectionContextFactory.CreateNative(rig) == nominal;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
