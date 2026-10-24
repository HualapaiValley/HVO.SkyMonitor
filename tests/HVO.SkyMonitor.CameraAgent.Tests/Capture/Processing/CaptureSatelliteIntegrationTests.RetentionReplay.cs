using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class CaptureSatelliteIntegrationTests
{
    [TestMethod]
    public async Task ArchivedProductsKeepTheReferenceWhenCanonicalFilesDisappear()
    {
        var root = CreateRoot();
        try
        {
            var f = new CaptureSatelliteFixture();
            using var provider = CreateProvider(root, f, new Clock(f.Descriptor.Timing.DurableIngressUtc));
            var archive = Path.Combine(root, "archive");
            var config = Configuration(root, f);
            config = config with
            {
                Pipeline = config.Pipeline with
                {
                    Steps = config.Pipeline.Steps.Select(step => step.Id == "storage"
                ? step with
                {
                    Options = System.Text.Json.JsonSerializer.SerializeToElement(new FileStorageCaptureProcessingStepOptions
                    { StorageRoot = archive, QueueForUpload = false, UpdateLatestFrame = false })
                } : step).ToArray()
                }
            };
            var raw = await CaptureAsync(provider, config, f).ConfigureAwait(false);
            var handled = await ProcessAsync(provider, config).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, handled.Outcome, handled.Reason);
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var node = await store.ReadNodeAsync(raw.Manifest.Descriptor.Capture.CaptureId, "satellite", CancellationToken.None).ConfigureAwait(false);
            var input = CaptureSatelliteProducts.ParseInput(await File.ReadAllBytesAsync(Path.Combine(root,
                node!.Outputs.Single(p => p.ProductSchemaVersion == CaptureSatelliteProducts.InputSchema).PayloadRelativePath)).ConfigureAwait(false));
            // Durable-local publication owns its canonical copy. Exercise an already retained archive consumer
            // through the existing immutable metadata-copy primitive, rather than expecting Storage to duplicate it.
            var retained = node.Outputs.Single(p => p.ProductSchemaVersion == CaptureSatelliteProducts.InputSchema);
            var product = new ProcessingProduct(retained.Artifact.Role, retained.Artifact.Variant, retained.OutputIdentitySha256,
                retained.Artifact.MediaType, null, await File.ReadAllBytesAsync(Path.Combine(root, retained.PayloadRelativePath)).ConfigureAwait(false),
                retained.Artifact.ChecksumSha256, new(retained.Artifact.Recipe, retained.RecipeIdentitySha256), retained.Algorithms,
                retained.Artifact.SourceArtifactIds, retained.TotalIntegration, retained.Compatibility)
            { Kind = ProcessingProductKind.Metadata, SchemaVersion = retained.ProductSchemaVersion, ContentIdentitySha256 = retained.ContentIdentitySha256 };
            await CaptureProcessingPersistence.CopyMetadataProductAsync(archive, raw.Manifest.Descriptor, "satellite", product,
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotEmpty(Directory.EnumerateFiles(archive, "*.manifest.json", SearchOption.AllDirectories));
            // A canonical-root loss must not authorize release while the recorded archive still has consumers.
            foreach (var file in await store.ReadSatelliteConsumersAsync(raw.Manifest.Descriptor.Capture.CaptureId,
                "satellite", CancellationToken.None).ConfigureAwait(false))
            {
                File.Delete(Path.Combine(root, file.PayloadRelativePath));
                File.Delete(Path.Combine(root, file.SidecarRelativePath));
            }
            File.Delete(raw.StoredFrame.AbsolutePath);
            File.Delete(Path.ChangeExtension(raw.StoredFrame.AbsolutePath, ".json"));
            var work = provider.GetRequiredService<CaptureSatelliteWorkStore>();
            await work.ReconcileAsync(root, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1L, await ReferenceCountAsync(root, input.InputManifest()).ConfigureAwait(false));
            Directory.Delete(archive, true);
            await Assert.ThrowsExactlyAsync<IOException>(() => work.ReconcileAsync(root, CancellationToken.None).AsTask()).ConfigureAwait(false);
            Assert.AreEqual(1L, await ReferenceCountAsync(root, input.InputManifest()).ConfigureAwait(false),
                "A missing archive root may be disconnected storage, so release remains deferred.");
            Directory.CreateDirectory(archive); // A known empty root permits the existing bounded archive scan.
            await work.ReconcileAsync(root, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(0L, await ReferenceCountAsync(root, input.InputManifest()).ConfigureAwait(false));
        }
        finally { Directory.Delete(root, true); }
    }

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
            var input = CaptureSatelliteProducts.ParseInput(await File.ReadAllBytesAsync(Path.Combine(root,
                node!.Outputs.Single(p => p.ProductSchemaVersion == CaptureSatelliteProducts.InputSchema).PayloadRelativePath)).ConfigureAwait(false));
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
    public async Task RetentionProtectsRawAndSupportUntilConsumersExpireThenReleasesOrbitalReference()
    {
        var root = CreateRoot();
        try
        {
            var f = new CaptureSatelliteFixture();
            var clock = new Clock(f.Descriptor.Timing.DurableIngressUtc);
            using var provider = CreateProvider(root, f, clock);
            var config = Configuration(root, f);
            var raw = await CaptureAsync(provider, config, f).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, (await ProcessAsync(provider, config).ConfigureAwait(false)).Outcome);
            var work = provider.GetRequiredService<CaptureSatelliteWorkStore>();
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var node = await store.ReadNodeAsync(raw.Manifest.Descriptor.Capture.CaptureId, "satellite", CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(node);
            var inputs = node.Outputs.Single(static p => p.ProductSchemaVersion == CaptureSatelliteProducts.InputSchema);
            var evidence = CaptureSatelliteProducts.ParseInput(await File.ReadAllBytesAsync(Path.Combine(root, inputs.PayloadRelativePath)).ConfigureAwait(false));
            var source = evidence.InputManifest();
            var sceneOutput = await RetainCanonicalSceneAsync(root, provider, raw, f).ConfigureAwait(false);
            var holds = await work.GetRetentionHoldsAsync(root, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(holds.Any(h => h.ArtifactId == raw.Manifest.Descriptor.Artifact.ArtifactId));
            Assert.IsTrue(node.Outputs.All(output => holds.Any(hold => hold.ArtifactId == output.ArtifactId)),
                "The retained manifest holds every reconstruction input and typed product.");
            var persistence = provider.GetRequiredService<CaptureProcessingPersistence>();
            await persistence.ExpireOutputsAsync(root, DateTimeOffset.UtcNow.AddYears(1), Absolute(holds), CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(node.Outputs.All(p => File.Exists(Path.Combine(root, p.PayloadRelativePath))));
            Assert.IsTrue(File.Exists(Path.Combine(root, sceneOutput.PayloadRelativePath)), "The existing scene guard retains support while raw exists.");
            holds = await work.GetRetentionHoldsAsync(root, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, holds, "Independent scene support must not form a scene/raw/satellite retention cycle.");
            await persistence.ExpireOutputsAsync(root, DateTimeOffset.UtcNow.AddYears(1), Absolute(holds), CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(node.Outputs.All(p => !File.Exists(Path.Combine(root, p.PayloadRelativePath))));
            await work.ReconcileAsync(root, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1L, await ReferenceCountAsync(root, source).ConfigureAwait(false), "The raw still needs its frozen prediction inputs.");
            File.Delete(raw.StoredFrame.AbsolutePath);
            File.Delete(Path.ChangeExtension(raw.StoredFrame.AbsolutePath, ".json"));
            await persistence.ExpireOutputsAsync(root, DateTimeOffset.UtcNow.AddYears(1), new HashSet<string>(), CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(File.Exists(Path.Combine(root, sceneOutput.PayloadRelativePath)), "Scene support can expire after raw and its other consumers.");
            await work.ReconcileAsync(root, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(0L, await ReferenceCountAsync(root, source).ConfigureAwait(false));
            await work.ReconcileAsync(root, CancellationToken.None).ConfigureAwait(false);
            Assert.IsEmpty(Directory.EnumerateDirectories(Path.Combine(root, "journal", "capture-satellites")));

            HashSet<string> Absolute(IReadOnlyList<ProcessingRetentionHold> values) => values.SelectMany(h => new[]
                { Path.Combine(root, h.PayloadRelativePath), Path.Combine(root, h.SidecarRelativePath) }).ToHashSet(StringComparer.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ArchivedReplayPinsOriginalProductsAndIgnoresChangedCurrentSiteAndSources()
    {
        var root = CreateRoot();
        try
        {
            var f = new CaptureSatelliteFixture();
            var clock = new Clock(f.Descriptor.Timing.DurableIngressUtc);
            HVO.SkyMonitor.CameraAgent.Common.RawIngress.RawCaptureReceipt raw;
            string originalChecksum;
            string revision;
            using (var provider = CreateProvider(root, f, clock))
            {
                var config = Configuration(root, f);
                raw = await CaptureAsync(provider, config, f).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, (await ProcessAsync(provider, config).ConfigureAwait(false)).Outcome);
                var original = await provider.GetRequiredService<SqliteCaptureProcessingStore>().ReadNodeAsync(
                    raw.Manifest.Descriptor.Capture.CaptureId, "satellite", CancellationToken.None).ConfigureAwait(false);
                originalChecksum = original!.Outputs.Single(p => p.ProductSchemaVersion == CaptureOrbitalPredictionJson.SchemaVersion).Artifact.ChecksumSha256;
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
                }
                finally { graph.DisposeSteps(); }
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<DurableProcessingOutput> RetainCanonicalSceneAsync(string root, ServiceProvider provider,
        HVO.SkyMonitor.CameraAgent.Common.RawIngress.RawCaptureReceipt raw, CaptureSatelliteFixture f)
    {
        // Seed the existing compact-scene retained state after ordinary capture processing. The stage may already
        // have expired; this is a real canonical scene product and a valid compact raw manifest, not a renderer stub.
        var descriptor = raw.Manifest.Descriptor;
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([])).BuildAsync(new VisibleSceneRequest(
            descriptor.Timing.ExposureStartedUtc, f.Input.Plan.Observer, RigProjectionContextFactory.Create(f.Configuration.Rig),
            new CatalogQuery(6.5, 10), new CatalogMetadata("satellite-retention", "1", new Uri("https://example.invalid"),
                new string('A', 64), "fixture", "1"), projectionVersion: "fixture-scene-v1")).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(descriptor.Layout.Width, descriptor.Layout.Height),
            new ProjectedSceneSource(descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId,
                CaptureContractJson.ComputeDescriptorSha256(descriptor)), "configured-rig", "fixture-scene-v1");
        var payload = ProjectedSceneJson.Serialize(scene);
        var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(BuiltInProcessingRecipes.ProjectedScene,
            "1.0.0", "retention-fixture-v1", JsonSerializer.SerializeToElement(new { })));
        var sources = new[] { descriptor.Artifact.ArtifactId };
        var product = new ProcessingProduct(FrameArtifactRole.Metadata, "projected-scene-v1",
            ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Metadata, "projected-scene-v1", recipe.IdentitySha256, sources),
            "application/json", null, payload, ProcessingIdentity.ComputePayloadSha256(payload), recipe,
            [new("retained-scene-fixture", "v1")], sources, TimeSpan.Zero, CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor))
        { Kind = ProcessingProductKind.Metadata, SchemaVersion = scene.SchemaVersion, ContentIdentitySha256 = scene.SceneIdentitySha256 };
        var output = await CaptureProcessingPersistence.CopyMetadataProductAsync(root, descriptor, "retained-scene", product,
            CancellationToken.None).ConfigureAwait(false);
        var node = new CaptureProcessingGraphNode("retained-scene", new RetainedSceneStep(product), ["$raw"], true,
            BuiltInProcessingRecipes.ProjectedScene, FrameArtifactRole.Metadata, product.Variant, new string('F', 64));
        await provider.GetRequiredService<SqliteCaptureProcessingStore>().WriteNodeAsync(descriptor.Capture.CaptureId, node,
            DurableProcessingNodeStatus.Completed, null, 1, null, null, descriptor.Timing.DurableIngressUtc, TimeSpan.Zero,
            ProcessingOutcomeStatus.Produced, [], 0, null, [output], CancellationToken.None).ConfigureAwait(false);
        var manifest = raw.Manifest with
        {
            Scene = new(scene.SceneIdentitySha256, "fixture-rig", "satellite-retention", "1",
            new string('A', 64), "configured-rig", "fixture-scene-v1", "fixture-v1", "fixture-v1",
            RigProfileHashSha256: descriptor.Profiles.Rig.Sha256, SceneUtc: descriptor.Timing.ExposureStartedUtc,
            ProjectedSceneStageSchemaVersion: "projected-scene-stage-v1", ProjectedSceneStageKey: new string('B', 64),
            ProjectedSceneSchemaVersion: scene.SchemaVersion)
        };
        Assert.IsTrue(manifest.Validate().IsValid);
        var bytes = CaptureContractJson.Serialize(manifest);
        await File.WriteAllBytesAsync(Path.ChangeExtension(raw.StoredFrame.AbsolutePath, ".json"), bytes).ConfigureAwait(false);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE raw_captures SET manifest_json = $body, manifest_sha256 = $hash WHERE capture_id = $capture;";
        command.Parameters.AddWithValue("$body", bytes);
        command.Parameters.AddWithValue("$hash", CaptureContractJson.ComputeManifestSha256(manifest));
        command.Parameters.AddWithValue("$capture", descriptor.Capture.CaptureId.ToString("N"));
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
        return output;
    }

    private sealed class RetainedSceneStep(ProcessingProduct product) : ICaptureProcessingStep
    {
        public string Name => "retained-scene";
        public int Order => 0;
        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            context.RegisterProcessingProduct(product);
            return ValueTask.CompletedTask;
        }
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
