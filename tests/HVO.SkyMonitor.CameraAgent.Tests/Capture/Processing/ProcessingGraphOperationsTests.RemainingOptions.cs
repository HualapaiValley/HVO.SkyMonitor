using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
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
            var configuration = CreateConfiguration() with { Pipeline = pipeline };
            string sourceRevision;
            string editedRevision;
            using (var provider = CreateProvider(root))
            {
                await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var initial = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
                sourceRevision = initial.ActiveRevisionId;
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
            using var restarted = CreateProvider(root);
            await restarted.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var reopened = restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var retained = await reopened.ReadRevisionPipelineAsync(sourceRevision, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(JsonSerializer.Serialize(pipeline), JsonSerializer.Serialize(retained));
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
}
