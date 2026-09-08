using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;

/// <summary>
/// The pre-wait state of the shared capture pipeline, captured by the observing test before it
/// begins waiting. Every field is an immutable snapshot reference, so a later reference comparison
/// proves whether the shared singleton actually republished that role.
/// </summary>
internal sealed record VirtualSkyPipelineBaseline(
    DateTimeOffset ListenerStartedUtc,
    CaptureTelemetrySample? Telemetry,
    LatestFrameSnapshot? Raw,
    LatestFrameSnapshot? Combined,
    LatestFrameSnapshot? Preview);

/// <summary>
/// One internally consistent observation of the configured VirtualSky pipeline: a successful
/// post-listener telemetry report together with the raw, combined and annotated-preview snapshots
/// of a single capture. Assertions consume this record instead of rereading the mutable shared
/// singletons, so every assertion in a test run describes the same capture.
/// </summary>
internal sealed record VirtualSkyPipelineObservation(
    CaptureTelemetrySample Telemetry,
    LatestFrameSnapshot Raw,
    LatestFrameSnapshot Combined,
    LatestFrameSnapshot Preview,
    DateTimeOffset CaptureTimestampUtc,
    long? CaptureSequence);

/// <summary>
/// The verdict for one candidate observation. <see cref="ReasonCode"/> always names the first
/// unsatisfied requirement, so a readiness timeout reports which part of the pipeline never
/// advanced instead of a single generic message.
/// </summary>
internal sealed record VirtualSkyPipelineQualification(
    VirtualSkyPipelineObservation? Observation,
    string ReasonCode)
{
    public bool IsQualified => Observation is not null;
}

/// <summary>
/// Host-neutral readiness qualification for the configured VirtualSky pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The configured integration graph publishes the latest raw, combined and display roles from a
/// single <c>LocalStorage</c> step (order 100) and only then reports telemetry from the
/// <c>Telemetry</c> step (order 1000). A coherent set is therefore always reachable: whenever a new
/// telemetry sample becomes visible, the role snapshots for that capture have already been
/// published.
/// </para>
/// <para>
/// The per-capture identity is the frame timestamp. Every role of one capture carries the same one,
/// because <c>CameraAgentRecipeExecutionAdapter.CreateFrame</c> propagates the source timestamp
/// through calibration, rolling combination, preview and annotation, and because the configured
/// capture interval keeps successive captures distinct. Requiring the three roles to agree on it is
/// what distinguishes a single published capture from a torn set spanning two captures, and it
/// needs no production contract change.
/// </para>
/// <para>
/// The module's own <c>captureSequence</c> metadata cannot serve as that identity here: the durable
/// lane rehydrates the raw frame through <c>FrameReconstructor</c>, which rebuilds
/// <c>FrameMetadata</c> from the reconstruction descriptor and carries no <c>Extra</c> dictionary,
/// so the sequence the VirtualSky module wrote never reaches a published snapshot. It is kept only
/// as a strengthening cross-check for snapshots that do carry it.
/// </para>
/// </remarks>
internal static class VirtualSkyPipelineReadiness
{
    /// <summary>Frame metadata key carrying the VirtualSky per-capture sequence number.</summary>
    public const string CaptureSequenceMetadataKey = "captureSequence";

    public const string Qualified = "qualified";
    public const string RawMissing = "role.missing:raw";
    public const string CombinedMissing = "role.missing:combined";
    public const string PreviewMissing = "role.missing:preview";
    public const string RawUnchanged = "role.unchanged:raw";
    public const string CombinedUnchanged = "role.unchanged:combined";
    public const string PreviewUnchanged = "role.unchanged:preview";
    public const string SequenceMismatch = "role.sequence-mismatch";
    public const string SequencePartial = "role.sequence-partial";
    public const string TimestampMismatch = "role.timestamp-mismatch";
    public const string NotAdvanced = "role.not-advanced";
    public const string TelemetryMissing = "telemetry.missing";
    public const string TelemetryStale = "telemetry.stale";
    public const string TelemetryFrameNotStored = "telemetry.frame-not-stored";
    public const string TelemetryStepsMissing = "telemetry.steps-missing";
    public const string TelemetryStepFailed = "telemetry.step-failed";
    public const string TelemetryOlderThanRoles = "telemetry.older-than-roles";

    /// <summary>
    /// Decides whether the supplied candidate state is one internally consistent, fully advanced
    /// observation of the configured pipeline.
    /// </summary>
    /// <remarks>
    /// Callers must read the three role snapshots before reading telemetry. Doing so makes the
    /// observed telemetry sample belong to the same capture as the roles or to a later one, which
    /// is what <see cref="TelemetryOlderThanRoles"/> enforces.
    /// </remarks>
    public static VirtualSkyPipelineQualification Qualify(
        VirtualSkyPipelineBaseline baseline,
        LatestFrameSnapshot? raw,
        LatestFrameSnapshot? combined,
        LatestFrameSnapshot? preview,
        CaptureTelemetrySample? telemetry)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        if (raw is null)
        {
            return Rejected(RawMissing);
        }
        if (combined is null)
        {
            return Rejected(CombinedMissing);
        }
        if (preview is null)
        {
            return Rejected(PreviewMissing);
        }

        // A role that is still the pre-wait object was not republished by any post-listener capture.
        if (ReferenceEquals(raw, baseline.Raw))
        {
            return Rejected(RawUnchanged);
        }
        if (ReferenceEquals(combined, baseline.Combined))
        {
            return Rejected(CombinedUnchanged);
        }
        if (ReferenceEquals(preview, baseline.Preview))
        {
            return Rejected(PreviewUnchanged);
        }

        // One capture publishes all three roles together, so their capture identities must agree.
        if (raw.TimestampUtc != combined.TimestampUtc || raw.TimestampUtc != preview.TimestampUtc)
        {
            return Rejected(TimestampMismatch);
        }

        // Strengthening cross-check for snapshots that still carry the module's capture sequence.
        var hasRawSequence = TryReadCaptureSequence(raw, out var rawSequence);
        var hasCombinedSequence = TryReadCaptureSequence(combined, out var combinedSequence);
        var hasPreviewSequence = TryReadCaptureSequence(preview, out var previewSequence);
        long? captureSequence = null;
        if (hasRawSequence && hasCombinedSequence && hasPreviewSequence)
        {
            if (rawSequence != combinedSequence || rawSequence != previewSequence)
            {
                return Rejected(SequenceMismatch);
            }
            captureSequence = rawSequence;
        }
        else if (hasRawSequence || hasCombinedSequence || hasPreviewSequence)
        {
            // A set where only some roles carry the sequence did not come from one publication.
            return Rejected(SequencePartial);
        }

        // The observed capture must be strictly newer than the pre-wait baseline capture. Frame
        // timestamps are wall-clock request starts and stay monotonic across a module restart,
        // whereas the module resets its capture sequence to zero when it reinitializes.
        if (baseline.Raw is { } baselineRaw && raw.TimestampUtc <= baselineRaw.TimestampUtc)
        {
            return Rejected(NotAdvanced);
        }

        if (telemetry is null)
        {
            return Rejected(TelemetryMissing);
        }
        if (telemetry.StartedUtc <= baseline.ListenerStartedUtc)
        {
            return Rejected(TelemetryStale);
        }
        if (!telemetry.FrameStored)
        {
            return Rejected(TelemetryFrameNotStored);
        }
        if (telemetry.ProcessingSteps.Count == 0)
        {
            return Rejected(TelemetryStepsMissing);
        }
        if (telemetry.ProcessingSteps.Any(static step => !step.Succeeded))
        {
            return Rejected(TelemetryStepFailed);
        }

        // The capture loop records the telemetry start after the scheduled request start, so a
        // sample that predates the observed roles cannot describe their graph execution.
        if (telemetry.StartedUtc < raw.TimestampUtc)
        {
            return Rejected(TelemetryOlderThanRoles);
        }

        return new VirtualSkyPipelineQualification(
            new VirtualSkyPipelineObservation(
                telemetry,
                raw,
                combined,
                preview,
                raw.TimestampUtc,
                captureSequence),
            Qualified);

        static VirtualSkyPipelineQualification Rejected(string reasonCode)
            => new(null, reasonCode);
    }

    /// <summary>Reads the VirtualSky per-capture sequence carried by a published role snapshot.</summary>
    public static bool TryReadCaptureSequence(LatestFrameSnapshot? snapshot, out long captureSequence)
    {
        captureSequence = -1;
        return snapshot?.Metadata?.Extra is { } extra &&
            extra.TryGetValue(CaptureSequenceMetadataKey, out var value) &&
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out captureSequence);
    }

    /// <summary>
    /// Renders the observation half of a readiness timeout diagnostic: elapsed time against the
    /// budget, the rejection reason, the pre-wait baseline, the telemetry buffer including failed
    /// steps, and the identity of every required role.
    /// </summary>
    public static string DescribeObservationState(
        VirtualSkyPipelineBaseline baseline,
        string reasonCode,
        TimeSpan elapsed,
        TimeSpan budget,
        CaptureTelemetrySnapshot? telemetrySnapshot,
        CaptureTelemetrySample? telemetry,
        LatestFrameSnapshot? raw,
        LatestFrameSnapshot? combined,
        LatestFrameSnapshot? preview)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        var builder = new StringBuilder();
        builder.AppendLine(Inv($"reason: {reasonCode}"));
        builder.AppendLine(Inv(
            $"elapsed: {elapsed.TotalSeconds:F3} s of {budget.TotalSeconds:F3} s budget (exceeded: {elapsed >= budget})"));
        builder.AppendLine(Inv($"listenerStartedUtc: {baseline.ListenerStartedUtc:O}"));
        builder.AppendLine("baseline:");
        builder.AppendLine(Inv($"  telemetry: {DescribeSample(baseline.Telemetry)}"));
        builder.AppendLine(Inv($"  raw:       {DescribeRole(baseline.Raw)}"));
        builder.AppendLine(Inv($"  combined:  {DescribeRole(baseline.Combined)}"));
        builder.AppendLine(Inv($"  preview:   {DescribeRole(baseline.Preview)}"));
        builder.AppendLine("observed:");
        builder.AppendLine(Inv($"  telemetry: {DescribeSample(telemetry)}"));
        builder.AppendLine(Inv(
            $"  telemetry changed from baseline: {!ReferenceEquals(telemetry, baseline.Telemetry)}"));
        builder.AppendLine(Inv($"  raw:       {DescribeRole(raw)} changed={!ReferenceEquals(raw, baseline.Raw)}"));
        builder.AppendLine(Inv(
            $"  combined:  {DescribeRole(combined)} changed={!ReferenceEquals(combined, baseline.Combined)}"));
        builder.AppendLine(Inv(
            $"  preview:   {DescribeRole(preview)} changed={!ReferenceEquals(preview, baseline.Preview)}"));
        builder.AppendLine(Inv($"  failed steps: {DescribeFailedSteps(telemetry)}"));
        if (telemetrySnapshot is not null)
        {
            var samples = telemetrySnapshot.Samples;
            builder.AppendLine(
                Inv($"telemetry buffer: count={samples.Count} stored={samples.Count(static item => item.FrameStored)}") +
                Inv($" withFailedStep={samples.Count(static item => item.ProcessingSteps.Any(static step => !step.Succeeded))}"));
            builder.AppendLine(Inv(
                $"  first={DescribeSample(samples.Count == 0 ? null : samples[0])}"));
            builder.AppendLine(Inv(
                $"  last={DescribeSample(samples.Count == 0 ? null : samples[^1])}"));
            var postListener = samples
                .Where(item => item.StartedUtc > baseline.ListenerStartedUtc)
                .ToArray();
            builder.AppendLine(Inv(
                $"  postListener={postListener.Length} postListenerStored={postListener.Count(static item => item.FrameStored)}"));
        }

        return builder.ToString();
    }

    public static string DescribeSample(CaptureTelemetrySample? sample)
        => sample is null
            ? "<none>"
            : Inv($"startedUtc={sample.StartedUtc:O} frameStored={sample.FrameStored}") +
              Inv($" steps={sample.ProcessingSteps.Count} allSucceeded={sample.ProcessingSteps.All(static step => step.Succeeded)}") +
              Inv($" loopMs={sample.LoopDuration.TotalMilliseconds:F1} processingMs={sample.ProcessingLatency.TotalMilliseconds:F1}");

    public static string DescribeRole(LatestFrameSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return "<none>";
        }
        var sequence = TryReadCaptureSequence(snapshot, out var value)
            ? value.ToString(CultureInfo.InvariantCulture)
            : "<unavailable>";
        return Inv($"captureSequence={sequence} timestampUtc={snapshot.TimestampUtc:O} sourceId={snapshot.Metadata?.SourceId ?? "<none>"}") +
            Inv($" format={snapshot.PixelFormat} size={snapshot.Width}x{snapshot.Height} recipeVersion={snapshot.RecipeVersion ?? "<none>"}");
    }

    private static string DescribeFailedSteps(CaptureTelemetrySample? sample)
    {
        if (sample is null)
        {
            return "<no sample>";
        }
        var failed = sample.ProcessingSteps
            .Where(static step => !step.Succeeded)
            .Select(static step => Inv($"{step.Name}: {step.ErrorMessage ?? "<no message>"}"))
            .ToArray();
        return failed.Length == 0 ? "<none>" : string.Join("; ", failed);
    }

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);
}
