using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    public async Task SavedV12Draft_RejectsVersionSubstitutionAndExecutesExplicitlyRecompiledV13Revision()
    {
        const string previous = "projected-scene-presentation-v12-plain-circle-markers";
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-saved-v12-execution");
        try
        {
            using var provider = CreateProvider(root);
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var config = CreateProjectedSceneConfiguration();
            config = config with
            {
                Rig = config.Rig with { Sensor = config.Rig.Sensor with { WidthPixels = 64, HeightPixels = 64 } },
                Pipeline = new CapturePipelineConfig([
                    new CaptureProcessingStepConfig("ProjectedScene", "projected-scene", 10, DependsOn: ["$raw"]),
                    new CaptureProcessingStepConfig("ScenePresentationLayer", "overlay", 20,
                        JsonSerializer.SerializeToElement(new { constellationLineThickness = 5 }), ["projected-scene"])
                ])
            };
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var graph = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateGraph(config);
            byte[] savedBytes;
            try
            {
                savedBytes = Encoding.UTF8.GetBytes(graph.SharedPlan!.CanonicalDefinition.GetRawText()
                    .Replace(PresentationLayerProducers.SceneProducerVersion, previous, StringComparison.Ordinal));
            }
            finally
            {
                graph.DisposeSteps();
            }

            // This is a saved portable draft, not a pipeline whose alias silently selects the installed implementation.
            var saved = ProcessingGraphJson.Parse(savedBytes).Definition!;
            Assert.IsEmpty(ProcessingNodeOptionsCatalog.BuiltIn.Validate(saved));
            var declared = ProcessingGraphCompiler.Compile(saved).Plan!;
            Assert.AreEqual(previous, declared.Nodes.Single(static n => n.Definition.Id == "overlay").Definition.StepVersion);
            var now = DateTimeOffset.UtcNow;

        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
