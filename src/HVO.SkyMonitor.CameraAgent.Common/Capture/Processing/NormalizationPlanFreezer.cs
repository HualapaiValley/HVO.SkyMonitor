using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>
/// Freezes the normalization plan of one raw stack window (#1475). The plan is prepared outside the store gate: each
/// frozen input's family masters are selected and checked, its coefficient is resolved, and the plan is serialized
/// and hashed. One short immediate transaction under the execution lease then confirms the frozen window and the
/// library snapshot and writes the plan. A retry uses the plan its first attempt froze, after confirming its masters
/// again; the live family, response and masters are never consulted for it.
/// </summary>
internal sealed class NormalizationPlanFreezer(
    SqliteCaptureProcessingStore processing,
    SqliteCalibrationLibraryStore calibration)
{
    /// <summary>
    /// No input of the window has an applicable member, checked masters and an in-range coefficient within the window's
    /// maximum age of the primary.
    /// </summary>
    public const string NoEligibleInput = "normalization.plan.no-eligible-input";

    /// <summary>
    /// The gain response's stored codes are not right-aligned native samples (for example full-range-scaled 12-bit
    /// codes). The fixed ×16 output scale is defined over right-aligned codes only, so such a window is not planned.
    /// </summary>
    public const string NonRightAlignedResponse = "normalization.plan.non-right-aligned-response";

    /// <summary>The input started further from the primary than the window's configured maximum age.</summary>
    public const string MaximumAgeExceeded = "normalization.plan.maximum-age-exceeded";

    /// <summary>Selecting the input would take the window past its configured maximum integration.</summary>
    public const string MaximumIntegrationExceeded = "normalization.plan.maximum-integration-exceeded";

    /// <summary>A newer input of a trailing window already ended it, so this older input is outside the window.</summary>
    public const string BeyondWindowBoundary = "normalization.plan.beyond-window-boundary";

    /// <summary>
    /// The plan does not select every position the window requires, or selects fewer inputs than its minimum, so the
    /// window is not produced.
    /// </summary>
    public const string WindowIncomplete = "normalization.plan.window-incomplete";

    /// <summary>
    /// Returns the node's frozen plan, freezing one first when there is none. <paramref name="familyJson"/> is the
    /// active family's canonical bytes; a family that does not parse fails explicitly.
    /// <paramref name="forceEvidenceValidation"/> is passed to <see cref="ConfirmAsync"/>, and
    /// <paramref name="shape"/> to <see cref="PrepareAsync"/>.
    /// </summary>
    internal async ValueTask<FrozenNormalizationPlan> FreezeAsync(
        ProcessingExecutionContext execution,
        string nodeId,
        ReadOnlyMemory<byte> familyJson,
        GainNormalizationPolicyV1 policy,
        GainResponse response,
        CancellationToken cancellationToken,
        bool forceEvidenceValidation = true,
        NormalizationWindowShape? shape = null)
    {
        if (await ReadConfirmedAsync(execution, nodeId, cancellationToken, forceEvidenceValidation)
                .ConfigureAwait(false) is { } frozen)
        {
            return frozen;
        }
        var family = CalibrationFamilyContractJson.Parse(familyJson).Value
            ?? throw new NormalizationPlanException(CalibrationFamilyReasonCodes.InvalidFamily);
        var inputs = await processing.ReadFrozenExecutionRawInputsAsync(execution.ExecutionId, nodeId, cancellationToken)
            .ConfigureAwait(false);
        var plan = await PrepareAsync(calibration, family, policy, response, inputs, cancellationToken, shape)
            .ConfigureAwait(false);
        return await processing.FreezeNormalizationPlanAsync(
            calibration, execution, nodeId, plan, cancellationToken, forceEvidenceValidation).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the node's frozen plan after checking the execution lease and confirming its masters, or null when no
    /// plan is frozen yet. A retry reaches its plan here before anything live is read, so it never consults the
    /// active family.
    /// </summary>
    internal async ValueTask<FrozenNormalizationPlan?> ReadConfirmedAsync(
        ProcessingExecutionContext execution,
        string nodeId,
        CancellationToken cancellationToken,
        bool forceEvidenceValidation = true)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        if (await processing.ReadNormalizationPlanAsync(execution.ExecutionId, nodeId, cancellationToken)
                .ConfigureAwait(false) is not { } frozen)
        {
            return null;
        }
        await processing.EnsureExecutionLeaseAsync(execution, cancellationToken).ConfigureAwait(false);
        await ConfirmAsync(calibration, frozen.Plan, cancellationToken, forceEvidenceValidation).ConfigureAwait(false);
        return frozen;
    }

    /// <summary>
    /// Builds the plan for frozen inputs in window order. The response must identify the family and every capture's
    /// sensor, or the window fails with an identity mismatch. An input is ineligible, and excluded with its stable
    /// reason, when the family selects no checked masters for it, its gain has no coefficient, or its layout differs
    /// from the response's code mapping. The eligible inputs are then taken nearest the primary first, as the rolling
    /// mean takes them: an input whose layout differs from the first one taken is excluded, and so is one that starts
    /// further from the primary than <paramref name="shape"/>'s maximum age, would take the window past its maximum
    /// integration, or would take the selected coefficients past the policy's ratio bound. In a trailing window the
    /// first input past a bound ends the window, so every older input is excluded too; a centered window excludes
    /// each input on its own, and whether what remains fills it is the caller's to check. The selection therefore
    /// stays inside the bound, and an older input past it never takes an eligible primary with it. The plan fails
    /// when no eligible input lies within the maximum age. A response whose stored codes are not right-aligned fails
    /// the whole window.
    /// </summary>
    internal static async ValueTask<NormalizationPlanV1> PrepareAsync(
        SqliteCalibrationLibraryStore calibration,
        CalibrationFamilyV1 family,
        GainNormalizationPolicyV1 policy,
        GainResponse response,
        IReadOnlyList<ProcessingFrozenRawInput> inputs,
        CancellationToken cancellationToken,
        NormalizationWindowShape? shape = null)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count is 0 or > NormalizationPlanV1.MaximumInputs)
        {
            throw new ArgumentException(
                $"Between 1 and {NormalizationPlanV1.MaximumInputs} frozen inputs are required.", nameof(inputs));
        }
        if (!string.Equals(response.Descriptor.SensorProfileSha256, family.SensorProfileSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new NormalizationPlanException(CalibrationFamilyReasonCodes.IdentityMismatch);
        }
        if (inputs.Any(input => !string.Equals(
                response.Descriptor.SensorProfileSha256, input.Descriptor.Profiles.Sensor.Sha256, StringComparison.OrdinalIgnoreCase)))
        {
            throw new NormalizationPlanException(CalibrationFamilyReasonCodes.IdentityMismatch);
        }
        if (response.Descriptor.StoredCodeTransform != FrameStoredCodeTransform.RightAlignedV1)
        {
            throw new NormalizationPlanException(NonRightAlignedResponse);
        }
        var reference = GainNormalizationContract.ResolveReference(policy, response);
        if (reference.Coefficient is not { } referenceCoefficient)
        {
            throw new NormalizationPlanException(reference.ReasonCode!);
        }
        shape ??= NormalizationWindowShape.Trailing;
        var entries = new NormalizationPlanInputV1[inputs.Count];
        var eligible = new (CalibrationFamilyMasterSelection Selection, GainCoefficientV1 Coefficient)?[inputs.Count];
        for (var ordinal = 0; ordinal < inputs.Count; ordinal++)
        {
            var input = inputs[ordinal];
            var descriptor = input.Descriptor;
            entries[ordinal] = new NormalizationPlanInputV1(
                ordinal,
                input.WindowPosition,
                descriptor.Capture.CaptureId,
                descriptor.Artifact.ArtifactId,
                input.DescriptorSha256,
                input.PayloadSha256,
                descriptor.Controls.EffectiveGain,
                descriptor.Controls.EffectiveExposure,
                descriptor.Controls.EffectiveOffset,
                descriptor.Controls.EffectiveTemperatureC,
                NormalizationPlanInputDispositions.Excluded,
                null,
                null,
                null,
                null);
            var selection = await calibration.SelectFamilyMastersAsync(family, descriptor, cancellationToken)
                .ConfigureAwait(false);
            if (selection.Selection is not { } selected)
            {
                entries[ordinal] = entries[ordinal] with { ExclusionReason = selection.ReasonCode };
                continue;
            }
            if (descriptor.Layout.StoredCodeTransform != response.Descriptor.StoredCodeTransform ||
                descriptor.Layout.SampleDepthBits != response.Descriptor.NativeSampleDepthBits)
            {
                entries[ordinal] = entries[ordinal] with { ExclusionReason = GainNormalizationReasonCodes.UnsupportedCodeMapping };
                continue;
            }
            var coefficient = response.ResolveCoefficient(descriptor.Controls.EffectiveGain);
            if (coefficient.Coefficient is not { } resolved)
            {
                entries[ordinal] = entries[ordinal] with { ExclusionReason = coefficient.ReasonCode };
                continue;
            }
            eligible[ordinal] = (selected, resolved);
        }
        // Nearest the primary first, older before newer at equal distance, as the rolling mean walks back from it.
        var walk = Enumerable.Range(0, inputs.Count)
            .OrderBy(ordinal => Math.Abs(inputs[ordinal].WindowPosition))
            .ThenBy(ordinal => inputs[ordinal].WindowPosition)
            .ToArray();
        var primaryStartedUtc = inputs[walk[0]].Descriptor.Timing.ExposureStartedUtc;
        FrameLayoutDescriptor? layout = null;
        var coefficients = new List<GainCoefficientV1>(inputs.Count);
        var integration = TimeSpan.Zero;
        var ended = false;
        foreach (var ordinal in walk)
        {
            if (eligible[ordinal] is not { } candidate)
            {
                continue;
            }
            var descriptor = inputs[ordinal].Descriptor;
            var reason = ended
                ? BeyondWindowBoundary
                : layout is not null && descriptor.Layout != layout
                    ? CalibrationFamilyReasonCodes.ReadoutMismatch
                    : WindowBoundReason(descriptor, candidate.Coefficient);
            if (reason is not null)
            {
                entries[ordinal] = entries[ordinal] with { ExclusionReason = reason };
                ended |= shape.Kind == ProcessingGraphWindowKind.Trailing &&
                    !string.Equals(reason, CalibrationFamilyReasonCodes.ReadoutMismatch, StringComparison.Ordinal);
                continue;
            }
            layout ??= descriptor.Layout;
            coefficients.Add(candidate.Coefficient);
            integration += descriptor.Controls.EffectiveExposure;
            entries[ordinal] = entries[ordinal] with
            {
                Disposition = NormalizationPlanInputDispositions.Selected,
                MemberId = candidate.Selection.MemberId,
                Masters = [.. candidate.Selection.Masters.Select(static master =>
                    new NormalizationPlanMasterV1(master.Kind, master.ArtifactId, master.PayloadSha256, master.Exposure))],
                Coefficient = candidate.Coefficient
            };
        }
        if (coefficients.Count == 0)
        {
            throw new NormalizationPlanException(NoEligibleInput);
        }
        return new NormalizationPlanV1(
            NormalizationPlanV1.CurrentSchemaVersion,
            CalibrationFamilyContractJson.ComputeIdentitySha256(family),
            policy,
            response.Descriptor,
            response.IdentitySha256,
            referenceCoefficient,
            layout!,
            NormalizationKernelV1.Linear16(response.Descriptor),
            entries);

        // Age is measured from the primary even when the primary itself is ineligible; the integration and ratio
        // bounds never exclude the first input taken.
        string? WindowBoundReason(ReconstructionDescriptor descriptor, GainCoefficientV1 coefficient)
        {
            if (shape.MaximumAgeMilliseconds is { } maximumAge &&
                (descriptor.Timing.ExposureStartedUtc - primaryStartedUtc).Duration().TotalMilliseconds > maximumAge)
            {
                return MaximumAgeExceeded;
            }
            if (coefficients.Count == 0)
            {
                return null;
            }
            if (shape.MaximumIntegrationMilliseconds is { } maximumIntegration &&
                (integration + descriptor.Controls.EffectiveExposure).TotalMilliseconds > maximumIntegration)
            {
                return MaximumIntegrationExceeded;
            }
            return GainNormalizationContract.EvaluateCoefficientBound(policy, [.. coefficients, coefficient]);
        }
    }

    /// <summary>
    /// Confirms, at claim time, that every master a frozen plan selects is still intact. A frozen plan is never
    /// changed, so a failure here fails the claim explicitly with the master's stable reason. Every payload is hashed
    /// again; <paramref name="forceEvidenceValidation"/> false lets the bundle evidence check reuse the
    /// file-metadata cache, as selection does.
    /// </summary>
    internal static async ValueTask ConfirmAsync(
        SqliteCalibrationLibraryStore calibration,
        NormalizationPlanV1 plan,
        CancellationToken cancellationToken,
        bool forceEvidenceValidation = true)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        ArgumentNullException.ThrowIfNull(plan);
        var masters = plan.Inputs
            .Where(static input => input.Masters is not null)
            .SelectMany(static input => input.Masters!)
            .DistinctBy(static master => master.ArtifactId)
            .Select(static master => new CalibrationFamilyMasterV1(master.Kind, master.ArtifactId, master.PayloadSha256))
            .ToArray();
        if (await calibration.ConfirmFamilyMastersAsync(masters, cancellationToken, forceEvidenceValidation)
                .ConfigureAwait(false) is { } reason)
        {
            throw new NormalizationPlanException(reason);
        }
    }
}

/// <summary>
/// The window a normalization plan is frozen for (#1475): its kind, the positions it must keep, the fewest inputs it
/// may hold, and its age and integration bounds, as the step that consumes the plan declares them.
/// </summary>
internal sealed record NormalizationWindowShape(
    ProcessingGraphWindowKind Kind,
    IReadOnlyList<int> RequiredPositions,
    int MinimumInputCount,
    double? MaximumAgeMilliseconds,
    double? MaximumIntegrationMilliseconds)
{
    /// <summary>A trailing window with no required position and no age or integration bound.</summary>
    public static NormalizationWindowShape Trailing { get; } =
        new(ProcessingGraphWindowKind.Trailing, [], 1, null, null);

    /// <summary>Whether <paramref name="plan"/> selects every required position and at least the fewest inputs.</summary>
    public bool IsFilledBy(NormalizationPlanV1 plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var selected = plan.Inputs
            .Where(static input => input.Disposition == NormalizationPlanInputDispositions.Selected)
            .Select(static input => input.WindowPosition)
            .ToHashSet();
        return selected.Count >= MinimumInputCount && RequiredPositions.All(selected.Contains);
    }
}
