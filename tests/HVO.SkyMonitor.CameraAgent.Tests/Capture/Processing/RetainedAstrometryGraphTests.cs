using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    private static AstrometryCatalogSnapshotV1 EmptyAstrometryCatalog() => new(new CatalogMetadata("fixture", "1",
        new Uri("https://example.test/catalog"), new string('A', 64), "test", "1"), [],
        new("fixture", "1", "fixture", "unspecified"), true, 7);

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RetainedAstrometry_CadenceSkipsBeforeResolvingInputsOrDispatchingRecipe()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var context = new CaptureProcessingContext(fixture.Item.Config, fixture.Item.Submission, fixture.Item.RawCapture);
            Assert.AreNotEqual(0L, (fixture.Manifest.Descriptor.Capture.CaptureSequence - 1) % 60);
            using var gate = new CaptureAstrometryExecutionGate();
            var step = new MeasuredAstrometryCaptureProcessingStep(new("astrometry", "MeasuredAstrometry", 1), new() { Catalog = EmptyAstrometryCatalog() },
                new(new FixedOutcomeExecutor(ProcessingOutcome.TerminalFailure("unexpected-dispatch"))), gate);
            await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("astrometry.capture-cadence", context.ProcessingOutcomes.Single().ReasonCode);
            Assert.IsEmpty(context.ProcessingProducts);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("valid")]
    [DataRow("memory-only")]
    [DataRow("legacy")]
    [DataRow("fast-cadence")]
    [DataRow("required")]
    public void RetainedAstrometry_GraphRequiresExplicitDurableInputsAndBoundedCadence(string mode)
    {
        var root = CreateTestRoot();
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["CameraAgent:RawIngressRoot"] = root }).Build());
            using var provider = services.BuildServiceProvider();
            var registration = provider.GetServices<CaptureProcessingStepRegistration>()
                .Single(r => r.Alias == MeasuredAstrometryCaptureProcessingStep.Alias);
            Assert.IsFalse(registration.AutoInclude);
            var catalog = EmptyAstrometryCatalog();
            var options = new MeasuredAstrometryCaptureOptions { Catalog = catalog, CaptureStride = mode == "fast-cadence" ? 1 : 60 };
            var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
            var config = CreateConfig() with
            {
                Pipeline = new([
                new("ProjectedScene", "scene", DependsOn: ["$raw"], Publication: durable),
                new("MeasuredStellarAssociations", "measured", DependsOn: ["scene"], Publication: durable),
                new("MeasuredAstrometry", "astrometry", DependsOn: ["scene", "measured"], Required: mode == "required", Options: JsonSerializer.SerializeToElement(options),
                    Publication: mode == "memory-only" ? new(CaptureProcessingPersistenceMode.MemoryOnly) : durable),
                new("Storage", "storage", DependsOn: ["$raw"], Options: JsonSerializer.SerializeToElement(
                    new FileStorageCaptureProcessingStepOptions { StorageRoot = root, QueueForUpload = false, UpdateLatestFrame = false }))
            ], mode == "legacy" ? CapturePipelineSchemaVersions.LegacyV1 : CapturePipelineSchemaVersions.ExplicitV2)
            };
            var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
            if (mode == "valid")
            {
                var graph = factory.CreateGraph(config);
                Assert.AreEqual(1, graph.Nodes.Count(n => n.Step is MeasuredAstrometryCaptureProcessingStep));
                Assert.HasCount(2, ((IMultiOutputCaptureProcessingGraphStep)graph.Nodes.Single(n => n.Id == "astrometry").Step).Outputs);
            }
            else if (mode == "fast-cadence") Assert.ThrowsExactly<System.ComponentModel.DataAnnotations.ValidationException>(() => factory.CreateGraph(config));
            else Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateGraph(config));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
