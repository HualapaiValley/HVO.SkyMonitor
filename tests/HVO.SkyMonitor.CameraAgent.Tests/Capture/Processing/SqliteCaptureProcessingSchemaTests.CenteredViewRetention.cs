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
    [TestMethod]
    public async Task CenteredViewRetentionPrunesExpiredCaptureHistoryAndPreservesRetainedReferences()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        using var store = new SqliteCaptureProcessingStore(fixture.Options);
        using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        using var telemetry = new CaptureProcessingTelemetry();
        var persistence = new CaptureProcessingPersistence(fixture.Options, store, storage, telemetry,
            NullLogger<CaptureProcessingPersistence>.Instance);
        var index = new SqliteCenteredViews(store, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        var token = TestContext.CancellationToken;
        // Keep the newly committed preview newer than the cutoff wherever this fixture runs.
        var cutoff = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-1));
        var scope = ViewScope();
        using var connection = await fixture.OpenAsync().ConfigureAwait(false);

        var expired = await RegisterAsync(101, cutoff.AddMilliseconds(-1)).ConfigureAwait(false);
        var expiredLive = await index.SelectLiveAsync(scope, expired.Base.BaseId, token).ConfigureAwait(false);
        Assert.IsNotNull(expiredLive);
        var initialBytes = await SnapshotBytesAsync(connection).ConfigureAwait(false);
        var enriched = await index.PublishLayerAsync(expired.Base.BaseId,
            ExampleLayer(expired.Base.Definition, "markers", 1), token).ConfigureAwait(false);
        Assert.IsNotNull(enriched);
        var enrichedBytes = await SnapshotBytesAsync(connection).ConfigureAwait(false);
        Assert.IsGreaterThan(initialBytes, enrichedBytes);
        TestContext.WriteLine($"Initial centered revision snapshot: {initialBytes:N0} bytes; after one layer revision: {enrichedBytes:N0} bytes.");
        var historical = await RegisterAsync(102, cutoff.AddDays(1)).ConfigureAwait(false);
        Assert.IsNotNull(await index.SelectLiveAsync(scope, historical.Base.BaseId, token).ConfigureAwait(false));
        var saved = await RegisterAsync(104, cutoff.AddDays(-2)).ConfigureAwait(false);
        var savedId = Guid.NewGuid();
        Assert.IsNotNull(await index.SaveAsync("operator", savedId, saved.Base.BaseId, saved.Revision.RevisionId, [], token)
            .ConfigureAwait(false));
        // A newer publication can close an interval owned by a newer-dated capture after a clock correction.
        var closing = await RegisterAsync(105, cutoff.AddDays(-2)).ConfigureAwait(false);
        var closingInterval = await index.SelectLiveAsync(scope, closing.Base.BaseId, token).ConfigureAwait(false);
        Assert.IsNotNull(closingInterval);
        // Only the closing change for the historical interval is needed. Its own expired
        // interval must not pin the next view and every view after it.
        var boundary = await RegisterAsync(106, cutoff.AddDays(1).AddMilliseconds(-1)).ConfigureAwait(false);
        Assert.IsNotNull(await index.SelectLiveAsync(scope, boundary.Base.BaseId, token).ConfigureAwait(false));
        var live = await RegisterAsync(107, cutoff.AddDays(-2)).ConfigureAwait(false);
        var stillLive = await index.SelectLiveAsync(scope, live.Base.BaseId, token).ConfigureAwait(false);
        Assert.IsNotNull(stillLive);
        var younger = await RegisterAsync(108, cutoff.AddDays(1)).ConfigureAwait(false);
        var heldRaw = await RegisterAsync(109, cutoff.AddDays(-2), retainedPayload: true).ConfigureAwait(false);
        var heldPreview = await RegisterAsync(110, cutoff.AddDays(-2), retainedPreview: true).ConfigureAwait(false);
        await SeedCompletedWindowCheckpointAsync(connection, expired, token).ConfigureAwait(false);
        var expiredIds = new[] { expired.Base.BaseId, boundary.Base.BaseId };
        var beforeBytes = await SnapshotBytesAsync(connection).ConfigureAwait(false);

        await persistence.ExpireOutputsAsync(fixture.Options.Value.RawIngressRoot, cutoff, new HashSet<string>(), token)
            .ConfigureAwait(false);

        using var reopenedStore = new SqliteCaptureProcessingStore(fixture.Options);
        var reopened = new SqliteCenteredViews(reopenedStore, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        Assert.IsEmpty(await reopened.ReadArchiveAsync(expired.Base.Definition.Reference.Capture.CaptureId, token).ConfigureAwait(false));
        Assert.IsNull(await reopened.ReadRevisionAsync(expired.Base.BaseId, enriched.Revision.RevisionId, token).ConfigureAwait(false));
        Assert.IsNull(await reopened.ReadRevisionAsync(boundary.Base.BaseId, boundary.Revision.RevisionId, token).ConfigureAwait(false));
        Assert.AreEqual(stillLive.Interval, (await reopened.ReadCurrentAsync(scope, token).ConfigureAwait(false))?.Interval);
        Assert.AreEqual(saved.Revision.RevisionId, (await reopened.ReadSavedAsync("operator", savedId, token)
            .ConfigureAwait(false))?.RevisionId);
        foreach (var view in new[] { historical, closing, live, saved, younger, heldRaw, heldPreview })
        {
            Assert.AreEqual(view.Revision.RevisionId, (await reopened.ReadRevisionAsync(view.Base.BaseId,
                view.Revision.RevisionId, token).ConfigureAwait(false))?.Revision.RevisionId);
            Assert.HasCount(1, await reopened.ReadArchiveAsync(view.Base.Definition.Reference.Capture.CaptureId, token).ConfigureAwait(false));
        }
        var changes = await reopened.ReadChangesAsync(scope, 0, 32, token).ConfigureAwait(false);
        Assert.IsFalse(changes.Any(change => expiredIds.Contains(change.BaseId)));
        var historicalInterval = Assert.ContainsSingle(await reopened.ReadLiveHistoryAsync(historical.Base.BaseId, 0, 32, token)
            .ConfigureAwait(false));
        Assert.AreEqual(closingInterval.Interval.StartedOrder, historicalInterval.EndedOrder);
        Assert.IsEmpty(await reopened.ReadLiveHistoryAsync(closing.Base.BaseId, 0, 32, token).ConfigureAwait(false));
        Assert.IsEmpty(await reopened.ReadLiveHistoryAsync(expired.Base.BaseId, 0, 32, token).ConfigureAwait(false));
        Assert.AreEqual(7L, await ScalarAsync(connection, "SELECT COUNT(*) FROM processing_centered_view_members;").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarAsync(connection, """
            SELECT COUNT(*) FROM processing_centered_view_window_work
            WHERE live_base_id IS NULL AND retry_utc IS NULL AND terminal_reason IS NULL;
            """).ConfigureAwait(false));
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;").ConfigureAwait(false));
        var afterBytes = await SnapshotBytesAsync(connection).ConfigureAwait(false);
        Assert.IsLessThan(beforeBytes, afterBytes);
        TestContext.WriteLine($"Total serialized centered revision snapshots before sweep: {beforeBytes:N0} bytes; after: {afterBytes:N0} bytes.");

        await persistence.ExpireOutputsAsync(fixture.Options.Value.RawIngressRoot, cutoff, new HashSet<string>(), token)
            .ConfigureAwait(false);
        Assert.AreEqual(afterBytes, await SnapshotBytesAsync(connection).ConfigureAwait(false));

        async Task<CenteredView> RegisterAsync(long sequence, DateTimeOffset capturedUtc, bool retainedPayload = false,
            bool retainedPreview = false)
        {
            var definition = RetentionViewDefinition(scope, sequence, capturedUtc);
            await SeedCenteredRetentionCaptureAsync(connection, fixture.Options.Value.RawIngressRoot,
                definition, capturedUtc, retainedPayload, token).ConfigureAwait(false);
            if (retainedPreview)
            {
                var reference = definition.Reference with { Linear = null };
                var raw = reference.Input.Raw;
                var pixels = new ProcessingArtifact(raw.Artifact.ArtifactId, FrameArtifactRole.Raw, raw.Artifact.Variant,
                    ProcessingIdentity.CreateRecipeIdentity(raw.Artifact.Recipe).IdentitySha256, raw.Artifact.MediaType,
                    raw.Layout, new byte[checked((int)raw.Layout.ByteLength)], capturedUtc, raw.Controls.EffectiveExposure,
                    CameraAgentRecipeExecutionAdapter.CreateCompatibility(raw));
                var preview = CenteredViewPreviewEncoder.Encode([pixels], raw.Layout, CenteredViewImageSource.RawFallback,
                    LinearExposureDisposition.Unsupported, "normalization-unavailable", new(), token);
                await persistence.PersistCenteredViewProductAsync(raw, preview.Product, token).ConfigureAwait(false);
                definition = definition with
                {
                    Reference = reference,
                    Science = reference.RawArtifact(),
                    Preview = preview.Preview,
                    Contributors = [definition.Contributors[0] with { Exposure = reference }]
                };
            }
            return await index.RegisterBaseAsync(definition, [], token).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CenteredViewRetentionRollsBackTheWholeJournalPruneThenRetries()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        using var store = new SqliteCaptureProcessingStore(fixture.Options);
        using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        using var telemetry = new CaptureProcessingTelemetry();
        var persistence = new CaptureProcessingPersistence(fixture.Options, store, storage, telemetry,
            NullLogger<CaptureProcessingPersistence>.Instance);
        var index = new SqliteCenteredViews(store, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        var token = TestContext.CancellationToken;
        var cutoff = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        var capturedUtc = cutoff.AddDays(-1);
        var scope = ViewScope();
        using var connection = await fixture.OpenAsync().ConfigureAwait(false);
        var definition = RetentionViewDefinition(scope, 101, capturedUtc);
        await SeedCenteredRetentionCaptureAsync(connection, fixture.Options.Value.RawIngressRoot,
            definition, capturedUtc, retainedPayload: false, token).ConfigureAwait(false);
        var initial = await index.RegisterBaseAsync(definition, [], token).ConfigureAwait(false);
        using var failure = connection.CreateCommand();
        failure.CommandText = """
            CREATE TRIGGER reject_centered_view_delete BEFORE DELETE ON processing_centered_view_bases
            BEGIN SELECT RAISE(ABORT, 'injected centered retention failure'); END;
            """;
        await failure.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var heldPaths = new HashSet<string>();

        var error = await Assert.ThrowsExactlyAsync<SqliteException>(() => persistence.ExpireOutputsAsync(
            fixture.Options.Value.RawIngressRoot, cutoff, heldPaths, token).AsTask()).ConfigureAwait(false);

        StringAssert.Contains(error.Message, "injected centered retention failure", StringComparison.Ordinal);
        Assert.AreEqual(initial.Revision.RevisionId, (await index.ReadRevisionAsync(initial.Base.BaseId,
            initial.Revision.RevisionId, token).ConfigureAwait(false))?.Revision.RevisionId);
        Assert.HasCount(1, await index.ReadChangesAsync(scope, 0, 32, token).ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM processing_centered_view_members;").ConfigureAwait(false));
        failure.CommandText = "DROP TRIGGER reject_centered_view_delete;";
        await failure.ExecuteNonQueryAsync(token).ConfigureAwait(false);

        await persistence.ExpireOutputsAsync(fixture.Options.Value.RawIngressRoot, cutoff, heldPaths, token).ConfigureAwait(false);
        Assert.IsNull(await index.ReadRevisionAsync(initial.Base.BaseId, initial.Revision.RevisionId, token).ConfigureAwait(false));
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;").ConfigureAwait(false));
    }

    private static Task<long> SnapshotBytesAsync(SqliteConnection connection) =>
        ScalarAsync(connection, """
            SELECT COALESCE(SUM(length(CAST(manifest AS BLOB)) + length(CAST(layers AS BLOB))), 0)
            FROM processing_centered_view_revisions;
            """);

    private static CenteredViewBaseDefinition RetentionViewDefinition(CenteredViewStream scope, long sequence,
        DateTimeOffset capturedUtc)
    {
        var definition = ViewDefinition(scope, sequence);
        var raw = definition.Reference.Input.Raw with
        {
            Timing = new CaptureTimingDescriptor(capturedUtc, capturedUtc, capturedUtc, capturedUtc, capturedUtc)
        };
        var reference = definition.Reference with { Input = definition.Reference.Input with { Raw = raw } };
        return definition with { Reference = reference, Contributors = [definition.Contributors[0] with { Exposure = reference }] };
    }

    private static async Task SeedCompletedWindowCheckpointAsync(SqliteConnection connection, CenteredView view,
        CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO processing_centered_windows (window_sha256, deadline_utc_ticks, registration_deadline_utc_ticks,
                checkpoint_json, reference_capture_id, closure_json, terminal)
            VALUES ($window, 1, 2, X'7B7D', $capture, X'7B7D', 1);
            INSERT INTO processing_centered_view_window_work (window_sha256, live_base_id)
            VALUES ($window, $base);
            """;
        command.Parameters.AddWithValue("$window", new string('A', 64));
        command.Parameters.AddWithValue("$capture", view.Base.Definition.Reference.Capture.CaptureId.ToString("N"));
        command.Parameters.AddWithValue("$base", view.Base.BaseId);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task SeedCenteredRetentionCaptureAsync(SqliteConnection connection, string root,
        CenteredViewBaseDefinition definition, DateTimeOffset capturedUtc, bool retainedPayload, CancellationToken token)
    {
        var capture = definition.Reference.Capture;
        var artifactId = definition.Reference.Input.Raw.Artifact.ArtifactId;
        var paths = RawIngressFileStore.GetPaths(root, capturedUtc, artifactId);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raw_capture_assignments (capture_id, raw_artifact_id, agent_id, capture_sequence)
            VALUES ($capture, $artifact, $agent, $sequence);
            INSERT INTO raw_captures (capture_id, raw_artifact_id, agent_id, capture_sequence, descriptor_sha256,
                manifest_sha256, payload_sha256, payload_length, payload_relative_path, sidecar_relative_path,
                manifest_json, exposure_started_unix_ms, durable_ingress_unix_ms, committed_unix_ms, state, retention_hold)
            VALUES ($capture, $artifact, $agent, $sequence, $capture || $capture, $artifact || $artifact,
                $artifact || $artifact, 1, $payload, $sidecar, X'7B7D', $utc, $utc, $utc, 'committed', 0);
            """;
        command.Parameters.AddWithValue("$capture", capture.CaptureId.ToString("N"));
        command.Parameters.AddWithValue("$artifact", artifactId.ToString("N"));
        command.Parameters.AddWithValue("$agent", capture.AgentId);
        command.Parameters.AddWithValue("$sequence", capture.CaptureSequence);
        command.Parameters.AddWithValue("$payload", paths.PayloadRelativePath);
        command.Parameters.AddWithValue("$sidecar", paths.SidecarRelativePath);
        command.Parameters.AddWithValue("$utc", capturedUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        if (retainedPayload)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.PayloadAbsolutePath)!);
            await File.WriteAllBytesAsync(paths.PayloadAbsolutePath, new byte[1], token).ConfigureAwait(false);
        }
    }
}
