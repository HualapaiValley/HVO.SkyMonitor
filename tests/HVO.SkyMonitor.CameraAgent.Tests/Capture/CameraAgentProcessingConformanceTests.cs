using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture;

[TestClass]
public sealed class CameraAgentProcessingConformanceTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public async Task CameraAgentAdapterProducesCanonicalPreviewFixture()
    {
        var descriptor = ProcessingConformanceFixture.CreateDescriptor();
        var frame = new CameraFrame(
            ProcessingConformanceFixture.CapturedUtc,
            descriptor.Layout.Width,
            descriptor.Layout.Height,
            descriptor.Layout.PixelFormat,
            ProcessingConformanceFixture.Payload,
            new FrameMetadata(TimeSpan.FromSeconds(20), 150, 0));
        var artifact = new FrameArtifact(
            ProcessingConformanceFixture.ArtifactId,
            FrameArtifactRole.Raw,
            frame,
            recipeVersion: ProcessingIdentity.CreateRecipeIdentity(
                ProcessingConformanceFixture.SourceRecipe).IdentitySha256);
        var adapter = new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor());
        var input = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            ProcessingConformanceFixture.CameraConfig, artifact, "source");
        Assert.AreEqual(frame.TimestampUtc, input.ObservationStartedUtc);
        Assert.AreEqual(frame.TimestampUtc.Add(frame.Metadata.Exposure), input.ObservationEndedUtc);

        var reportedTiming = new CaptureAcquisitionTiming(
            frame.TimestampUtc.AddSeconds(-2),
            frame.TimestampUtc.AddSeconds(18),
            frame.TimestampUtc.AddSeconds(19));
        var reportedInput = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            ProcessingConformanceFixture.CameraConfig, artifact, "source", reportedTiming);
        Assert.AreEqual(reportedTiming.ExposureStartedUtc, reportedInput.ObservationStartedUtc);
        Assert.AreEqual(reportedTiming.ExposureEndedUtc, reportedInput.ObservationEndedUtc);
        var acceleratedTiming = new CaptureAcquisitionTiming(
            frame.TimestampUtc,
            frame.TimestampUtc,
            frame.TimestampUtc);
        var acceleratedInput = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            ProcessingConformanceFixture.CameraConfig, artifact, "source", acceleratedTiming);
        Assert.AreEqual(frame.TimestampUtc, acceleratedInput.ObservationStartedUtc);
        Assert.AreEqual(frame.TimestampUtc.Add(frame.Metadata.Exposure), acceleratedInput.ObservationEndedUtc);

        var fallbackTimestamp = frame.TimestampUtc.AddTicks(1234);
        var fallbackFrame = frame with
        {
            TimestampUtc = fallbackTimestamp,
            Metadata = frame.Metadata with { Exposure = TimeSpan.FromSeconds(1) }
        };
        var fallbackSubmission = new CaptureLoopSubmission(
            new CaptureRequest(fallbackTimestamp, fallbackFrame.Metadata.Exposure, CaptureMode.Still),
            new CaptureResult(
                fallbackFrame,
                new CaptureSetpoint(fallbackFrame.Metadata.Exposure, fallbackFrame.Metadata.Gain, null, null),
                TimeSpan.Zero,
                CaptureMode.Still,
                false),
            fallbackTimestamp,
            fallbackFrame.Metadata.Exposure,
            TimeSpan.Zero);
        var fallbackDescriptor = RawCaptureDescriptorFactory.Create(
            ProcessingConformanceFixture.CameraConfig,
            fallbackSubmission,
            new RawCaptureIdentity(
                ProcessingConformanceFixture.CameraConfig.AgentId!,
                1,
                Guid.NewGuid(),
                Guid.NewGuid()),
            new string('A', 64),
            fallbackTimestamp.AddSeconds(2));
        var canonicalFallbackStart = DateTimeOffset.FromUnixTimeMilliseconds(fallbackTimestamp.ToUnixTimeMilliseconds());
        Assert.AreEqual(canonicalFallbackStart, fallbackDescriptor.Timing.ExposureStartedUtc);
        Assert.AreEqual(canonicalFallbackStart.AddSeconds(1), fallbackDescriptor.Timing.ExposureEndedUtc);
        Assert.AreEqual(fallbackDescriptor.Timing.ExposureEndedUtc, fallbackDescriptor.Timing.ReadoutCompletedUtc);
        var syntheticIdentity = new string('B', 64);
        var syntheticFrame = fallbackFrame with
        {
            Metadata = fallbackFrame.Metadata with
            {
                Extra = new Dictionary<string, string>
                {
                    ["syntheticCalibrationSchema"] = "synthetic-calibration-model-v1",
                    ["syntheticCalibrationModelSha256"] = syntheticIdentity
                }
            }
        };
        var syntheticDescriptor = RawCaptureDescriptorFactory.Create(
            ProcessingConformanceFixture.CameraConfig,
            fallbackSubmission with
            {
                Result = fallbackSubmission.Result with { Frame = syntheticFrame }
            },
            new RawCaptureIdentity(
                ProcessingConformanceFixture.CameraConfig.AgentId!,
                2,
                Guid.NewGuid(),
                Guid.NewGuid()),
            new string('C', 64),
            fallbackTimestamp.AddSeconds(2));
        Assert.AreEqual("synthetic-calibration-model", syntheticDescriptor.Profiles.Calibration.Name);
        Assert.AreEqual("synthetic-calibration-model-v1", syntheticDescriptor.Profiles.Calibration.Version);
        Assert.AreEqual(syntheticIdentity, syntheticDescriptor.Profiles.Calibration.Sha256);
        var model = new SyntheticCalibrationModelV1();
        var matchingDescriptor = syntheticDescriptor with
        {
            Profiles = syntheticDescriptor.Profiles with
            {
                Calibration = syntheticDescriptor.Profiles.Calibration with
                {
                    Sha256 = SyntheticCalibrationReferenceGenerator.ComputeModelIdentitySha256(model)
                }
            }
        };
        Assert.IsTrue(CalibrationCaptureProcessingStep.MatchesSyntheticCalibrationModel(matchingDescriptor, model));
        Assert.IsFalse(CalibrationCaptureProcessingStep.MatchesSyntheticCalibrationModel(
            matchingDescriptor, model with { Seed = model.Seed + 1 }));
        var reconstructedArtifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            ProcessingConformanceFixture.CameraConfig,
            new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, fallbackFrame),
            "source",
            reconstructionDescriptor: fallbackDescriptor);
        Assert.AreEqual(fallbackDescriptor.Capture.CaptureSequence, reconstructedArtifact.CaptureSequence);

        var outcome = await adapter.ExecuteAsync(
            ProcessingConformanceFixture.CreateRequest(input), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status);
        ProcessingConformanceFixture.AssertProduct(outcome.Products.Single());

        var configuredSensor = ProcessingConformanceFixture.CameraConfig.Rig.Sensor with
        {
            ByteOrder = SampleByteOrder.BigEndian
        };
        var configuredRig = ProcessingConformanceFixture.CameraConfig.Rig with { Sensor = configuredSensor };
        var configuredFrame = frame with
        {
            Metadata = frame.Metadata with
            {
                Extra = new Dictionary<string, string>
                {
                    ["blackLevelAdu"] = "64",
                    ["sensorAdcBitDepth"] = "12"
                }
            }
        };
        var configuredArtifact = new FrameArtifact(
            artifact.ArtifactId,
            artifact.Role,
            configuredFrame,
            artifact.SourceArtifactIds,
            artifact.RecipeVersion);
        var projected = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            ProcessingConformanceFixture.CameraConfig with { Rig = configuredRig }, configuredArtifact, "source");
        Assert.AreEqual(FrameByteOrder.BigEndian, projected.Layout!.ByteOrder);
        Assert.AreEqual(64d, projected.Layout.BlackLevel);
        Assert.AreEqual(4095d, projected.Layout.WhiteLevel);

        configuredFrame = configuredFrame with
        {
            Metadata = configuredFrame.Metadata with
            {
                Extra = new Dictionary<string, string>
                {
                    ["blackLevelAdu"] = "64",
                    ["sensorAdcBitDepth"] = "14",
                    ["containerBitDepth"] = "16",
                    ["whiteLevelAdu"] = "65535"
                }
            }
        };
        configuredArtifact = new FrameArtifact(
            artifact.ArtifactId,
            artifact.Role,
            configuredFrame,
            artifact.SourceArtifactIds,
            artifact.RecipeVersion);
        projected = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            ProcessingConformanceFixture.CameraConfig with { Rig = configuredRig }, configuredArtifact, "source");
        Assert.AreEqual(64d, projected.Layout!.BlackLevel);
        Assert.AreEqual(ushort.MaxValue, projected.Layout.WhiteLevel);

        var previewFrame = new CameraFrame(
            configuredFrame.TimestampUtc,
            2,
            2,
            CameraPixelFormat.Mono8,
            new byte[4],
            configuredFrame.Metadata);
        var previewArtifact = new FrameArtifact(
            Guid.NewGuid(), FrameArtifactRole.Preview, previewFrame, recipeVersion: new string('A', 64));
        var projectedPreview = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            ProcessingConformanceFixture.CameraConfig with { Rig = configuredRig }, previewArtifact, "preview");
        Assert.AreEqual(FrameByteOrder.NotApplicable, projectedPreview.Layout!.ByteOrder);
        Assert.IsNull(projectedPreview.Layout.BlackLevel);
        Assert.AreEqual(byte.MaxValue, projectedPreview.Layout.WhiteLevel);
    }
}
