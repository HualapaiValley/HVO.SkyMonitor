using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;

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
        context.BeginNode("cloud", []);
        var step = new CloudAssessmentCaptureProcessingStep(
            new CaptureProcessingStepMetadata("cloud", "CloudAssessment", 80),
            new CloudAssessmentProcessingStepOptions
            {
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
        Assert.AreEqual(CloudAssessmentStatus.InsufficientEvidence, assessment.Status);
        Assert.IsNull(assessment.CoverageMillionths);
        Assert.HasCount(1, product.SourceArtifactIds);
        Assert.HasCount(1, context.AllArtifacts);
        Assert.IsFalse(context.AllArtifacts.Any(static artifact => artifact.Role == FrameArtifactRole.Metadata));
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
