using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
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
