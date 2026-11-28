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
    public async Task RetainedAstrometry_LargeRawJournalWithoutAssessmentsReusesEmptyCandidates()
    {
        const int rawCount = 100_000;
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            using var store = SiloInfrastructureFixture.OpenProcessingStore(fixture.Options);
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
            await connection.OpenAsync().ConfigureAwait(false);
            await SeedRawAstrometryHistoryAsync(connection, rawCount, exposureUnixMilliseconds: 1).ConfigureAwait(false);
            using var command = connection.CreateCommand();

            var cold = await store.ReadAstrometryCandidatesAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsEmpty(cold);
            // A broken cold query would fail while the warm lookup should use the existing snapshot without SQL.
            command.CommandText = "DROP INDEX ix_processing_outputs_astrometry_lookup;";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            Assert.AreSame(cold, await store.ReadAstrometryCandidatesAsync(CancellationToken.None).ConfigureAwait(false));
            using var reader = CreateUnpooledAstrometryReader(store,
                new Moq.Mock<HVO.SkyMonitor.CameraAgent.Common.Gallery.ICameraAgentArtifactService>(Moq.MockBehavior.Strict).Object);
            var result = await reader.ReadLatestAcceptedAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(result.Completed);
            Assert.IsNull(result.Summary);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task SeedRawAstrometryHistoryAsync(Microsoft.Data.Sqlite.SqliteConnection connection,
        int rawCount, long exposureUnixMilliseconds)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE numbers(n) AS (
                SELECT 1 UNION ALL SELECT n + 1 FROM numbers WHERE n < $count
            )
            INSERT INTO raw_capture_assignments(capture_id,raw_artifact_id,agent_id,capture_sequence)
            SELECT 'fa00' || printf('%028x',n), 'fb00' || printf('%028x',n), 'history-fixture', n FROM numbers;
            INSERT INTO raw_captures(capture_id,raw_artifact_id,agent_id,capture_sequence,
                descriptor_sha256,manifest_sha256,payload_sha256,payload_length,payload_relative_path,
                sidecar_relative_path,manifest_json,exposure_started_unix_ms,durable_ingress_unix_ms,
                committed_unix_ms,state,retention_hold)
            SELECT capture_id,raw_artifact_id,agent_id,capture_sequence,printf('%064x',capture_sequence),
                printf('%064x',capture_sequence),printf('%064x',1),0,'raw-' || capture_sequence,
                'sidecar-' || capture_sequence,X'7B7D',$time,$time,$time,'committed',0
            FROM raw_capture_assignments WHERE agent_id = 'history-fixture';
            """;
        command.Parameters.AddWithValue("$count", rawCount);
        command.Parameters.AddWithValue("$time", exposureUnixMilliseconds);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RetainedAstrometry_HistoryLookupStartsFromOutputsAndSeeksRawByCaptureId()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            using var store = SiloInfrastructureFixture.OpenProcessingStore(fixture.Options);
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            await SeedRawAstrometryHistoryAsync(connection, 8193, exposureUnixMilliseconds: 1).ConfigureAwait(false);
            command.CommandText = """
                INSERT INTO processing_outputs(output_identity_sha256,capture_id,agent_id,node_id,artifact_id,role,variant,
                    payload_relative_path,sidecar_relative_path,descriptor_json,recipe_identity_sha256,algorithms_json,
                    compatibility_json,total_integration_ticks,capture_sequence,committed_unix_ms,product_kind,
                    product_schema_version,content_identity_sha256)
                SELECT printf('%064x',raw_capture_row_id),capture_id,agent_id,'fixture',
                    'fc00' || printf('%028x',raw_capture_row_id),'Metadata','fixture','unused','unused',
                    X'7B7D',descriptor_sha256,X'5B5D',X'7B7D',0,capture_sequence,committed_unix_ms,'Metadata',
                    CASE WHEN capture_sequence = 1 THEN 'measured-astrometry-solution-v1' ELSE 'cloud-assessment-v1' END,
                    descriptor_sha256
                FROM raw_captures WHERE agent_id = 'history-fixture';
                """;
            Assert.AreEqual(8193, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
            command.CommandText = "EXPLAIN QUERY PLAN " + SqliteCaptureProcessingStore.LatestAstrometryQuery;
            var details = new List<string>();
            using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
                while (await reader.ReadAsync().ConfigureAwait(false)) details.Add(reader.GetString(3));
            var plan = string.Join("\n", details);
            StringAssert.Contains(plan, "USING COVERING INDEX ix_processing_outputs_astrometry_lookup (role=? AND product_schema_version=?)", StringComparison.Ordinal);
            StringAssert.Contains(plan, "SEARCH raw USING INDEX sqlite_autoindex_raw_captures_1 (capture_id=?)", StringComparison.Ordinal);
            Assert.IsFalse(plan.Contains("SCAN raw", StringComparison.Ordinal), plan);
            Assert.IsFalse(plan.Contains("TEMP B-TREE", StringComparison.Ordinal), plan);
            Assert.HasCount(1, await store.ReadAstrometryCandidatesAsync(CancellationToken.None).ConfigureAwait(false));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("accepted")]
    [DataRow("changed")]
    [DataRow("incomplete")]
    [DataRow("missing")]
    [DataRow("canceled")]
    public async Task RetainedAstrometry_PinnedInstalledSelectionFailsClosed(string scenario)
    {
        var original = EmptyAstrometryCatalog();
        var data = new AstrometricCatalogData(original.Metadata, original.Stars, true, 7, provenance: original.Package);
        var pin = new MeasuredAstrometryCatalogSelection(data.IdentitySha256, data.SelectionIdentitySha256, 7);
        var source = new Moq.Mock<IAstrometricCatalogSource>();
        source.Setup(s => s.ReadAsync(7, 2500, Moq.It.IsAny<CancellationToken>()))
            .Returns((double _, int _, CancellationToken token) =>
            {
                token.ThrowIfCancellationRequested();
                return ValueTask.FromResult(scenario == "incomplete"
                    ? new AstrometricCatalogData(original.Metadata, original.Stars, false, 7, provenance: original.Package)
                    : data);
            });
        if (scenario == "changed") pin = pin with { SelectionIdentitySha256 = new string('B', 64) };
        if (scenario == "canceled")
        {
            using var canceled = new CancellationTokenSource();
            await canceled.CancelAsync().ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await pin.ResolveAsync(source.Object, canceled.Token).ConfigureAwait(false)).ConfigureAwait(false);
        }
        else if (scenario != "accepted")
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await pin.ResolveAsync(scenario == "missing" ? null : source.Object, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        else
        {
            var frozen = await pin.ResolveAsync(source.Object, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(original.Metadata, frozen.Metadata);
            Assert.AreEqual(original.Package, frozen.Package);
            Assert.IsTrue(frozen.Complete);
            Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(pin).Length < 1024);
        }
    }

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
    [DataRow("no-canonical")]
    [DataRow("two-canonical")]
    [DataRow("required")]
    [DataRow("memory-only")]
    [DataRow("storage-consumer")]
    [DataRow("indirect-storage-consumer")]
    public void AstrometryPrediction_IsOptionalAndPreservesTheCanonicalScenePlan(string mode)
    {
        var root = CreateTestRoot();
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSiloTestInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = root
            }).Build());
            using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
            var baseline = CreateConfig() with
            {
                Module = new("VirtualSky"),
                Pipeline = new([
                    new("ProjectedScene", "scene", DependsOn: ["$raw"], Publication: new(CaptureProcessingPersistenceMode.DurableLocal),
                        Options: JsonSerializer.SerializeToElement(new { constellationIds = (string[])["CAS"], solarSystemBodies = (string[])["Moon"] })),
                    new("Storage", "storage", DependsOn: ["$raw", "scene"], Options: JsonSerializer.SerializeToElement(
                        new SiloCaptureProcessingStepOptions { UpdateLatestFrame = false }))
                ], CapturePipelineSchemaVersions.ExplicitV2)
            };
            var prediction = new CaptureProcessingStepConfig(AstrometryPredictionCaptureProcessingStep.Alias, "prediction",
                DependsOn: ["$raw"], Required: mode == "required",
                Publication: new(mode == "memory-only" ? CaptureProcessingPersistenceMode.MemoryOnly : CaptureProcessingPersistenceMode.DurableLocal));
            var steps = baseline.Pipeline.Steps.ToList();
            if (mode == "no-canonical")
            {
                steps.RemoveAt(0);
                steps[0] = steps[0] with { DependsOn = ["$raw"] };
            }
            if (mode == "two-canonical")
            {
                steps.Add(steps[0] with
                {
                    Id = "second-scene",
                    Options = JsonSerializer.SerializeToElement(new { outputVariant = "other-scene" })
                });
            }
            if (mode == "storage-consumer") steps[1] = steps[1] with { DependsOn = ["$raw", "scene", "prediction"] };
            steps.Add(prediction);
            if (mode == "indirect-storage-consumer")
            {
                steps.Add(new("MeasuredStellarAssociations", "measured", DependsOn: ["prediction"], Required: false,
                    Publication: prediction.Publication, Options: JsonSerializer.SerializeToElement(new { useAstrometryPrediction = true })));
                steps[1] = steps[1] with { DependsOn = ["$raw", "scene", "measured"] };
            }
            var profile = baseline with { Pipeline = baseline.Pipeline with { Steps = steps } };
            if (mode != "valid")
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateGraph(profile));
                return;
            }
            var registration = provider.GetServices<CaptureProcessingStepRegistration>()
                .Single(r => r.Alias == AstrometryPredictionCaptureProcessingStep.Alias);
            Assert.IsFalse(registration.AutoInclude);
            var before = factory.CreateGraph(baseline);
            var after = factory.CreateGraph(profile);
            try
            {
                Assert.HasCount(2, before.Nodes);
                Assert.HasCount(3, after.Nodes);
                foreach (var node in before.Nodes)
                    Assert.AreEqual(node.PlanSha256, after.Nodes.Single(n => n.Id == node.Id).PlanSha256, node.Id);
                Assert.AreEqual(BuiltInProcessingRecipes.ProjectedScene, after.Nodes.Single(n => n.Id == "scene").RecipeName);
                Assert.AreEqual(BuiltInProcessingRecipes.AstrometryPrediction, after.Nodes.Single(n => n.Id == "prediction").RecipeName);
            }
            finally
            {
                before.DisposeSteps();
                after.DisposeSteps();
            }
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
            services.AddSiloTestInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
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
                    new SiloCaptureProcessingStepOptions { UpdateLatestFrame = false }))
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
