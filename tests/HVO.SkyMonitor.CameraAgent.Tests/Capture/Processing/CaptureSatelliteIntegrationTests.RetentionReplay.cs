using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class CaptureSatelliteIntegrationTests
{
    [TestMethod]
    public async Task CorruptIntentCannotRedirectRetentionAwayFromItsActualRawCapture()
    {
        var root = CreateRoot();
        try
        {
            var f = new CaptureSatelliteFixture();
            using var provider = CreateProvider(root, f, new Clock(f.Descriptor.Timing.DurableIngressUtc));
            var config = Configuration(root, f);
            var raw = await CaptureAsync(provider, config, f).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, (await ProcessAsync(provider, config).ConfigureAwait(false)).Outcome);
            var node = await provider.GetRequiredService<SqliteCaptureProcessingStore>().ReadNodeAsync(
                raw.Manifest.Descriptor.Capture.CaptureId, "satellite", CancellationToken.None).ConfigureAwait(false);
            var retained = node!.Outputs.Single(p => p.ProductSchemaVersion == CaptureSatelliteProducts.InputSchema);
            var input = CaptureSatelliteProducts.ParseInput(await SiloInfrastructureFixture.ReadArtifactBytesAsync(
                provider, retained.ArtifactId, CancellationToken.None).ConfigureAwait(false));
            var path = Directory.GetFiles(Path.Combine(root, "journal", "capture-satellites"), "header.json", SearchOption.AllDirectories).Single();
            var header = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(false))!;
            header["rawPayloadPath"] = "missing.bin";
            await File.WriteAllTextAsync(path, header.ToJsonString()).ConfigureAwait(false);
            var work = provider.GetRequiredService<CaptureSatelliteWorkStore>();
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => work.GetRetentionHoldsAsync(root, CancellationToken.None).AsTask()).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => work.ReconcileAsync(root, CancellationToken.None).AsTask()).ConfigureAwait(false);
            Assert.AreEqual(1L, await ReferenceCountAsync(root, input.InputManifest()).ConfigureAwait(false));
            Assert.IsTrue(File.Exists(raw.StoredFrame.AbsolutePath));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task ArchivedReplayPinsOriginalProductsAndIgnoresChangedCurrentSiteAndSources(bool csvSnapshot, bool hideObjectLabels)
    {
        var root = CreateRoot();
        try
        {
            var f = new CaptureSatelliteFixture();
            var clock = new Clock(f.Descriptor.Timing.DurableIngressUtc);
            HVO.SkyMonitor.CameraAgent.Common.RawIngress.RawCaptureReceipt raw;
            string originalChecksum;
            string originalLayerChecksum;
            string revision;
            using (var provider = CreateProvider(root, f, clock))
            {
                var config = Configuration(root, f, hideObjectLabels: hideObjectLabels);
                if (csvSnapshot)
                    await InstallCsvSnapshotAsync(provider, TestContext.CancellationToken).ConfigureAwait(false);
                raw = await CaptureAsync(provider, config, f).ConfigureAwait(false);
                await SiloInfrastructureFixture.SettleRawAdmissionsAsync(provider, TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, (await ProcessAsync(provider, config).ConfigureAwait(false)).Outcome);
                var original = await provider.GetRequiredService<SqliteCaptureProcessingStore>().ReadNodeAsync(
                    raw.Manifest.Descriptor.Capture.CaptureId, "satellite", CancellationToken.None).ConfigureAwait(false);
                var prediction = original!.Outputs.Single(p => p.ProductSchemaVersion == CaptureOrbitalPredictionJson.SchemaVersion);
                originalChecksum = prediction.Artifact.ChecksumSha256;
                var layerNode = await provider.GetRequiredService<SqliteCaptureProcessingStore>().ReadNodeAsync(
                    raw.Manifest.Descriptor.Capture.CaptureId, "satellite-layer", TestContext.CancellationToken).ConfigureAwait(false);
                var layerOutput = layerNode!.Outputs.Single(p => p.ProductSchemaVersion == PresentationLayerPayloadV1.CurrentSchemaVersion);
                originalLayerChecksum = layerOutput.Artifact.ChecksumSha256;
                var layer = PresentationLayerPayloadJson.Parse(await SiloInfrastructureFixture.ReadArtifactBytesAsync(provider, layerOutput.ArtifactId,
                    TestContext.CancellationToken).ConfigureAwait(false)).Payload;
                Assert.IsNotNull(layer);
                Assert.IsNotEmpty(layer.Segments);
                var sourceOutput = layerNode.Outputs.Single(p => p.ProductSchemaVersion == CaptureSatelliteProducts.SourceSchema);
                using var layerSource = JsonDocument.Parse(await SiloInfrastructureFixture.ReadArtifactBytesAsync(provider, sourceOutput.ArtifactId,
                    TestContext.CancellationToken).ConfigureAwait(false));
                Assert.AreEqual(hideObjectLabels, layerSource.RootElement.TryGetProperty("ObjectLabelsHidden", out var flag) && flag.GetBoolean());
                if (hideObjectLabels) Assert.HasCount(1, layer.TextBlocks);
                if (csvSnapshot)
                {
                    using var product = JsonDocument.Parse(await SiloInfrastructureFixture.ReadArtifactBytesAsync(provider, prediction.ArtifactId,
                        TestContext.CancellationToken).ConfigureAwait(false));
                    var manifest = product.RootElement.GetProperty("input").GetProperty("snapshotManifest");
                    Assert.AreEqual(CsvFixtureManifest, manifest.GetProperty("manifestSha256").GetString());
                    Assert.AreEqual("US Space Force / Space-Track public GP data via CelesTrak",
                        manifest.GetProperty("sources")[0].GetProperty("underlyingLineage").GetString());
                    var track = Assert.ContainsSingle(product.RootElement.GetProperty("tracks").EnumerateArray());
                    Assert.AreEqual("25544", track.GetProperty("lineage").GetProperty("objectIdentity").GetProperty("catalogNumber").GetString());
                }
                using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT graph_revision_id FROM processing_executions WHERE execution_class = 'Live';";
                revision = (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
            }
            using (var restarted = CreateProvider(root, f, clock, unavailable: "throw"))
            {
                restarted.GetRequiredService<HVO.SkyMonitor.CameraAgent.Common.Configuration.ICameraAgentConfigurationAccessor>()
                    .SetConfiguration(Configuration(root, f) with
                    {
                        Rig = f.Configuration.Rig with { ProfileVersion = "current-profile-changed" },
                        DeploymentLocation = DeploymentLocationSnapshot.Create("current-site", 2, "fixture", null,
                            DateTimeOffset.UnixEpoch, null, 0, 0, 500, "UTC")
                    });
                var ingress = restarted.GetRequiredService<HVO.SkyMonitor.CameraAgent.Common.RawIngress.RawCaptureIngress>();
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var store = restarted.GetRequiredService<SqliteCaptureProcessingStore>();
                var source = await store.ReadReplaySourceAsync(raw.Manifest.Descriptor.Capture.CaptureId,
                    raw.Manifest.Descriptor.Artifact.ArtifactId, CancellationToken.None).ConfigureAwait(false);
                var target = await store.ReadRevisionAsync(revision, CancellationToken.None).ConfigureAwait(false);
                var config = source.Configuration with { Pipeline = target.Pipeline };
                var submission = new ProcessingReplaySubmission(raw.Manifest.Descriptor.Capture.CaptureId, revision,
                    raw.Manifest.Descriptor.Artifact.ArtifactId);
                var executionId = Guid.NewGuid();
                var configurationBytes = Encoding.UTF8.GetBytes(CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(config)).GetRawText());
                var requested = await store.InsertReplayAsync(submission, source, target, executionId, configurationBytes,
                    "satellite-replay", "test-owner", CancellationToken.None).ConfigureAwait(false);
                var duplicate = await store.InsertReplayAsync(submission, source, target, executionId, configurationBytes,
                    "satellite-replay", "test-owner", CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(duplicate.Replayed);
                Assert.AreEqual(requested.Execution.ExecutionId, duplicate.Execution.ExecutionId);
                var lease = await store.ClaimReplayAsync("satellite-replay", CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(lease);
                var execution = new ProcessingExecutionContext(lease.Execution.ExecutionId, ProcessingGraphExecutionClass.Replay,
                    lease.Execution.GraphRevisionId, lease.Execution.LocalPlanIdentitySha256, false, lease.WorkId,
                    lease.LeaseToken, lease.LeaseOwner, lease.Execution.DeadlineUtc, lease.ClaimCount);
                var graph = restarted.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateRetainedGraph(lease.Configuration);
                try
                {
                    var result = await FrameProcessingWorker.ProcessGraphItemAsync(new(lease.Configuration, lease.Submission,
                        lease.RawCapture, lease.WorkId, lease.LeaseToken, execution), graph,
                        restarted.GetRequiredService<CaptureProcessingPersistence>(), restarted.GetRequiredService<CaptureProcessingTelemetry>(),
                        1, NullLogger.Instance, CancellationToken.None, timeProvider: clock, durableAttempt: lease.ClaimCount).ConfigureAwait(false);
                    Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
                    Assert.AreEqual(ProcessingGraphExecutionStatus.Completed,
                        (await store.CompleteReplayAsync(lease, result, CancellationToken.None).ConfigureAwait(false)).Status);
                    var node = await store.ReadExecutionNodeAsync(execution.ExecutionId, raw.Manifest.Descriptor.Capture.CaptureId,
                        "satellite", CancellationToken.None).ConfigureAwait(false);
                    Assert.IsNotNull(node);
                    Assert.AreEqual(originalChecksum, node.Outputs.Single(p => p.ProductSchemaVersion == CaptureOrbitalPredictionJson.SchemaVersion).Artifact.ChecksumSha256);
                    var layerNode = await store.ReadExecutionNodeAsync(execution.ExecutionId, raw.Manifest.Descriptor.Capture.CaptureId,
                        "satellite-layer", TestContext.CancellationToken).ConfigureAwait(false);
                    Assert.IsNotNull(layerNode);
                    Assert.AreEqual(originalLayerChecksum, layerNode.Outputs.Single(p =>
                        p.ProductSchemaVersion == PresentationLayerPayloadV1.CurrentSchemaVersion).Artifact.ChecksumSha256);
                }
                finally { graph.DisposeSteps(); }
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private const string CsvFixtureManifest = "519862B7B1E16722A01210710C0D17B3E9D60B3CEE8D33D997AC4F330719FA72";

    private static async Task InstallCsvSnapshotAsync(ServiceProvider provider, CancellationToken token)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "orbital", "csv-omm-v1");
        var parsed = OrbitalSnapshot.Parse(await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json"), token).ConfigureAwait(false),
            await File.ReadAllBytesAsync(Path.Combine(directory, "records.json"), token).ConfigureAwait(false));
        Assert.IsNotNull(parsed.Snapshot, parsed.Reason);
        Assert.AreEqual(CsvFixtureManifest, parsed.Snapshot.Manifest.ManifestSha256);
        var store = provider.GetRequiredService<IOrbitalElementsStore>();
        await store.InitializeAsync(token).ConfigureAwait(false);
        var attempt = await store.TryBeginAttemptAsync("retained-csv-fixture", OrbitalAttemptTrigger.Manual, token).ConfigureAwait(false);
        Assert.AreEqual(OrbitalAdmissionStatus.Admitted, attempt.Status);
        await store.PublishAndActivateAsync(attempt.AttemptId!, parsed.Snapshot,
            await File.ReadAllBytesAsync(Path.Combine(directory, "source.csv"), token).ConfigureAwait(false), token).ConfigureAwait(false);
    }

    private static async Task<long> ReferenceCountAsync(string root, string source)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, ".orbital-elements", "orbital-elements.db")}");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM orbital_snapshot_references WHERE manifest_sha256 = $source;";
        command.Parameters.AddWithValue("$source", source);
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }
}
