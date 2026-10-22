using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>
/// Supplies the mixed-gain recipe with its frozen normalization plan and the library masters the plan selects
/// (#1475 slice F2). The node's frozen plan is used when there is one, so a retry never consults the live family.
/// Otherwise the active family is selected and a plan is frozen for the node's frozen raw window. Either way the
/// plan's masters are confirmed with the cached evidence check, and the recipe hashes every payload against the plan
/// again; no forced family confirmation runs per window.
/// </summary>
internal sealed class GainNormalizationPreparer(
    ActiveCalibrationFamilySelector families,
    NormalizationPlanFreezer freezer,
    SqliteCalibrationLibraryStore calibration,
    CalibrationLibraryProcessingInputLoader loader)
{
    /// <summary>
    /// Returns the plan, its masters and the plan input for the current durable node, or the outcome the node records
    /// instead: skipped when no family is active or the plan does not fill <paramref name="shape"/>, a terminal failure
    /// with the stable reason otherwise. Outside a durable execution there is no plan to freeze, and the recipe skips
    /// the window.
    /// </summary>
    public async ValueTask<GainNormalizationPreparation> PrepareAsync(
        CaptureProcessingContext context,
        CancellationToken cancellationToken,
        NormalizationWindowShape? shape = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        shape ??= NormalizationWindowShape.Trailing;
        if (context.ProcessingExecution is not { } execution || context.CurrentNodeId is not { } nodeId ||
            context.ReconstructionDescriptor is not { } descriptor)
        {
            return GainNormalizationPreparation.None;
        }
        FrozenNormalizationPlan frozen;
        try
        {
            // The owner decision for #1475 F2: consume time relies on the recipe's per-payload re-hash plus the cached,
            // unforced evidence check, not a forced family confirmation per window.
            if (await freezer.ReadConfirmedAsync(execution, nodeId, cancellationToken, forceEvidenceValidation: false)
                    .ConfigureAwait(false) is { } existing)
            {
                frozen = existing;
            }
            else if (execution.ExecutionClass == ProcessingGraphExecutionClass.Replay)
            {
                return GainNormalizationPreparation.Failed(ProcessingOutcome.TerminalFailure(
                    ProcessingReasonCodes.MissingNormalizationPlan, GainNormalizationCompatibility.PlanInputName));
            }
            else
            {
                var selection = await families.SelectAsync(context.Config, descriptor, cancellationToken).ConfigureAwait(false);
                if (selection.ReasonCode is { } reason)
                {
                    return GainNormalizationPreparation.Failed(
                        string.Equals(reason, ActiveCalibrationFamilySelector.NoActiveFamily, StringComparison.Ordinal)
                            ? ProcessingOutcome.Skipped(reason, GainNormalizationCompatibility.PlanInputName)
                            : ProcessingOutcome.TerminalFailure(reason, GainNormalizationCompatibility.PlanInputName));
                }
                frozen = await freezer.FreezeAsync(
                    execution,
                    nodeId,
                    selection.FamilyJson,
                    GainNormalizationPolicyV1.Approved,
                    selection.Response!,
                    cancellationToken,
                    forceEvidenceValidation: false,
                    shape).ConfigureAwait(false);
            }
        }
        catch (NormalizationPlanException exception)
        {
            return GainNormalizationPreparation.Failed(
                ProcessingOutcome.TerminalFailure(exception.ReasonCode, GainNormalizationCompatibility.PlanInputName));
        }
        catch (InvalidDataException)
        {
            // Corrupt frozen evidence fails the window; nothing is re-derived from live state.
            return GainNormalizationPreparation.Failed(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidNormalizationPlan, GainNormalizationCompatibility.PlanInputName));
        }

        // An input the plan excludes never fills a position, so a centered window that lost one is not produced.
        if (!shape.IsFilledBy(frozen.Plan))
        {
            return GainNormalizationPreparation.Failed(ProcessingOutcome.Skipped(
                NormalizationPlanFreezer.WindowIncomplete, GainNormalizationCompatibility.PlanInputName));
        }

        var masterIds = frozen.Plan.Inputs
            .Where(static input => input.Disposition == NormalizationPlanInputDispositions.Selected)
            .SelectMany(static input => input.Masters ?? [])
            .Select(static master => master.ArtifactId)
            .ToHashSet();
        var masters = new List<ProcessingArtifact>(masterIds.Count);
        try
        {
            foreach (var bundle in await calibration.ReadFamilyMasterBundlesAsync(masterIds, cancellationToken)
                         .ConfigureAwait(false))
            {
                var loaded = await loader.LoadAsync(bundle, cancellationToken).ConfigureAwait(false);
                masters.AddRange(loaded.References.Values.Where(reference => masterIds.Contains(reference.ArtifactId)));
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return GainNormalizationPreparation.Failed(ProcessingOutcome.TerminalFailure(
                CalibrationFamilyReasonCodes.MasterMissing, GainNormalizationCompatibility.PlanInputName));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return GainNormalizationPreparation.Failed(ProcessingOutcome.TerminalFailure(
                CalibrationFamilyReasonCodes.MasterCorrupt, GainNormalizationCompatibility.PlanInputName));
        }
        // A master the library no longer holds is left out, and the recipe fails the window as master-missing.
        return new GainNormalizationPreparation(
            frozen.Plan,
            masters,
            new ProcessingAuxiliaryInput(
                GainNormalizationCompatibility.PlanInputName,
                ProcessingAuxiliaryInputKind.CanonicalJson,
                SchemaVersion: NormalizationPlanV1.CurrentSchemaVersion,
                IdentitySha256: frozen.IdentitySha256,
                Payload: frozen.CanonicalJson),
            null);
    }
}

/// <summary>
/// What the mixed-gain recipe is executed with: the frozen plan, the masters it selects and the plan input, or the
/// outcome the node records instead of executing. With neither, the recipe runs without a plan and skips.
/// </summary>
internal sealed record GainNormalizationPreparation(
    NormalizationPlanV1? Plan,
    IReadOnlyList<ProcessingArtifact> Masters,
    ProcessingAuxiliaryInput? PlanInput,
    ProcessingOutcome? Outcome)
{
    public static GainNormalizationPreparation None { get; } = new(null, [], null, null);

    public static GainNormalizationPreparation Failed(ProcessingOutcome outcome) => new(null, [], null, outcome);
}
