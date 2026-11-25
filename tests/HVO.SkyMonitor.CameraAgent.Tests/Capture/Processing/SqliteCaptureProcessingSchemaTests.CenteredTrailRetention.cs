using System.Diagnostics;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class SqliteCaptureProcessingSchemaTests
{
    private const string TrailWorkCountSql = "SELECT COUNT(*) FROM processing_centered_view_trail_work;";

    [TestMethod]
    public async Task CenteredTrailRetentionBoundsExpiredWorkAcrossSweeps()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        using var store = new SqliteCaptureProcessingStore(fixture.Options);
        using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        using var telemetry = new CaptureProcessingTelemetry();
        var persistence = new CaptureProcessingPersistence(fixture.Options, store, storage, telemetry,
            NullLogger<CaptureProcessingPersistence>.Instance);
        using var connection = await fixture.OpenAsync().ConfigureAwait(false);
        var root = fixture.Options.Value.RawIngressRoot;
        var token = TestContext.CancellationToken;
        var cutoff = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        var scope = ViewScope();
        var expiredPerSweep = CaptureProcessingPersistence.CenteredViewPruneBatchSize + 4;
        // The retained exposure starts just beyond the end of the cutoff day.
        await SeedTrailRetentionCaptureAsync(connection, root, scope, 1, cutoff.AddDays(1), token).ConfigureAwait(false);

        for (var sweep = 0; sweep < 3; sweep++)
        {
            var sequence = sweep * expiredPerSweep + 2;
            for (var index = 0; index < expiredPerSweep; index++)
            {
                // This represents trail history whose bases were already pruned by a previous version.
                await SeedTrailRetentionCaptureAsync(connection, root, scope, sequence + index,
                    cutoff.AddDays(1).AddMilliseconds(-1), token).ConfigureAwait(false);
            }

            var before = await ScalarAsync(connection, TrailWorkCountSql).ConfigureAwait(false);
            Assert.AreEqual(expiredPerSweep + 1L, before);
            var started = Stopwatch.GetTimestamp();
            await persistence.ExpireOutputsAsync(root, cutoff, new HashSet<string>(), token).ConfigureAwait(false);
            var after = await ScalarAsync(connection, TrailWorkCountSql).ConfigureAwait(false);
            TestContext.WriteLine($"Trail retention sweep {sweep + 1}: {before} -> {after} rows in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms.");
            Assert.AreEqual(1L, after);
        }

        Assert.AreEqual(1L, await ScalarAsync(connection, "SELECT revision FROM processing_centered_view_trail_work;")
            .ConfigureAwait(false));
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CenteredTrailRetentionRollsBackFailedBatchAndResumesAfterCancellation()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        using var store = new SqliteCaptureProcessingStore(fixture.Options);
        using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        using var telemetry = new CaptureProcessingTelemetry();
        var logger = new CenteredRetentionLog();
        var persistence = new CaptureProcessingPersistence(fixture.Options, store, storage, telemetry, logger);
        using var connection = await fixture.OpenAsync().ConfigureAwait(false);
        var root = fixture.Options.Value.RawIngressRoot;
        var token = TestContext.CancellationToken;
        var cutoff = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        var scope = ViewScope();
        var batchSize = CaptureProcessingPersistence.CenteredViewPruneBatchSize;
        var total = batchSize + 1;
        for (var sequence = 1; sequence <= total; sequence++)
            await SeedTrailRetentionCaptureAsync(connection, root, scope, sequence, cutoff.AddDays(-1), token)
                .ConfigureAwait(false);

        await store.InitializeAsync(token).ConfigureAwait(false);
        using var failure = connection.CreateCommand();
        failure.CommandText = """
            CREATE TRIGGER reject_centered_trail_delete BEFORE DELETE ON processing_centered_view_trail_work
            WHEN OLD.revision = 2
            BEGIN SELECT RAISE(ABORT, 'fixture trail deletion failure'); END;
            """;
        await failure.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var held = new HashSet<string>();
        await persistence.ExpireOutputsAsync(root, cutoff, held, token).ConfigureAwait(false);
        Assert.AreEqual((long)total, await ScalarAsync(connection, TrailWorkCountSql).ConfigureAwait(false));
        var error = Assert.IsInstanceOfType<SqliteException>(Assert.ContainsSingle(logger.Warnings));
        StringAssert.Contains(error.Message, "fixture trail deletion failure", StringComparison.Ordinal);
        Assert.AreEqual(2546, Assert.ContainsSingle(logger.WarningEvents).Id);
        Assert.IsEmpty(logger.Reports);
        failure.CommandText = "DROP TRIGGER reject_centered_trail_delete;";
        await failure.ExecuteNonQueryAsync(token).ConfigureAwait(false);

        using var interruption = new CancellationTokenSource();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => persistence.ExpireOutputsAsync(root, cutoff, held,
            async _ =>
            {
                Assert.AreEqual(1L, await ScalarAsync(connection, TrailWorkCountSql).ConfigureAwait(false));
                await interruption.CancelAsync().ConfigureAwait(false);
            }, token, interruption.Token).AsTask()).ConfigureAwait(false);
        Assert.AreEqual(total, await ScalarAsync(connection, "SELECT revision FROM processing_centered_view_trail_work;")
            .ConfigureAwait(false));
        Assert.AreEqual(2549, Assert.ContainsSingle(logger.ReportIds));

        await persistence.ExpireOutputsAsync(root, cutoff, held, token).ConfigureAwait(false);
        Assert.AreEqual(0L, await ScalarAsync(connection, TrailWorkCountSql).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CenteredTrailRetentionPreservesPendingAndTerminalEvidenceWithRetainedConsumers()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        using var store = new SqliteCaptureProcessingStore(fixture.Options);
        using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        using var telemetry = new CaptureProcessingTelemetry();
        using var connection = await fixture.OpenAsync().ConfigureAwait(false);
        var root = fixture.Options.Value.RawIngressRoot;
        var token = TestContext.CancellationToken;
        var cutoff = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-1));
        var clock = new CenteredTestClock { UtcNow = cutoff.AddDays(2) };
        var persistence = new CaptureProcessingPersistence(fixture.Options, store, storage, telemetry,
            NullLogger<CaptureProcessingPersistence>.Instance);
        var views = new SqliteCenteredViews(store, clock, NullLogger<SqliteCenteredViews>.Instance);
        var scope = ViewScope();
        var old = cutoff.AddDays(-1);

        var contributor = await SeedTrailRetentionCaptureAsync(connection, root, scope, 1, old, token).ConfigureAwait(false);
        var contributorKey = contributor.Reference.Input.Key;
        var single = await views.RegisterBaseAsync(contributor, [], token).ConfigureAwait(false);
        var reference = RetentionViewDefinition(scope, 2, old);
        await SeedCenteredRetentionCaptureAsync(connection, root, reference, old, retainedPayload: false, token).ConfigureAwait(false);
        var retainedStack = await views.RegisterBaseAsync(reference with
        {
            Kind = CenteredViewKind.Stack,
            Preview = reference.Preview with { Source = CenteredViewImageSource.RegisteredLinear },
            ClosedWindow = ExamplePin(),
            Contributors = [reference.Contributors[0], contributor.Contributors[0]]
        }, [], token).ConfigureAwait(false);
        var selectionId = Guid.NewGuid();
        Assert.IsNotNull(await views.SaveAsync("operator", selectionId, retainedStack.Base.BaseId,
            retainedStack.Revision.RevisionId, [], token).ConfigureAwait(false));
        var consumed = await views.ReadLatestTrailWorkAsync(contributorKey, token).ConfigureAwait(false);
        Assert.IsNotNull(consumed);
        var terminalEvidence = new CenteredViewTrailEvidence(null, ExamplePin(), true, "fixture.terminal");
        await views.PinTrailEvidenceAsync(consumed, terminalEvidence, token).ConfigureAwait(false);

        var pending = await SeedTrailRetentionCaptureAsync(connection, root, scope, 3, old, token).ConfigureAwait(false);
        var pendingKey = pending.Reference.Input.Key;
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO processing_centered_view_trail_work(capture_id, candidate_id, stage_key, stage_state, observed_utc)
                VALUES ($capture, $candidate, 'candidate-persisted', 'succeeded', $utc);
                """;
            insert.Parameters.AddWithValue("$capture", pendingKey.CaptureId.ToString("N"));
            insert.Parameters.AddWithValue("$candidate", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("$utc", old.UtcTicks);
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        var retry = Assert.ContainsSingle(await views.ReadDueTrailWorkAsync(16, token).ConfigureAwait(false));
        var retryEvidence = new CenteredViewTrailEvidence(null, ExamplePin(), true, "fixture.retry");
        await views.PinTrailEvidenceAsync(retry, retryEvidence, token).ConfigureAwait(false);
        await views.CheckpointTrailWorkAsync(retry, TimeSpan.FromSeconds(15), token).ConfigureAwait(false);

        var rawHeld = await SeedTrailRetentionCaptureAsync(connection, root, scope, 4, old, token, rawHold: true).ConfigureAwait(false);
        var fileHeld = await SeedTrailRetentionCaptureAsync(connection, root, scope, 5, old, token, retainedPayload: true)
            .ConfigureAwait(false);
        var heldByPath = await SeedTrailRetentionCaptureAsync(connection, root, scope, 6, old, token).ConfigureAwait(false);
        var heldPath = RawIngressFileStore.GetPaths(root, old, heldByPath.Reference.Input.Raw.Artifact.ArtifactId).SidecarAbsolutePath;
        var held = new HashSet<string> { heldPath };
        var leased = await SeedTrailRetentionCaptureAsync(connection, root, scope, 7, old, token).ConfigureAwait(false);
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO centered_exposure_holds (owner_id, capture_id, output_identity, bytes, expires_unix_ms)
                VALUES ('fixture-trail-hold', $capture, 'raw:fixture', 1, $expiry);
                """;
            insert.Parameters.AddWithValue("$capture", leased.Reference.Input.Key.CaptureId.ToString("N"));
            insert.Parameters.AddWithValue("$expiry", DateTimeOffset.MaxValue.ToUnixTimeMilliseconds());
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        // The preview committed before registration, which can resume using the trail evidence.
        var previewed = await SeedTrailRetentionCaptureAsync(connection, root, scope, 8, old, token).ConfigureAwait(false);
        var raw = previewed.Reference.Input.Raw;
        var pixels = new ProcessingArtifact(raw.Artifact.ArtifactId, FrameArtifactRole.Raw, raw.Artifact.Variant,
            ProcessingIdentity.CreateRecipeIdentity(raw.Artifact.Recipe).IdentitySha256, raw.Artifact.MediaType,
            raw.Layout, new byte[checked((int)raw.Layout.ByteLength)], old, raw.Controls.EffectiveExposure,
            CameraAgentRecipeExecutionAdapter.CreateCompatibility(raw));
        var preview = CenteredViewPreviewEncoder.Encode([pixels], raw.Layout, CenteredViewImageSource.RawFallback,
            LinearExposureDisposition.Unsupported, "normalization-unavailable", new(), token);
        await persistence.PersistCenteredViewProductAsync(raw, preview.Product, token).ConfigureAwait(false);

        await persistence.ExpireOutputsAsync(root, cutoff, held, token).ConfigureAwait(false);

        Assert.IsNull(await views.ReadBaseAsync(single.Base.BaseId, token).ConfigureAwait(false));
        Assert.AreEqual(retainedStack.Revision.RevisionId, (await views.ReadSavedAsync("operator", selectionId, token)
            .ConfigureAwait(false))?.RevisionId);
        Assert.AreEqual(consumed, await views.ReadLatestTrailWorkAsync(contributorKey, token).ConfigureAwait(false));
        Assert.AreEqual(terminalEvidence, await views.ReadTrailEvidenceAsync(consumed, token).ConfigureAwait(false));
        Assert.IsEmpty(await views.ReadDueTrailWorkAsync(16, token).ConfigureAwait(false));
        Assert.AreEqual(8L, await ScalarAsync(connection, TrailWorkCountSql).ConfigureAwait(false));
        clock.UtcNow += TimeSpan.FromSeconds(15);
        Assert.AreEqual(retry, Assert.ContainsSingle(await views.ReadDueTrailWorkAsync(16, token).ConfigureAwait(false)));
        Assert.AreEqual(retryEvidence, await views.ReadTrailEvidenceAsync(retry, token).ConfigureAwait(false));
        await views.CheckpointTrailWorkAsync(retry, retryAfter: null, token).ConfigureAwait(false);
        using (var release = connection.CreateCommand())
        {
            release.CommandText = "DELETE FROM centered_exposure_holds WHERE owner_id = 'fixture-trail-hold';";
            await release.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        await persistence.ExpireOutputsAsync(root, cutoff, held, token).ConfigureAwait(false);
        Assert.IsNull(await views.ReadLatestTrailWorkAsync(pendingKey, token).ConfigureAwait(false));
        Assert.IsNull(await views.ReadLatestTrailWorkAsync(leased.Reference.Input.Key, token).ConfigureAwait(false));
        foreach (var definition in new[] { contributor, rawHeld, fileHeld, heldByPath, previewed })
            Assert.IsNotNull(await views.ReadLatestTrailWorkAsync(definition.Reference.Input.Key, token).ConfigureAwait(false));
        Assert.AreEqual(5L, await ScalarAsync(connection, TrailWorkCountSql).ConfigureAwait(false));
    }

    private static async Task<CenteredViewBaseDefinition> SeedTrailRetentionCaptureAsync(SqliteConnection connection, string root,
        CenteredViewStream scope, long sequence, DateTimeOffset capturedUtc, CancellationToken token, bool rawHold = false,
        bool retainedPayload = false)
    {
        var definition = RetentionViewDefinition(scope, sequence, capturedUtc);
        await SeedCenteredRetentionCaptureAsync(connection, root, definition, capturedUtc, retainedPayload, token, rawHold)
            .ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO centered_exposure_runs (run_id, agent_id, camera_id, snapshot_json, snapshot_sha256)
            VALUES ($run, $agent, $camera, X'7B7D', $run || $run) ON CONFLICT(run_id) DO NOTHING;
            INSERT INTO centered_exposure_attempts (attempt_id, run_id, ordinal, capture_id, status)
            VALUES ($capture, $run, $sequence, $capture, 'observed');
            INSERT INTO processing_centered_view_trail_work(capture_id, candidate_id, stage_key, stage_state, observed_utc, consumed)
            VALUES ($capture, '', 'causal-scan', 'succeeded', $utc, 1);
            """;
        command.Parameters.AddWithValue("$run", scope.RunId.ToString("N"));
        command.Parameters.AddWithValue("$agent", scope.Camera.AgentId);
        command.Parameters.AddWithValue("$camera", scope.Camera.CameraId);
        command.Parameters.AddWithValue("$capture", definition.Reference.Capture.CaptureId.ToString("N"));
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$utc", capturedUtc.UtcTicks);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        return definition;
    }
}
