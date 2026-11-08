using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    [DataRow("MeasuredStellarAssociations")]
    [DataRow("MeasuredAstrometry")]
    [DataRow("CaptureSatelliteTracks")]
    [DataRow("CaptureSatelliteLayer")]
    [DataRow("Storage")]
    public void RemainingNodeDefaultsMatchRuntimeBinding(string alias)
    {
        object runtime = alias switch
        {
            "MeasuredStellarAssociations" => new MeasuredStellarAssociationProcessingStepOptions(),
            "MeasuredAstrometry" => new MeasuredAstrometryCaptureOptions(),
            "CaptureSatelliteTracks" => new CaptureSatelliteTracksOptions(),
            "CaptureSatelliteLayer" => new CaptureSatelliteLayerOptions(),
            _ => new FileStorageCaptureProcessingStepOptions()
        };
        var descriptor = ProcessingNodeOptionsForm.DescriptorFor(alias);
        Assert.IsNotNull(descriptor);
        var defaults = ProcessingNodeOptionsCodec.Decode(descriptor, JsonSerializer.SerializeToElement(new { }));
        var bound = ProcessingNodeOptionsCodec.Decode(descriptor, JsonSerializer.SerializeToElement(runtime));
        Assert.IsTrue(defaults.IsValid);
        Assert.IsTrue(bound.IsValid);
        foreach (var option in defaults.Options)
            Assert.AreEqual(option.Value, bound.Get(option.Descriptor.Key).Value, option.Descriptor.Key);
        Assert.IsNull(ProcessingNodeOptionsCatalog.BuiltIn.Resolve(alias, descriptor.StepVersion + "-future").Descriptor);
    }

    [TestMethod]
    public void EveryRegisteredNodeHasAnOptionsPanel()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-node-inventory");
        try
        {
            using var provider = CreateRemainingOptionsProvider(root);
            foreach (var registration in provider.GetServices<CaptureProcessingStepRegistration>())
                Assert.IsNotNull(ProcessingNodeOptionsForm.DescriptorFor(registration.Alias), registration.Alias);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow("MeasuredStellarAssociations", "outputVariant", "measured-custom")]
    [DataRow("MeasuredAstrometry", "captureStride", "120")]
    [DataRow("CaptureSatelliteTracks", "allowStaleDiagnostics", "True")]
    [DataRow("CaptureSatelliteLayer", "inputVariant", "tracks-custom")]
    [DataRow("Storage", "retentionDays", "60")]
    public async Task RemainingNodeOptionsPersistAcrossRestartWithExplicitActivationAndRollback(string alias, string key, string value)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-remaining-options");
        try
        {
            var pipeline = RemainingOptionsPipeline(root);
            var configuration = CreateConfiguration() with { Pipeline = pipeline };
            string sourceRevision;
            string editedRevision;
            string storedSource;
            using (var provider = CreateRemainingOptionsProvider(root))
            {
                await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var initial = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
                sourceRevision = initial.ActiveRevisionId;
                storedSource = JsonSerializer.Serialize(await operations.ReadRevisionPipelineAsync(sourceRevision, CancellationToken.None).ConfigureAwait(false));
                var model = ProcessingGraphDraftModel.FromPipeline(pipeline, sourceRevision, "remaining-options", "2");
                var row = model.Nodes.Single(node => node.Type == alias);
                Assert.IsNotNull(row.OptionsForm, alias);
                row.OptionsForm.Set(key, value);
                if (alias == "CaptureSatelliteLayer")
                    model.Nodes.Single(node => node.Type == "CaptureSatelliteTracks").OptionsForm!.Set("outputVariant", value);
                Assert.IsTrue(model.TryBuild(out var edited, out var errors), string.Join(' ', errors));
                var draft = await operations.CreateRevisionAsync("remaining-options", "2", edited, "create", "test", null, CancellationToken.None).ConfigureAwait(false);
                editedRevision = draft.RevisionId;
                Assert.AreEqual(ProcessingGraphRevisionLifecycle.Draft, draft.Lifecycle);
                await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                    await operations.ActivateRevisionAsync(editedRevision, initial.StateVersion, "early", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                await operations.ValidateRevisionAsync(editedRevision, "validate", "test", null, CancellationToken.None).ConfigureAwait(false);
                var activated = await operations.ActivateRevisionAsync(editedRevision, initial.StateVersion, "activate", "test", null, CancellationToken.None).ConfigureAwait(false);
                await operations.RollbackRevisionAsync(sourceRevision, activated.StateVersion, "rollback", "test", null, CancellationToken.None).ConfigureAwait(false);
            }
            using var restarted = CreateRemainingOptionsProvider(root);
            await restarted.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var reopened = restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var retained = await reopened.ReadRevisionPipelineAsync(sourceRevision, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(storedSource, JsonSerializer.Serialize(retained));
            var saved = await reopened.ReadRevisionPipelineAsync(editedRevision, CancellationToken.None).ConfigureAwait(false);
            var restored = ProcessingGraphDraftModel.FromPipeline(saved!, editedRevision, "remaining-options", "3");
            Assert.AreEqual(value, restored.Nodes.Single(node => node.Type == alias).OptionsForm!.Get(key));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    internal static CapturePipelineConfig RemainingOptionsPipeline(string root)
    {
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        var catalog = new AstrometryCatalogSnapshotV1(new CatalogMetadata("fixture", "1",
            new Uri("https://example.test/catalog"), new string('A', 64), "test", "1"), [],
            new("fixture", "1", "fixture", "unspecified"), true, 7);
        var pipeline = new CapturePipelineConfig([
            new("ProjectedScene", "scene", DependsOn: ["$raw"], Publication: durable),
        new("MeasuredStellarAssociations", "measured", DependsOn: ["scene"], Publication: durable),
        new("MeasuredAstrometry", "astrometry", DependsOn: ["scene", "measured"], Required: false,
            Options: JsonSerializer.SerializeToElement(new MeasuredAstrometryCaptureOptions { Catalog = catalog }), Publication: durable),
        new("CaptureSatelliteTracks", "satellites", DependsOn: ["$raw"], Required: false,
            Options: JsonSerializer.SerializeToElement(new CaptureSatelliteTracksOptions { CatalogNumbers = ["25544"] }), Publication: durable),
        new("CaptureSatelliteLayer", "satellite-layer", DependsOn: ["satellites"], Required: false, Publication: durable),
        new("Storage", "storage", DependsOn: ["$raw"],
            Options: JsonSerializer.SerializeToElement(new FileStorageCaptureProcessingStepOptions { StorageRoot = root }))
        ]);
        return pipeline;
    }

    internal static ServiceProvider CreateRemainingOptionsProvider(string root) => CreateProvider(root,
        configureServices: services =>
        {
            services.AddSingleton(Moq.Mock.Of<ICameraAgentOrbitalTimeSource>());
            services.AddSingleton(Moq.Mock.Of<IDeploymentLocationStore>());
        });
}
