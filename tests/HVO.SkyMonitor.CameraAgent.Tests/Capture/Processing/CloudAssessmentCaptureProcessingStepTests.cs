using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using System.Text.Json;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
public sealed class CloudAssessmentCaptureProcessingStepTests
{
    [TestMethod]
    public async Task DisabledStepDoesNotPinConfiguredReference()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-disabled-reference", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var loader = new CameraAgentClearReferenceLoader(Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root
            }));
            var step = new CloudAssessmentCaptureProcessingStep(
                new CaptureProcessingStepMetadata("cloud", "CloudAssessment", 80),
                new CloudAssessmentProcessingStepOptions
                {
                    Enabled = false,
                    ClearReferenceManifestPath = "missing.manifest.json"
                },
                new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor()),
                loader);

            Assert.IsFalse(step.Enabled);
            Assert.IsEmpty(await loader.GetRetentionHoldsAsync(root, CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task MissingConfiguredReferenceDoesNotCreateRetentionHold()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-missing-reference", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var loader = new CameraAgentClearReferenceLoader(Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root
            }));
            loader.RegisterRetentionHold("w6/clear-reference.manifest.json");

            Assert.IsEmpty(await loader.GetRetentionHoldsAsync(root, CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task MissingConfiguredReferenceProducesMetadataWithoutFabricatingFrame()
    {
        var currentFrame = CreateFrame((x, y) =>
        {
            var reference = 1000 + x * 100 + y * 10;
            return (ushort)(x < 1 ? reference : reference / 2 + 100);
        });
        var request = new CaptureRequest(currentFrame.TimestampUtc, currentFrame.Metadata.Exposure, CaptureMode.Still);
        var result = new CaptureResult(
            currentFrame,
            new CaptureSetpoint(currentFrame.Metadata.Exposure, currentFrame.Metadata.Gain, null, null),
            TimeSpan.Zero,
            CaptureMode.Still,
            false);
        var context = new CaptureProcessingContext(
            ProcessingConformanceFixture.CameraConfig,
            new CaptureLoopSubmission(
                request,
                result,
                currentFrame.TimestampUtc,
                currentFrame.Metadata.Exposure,
                TimeSpan.Zero));
        context.BeginNode("calibration", []);
        var calibrated = context.AddDerivative(
            FrameArtifactRole.Calibrated,
            currentFrame,
            "calibration-v1");
        context.BeginNode("cloud", ["calibration"]);
        var step = new CloudAssessmentCaptureProcessingStep(
            new CaptureProcessingStepMetadata("cloud", "CloudAssessment", 80),
            new CloudAssessmentProcessingStepOptions
            {
                ClearReferenceManifestPath = $"missing-{Guid.NewGuid():N}.manifest.json",
                GridColumns = 1,
                GridRows = 1,
                TransmissionThresholdMillionths = 750_000,
                MinimumReferenceSignal = 1,
                MinimumSamplesPerTile = 2
            },
            new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor()),
            new CameraAgentClearReferenceLoader(Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = Path.GetTempPath()
            })));

        await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);

        var product = context.ProcessingOutcomes.Single().Products.Single();
        var assessment = CloudAssessmentJson.Parse(product.Payload).Assessment!;
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, context.ProcessingOutcomes.Single().Status);
        Assert.AreEqual(FrameArtifactRole.Metadata, product.Role);
        Assert.IsNull(product.Layout);
        Assert.AreEqual(ProcessingProductKind.Metadata, product.Kind);
        Assert.AreEqual(CloudAssessmentV1.CurrentSchemaVersion, product.SchemaVersion);
        Assert.AreEqual(assessment.AssessmentIdentitySha256, product.ContentIdentitySha256);
        Assert.AreEqual(CloudAssessmentStatus.InsufficientEvidence, assessment.Status);
        Assert.IsNull(assessment.CoverageMillionths);
        Assert.HasCount(1, product.SourceArtifactIds);
        Assert.AreEqual(calibrated.ArtifactId, product.SourceArtifactIds[0]);
        Assert.HasCount(2, context.AllArtifacts);
        Assert.IsFalse(context.AllArtifacts.Any(static artifact => artifact.Role == FrameArtifactRole.Metadata));
        Assert.IsTrue(context.GetCurrentInputEvidence().Any(static input =>
            input.Kind == "CanonicalContext" && input.Name == "environment" && input.IdentitySha256 is { Length: 64 }));

        context.RegisterProcessingProduct(product);
        context.BeginNode("cloud-presentation", ["cloud"]);
        var layer = new CloudPresentationLayerCaptureProcessingStep(
            new CaptureProcessingStepMetadata("cloud-presentation", "CloudPresentationLayer", 90),
            new CloudPresentationLayerProcessingStepOptions
            {
                MaskOutputVariant = "cloud-mask",
                LabelOutputVariant = "cloud-label",
                WidthPixels = currentFrame.Width,
                HeightPixels = currentFrame.Height,
                DrawLabels = false
            });
        await layer.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);

        var layers = context.ProcessingOutcomes[^1].Products;
        Assert.HasCount(2, layers);
        Assert.IsTrue(layers.All(item => item.Kind == ProcessingProductKind.Metadata &&
            item.SchemaVersion == PresentationLayerPayloadV1.CurrentSchemaVersion));
        Assert.IsTrue(layers.All(item => PresentationLayerPayloadJson.Parse(item.Payload).Payload?.SourceIdentitySha256 ==
            assessment.AssessmentIdentitySha256));
    }

    [TestMethod]
    public async Task ConfiguredReferenceLoaderValidatesAndLoadsReconstructableArtifact()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-reference", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payload = new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 };
            var template = ReconstructableCaptureContractTests.CreateManifest(
                CameraPixelFormat.Mono16, 2, 2, 4, payload);
            var manifest = template with { RelativeArtifactPath = "reference.bin" };
            await File.WriteAllBytesAsync(Path.Combine(root, "reference.bin"), payload).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                Path.Combine(root, "reference.manifest.json"),
                CaptureContractJson.Serialize(manifest)).ConfigureAwait(false);
            var loader = new CameraAgentClearReferenceLoader(Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root
            }));
            loader.RegisterRetentionHold("reference.manifest.json");

            var reference = await loader.LoadAsync(
                "reference.manifest.json",
                CancellationToken.None).ConfigureAwait(false);
            var holds = await loader.GetRetentionHoldsAsync(root, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(manifest.Descriptor.Artifact.ArtifactId, reference.ArtifactId);
            Assert.AreEqual(manifest.Descriptor.Layout, reference.Layout);
            Assert.AreEqual(
                CameraAgentRecipeExecutionAdapter.CreateCompatibility(manifest.Descriptor),
                reference.Compatibility);
            CollectionAssert.AreEqual(payload, reference.Payload.ToArray());
            Assert.HasCount(1, holds);
            var hold = holds[0];
            Assert.AreEqual(manifest.Descriptor.Artifact.ArtifactId, hold.ArtifactId);
            Assert.AreEqual("reference.bin", hold.PayloadRelativePath);
            Assert.AreEqual("reference.manifest.json", hold.SidecarRelativePath);
            Assert.IsEmpty(await loader.GetRetentionHoldsAsync(
                Path.Combine(root, "other-storage"), CancellationToken.None).ConfigureAwait(false));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await loader.LoadAsync("../outside.json", CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CloudEnvironmentUsesPersistedFreshRainAssociation()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-cloud-environment", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var manifest = ReconstructableCaptureContractTests.CreateManifest(
                CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
            var timing = manifest.Descriptor.Timing;
            using var parametersDocument = JsonDocument.Parse("{}");
            var parameters = parametersDocument.RootElement.Clone();
            var fact = new EnvironmentalObservationFactV1(
                EnvironmentalObservationSchemaVersions.V1,
                Guid.Parse("93000000-0000-0000-0000-000000000001"),
                new EnvironmentalObservationSource(
                    "test-provider",
                    "rain-state",
                    "1.0.0",
                    EnvironmentalObservationSourceKind.Measured,
                    new EnvironmentalObservationProvenance(
                        new ProcessingAlgorithmIdentity("normalizer", "1.0.0"),
                        parameters,
                        CaptureContractJson.ComputeCanonicalJsonSha256(parameters))),
                timing.ExposureStartedUtc,
                null,
                null,
                timing.ExposureStartedUtc.AddMinutes(-1),
                timing.ExposureEndedUtc.AddMinutes(1),
                timing.ExposureEndedUtc.AddMinutes(1),
                new EnvironmentalObservationValue(
                    EnvironmentalObservationKind.RainState,
                    EnvironmentalObservationUnit.Boolean,
                    null,
                    true,
                    EnvironmentalObservationQuality.Good),
                []);
            using var store = new SqliteEnvironmentalObservationOutbox();
            var committed = await store.CommitLocalAsync(root, fact, CancellationToken.None).ConfigureAwait(false);
            var configured = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions { Enabled = true }
            });
            var associations = new EnvironmentalAssociationService(store, store, configured, TimeProvider.System);
            var environment = new CameraAgentCloudEnvironment(
                associations,
                store,
                configured);
            var frame = CreateFrame(static (_, _) => 1);
            var request = new CaptureRequest(frame.TimestampUtc, frame.Metadata.Exposure, CaptureMode.Still);
            var result = new CaptureResult(
                frame,
                new CaptureSetpoint(frame.Metadata.Exposure, frame.Metadata.Gain, null, null),
                TimeSpan.Zero,
                CaptureMode.Still,
                false);
            var submission = new CaptureLoopSubmission(
                request, result, frame.TimestampUtc, frame.Metadata.Exposure, TimeSpan.Zero);
            var receipt = new RawCaptureReceipt(
                RawIngressOutcome.Committed,
                manifest,
                new StoredFrameReference("frames/raw.bin", Path.Combine(root, "frames", "raw.bin"), frame.TimestampUtc, FrameArtifactRole.Raw),
                new string('A', 64));
            var context = new CaptureProcessingContext(ProcessingConformanceFixture.CameraConfig, submission, receipt);

            Assert.IsNull(await environment.CreateInputAsync(context, CancellationToken.None).ConfigureAwait(false));
            _ = await associations.AssociateAsync(
                manifest.Descriptor.Capture.CaptureId,
                manifest.Descriptor.Capture.CaptureSequence,
                timing.ExposureStartedUtc,
                timing.ExposureEndedUtc,
                manifest.Descriptor.Capture.RigId,
                [EnvironmentalObservationKind.RainState],
                CancellationToken.None).ConfigureAwait(false);
            var input = await environment.CreateInputAsync(context, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(input);
            using var payload = JsonDocument.Parse(input!.Payload);
            var rootElement = payload.RootElement;

            Assert.AreEqual("Fresh", rootElement.GetProperty("precipitationStatus").GetString());
            Assert.IsTrue(rootElement.GetProperty("precipitationDetected").GetBoolean());
            Assert.AreEqual(fact.ObservationId, rootElement.GetProperty("precipitationObservationId").GetGuid());
            Assert.AreEqual(committed.Record.ContentSha256, rootElement.GetProperty("precipitationContentSha256").GetString());
            Assert.AreEqual(64, rootElement.GetProperty("inputIdentitySha256").GetString()!.Length);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static CameraFrame CreateFrame(Func<int, int, ushort> value)
    {
        const int width = 2;
        const int height = 2;
        var payload = new byte[width * height * 2];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sample = value(x, y);
                var offset = (y * width + x) * 2;
                payload[offset] = (byte)sample;
                payload[offset + 1] = (byte)(sample >> 8);
            }
        }
        return new CameraFrame(
            DateTimeOffset.Parse("2026-01-15T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            width,
            height,
            CameraPixelFormat.Mono16,
            payload,
            new FrameMetadata(
                TimeSpan.FromSeconds(1),
                1,
                0,
                Extra: new Dictionary<string, string>
                {
                    ["blackLevelAdu"] = "0",
                    ["sensorAdcBitDepth"] = "16"
                }));
    }
}
