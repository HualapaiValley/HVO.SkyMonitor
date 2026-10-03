using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

public sealed partial class HostRecipeContractConformanceTests
{
    [TestMethod]
    public async Task ReconstructionRejectsUntrustedCaptureFactsBeforeDispatch()
    {
        var d = ProcessingConformanceFixture.CreateDescriptor();
        (string Name, ReconstructionDescriptor Descriptor, string Reason)[] cases =
        [
            ("missing agent", d with { Capture = d.Capture with { AgentId = "" } }, ProcessingReasonCodes.InvalidInput),
            ("missing capture identity", d with { Capture = d.Capture with { CaptureId = Guid.Empty } }, ProcessingReasonCodes.InvalidInput),
            ("negative sequence", d with { Capture = d.Capture with { CaptureSequence = -1 } }, ProcessingReasonCodes.InvalidInput),
            ("non-UTC request", d with { Timing = d.Timing with { RequestedStartUtc = d.Timing.RequestedStartUtc.ToOffset(TimeSpan.FromHours(1)) } }, ProcessingReasonCodes.InvalidInput),
            ("reversed exposure", d with { Timing = d.Timing with { ExposureEndedUtc = d.Timing.ExposureStartedUtc.AddSeconds(-1) } }, ProcessingReasonCodes.InvalidInput),
            ("readout before exposure end", d with { Timing = d.Timing with { ReadoutCompletedUtc = d.Timing.ExposureStartedUtc } }, ProcessingReasonCodes.InvalidInput),
            ("ingress before readout", d with { Timing = d.Timing with { DurableIngressUtc = d.Timing.ExposureStartedUtc } }, ProcessingReasonCodes.InvalidInput),
            ("late active setpoint", d with { Timing = d.Timing with { SetpointAppliedUtc = d.Timing.ExposureEndedUtc } }, ProcessingReasonCodes.InvalidInput),
            ("negative exposure", d with { Controls = d.Controls with { EffectiveExposure = TimeSpan.FromTicks(-1) } }, ProcessingReasonCodes.InvalidInput),
            ("nonfinite gain", d with { Controls = d.Controls with { EffectiveGain = double.NaN } }, ProcessingReasonCodes.InvalidInput),
            ("nonfinite temperature", d with { Controls = d.Controls with { EffectiveTemperatureC = double.PositiveInfinity } }, ProcessingReasonCodes.InvalidInput),
            ("missing rig profile", d with { Profiles = d.Profiles with { Rig = null! } }, ProcessingReasonCodes.InvalidInput),
            ("unbound sensor profile", d with { Profiles = d.Profiles with { Sensor = d.Profiles.Sensor with { Sha256 = "bad" } } }, ProcessingReasonCodes.InvalidInput),
            ("zero width", d with { Layout = d.Layout with { Width = 0 } }, ProcessingReasonCodes.InvalidLayout),
            ("short stride", d with { Layout = d.Layout with { StrideBytes = 1 } }, ProcessingReasonCodes.InvalidLayout),
            ("wrong container", d with { Layout = d.Layout with { ContainerDepthBits = 8 } }, ProcessingReasonCodes.InvalidLayout),
            ("unsupported depth", d with { Layout = d.Layout with { SampleDepthBits = 7 } }, ProcessingReasonCodes.InvalidLayout),
            ("missing byte order", d with { Layout = d.Layout with { ByteOrder = FrameByteOrder.NotApplicable } }, ProcessingReasonCodes.InvalidLayout),
            ("packed bytes", d with { Layout = d.Layout with { Packing = FrameSamplePacking.Packed } }, ProcessingReasonCodes.InvalidLayout),
            ("invented CFA", d with { Layout = d.Layout with { CfaPattern = ColorFilterArrayPattern.Rggb } }, ProcessingReasonCodes.InvalidLayout),
            ("unknown format", d with { Layout = d.Layout with { PixelFormat = (CameraPixelFormat)99 } }, ProcessingReasonCodes.UnsupportedFormat),
            ("nonfinite black", d with { Layout = d.Layout with { BlackLevel = double.NaN } }, ProcessingReasonCodes.InvalidLayout),
            ("reversed levels", d with { Layout = d.Layout with { BlackLevel = 100, WhiteLevel = 50 } }, ProcessingReasonCodes.InvalidLayout),
            ("unbound code space", d with { Layout = d.Layout with { SampleDepthBits = 12 } }, ProcessingReasonCodes.InvalidInput),
            ("forged checksum", d with { Artifact = d.Artifact with { ChecksumSha256 = new string('0', 64) } }, ProcessingReasonCodes.InvalidInput),
            ("missing artifact", d with { Artifact = d.Artifact with { ArtifactId = Guid.Empty } }, ProcessingReasonCodes.InvalidInput),
            ("duplicate lineage", d with { Artifact = d.Artifact with { SourceArtifactIds = [d.Capture.CaptureId, d.Capture.CaptureId] } }, ProcessingReasonCodes.InvalidLineage),
            ("self lineage", d with { Artifact = d.Artifact with { SourceArtifactIds = [d.Artifact.ArtifactId] } }, ProcessingReasonCodes.InvalidLineage),
            ("unbound recipe options", d with { Artifact = d.Artifact with { Recipe = d.Artifact.Recipe with { OptionsSha256 = new string('0', 64) } } }, ProcessingReasonCodes.InvalidInput)
        ];
        foreach (var item in cases)
        {
            var recorder = new RecordingExecutor();
            var result = await Reconstruct(recorder, item.Descriptor);
            Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, result.Status, item.Name);
            Assert.AreEqual(item.Reason, result.ReasonCode, item.Name);
            Assert.HasCount(0, result.Products, item.Name);
            Assert.IsNull(recorder.Request, $"{item.Name}: rejection must precede recipe dispatch.");
        }
    }

    [TestMethod]
    public async Task CaptureControlEvidenceSurvivesReconstructionAndContradictionsCannotExecute()
    {
        var d = ProcessingConformanceFixture.CreateDescriptor();
        var afterReadout = d.Timing.ReadoutCompletedUtc;
        var disabled = new CaptureCycleEvidence(CaptureCadenceMode.Continuous, CaptureStartReason.ContinuousReady,
            AutomaticControlOwnership.Disabled, AutomaticControlOwnership.Disabled, null,
            d.Timing.ExposureStartedUtc, TimeSpan.Zero, null,
            new(afterReadout, afterReadout.AddMilliseconds(1), d.Controls.EffectiveExposure, d.Controls.EffectiveGain,
                d.Controls.EffectiveExposure, d.Controls.EffectiveGain, CaptureControlDecisionReason.Disabled),
            afterReadout.AddMilliseconds(2));
        var metered = disabled with
        {
            ExposureControl = AutomaticControlOwnership.HostMetered,
            GainControl = AutomaticControlOwnership.HostMetered,
            SolarRegime = CaptureSolarRegime.Night,
            Metering = new(afterReadout, afterReadout, 4, 4, 0, 8, .5, CaptureMeteringOutcome.Measured),
            Decision = disabled.Decision with { Reason = CaptureControlDecisionReason.WithinHysteresis }
        };
        CaptureCycleEvidence[] valid =
        [
            disabled,
            disabled with { ExposureControl = AutomaticControlOwnership.CameraNative,
                Decision = disabled.Decision with { Reason = CaptureControlDecisionReason.CameraNative } },
            metered,
            metered with { Decision = metered.Decision with { Reason = CaptureControlDecisionReason.ExposureAdjusted, DecidedExposure = TimeSpan.FromSeconds(10) } },
            metered with { Decision = metered.Decision with { Reason = CaptureControlDecisionReason.GainAdjusted, DecidedGain = 100 } },
            metered with { Decision = metered.Decision with { Reason = CaptureControlDecisionReason.ExposureAndGainAdjusted, DecidedExposure = TimeSpan.FromSeconds(10), DecidedGain = 100 } },
            metered with { Metering = metered.Metering! with { Outcome = CaptureMeteringOutcome.SaturationRejected, AcceptedSampleCount = 0, SaturatedSampleCount = 4, NormalizedLevel = null },
                Decision = metered.Decision with { Reason = CaptureControlDecisionReason.SaturationRejected, DecidedExposure = TimeSpan.FromSeconds(10) } },
            metered with { Metering = metered.Metering! with { Outcome = CaptureMeteringOutcome.NoFrame, ConsideredSampleCount = 0, AcceptedSampleCount = 0, ScannedBytes = 0, NormalizedLevel = null },
                Decision = metered.Decision with { Reason = CaptureControlDecisionReason.NoSample } }
        ];
        foreach (var evidence in valid)
        {
            var recorder = new RecordingExecutor();
            var result = await Reconstruct(recorder, d with { CycleEvidence = evidence });
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, result.Status, evidence.Decision.Reason.ToString());
            ProcessingConformanceFixture.AssertProduct(Assert.ContainsSingle(result.Products));
        }
        CaptureCycleEvidence[] invalid =
        [
            disabled with { StartReason = CaptureStartReason.DeadlineReached },
            disabled with { MonotonicStartJitter = TimeSpan.FromTicks(-1) },
            disabled with { ExposureControl = AutomaticControlOwnership.Unspecified },
            disabled with { Decision = disabled.Decision with { DecidedExposure = TimeSpan.FromSeconds(1) } },
            disabled with { Decision = disabled.Decision with { ActiveGain = 149 } },
            disabled with { Decision = disabled.Decision with { CompletedUtc = afterReadout.AddSeconds(1) } },
            metered with { Metering = null },
            metered with { SolarRegime = null },
            metered with { Metering = metered.Metering! with { NormalizedLevel = 2 } },
            metered with { Metering = metered.Metering! with { AcceptedSampleCount = 5 } },
            metered with { Metering = metered.Metering! with { SaturatedSampleCount = 1 } },
            metered with { Decision = metered.Decision with { Reason = CaptureControlDecisionReason.ExposureAdjusted } },
            metered with { Decision = metered.Decision with { DecidedGain = 100 } }
        ];
        foreach (var evidence in invalid)
        {
            var recorder = new RecordingExecutor();
            var result = await Reconstruct(recorder, d with { CycleEvidence = evidence });
            Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, result.Status);
            Assert.AreEqual(ProcessingReasonCodes.InvalidInput, result.ReasonCode);
            Assert.IsNull(recorder.Request);
        }
    }

    private static ValueTask<ProcessingOutcome> Reconstruct(RecordingExecutor recorder, ReconstructionDescriptor descriptor)
        => new LogicHostRecipeExecutionAdapter(recorder).ExecuteAsync(descriptor, ProcessingConformanceFixture.Payload,
            BuiltInProcessingRecipes.EncodedPreview,
            JsonSerializer.SerializeToElement(new EncodedPreviewOptions(OutputEncoding: "Packed")),
            ProcessingInputSelector.Raw("source"), "conformance");
}
