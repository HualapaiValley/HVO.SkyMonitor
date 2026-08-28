using System.Text.Json;
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
    public void RawDescriptorEnforcesVirtualCalibrationProvenance()
    {
        var model = new VirtualCalibrationSourceModelV1();
        var modelIdentity = VirtualCalibrationSourceGenerator.ComputeModelIdentitySha256(model);
        var descriptor = CreateRawDescriptor(new Dictionary<string, string>
        {
            ["virtualCalibrationSchema"] = model.SchemaVersion,
            ["virtualCalibrationModelSha256"] = modelIdentity,
            ["virtualCalibrationAlgorithm"] = VirtualCalibrationSourceGenerator.LightCorruptionAlgorithmVersion
        });

        Assert.AreEqual("virtual-calibration-source-model", descriptor.Profiles.Calibration.Name);
        Assert.AreEqual(VirtualCalibrationSourceModelV1.CurrentSchemaVersion, descriptor.Profiles.Calibration.Version);
        Assert.AreEqual(modelIdentity, descriptor.Profiles.Calibration.Sha256);

        Assert.ThrowsExactly<InvalidDataException>(() => CreateRawDescriptor(new Dictionary<string, string>
        {
            ["virtualCalibrationSchema"] = model.SchemaVersion,
            ["virtualCalibrationModelSha256"] = "not-a-sha256-identity",
            ["virtualCalibrationAlgorithm"] = VirtualCalibrationSourceGenerator.LightCorruptionAlgorithmVersion
        }));
        Assert.ThrowsExactly<InvalidDataException>(() => CreateRawDescriptor(new Dictionary<string, string>
        {
            ["virtualCalibrationModelSha256"] = modelIdentity,
            ["virtualCalibrationAlgorithm"] = VirtualCalibrationSourceGenerator.LightCorruptionAlgorithmVersion
        }));
        Assert.ThrowsExactly<InvalidDataException>(() => CreateRawDescriptor(new Dictionary<string, string>
        {
            ["virtualCalibrationSchema"] = model.SchemaVersion,
            ["virtualCalibrationModelSha256"] = modelIdentity
        }));
        Assert.ThrowsExactly<InvalidDataException>(() => CreateRawDescriptor(new Dictionary<string, string>
        {
            ["virtualCalibrationSchema"] = model.SchemaVersion
        }));
        Assert.ThrowsExactly<InvalidDataException>(() => CreateRawDescriptor(new Dictionary<string, string>
        {
            ["virtualCalibrationAlgorithm"] = VirtualCalibrationSourceGenerator.LightCorruptionAlgorithmVersion
        }));
        Assert.ThrowsExactly<InvalidDataException>(() => CreateRawDescriptor(new Dictionary<string, string>
        {
            ["virtualCalibrationModelSha256"] = string.Empty
        }));
        Assert.ThrowsExactly<InvalidDataException>(() => CreateRawDescriptor(new Dictionary<string, string>
        {
            ["syntheticCalibrationSchema"] = SyntheticCalibrationModelV1.CurrentSchemaVersion,
            ["syntheticCalibrationModelSha256"] = new string('A', 64),
            ["virtualCalibrationSchema"] = model.SchemaVersion,
            ["virtualCalibrationModelSha256"] = modelIdentity,
            ["virtualCalibrationAlgorithm"] = VirtualCalibrationSourceGenerator.LightCorruptionAlgorithmVersion
        }));
    }

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
        Assert.AreEqual(
            CaptureContractJson.ComputeCanonicalJsonSha256(
                JsonSerializer.SerializeToElement(ProcessingConformanceFixture.CameraConfig.Rig.Sensor)),
            fallbackDescriptor.Profiles.Sensor.Sha256);
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
        Assert.ThrowsExactly<InvalidDataException>(() => CameraAgentRecipeExecutionAdapter.CreateArtifact(
            ProcessingConformanceFixture.CameraConfig,
            new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, fallbackFrame),
            "source",
            reconstructionDescriptor: fallbackDescriptor));
        var descriptorBoundArtifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            ProcessingConformanceFixture.CameraConfig,
            new FrameArtifact(
                fallbackDescriptor.Artifact.ArtifactId,
                FrameArtifactRole.Raw,
                fallbackFrame,
                recipeVersion: "conflicting-free-form-version"),
            "source",
            reconstructionDescriptor: fallbackDescriptor);
        Assert.AreEqual(
            ProcessingIdentity.CreateRecipeIdentity(fallbackDescriptor.Artifact.Recipe).IdentitySha256,
            descriptorBoundArtifact.RecipeIdentitySha256);

        var typedProduct = new ProcessingProduct(
            FrameArtifactRole.Metadata, "typed", new string('1', 64), "application/json", null, new byte[] { 1 },
            new string('2', 64), ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
                "typed", "1.0.0", "typed-v1", JsonSerializer.SerializeToElement(new { }))), [], [], TimeSpan.Zero,
            descriptorBoundArtifact.Compatibility)
        {
            Kind = ProcessingProductKind.Metadata,
            SchemaVersion = "typed-v1",
            ContentIdentitySha256 = new string('A', 64)
        };
        var typedArtifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            ProcessingConformanceFixture.CameraConfig,
            new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Metadata, fallbackFrame), "typed", product: typedProduct);
        Assert.AreEqual(ProcessingProductKind.Metadata, typedArtifact.ProductKind);
        Assert.AreEqual("typed-v1", typedArtifact.SchemaVersion);
        Assert.AreEqual(new string('A', 64), typedArtifact.ContentIdentitySha256);

        var outcome = await adapter.ExecuteAsync(
            ProcessingConformanceFixture.CreateRequest(input), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status);
        Assert.AreEqual(
            "81281FA59B3BF1788659B07D2BD415D760CF0BD5015782341AE173E93E9CF273",
            outcome.Products.Single().Recipe.IdentitySha256);
        Assert.AreNotEqual(
            "8EBC03FA468DE991D5B80040359752A5232D9C278B91045B180EA64C2CACAE6E",
            outcome.Products.Single().Recipe.IdentitySha256);
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

        var authoritativeLayout = projected.Layout with
        {
            SampleDepthBits = 14,
            StoredCodeTransform = FrameStoredCodeTransform.FullRangeScaledV1,
            LevelCodeSpace = FrameLevelCodeSpace.StoredContainer,
            Readout = new FrameReadoutDescriptor(
                configuredFrame.Width,
                configuredFrame.Height,
                0,
                0,
                configuredFrame.Width,
                configuredFrame.Height,
                1,
                1,
                FrameBinningAlgorithm.IdentityV1,
                null,
                null)
        };
        configuredFrame = configuredFrame with { Layout = authoritativeLayout };
        configuredArtifact = new FrameArtifact(
            configuredArtifact.ArtifactId,
            configuredArtifact.Role,
            configuredFrame,
            configuredArtifact.SourceArtifactIds,
            configuredArtifact.RecipeVersion);

        projected = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            ProcessingConformanceFixture.CameraConfig with { Rig = configuredRig }, configuredArtifact, "source");

        Assert.AreEqual(authoritativeLayout, projected.Layout);

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

    private static ReconstructionDescriptor CreateRawDescriptor(Dictionary<string, string> extra)
    {
        var timestamp = ProcessingConformanceFixture.CapturedUtc;
        var frame = new CameraFrame(
            timestamp,
            ProcessingConformanceFixture.Layout.Width,
            ProcessingConformanceFixture.Layout.Height,
            ProcessingConformanceFixture.Layout.PixelFormat,
            ProcessingConformanceFixture.Payload,
            new FrameMetadata(TimeSpan.FromSeconds(1), 150, 0, Extra: extra));
        var submission = new CaptureLoopSubmission(
            new CaptureRequest(timestamp, frame.Metadata.Exposure, CaptureMode.Still),
            new CaptureResult(
                frame,
                new CaptureSetpoint(frame.Metadata.Exposure, frame.Metadata.Gain, null, null),
                TimeSpan.Zero,
                CaptureMode.Still,
                false),
            timestamp,
            frame.Metadata.Exposure,
            TimeSpan.Zero);

        return RawCaptureDescriptorFactory.Create(
            ProcessingConformanceFixture.CameraConfig,
            submission,
            new RawCaptureIdentity(
                ProcessingConformanceFixture.CameraConfig.AgentId!,
                1,
                Guid.NewGuid(),
                Guid.NewGuid()),
            new string('F', 64),
            timestamp.AddSeconds(2));
    }
}
