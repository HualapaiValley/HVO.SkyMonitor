using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class SqliteCaptureProcessingSchemaTests
{
    private static readonly bool[] ExpectedCenteredChangedLive = [false, true, false, true, true];

    [TestMethod]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The checkpoint schema is a fixed store definition.")]
    public async Task CenteredRuntimeMigratesTheEmptyWindowCheckpointRetiresTheOldRunAndResumesAStoredCursor()
    {
        using var fixture = await SchemaFixture.CreateAsync().ConfigureAwait(false);
        var token = TestContext.CancellationToken;
        var scope = ViewScope();
        var profile = StoragePolicyRawIngressIntegrationTests.Profile(fixture.Options.Value.RawIngressRoot) with
        {
            AgentId = scope.Camera.AgentId,
            CenteredExposure = new(scope.Camera.CameraId, CenteredExposureProfile.CurrentExecutionVersion),
            DeploymentLocation = DeploymentLocationSnapshot.Create("migration-test", 1, "virtual", null,
                DateTimeOffset.UnixEpoch, null, 35, -113, 500, "UTC")
        };
        var oldRun = new CenteredRunSnapshot(scope.Camera, scope.RunId, scope.GenerationId, profile,
            profile.DeploymentLocation!.ToProvenance(), new(null, null, null, null, "migration-test"), null, null, null);
        var ingress = new SqliteRawCaptureJournal(fixture.DatabasePath, 1);
        var pending = new ExposureAttemptKey(scope.Camera, scope.RunId, 4, Guid.NewGuid());
        await ingress.StartCenteredRunAsync(oldRun, pending, token).ConfigureAwait(false);
        using var originalHistory = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        using var originalStore = new SqliteCaptureProcessingStore(originalHistory.Options);
        var originalIndex = new SqliteCenteredViews(originalStore, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        var definition = ViewDefinition(scope, 3);
        var initialView = await originalIndex.RegisterBaseAsync(definition, [], token).ConfigureAwait(false);
        var revised = await originalIndex.PublishLayerAsync(initialView.Base.BaseId, ExampleLayer(definition, "markers", 1), token)
            .ConfigureAwait(false);
        Assert.IsNotNull(revised);
        var issued = await originalIndex.SelectLiveAsync(scope, initialView.Base.BaseId, token).ConfigureAwait(false);
        Assert.IsNotNull(issued);
        const string selectionOwner = "schema-11-observer";
        var selection = await originalIndex.SaveAsync(selectionOwner, Guid.NewGuid(), revised.Base.BaseId,
            revised.Revision.RevisionId, [Assert.ContainsSingle(revised.Revision.Manifest.Layers).LayerIdentitySha256], token)
            .ConfigureAwait(false);
        Assert.IsNotNull(selection);
        using var originalConnection = await originalHistory.OpenAsync().ConfigureAwait(false);
        var expectedHistory = await ReadCenteredHistoryBytesAsync(originalConnection, token).ConfigureAwait(false);
        Assert.HasCount(2, expectedHistory);
        using (var connection = await fixture.OpenAsync().ConfigureAwait(false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = SqliteCaptureProcessingStore.LegacySchema11SqlForTests + """
                INSERT INTO centered_exposure_attempts(attempt_id, run_id, ordinal, capture_id, status)
                VALUES ($attempt1, $run, 1, $capture1, 'observed'),
                       ($attempt2, $run, 2, $capture2, 'observed'),
                       ($attempt3, $run, 3, $capture3, 'observed');
                """;
            command.Parameters.AddWithValue("$run", scope.RunId.ToString("N"));
            for (var ordinal = 1; ordinal <= 3; ordinal++)
            {
                var suffix = ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
                command.Parameters.AddWithValue("$attempt" + suffix, Guid.NewGuid().ToString("N"));
                command.Parameters.AddWithValue("$capture" + suffix, Guid.NewGuid().ToString("N"));
            }
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            // Preserve the real committed history in the identical tables of the canonical older schema.
            command.Parameters.Clear();
            command.CommandText = """
                ATTACH DATABASE $original AS retained;
                INSERT INTO processing_centered_view_bases SELECT * FROM retained.processing_centered_view_bases;
                INSERT INTO processing_centered_view_changes SELECT * FROM retained.processing_centered_view_changes;
                INSERT INTO processing_centered_view_revisions SELECT * FROM retained.processing_centered_view_revisions;
                INSERT INTO processing_centered_view_members SELECT * FROM retained.processing_centered_view_members;
                INSERT INTO processing_centered_view_intervals SELECT * FROM retained.processing_centered_view_intervals;
                INSERT INTO processing_centered_view_selections SELECT * FROM retained.processing_centered_view_selections;
                DETACH DATABASE retained;
                """;
            command.Parameters.AddWithValue("$original", originalHistory.DatabasePath);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        using (var interrupted = new SqliteCaptureProcessingStore(fixture.Options,
            faultInjector: new ThrowOnceFaultInjector(CaptureProcessingFaultPoint.BeforeSchemaCommit)))
        {
            await Assert.ThrowsExactlyAsync<InjectedProcessingSchemaException>(async () =>
                await interrupted.InitializeAsync(token).ConfigureAwait(false)).ConfigureAwait(false);
        }
        using (var rollback = await fixture.OpenAsync().ConfigureAwait(false))
        {
            Assert.AreEqual(11L, await ScalarAsync(rollback, "SELECT version FROM capture_processing_schema;").ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarAsync(rollback, "SELECT COUNT(*) FROM centered_exposure_runs WHERE closed_reason IS NULL;")
                .ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarAsync(rollback, "SELECT COUNT(*) FROM centered_exposure_attempts WHERE status = 'pending';")
                .ConfigureAwait(false));
            CollectionAssert.AreEqual(expectedHistory, await ReadCenteredHistoryBytesAsync(rollback, token).ConfigureAwait(false));
        }
        var camera = scope.Camera;
        using (var store = new SqliteCaptureProcessingStore(fixture.Options))
        {
            var views = new SqliteCenteredViews(store, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
            await views.CheckpointProductAsync(camera, 41, TimeSpan.Zero, advanceCursor: true, token).ConfigureAwait(false);
            await AssertRestoredHistoryAsync(views).ConfigureAwait(false);
        }
        await ingress.InitializeAsync(token).ConfigureAwait(false);
        using var reopened = new SqliteCaptureProcessingStore(fixture.Options);
        var readback = new SqliteCenteredViews(reopened, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        await AssertRestoredHistoryAsync(readback).ConfigureAwait(false);
        Assert.AreEqual(41L, await readback.ReadProductCursorAsync(camera, token).ConfigureAwait(false));
        Assert.AreEqual(41L, (await readback.ReadDueProductRetriesAsync(camera, 10, token).ConfigureAwait(false)).Single());
        await readback.CheckpointProductAsync(camera, 41, null, advanceCursor: false, token).ConfigureAwait(false);
        Assert.IsEmpty(await readback.ReadDueProductRetriesAsync(camera, 10, token).ConfigureAwait(false));
        using var verify = await fixture.OpenAsync().ConfigureAwait(false);
        Assert.AreEqual(12L, await ScalarAsync(verify, "SELECT version FROM capture_processing_schema;").ConfigureAwait(false));
        CollectionAssert.AreEqual(expectedHistory, await ReadCenteredHistoryBytesAsync(verify, token).ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarAsync(verify, """
            SELECT COUNT(*) FROM centered_exposure_runs WHERE closed_reason = 'schema-11.missing-slot-calendar';
            """).ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarAsync(verify, """
            SELECT COUNT(*) FROM centered_exposure_attempts
            WHERE ordinal = 4 AND status = 'expired' AND reason = 'schema-11.missing-slot-calendar';
            """).ConfigureAwait(false));
        Assert.AreEqual(3L, await ScalarAsync(verify, """
            SELECT COUNT(*) FROM centered_exposure_attempts WHERE status = 'observed' AND capture_id IS NOT NULL;
            """).ConfigureAwait(false));
        Assert.IsNull(await ingress.ReadCenteredPendingAsync(camera, token).ConfigureAwait(false));
        var newRun = oldRun with { RunId = Guid.NewGuid() };
        var initial = new ExposureAttemptKey(camera, newRun.RunId, 1, Guid.NewGuid());
        await ingress.StartCenteredRunAsync(newRun, initial, token).ConfigureAwait(false);
        Assert.AreEqual(initial, (await ingress.ReadCenteredPendingAsync(camera, token).ConfigureAwait(false))?.Key);

        async Task AssertRestoredHistoryAsync(SqliteCenteredViews views)
        {
            var archive = Assert.ContainsSingle(await views.ReadArchiveAsync(definition.Reference.Capture.CaptureId, token)
                .ConfigureAwait(false));
            Assert.AreEqual(initialView.Base.BaseId, archive.Base.BaseId);
            Assert.AreEqual(initialView.Revision.RevisionId, archive.InitialRevisionId);
            Assert.AreEqual(issued.View.Revision.RevisionId, archive.DefaultRevisionId);
            Assert.AreEqual(issued.Interval, (await views.ReadLastLiveAsync(camera, token).ConfigureAwait(false))?.Interval);
            var saved = await views.ReadSavedAsync(selectionOwner, selection.SelectionId, token).ConfigureAwait(false);
            Assert.IsNotNull(saved);
            Assert.AreEqual(selection.BaseId, saved.BaseId);
            Assert.AreEqual(selection.RevisionId, saved.RevisionId);
            CollectionAssert.AreEqual(selection.EnabledLayerIds.ToArray(), saved.EnabledLayerIds.ToArray());
        }
    }

    private static async Task<string[]> ReadCenteredHistoryBytesAsync(SqliteConnection connection, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT json_array(base.base_id, revision.revision_id, hex(base.definition), hex(revision.manifest),
                hex(revision.layers), interval.started_order, interval.started_utc, interval.ended_order,
                interval.ended_utc, saved.selection_id, hex(saved.enabled_layers))
            FROM processing_centered_view_bases base
            JOIN processing_centered_view_revisions revision ON revision.base_id = base.base_id
            LEFT JOIN processing_centered_view_intervals interval
                ON interval.base_id = base.base_id AND interval.revision_id = revision.revision_id
            LEFT JOIN processing_centered_view_selections saved
                ON saved.base_id = base.base_id AND saved.revision_id = revision.revision_id
            ORDER BY revision.change_order, interval.started_order, saved.selection_id;
            """;
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var rows = new List<string>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            rows.Add(reader.GetString(0));
        return rows.ToArray();
    }

    [TestMethod]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The checkpoint schema is a fixed store definition.")]
    public async Task CenteredRuntimePreservesPopulatedPreRuntimeWindowCheckpointForExplicitDisposition()
    {
        using var fixture = await SchemaFixture.CreateAsync().ConfigureAwait(false);
        var token = TestContext.CancellationToken;
        using (var connection = await fixture.OpenAsync().ConfigureAwait(false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = SqliteCaptureProcessingStore.LegacySchema11SqlForTests + """
                INSERT INTO processing_centered_windows(window_sha256, deadline_utc_ticks, checkpoint_json)
                VALUES ($window, 1, X'7B7D');
                """;
            command.Parameters.AddWithValue("$window", new string('A', 64));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        using var store = new SqliteCaptureProcessingStore(fixture.Options);
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => store.InitializeAsync(token).AsTask()).ConfigureAwait(false);
        StringAssert.Contains(failure.Message, "explicit state disposition", StringComparison.Ordinal);
        using var verify = await fixture.OpenAsync().ConfigureAwait(false);
        Assert.AreEqual(11L, await ScalarAsync(verify, "SELECT version FROM capture_processing_schema;").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarAsync(verify,
            "SELECT COUNT(*) FROM processing_centered_windows WHERE checkpoint_json = X'7B7D';").ConfigureAwait(false));
        Assert.AreEqual(0L, await ScalarAsync(verify,
            "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'processing_centered_view_window_work';").ConfigureAwait(false));
    }

    [TestMethod]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The legacy test schema is a fixed store definition.")]
    public async Task CenteredViewSchemaUpgradesFromTenAndRetainsTheLastLiveRevisionAfterRestart()
    {
        using var fixture = await SchemaFixture.CreateAsync().ConfigureAwait(false);
        var token = TestContext.CancellationToken;
        using (var connection = await fixture.OpenAsync().ConfigureAwait(false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = SqliteCaptureProcessingStore.LegacySchema10SqlForTests;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await WritePinProbeAsync(connection, null, Guid.Empty, "schema-ten-evidence", token).ConfigureAwait(false);
        }

        var scope = ViewScope();
        var single = ViewDefinition(scope, 103);
        var next = ViewDefinition(scope, 104);
        CenteredView first;
        CenteredView enriched;
        var savedId = Guid.NewGuid();
        using (var processing = new SqliteCaptureProcessingStore(fixture.Options))
        {
            var views = new SqliteCenteredViews(processing, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
            first = await views.RegisterBaseAsync(single, [], token).ConfigureAwait(false);
            var selected = await views.SelectLiveAsync(scope, first.Base.BaseId, token).ConfigureAwait(false);
            Assert.AreEqual(first.Revision.RevisionId, selected?.View.Revision.RevisionId);

            var replacement = await views.RegisterBaseAsync(next, [], token).ConfigureAwait(false);
            await views.SelectLiveAsync(scope, replacement.Base.BaseId, token).ConfigureAwait(false);
            enriched = (await views.PublishLayerAsync(first.Base.BaseId,
                ExampleLayer(first.Base.Definition, "markers", 1), token).ConfigureAwait(false))!;
            Assert.AreNotEqual(first.Revision.RevisionId, enriched.Revision.RevisionId);
            Assert.AreEqual(replacement.Base.BaseId, (await views.SelectLiveAsync(scope, first.Base.BaseId, token)
                .ConfigureAwait(false))?.View.Base.BaseId);
            var saved = await views.SaveAsync("operator-a", savedId, first.Base.BaseId, enriched.Revision.RevisionId,
                [enriched.Revision.Manifest.Layers.Single().LayerIdentitySha256], token).ConfigureAwait(false);
            Assert.IsNotNull(saved);
        }

        var ingress = new SqliteRawCaptureJournal(fixture.DatabasePath, 1);
        await ingress.InitializeAsync(token).ConfigureAwait(false);
        using var restarted = new SqliteCaptureProcessingStore(fixture.Options);
        var reopened = new SqliteCenteredViews(restarted, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        Assert.AreEqual(next.Reference.Capture.CaptureId, (await reopened.ReadCurrentAsync(scope, token)
            .ConfigureAwait(false))?.View.Base.Definition.Reference.Capture.CaptureId);
        var archive = (await reopened.ReadArchiveAsync(scope, single.Reference.Capture.CaptureId, token).ConfigureAwait(false)).Single();
        Assert.AreEqual(first.Revision.RevisionId, archive.InitialRevisionId);
        Assert.AreEqual(first.Revision.RevisionId, archive.LastLiveRevisionId);
        Assert.AreEqual(enriched.Revision.RevisionId, archive.LatestRevisionId);
        Assert.IsEmpty((await reopened.ReadRevisionAsync(archive.Base.BaseId, archive.InitialRevisionId, token)
            .ConfigureAwait(false))!.Revision.Manifest.Layers);
        var interval = (await reopened.ReadLiveHistoryAsync(first.Base.BaseId, 0, 10, token).ConfigureAwait(false)).Single();
        Assert.IsNotNull(interval.EndedOrder);
        Assert.IsLessThan(enriched.Revision.Order, interval.EndedOrder.GetValueOrDefault());
        Assert.IsNull(await reopened.ReadSavedAsync("operator-b", savedId, token).ConfigureAwait(false));
        var reopenedSaved = await reopened.ReadSavedAsync("operator-a", savedId, token).ConfigureAwait(false);
        Assert.AreEqual(enriched.Revision.RevisionId, reopenedSaved?.RevisionId);
        Assert.AreEqual(enriched.Revision.Manifest.Layers.Single().LayerIdentitySha256, reopenedSaved?.EnabledLayerIds.Single());

        using var verify = await fixture.OpenAsync().ConfigureAwait(false);
        Assert.AreEqual((long)SqliteCaptureProcessingStore.CurrentSchemaVersion,
            await ScalarAsync(verify, "SELECT version FROM capture_processing_schema;").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarAsync(verify, "SELECT COUNT(*) FROM processing_nodes WHERE node_id = 'schema-ten-evidence';").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CenteredViewSerializesConcurrentWritersAndRejectsStaleAndIncompatibleEvidence()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        var token = TestContext.CancellationToken;
        using var processing = new SqliteCaptureProcessingStore(fixture.Options);
        using var secondProcessing = new SqliteCaptureProcessingStore(fixture.Options);
        var first = new SqliteCenteredViews(processing, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        var second = new SqliteCenteredViews(secondProcessing, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        var scope = ViewScope();
        var definition = ViewDefinition(scope, 103);
        var starts = await Task.WhenAll(Task.Run(() => first.RegisterBaseAsync(definition, [], token).AsTask(), token),
            Task.Run(() => second.RegisterBaseAsync(definition, [], token).AsTask(), token)).ConfigureAwait(false);
        Assert.AreEqual(starts[0].Base.BaseId, starts[1].Base.BaseId);
        Assert.AreEqual(starts[0].Revision.Order, starts[1].Revision.Order);
        var baseId = starts[0].Base.BaseId;
        await first.SelectLiveAsync(scope, baseId, token).ConfigureAwait(false);
        var latestReference = ViewDefinition(scope, 104);
        await first.RegisterBaseAsync(latestReference, [], token).ConfigureAwait(false);
        Assert.AreEqual(latestReference.Reference.Capture.CaptureId, (await first.ReadLatestSingleAsync(scope, token)
            .ConfigureAwait(false))?.Base.Definition.Reference.Capture.CaptureId);

        var one = ExampleLayer(definition, "markers", 4);
        var two = ExampleLayer(definition, "weather", 9);
        await Task.WhenAll(Task.Run(() => first.PublishLayerAsync(baseId, one, token).AsTask(), token),
            Task.Run(() => second.PublishLayerAsync(baseId, two, token).AsTask(), token)).ConfigureAwait(false);
        var current = await first.ReadCurrentAsync(scope, token).ConfigureAwait(false);
        Assert.IsNotNull(current);
        Assert.HasCount(2, current.View.Revision.Layers);
        Assert.AreEqual(current.View.Revision.Order, current.Interval.StartedOrder);
        Assert.AreEqual(current.View.Revision.RevisionId, (await second.PublishLayerAsync(baseId, one, token)
            .ConfigureAwait(false))?.Revision.RevisionId);
        Assert.AreEqual(current.View.Revision.RevisionId, (await second.PublishLayerAsync(baseId, one with { EvidenceOrder = 3 }, token)
            .ConfigureAwait(false))?.Revision.RevisionId);
        var oneSource = definition.Reference;
        var slot = new ExposureProductSlot(oneSource.ExecutionId, "measurement", ExposureMeasurementProducts.SchemaVersion, "measurements");
        var expired = new ExposureProductLookup(oneSource.Input.Key, slot, ExposureProductAvailability.Expired,
            one.EvidenceOrder, null, "retention-expired");
        Assert.AreEqual(current.View.Revision.RevisionId, (await second.PublishUnavailableProductLayerAsync(baseId, one.Key,
            expired, oneSource.ExposureEndedUtc, token).ConfigureAwait(false))?.Revision.RevisionId);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => first.PublishLayerAsync(baseId, one with { Reason = "conflicting" }, token)
            .AsTask()).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => first.PublishLayerAsync(baseId,
            one with { Key = one.Key with { Scope = CenteredViewLayerScope.Contributor, SourceCaptureId = Guid.NewGuid() } }, token)
            .AsTask()).ConfigureAwait(false);
        Assert.IsNull(await first.ReadCurrentAsync(scope with { RunId = Guid.NewGuid() }, token).ConfigureAwait(false));
        var changes = await first.ReadChangesAsync(scope, 0, 10, token).ConfigureAwait(false);
        Assert.HasCount(5, changes);
        CollectionAssert.AreEqual(ExpectedCenteredChangedLive, changes.Select(change => change.SelectedLive).ToArray());
        Assert.HasCount(1, await first.ReadChangesAsync(scope, changes[^2].Order, 10, token).ConfigureAwait(false));
        var missing = ExampleLayer(definition, "restored-product", 10);
        var unavailable = await first.PublishUnavailableProductLayerAsync(baseId, missing.Key,
            expired with { Availability = ExposureProductAvailability.MissingEvidence, Revision = missing.EvidenceOrder },
            oneSource.ExposureEndedUtc, token).ConfigureAwait(false);
        Assert.IsNotNull(unavailable);
        Assert.AreEqual(CenteredViewReadiness.Incompatible, unavailable.Revision.Layers.Single(layer => layer.Key == missing.Key).Readiness);
        var restored = await first.PublishLayerAsync(baseId, missing, token).ConfigureAwait(false);
        Assert.IsNotNull(restored);
        Assert.AreNotEqual(unavailable.Revision.RevisionId, restored.Revision.RevisionId);
        Assert.AreEqual(CenteredViewReadiness.Ready, restored.Revision.Layers.Single(layer => layer.Key == missing.Key).Readiness);
    }

    [TestMethod]
    public async Task CenteredArchiveDistinguishesFirstLatestAndLastLiveRevisions()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        using var store = new SqliteCaptureProcessingStore(fixture.Options);
        var index = new SqliteCenteredViews(store, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        var token = TestContext.CancellationToken;
        var scope = ViewScope();
        var input = ViewDefinition(scope, 103);
        var initial = await index.RegisterBaseAsync(input, [], token).ConfigureAwait(false);
        var enriched = await index.PublishLayerAsync(initial.Base.BaseId, ExampleLayer(input, "markers", 1), token).ConfigureAwait(false);
        Assert.IsNotNull(enriched);
        var neverLive = (await index.ReadArchiveAsync(scope, input.Reference.Capture.CaptureId, token).ConfigureAwait(false)).Single();
        Assert.AreEqual(initial.Revision.RevisionId, neverLive.InitialRevisionId);
        Assert.AreEqual(enriched.Revision.RevisionId, neverLive.LatestRevisionId);
        Assert.IsNull(neverLive.LastLiveRevisionId);
        Assert.AreEqual(initial.Revision.RevisionId, neverLive.DefaultRevisionId);
        await index.SelectLiveAsync(scope, initial.Base.BaseId, token).ConfigureAwait(false);
        var replacement = await index.RegisterBaseAsync(ViewDefinition(scope, 104), [], token).ConfigureAwait(false);
        await index.SelectLiveAsync(scope, replacement.Base.BaseId, token).ConfigureAwait(false);
        var afterLive = await index.PublishLayerAsync(initial.Base.BaseId, ExampleLayer(input, "markers", 2), token).ConfigureAwait(false);
        Assert.IsNotNull(afterLive);
        var archived = (await index.ReadArchiveAsync(scope, input.Reference.Capture.CaptureId, token).ConfigureAwait(false)).Single();
        Assert.AreEqual(initial.Revision.RevisionId, archived.InitialRevisionId);
        Assert.AreEqual(enriched.Revision.RevisionId, archived.LastLiveRevisionId);
        Assert.AreEqual(enriched.Revision.RevisionId, archived.DefaultRevisionId);
        Assert.AreEqual(afterLive.Revision.RevisionId, archived.LatestRevisionId);
    }

    private static CenteredViewStream ViewScope() => new(new("agent-a", "camera-a"), Guid.NewGuid(), "centered-profile");

    private static CenteredViewBaseDefinition ViewDefinition(CenteredViewStream scope, long sequence)
    {
        var descriptor = ArtifactManifestFixture.CreateManifest(CameraPixelFormat.Mono8, 16, 12, 16, new byte[16 * 12]).Descriptor;
        var capture = descriptor.Capture with { CaptureId = Guid.NewGuid(), CaptureSequence = sequence };
        descriptor = descriptor with { Capture = capture, Artifact = descriptor.Artifact with { ArtifactId = Guid.NewGuid() } };
        var input = new ExposureInputV1(new(scope.Camera, scope.RunId, capture.CaptureId), descriptor,
            new(descriptor.Layout, new RigOrientation(0, 0, 0), new string('A', 64), "optics", null));
        var linear = ExamplePin();
        var exposure = new CenteredViewExposure(input, Guid.NewGuid(), linear);
        var previewPin = ExamplePin();
        var preview = new CenteredViewPreview(new(previewPin.ArtifactId, previewPin.IdentitySha256, "image/jpeg",
            new(16, 12, new string('B', 64), new string('C', 64))), previewPin.PayloadSha256,
            RecipeIdentityDescriptor.Create("preview", "1.0.0", "preview-v1", JsonSerializer.SerializeToElement(new { })),
            "JPEG", "jpeg-v1", new(16, 12, 0, 0, 16, 12, 16, 12))
        {
            LinearDisposition = LinearExposureDisposition.QualifiedNormalizationOnly
        };
        return new(scope, exposure, sequence, CenteredViewKind.Single, linear, preview,
            [new(exposure, "ReferenceIdentity", JsonSerializer.SerializeToElement(new { method = "identity" }))], null);
    }

    private static CenteredViewArtifact ExamplePin() => new(Guid.NewGuid(), new string('A', 64), new string('D', 64));

    private static CenteredViewLayer ExampleLayer(CenteredViewBaseDefinition definition, string kind, long order)
    {
        var pin = ExamplePin();
        var drawable = LayeredPresentationJson.CreateLayer(kind,
            new(pin.ArtifactId, pin.IdentitySha256, "application/json", definition.Preview.Product.Compatibility),
            null, PresentationCoordinateSpace.ScenePixels, "renderer-v1", "style-v1",
            200, PresentationBlendMode.Normal, 1_000_000, enabledByDefault: true, default);
        return new(new(kind, CenteredViewLayerScope.Reference, definition.Reference.Capture.CaptureId),
            order, pin, definition.Reference.ExposureEndedUtc, CenteredViewReadiness.Ready, null, drawable);
    }
}
