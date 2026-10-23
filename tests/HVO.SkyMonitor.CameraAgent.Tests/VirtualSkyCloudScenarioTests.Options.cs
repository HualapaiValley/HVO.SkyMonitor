using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCloudScenarioTests
{
    [TestMethod]
    public async Task ScalarPanelsExecuteRegisteredGraphTogglesRoutesAndRetainedRevisionDeterministically()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-scalar-options");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = root,
                ["CameraAgent:RawIngressReserveBytes"] = "0"
            }).Build());
            var publisher = new RecordingPublisher();
            var telemetry = new CaptureTelemetrySink();
            services.AddSingleton<IEnvironmentalObservationPublisher>(publisher);
            services.AddSingleton<ICaptureTelemetrySink>(telemetry);
            using var provider = services.BuildServiceProvider();
            var config = Config(CameraPixelFormat.Mono16, Definition()) with
            {
                Pipeline = new CapturePipelineConfig([
                    new("VirtualSkyCloudObservation", "cloud", 10, DependsOn: ["$raw"]),
                    new("ImageQuality", "quality", 20, DependsOn: ["$raw"]),
                    new("Telemetry", "telemetry", 30, DependsOn: ["quality"]),
                    new("ProjectedScene", "projection", 0, DependsOn: ["$raw"])
                ])
            };
            var module = Module();
            await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            var request = new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(5), CaptureMode.Still);
            var result = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
            var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
            async Task<ProcessingProduct?> Execute(CapturePipelineConfig pipeline)
            {
                var graph = factory.CreateGraph(config with { Pipeline = pipeline });
                try
                {
                    var context = Context(config with { Pipeline = pipeline }, request, result);
                    foreach (var node in graph.Nodes)
                    {
                        context.BeginNode(node.Id, node.Dependencies);
                        await node.Step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
                    }
                    return context.ProcessingOutcomes.SelectMany(static outcome => outcome.Products).SingleOrDefault();
                }
                finally { graph.DisposeSteps(); }
            }

            var graph = factory.CreateGraph(config);
            foreach (var node in graph.SharedPlan!.Nodes.Select(static item => item.Definition)
                .Where(static node => node.Id != "projection"))
            {
                var descriptor = ProcessingNodeOptionsCatalog.BuiltIn.Resolve(node.StepAlias, node.StepVersion).Descriptor;
                Assert.IsNotNull(descriptor, node.StepAlias + ":" + node.StepVersion);
                Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(descriptor, node.EffectiveOptions).IsValid);
            }
            var originalPlan = graph.SharedPlan.PlanIdentitySha256;
            graph.DisposeSteps();
            var original = await Execute(config.Pipeline).ConfigureAwait(false);
            Assert.AreEqual("image-quality-v1", original!.Variant);
            Assert.HasCount(1, publisher.Facts);
            Assert.AreEqual(EnvironmentalObservationSourceKind.Simulated, publisher.Facts.Single().Source.Kind);
            Assert.IsNotNull(telemetry.Latest);

            var model = ProcessingGraphDraftModel.FromPipeline(config.Pipeline, "original", "sky", "2");
            model.Nodes[0].Enabled = false;
            model.Nodes[1].OptionsForm!.Set("outputVariant", "custom-quality");
            model.Nodes[2].Enabled = false;
            Assert.IsTrue(model.TryBuild(out var edited, out var errors), string.Join(' ', errors));
            telemetry.Reset();
            var changed = await Execute(edited).ConfigureAwait(false);
            Assert.AreEqual("custom-quality", changed!.Variant);
            Assert.AreEqual(original.ChecksumSha256, changed.ChecksumSha256, "Routing changes identity, not statistics.");
            Assert.AreNotEqual(original.OutputIdentitySha256, changed.OutputIdentitySha256);
            Assert.HasCount(1, publisher.Facts);
            Assert.IsNull(telemetry.Latest);

            model.Nodes[1].Enabled = false;
            Assert.IsTrue(model.TryBuild(out var allDisabled, out errors), string.Join(' ', errors));
            Assert.IsNull(await Execute(allDisabled).ConfigureAwait(false));
            Assert.HasCount(1, publisher.Facts);
            Assert.IsNull(telemetry.Latest);
            var restored = await Execute(config.Pipeline).ConfigureAwait(false);
            Assert.AreEqual(original.OutputIdentitySha256, restored!.OutputIdentitySha256);
            CollectionAssert.AreEqual(original.Payload.ToArray(), restored.Payload.ToArray());
            Assert.HasCount(2, publisher.Facts);
            Assert.AreEqual(publisher.Facts[0].ObservationId, publisher.Facts[1].ObservationId);
            Assert.IsNotNull(telemetry.Latest);
            var restoredGraph = factory.CreateRetainedGraph(config);
            Assert.AreEqual(originalPlan, restoredGraph.SharedPlan!.PlanIdentitySha256);
            restoredGraph.DisposeSteps();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
