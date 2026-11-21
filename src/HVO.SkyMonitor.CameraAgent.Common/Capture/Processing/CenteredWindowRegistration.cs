using System.Collections.Immutable;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

// All reads are by frozen identity. The live reader loads admitted descriptors
// and files under the #1919 owner's active holds.
internal interface ICenteredWindowRegistrationSource : ICenteredStackInputReader
{
    ValueTask<ExposureInputV1?> ReadExposureAsync(ExposureKey key, CancellationToken cancellationToken);

    ValueTask<ProcessingArtifact?> ReadArtifactAsync(
        ExposureKey exposure, CenteredStackArtifactPin pin, CancellationToken cancellationToken);
}

internal sealed record CenteredWindowRegistrationAttempt(
    ProcessingOutcome Outcome, CenteredWindowRegistrationResult? Result);

internal sealed class CenteredWindowRegistrationResult
{
    internal const long MaximumRetainedInputBytes = CenteredRegisteredStackRecipe.MaximumInputBytes;

    private CenteredWindowRegistrationResult(CenteredWindowClosure closure, CenteredRegisteredStackEvidence evidence,
        ImmutableArray<ProcessingProduct> products, Guid evidenceArtifactId, Guid? recomputationId)
    {
        Closure = closure;
        RecomputationId = recomputationId;
        Evidence = evidence;
        Products = products;
        Publication = new(closure.Identity, closure.Center.Input!.Key.CaptureId,
            evidence.Sources.Where(source => source.Reason == CenteredRegistrationReason.Contributed)
                .Select(source => source.CaptureId!.Value).ToImmutableArray(),
            products.Select(product => ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256)).ToImmutableArray(),
            evidence.PlanSha256, evidenceArtifactId, evidence.Disposition);
    }

    internal CenteredWindowClosure Closure { get; }
    internal Guid? RecomputationId { get; }
    internal CenteredRegisteredStackEvidence Evidence { get; }
    internal ImmutableArray<ProcessingProduct> Products { get; }
    internal CenteredWindowPublication Publication { get; }

    internal static ValueTask<CenteredWindowRegistrationAttempt> ExecuteAsync(
        CenteredWindowClosure closure, ICenteredWindowRegistrationSource source, CancellationToken cancellationToken) =>
        ExecuteCoreAsync(closure, source, null, cancellationToken);

    internal static ValueTask<CenteredWindowRegistrationAttempt> RecomputeAsync(
        CenteredWindowClosure closure, ICenteredWindowRegistrationSource source, Guid replayExecutionId,
        CancellationToken cancellationToken)
    {
        if (replayExecutionId == Guid.Empty)
            throw new ArgumentException("An archive recomputation requires its own replay execution identity.", nameof(replayExecutionId));
        return ExecuteCoreAsync(closure, source, replayExecutionId, cancellationToken);
    }

    private static async ValueTask<CenteredWindowRegistrationAttempt> ExecuteCoreAsync(
        CenteredWindowClosure closure, ICenteredWindowRegistrationSource source, Guid? recomputationId,
        CancellationToken cancellationToken)
    {
        if (closure.HasLegacyRegistrationPins)
            return new(ProcessingOutcome.TerminalFailure("registration.legacy-window-pins-unavailable"), null);

        var recipe = new CenteredRegisteredStackRecipe(source);
        var recipeIdentity = ProcessingIdentity.CreateRecipeIdentity(recipe.Definition, recipe.NormalizeOptions(default));
        if (closure.Schedule.RegistrationRecipeIdentitySha256 != recipeIdentity.IdentitySha256)
            return new(ProcessingOutcome.TerminalFailure("registration.recipe-identity-mismatch"), null);

        var plan = BuildPlan(closure);
        if (recomputationId is { } replay)
        {
            plan = plan with
            {
                ClosureIdentity = PayloadChecksum.ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    Scope = "centered-archive-recomputation-v1",
                    LiveClosure = closure.Identity,
                    ReplayExecutionId = replay
                }))
            };
        }
        if (plan.Slots[CenteredWindowScheduler.CenterIndex].Exposure is not { } center)
            return new(ProcessingOutcome.Skipped("registration.reference-input-unavailable"), null);

        var artifacts = new List<ProcessingArtifact>();
        string? linearVariant = null;
        long inputBytes = 0;
        foreach (var slot in plan.Slots)
        {
            if (slot.Exposure is not { } exposure)
                continue;
            ProcessingArtifact? raw = null;
            ProcessingArtifact? linear = null;
            foreach (var pin in Pins(exposure))
            {
                ProcessingArtifact? artifact;
                if (pin == exposure.ValidityMask)
                {
                    artifact = raw is null || linear is null ? null : CenteredStackValidityMask.OpenAllValid(
                        CenteredStackArtifactHeader.FromArtifact(raw), CenteredStackArtifactHeader.FromArtifact(linear),
                        pin, linear.CreatedUtc, cancellationToken);
                }
                else
                {
                    artifact = await source.ReadArtifactAsync(exposure.Key, pin, cancellationToken).ConfigureAwait(false);
                }
                if (artifact is null)
                    continue;
                if (artifact.CaptureId != exposure.Key.CaptureId || CenteredStackArtifactPin.From(artifact) != pin)
                    throw new InvalidDataException("A retained registration artifact differs from the frozen pin.");
                inputBytes += artifact.Payload.Length;
                if (inputBytes > MaximumRetainedInputBytes)
                    return new(ProcessingOutcome.TerminalFailure(CenteredRegisteredStackRecipe.ResourceLimit), null);
                if (pin == center.Linear)
                    linearVariant = artifact.Variant;
                if (pin == exposure.Raw)
                    raw = artifact;
                if (pin == exposure.Linear)
                    linear = artifact;
                artifacts.Add(artifact);
            }
        }

        if (linearVariant is null)
            return new(ProcessingOutcome.RetryableFailure(CenteredRegisteredStackRecipe.RetainedInputsUnavailable), null);

        var request = CenteredRegisteredStackRecipe.CreateRequest(plan, linearVariant,
            recomputationId is null ? "centered-live" : "centered-archive-recomputation", artifacts);
        var outcome = await new ProcessingRecipeExecutor([recipe]).ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (outcome.Status != ProcessingOutcomeStatus.Produced)
            return new(outcome, null);

        var result = ReadProduced(closure, plan, outcome.Products, recomputationId, cancellationToken);
        return new(outcome, result);
    }

    private static CenteredStackPlanV1 BuildPlan(CenteredWindowClosure closure)
    {
        var slots = closure.Slots.Select(slot =>
        {
            var pin = SelectPin(slot);
            var state = pin is not null ? CenteredSlotState.Ready : slot.State switch
            {
                CenteredWindowSlotState.MissingExposure or CenteredWindowSlotState.OutsideRun or
                    CenteredWindowSlotState.FailedAttempt or CenteredWindowSlotState.UncertainAttempt => CenteredSlotState.Missing,
                CenteredWindowSlotState.Stalled => CenteredSlotState.NotReadyAtClosure,
                _ => CenteredSlotState.Ineligible
            };
            return new CenteredStackSlot(slot.Offset, slot.Time.ExpectedStartUtc, state,
                state == CenteredSlotState.Missing ? null : slot.Input?.Key.CaptureId, pin,
                slot.Reason ?? slot.Products?.Measurement?.Lookup.Reason);
        }).ToArray();
        var center = slots[CenteredWindowScheduler.CenterIndex];
        return new(closure.Schedule.Camera, closure.Schedule.RunId, closure.Schedule.GenerationId,
            center.CaptureId, center.Exposure?.Linear.ArtifactId, closure.Schedule.DeadlineUtc,
            closure.EvidenceCutoffUtc, closure.Identity, slots);
    }

    private static CenteredStackExposurePin? SelectPin(CenteredWindowClosedSlot slot)
    {
        if (slot.State != CenteredWindowSlotState.Eligible || slot.Products?.RegistrationPin is not { } pin)
            return null;

        var input = slot.Input!;
        var linear = slot.Products.Linear!;
        if (pin.Key != input.Key || pin.CaptureSequence != input.CaptureSequence ||
            pin.ExposureStartedUtc != input.Timing.ExposureStartedUtc || pin.Raw.ArtifactId != input.RawArtifactId ||
            pin.Raw.PayloadSha256 != input.RawPayloadSha256 || pin.Raw.DescriptorSha256 != input.RawDescriptorSha256 ||
            pin.Linear.ArtifactId != linear.ArtifactId || pin.Linear.PayloadSha256 != linear.PayloadSha256)
            throw new InvalidDataException("The centered plan does not match its admitted raw and linear inputs.");

        return pin;
    }

    private static IEnumerable<CenteredStackArtifactPin> Pins(CenteredStackExposurePin exposure)
    {
        yield return exposure.Raw;
        yield return exposure.Linear;
        yield return exposure.ValidityMask;
        if (exposure.Measurement is { } measurement)
            yield return measurement;
        if (exposure.Plate is { } plate)
        {
            yield return plate.Associations;
            yield return plate.Scene;
            yield return plate.Context;
            yield return plate.Solution;
        }
    }

    private static CenteredWindowRegistrationResult ReadProduced(CenteredWindowClosure closure, CenteredStackPlanV1 plan,
        IReadOnlyList<ProcessingProduct> products, Guid? recomputationId, CancellationToken cancellationToken)
    {
        var metadata = products.SingleOrDefault(product => product.SchemaVersion == CenteredRegisteredStackEvidence.CurrentSchemaVersion)
            ?? throw new InvalidDataException("The centered recipe produced no evidence artifact.");
        var evidence = CenteredRegisteredStackEvidence.Read(metadata);
        if (evidence.PlanSha256 != plan.ToAuxiliaryInput().IdentitySha256 ||
            evidence.Sources.Any(source => source.Reason == CenteredRegistrationReason.Contributed &&
                !HasVerifiedMapping(source, source.Offset)))
            throw new InvalidDataException("The centered result does not carry the exact frozen plan and verified mappings.");

        var planes = products.Where(product => product != metadata).ToArray();
        if (planes.Any(plane => plane.Recipe != metadata.Recipe))
            throw new InvalidDataException("The centered planes belong to a different recipe invocation.");
        evidence.VerifyPlanes(planes, cancellationToken);
        return new(closure, evidence, products.ToImmutableArray(), ProcessingIdentity.CreateArtifactId(metadata.OutputIdentitySha256),
            recomputationId);
    }

    private static bool HasVerifiedMapping(CenteredStackSourceEvidence source, int offset) => source.Method switch
    {
        CenteredRegistrationMethod.ReferenceIdentity => offset == 0,
        CenteredRegistrationMethod.VerifiedCentroidSimilarity => offset != 0 && source.Similarity is not null &&
            source.PairCheck?.Status == DirectRegistrationStatus.Verified,
        CenteredRegistrationMethod.VerifiedPlatePair => offset != 0 && source.Plate is not null &&
            source.PairCheck?.Status == DirectRegistrationStatus.Verified,
        _ => false
    };
}
