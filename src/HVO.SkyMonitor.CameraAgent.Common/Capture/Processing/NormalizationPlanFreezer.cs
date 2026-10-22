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
    /// <summary>No input of the window has an applicable member, checked masters and an in-range coefficient.</summary>
    public const string NoEligibleInput = "normalization.plan.no-eligible-input";

    /// <summary>
    /// The gain response's stored codes are not right-aligned native samples (for example full-range-scaled 12-bit
    /// codes). The fixed ×16 output scale is defined over right-aligned codes only, so such a window is not planned.
    /// </summary>
    public const string NonRightAlignedResponse = "normalization.plan.non-right-aligned-response";

    /// <summary>
    /// Returns the node's frozen plan, freezing one first when there is none. <paramref name="familyJson"/> is the
    /// active family's canonical bytes; a family that does not parse fails explicitly.
    /// <paramref name="forceEvidenceValidation"/> is passed to <see cref="ConfirmAsync"/>.
    /// </summary>
    internal async ValueTask<FrozenNormalizationPlan> FreezeAsync(
        ProcessingExecutionContext execution,
        string nodeId,
        ReadOnlyMemory<byte> familyJson,
        GainNormalizationPolicyV1 policy,
        GainResponse response,
        CancellationToken cancellationToken,
        bool forceEvidenceValidation = true)
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
        var plan = await PrepareAsync(calibration, family, policy, response, inputs, cancellationToken)
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
    /// sensor, or the window fails with an identity mismatch. An input is excluded, with its stable reason, when the family
    /// selects no checked masters for it, its gain has no coefficient, or its layout differs from the first selected
    /// input's or from the response's code mapping. The plan fails when no input remains or the remaining
    /// coefficients exceed the policy's ratio bound; choosing a window inside the bound is the caller's. A response
    /// whose stored codes are not right-aligned fails the whole window.
    /// </summary>
    internal static async ValueTask<NormalizationPlanV1> PrepareAsync(
        SqliteCalibrationLibraryStore calibration,
        CalibrationFamilyV1 family,
        GainNormalizationPolicyV1 policy,
        GainResponse response,
        IReadOnlyList<ProcessingFrozenRawInput> inputs,
        CancellationToken cancellationToken)
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
        var entries = new NormalizationPlanInputV1[inputs.Count];
        FrameLayoutDescriptor? layout = null;
        for (var ordinal = 0; ordinal < inputs.Count; ordinal++)
        {
            var input = inputs[ordinal];
            var descriptor = input.Descriptor;
            var excluded = new NormalizationPlanInputV1(
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
                entries[ordinal] = excluded with { ExclusionReason = selection.ReasonCode };
                continue;
            }
            if (descriptor.Layout.StoredCodeTransform != response.Descriptor.StoredCodeTransform ||
                descriptor.Layout.SampleDepthBits != response.Descriptor.NativeSampleDepthBits)
            {
                entries[ordinal] = excluded with { ExclusionReason = GainNormalizationReasonCodes.UnsupportedCodeMapping };
                continue;
            }
            if (layout is not null && descriptor.Layout != layout)
            {
                entries[ordinal] = excluded with { ExclusionReason = CalibrationFamilyReasonCodes.ReadoutMismatch };
                continue;
            }
            var coefficient = response.ResolveCoefficient(descriptor.Controls.EffectiveGain);
            if (coefficient.Coefficient is not { } resolved)
            {
                entries[ordinal] = excluded with { ExclusionReason = coefficient.ReasonCode };
                continue;
            }
            layout ??= descriptor.Layout;
            entries[ordinal] = excluded with
            {
                Disposition = NormalizationPlanInputDispositions.Selected,
                MemberId = selected.MemberId,
                Masters = [.. selected.Masters.Select(static master =>
                    new NormalizationPlanMasterV1(master.Kind, master.ArtifactId, master.PayloadSha256, master.Exposure))],
                Coefficient = resolved
            };
        }
        var coefficients = entries
            .Where(static entry => entry.Coefficient is not null)
            .Select(static entry => entry.Coefficient!)
            .ToArray();
        if (coefficients.Length == 0)
        {
            throw new NormalizationPlanException(NoEligibleInput);
        }
        if (GainNormalizationContract.EvaluateCoefficientBound(policy, coefficients) is { } boundReason)
        {
            throw new NormalizationPlanException(boundReason);
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
