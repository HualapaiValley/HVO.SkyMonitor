using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

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
        var definitionBytes = await ScalarAsync(connection, "SELECT length(CAST(definition AS BLOB)) FROM processing_centered_view_bases;")
            .ConfigureAwait(false);
        TestContext.WriteLine($"Snapshot definition {definitionBytes}");
        CenteredView? enriched = null;
        for (var count = 1; count <= 16; count++)
        {
            enriched = await index.PublishLayerAsync(expired.Base.BaseId,
                ExampleLayer(expired.Base.Definition, FormattableString.Invariant($"layer-{count:D2}"), count), token).ConfigureAwait(false);
            if (count is 1 or 2 or 3 or 4 or 8 or 16)
                TestContext.WriteLine($"Snapshot growth {count} {await SnapshotBytesAsync(connection).ConfigureAwait(false)}");
        }
        Assert.IsNotNull(enriched);
        var enrichedBytes = await SnapshotBytesAsync(connection).ConfigureAwait(false);
        Assert.IsGreaterThan(initialBytes, enrichedBytes);
        TestContext.WriteLine($"Snapshot growth 0 {initialBytes}");
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
        var closingLastLive = await index.PublishLayerAsync(closing.Base.BaseId,
            ExampleLayer(closing.Base.Definition, "markers", 1), token).ConfigureAwait(false);
        Assert.IsNotNull(closingLastLive);
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
        await SeedWindowCheckpointAsync(connection, expired, pending: false, token).ConfigureAwait(false);
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
        var closingArchive = Assert.ContainsSingle(await reopened.ReadArchiveAsync(
            closing.Base.Definition.Reference.Capture.CaptureId, token).ConfigureAwait(false));
        Assert.AreEqual(closingLastLive.Revision.RevisionId, closingArchive.LastLiveRevisionId);
        Assert.HasCount(1, await reopened.ReadLiveHistoryAsync(closing.Base.BaseId, 0, 32, token).ConfigureAwait(false));
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
    public async Task CenteredViewRetentionReportsAFailedBatchAndRetriesItsRolledBackWrites()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        using var store = new SqliteCaptureProcessingStore(fixture.Options);
        using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        using var telemetry = new CaptureProcessingTelemetry();
        var logger = new CenteredRetentionLog();
        var persistence = new CaptureProcessingPersistence(fixture.Options, store, storage, telemetry, logger);
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

        Assert.AreEqual(0, await persistence.ExpireOutputsAsync(fixture.Options.Value.RawIngressRoot, cutoff, heldPaths, token)
            .ConfigureAwait(false));
        var error = Assert.IsInstanceOfType<SqliteException>(Assert.ContainsSingle(logger.Warnings));
        StringAssert.Contains(error.Message, "injected centered retention failure", StringComparison.Ordinal);
        Assert.AreEqual(2546, Assert.ContainsSingle(logger.WarningEvents).Id);
        Assert.AreEqual(initial.Revision.RevisionId, (await index.ReadRevisionAsync(initial.Base.BaseId,
            initial.Revision.RevisionId, token).ConfigureAwait(false))?.Revision.RevisionId);
        Assert.HasCount(1, await index.ReadChangesAsync(scope, 0, 32, token).ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM processing_centered_view_members;").ConfigureAwait(false));
        failure.CommandText = "DROP TRIGGER reject_centered_view_delete;";
        await failure.ExecuteNonQueryAsync(token).ConfigureAwait(false);

        await persistence.ExpireOutputsAsync(fixture.Options.Value.RawIngressRoot, cutoff, heldPaths, token).ConfigureAwait(false);
        Assert.IsNull(await index.ReadRevisionAsync(initial.Base.BaseId, initial.Revision.RevisionId, token).ConfigureAwait(false));
        StringAssert.Contains(Assert.ContainsSingle(logger.Reports), "Pruned 1 expired centered-view bases", StringComparison.Ordinal);
        Assert.AreEqual(2547, Assert.ContainsSingle(logger.ReportIds));
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CenteredViewRetentionKeepsRecomputationParentsAndAllCaptureHolds()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        using var store = new SqliteCaptureProcessingStore(fixture.Options);
        using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        using var telemetry = new CaptureProcessingTelemetry();
        var logger = new CenteredRetentionLog();
        var persistence = new CaptureProcessingPersistence(fixture.Options, store, storage, telemetry, logger);
        var index = new SqliteCenteredViews(store, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        using var connection = await fixture.OpenAsync().ConfigureAwait(false);
        var cutoff = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        var captured = cutoff.AddDays(-1);
        var token = TestContext.CancellationToken;
        var root = fixture.Options.Value.RawIngressRoot;
        var scope = ViewScope();
        var (parent, child) = await RecomputeAsync(101).ConfigureAwait(false);
        Assert.IsNotNull(await index.SaveAsync("operator", Guid.NewGuid(), child.Base.BaseId, child.Revision.RevisionId, [], token)
            .ConfigureAwait(false));
        var (expiredParent, expiredChild) = await RecomputeAsync(102).ConfigureAwait(false);
        var pending = await RegisterSingleAsync(103).ConfigureAwait(false);
        await SeedWindowCheckpointAsync(connection, pending, pending: true, token).ConfigureAwait(false);
        var rawPin = await RegisterSingleAsync(104, rawHold: true).ConfigureAwait(false);
        var pathPin = await RegisterSingleAsync(105).ConfigureAwait(false);
        var sidecar = await RegisterSingleAsync(106).ConfigureAwait(false);
        var malformed = await RegisterSingleAsync(107).ConfigureAwait(false);
        var sidecarPath = Paths(sidecar).SidecarAbsolutePath;
        Directory.CreateDirectory(Path.GetDirectoryName(sidecarPath)!);
        await File.WriteAllBytesAsync(sidecarPath, [1], token).ConfigureAwait(false);
        using (var corrupt = connection.CreateCommand())
        {
            corrupt.CommandText = "UPDATE processing_centered_view_bases SET definition = '{' WHERE base_id = $base;";
            corrupt.Parameters.AddWithValue("$base", malformed.Base.BaseId);
            await corrupt.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        var held = new HashSet<string> { Paths(pathPin).PayloadAbsolutePath };
        await persistence.ExpireOutputsAsync(root, cutoff, held, token).ConfigureAwait(false);

        foreach (var view in new[] { parent, child, pending, rawPin, pathPin, sidecar })
            Assert.IsNotNull(await index.ReadRevisionAsync(view.Base.BaseId, view.Revision.RevisionId, token).ConfigureAwait(false));
        foreach (var view in new[] { expiredParent, expiredChild })
            Assert.IsNull(await index.ReadRevisionAsync(view.Base.BaseId, view.Revision.RevisionId, token).ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM processing_centered_view_bases WHERE definition = '{';")
            .ConfigureAwait(false));
        var warning = Assert.ContainsSingle(logger.WarningEvents);
        Assert.AreEqual(2548, warning.Id);
        StringAssert.Contains(warning.Message, malformed.Base.BaseId, StringComparison.Ordinal);
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;").ConfigureAwait(false));

        RawIngressPaths Paths(CenteredView view) => RawIngressFileStore.GetPaths(root, captured,
            view.Base.Definition.Reference.Input.Raw.Artifact.ArtifactId);

        async Task<CenteredView> RegisterSingleAsync(long order, bool rawHold = false)
        {
            var definition = RetentionViewDefinition(scope, order, captured);
            await SeedCenteredRetentionCaptureAsync(connection, root, definition, captured, false, token, rawHold).ConfigureAwait(false);
            return await index.RegisterBaseAsync(definition, [], token).ConfigureAwait(false);
        }

        async Task<(CenteredView Parent, CenteredView Child)> RecomputeAsync(long order)
        {
            var single = RetentionViewDefinition(scope, order, captured);
            var stack = single with
            {
                Kind = CenteredViewKind.Stack,
                Preview = single.Preview with { Source = CenteredViewImageSource.RegisteredLinear },
                ClosedWindow = ExamplePin(),
                Contributors = [single.Contributors[0], RetentionViewDefinition(scope, order + 100, captured).Contributors[0]]
            };
            await SeedCenteredRetentionCaptureAsync(connection, root, stack, captured, false, token).ConfigureAwait(false);
            var original = await index.RegisterBaseAsync(stack, [], token).ConfigureAwait(false);
            var recomputed = await index.RegisterBaseAsync(stack with { RecomputedFromBaseId = original.Base.BaseId }, [], token)
                .ConfigureAwait(false);
            return (original, recomputed);
        }
    }

    [TestMethod]
    public async Task CenteredViewRetentionPagesAndCommitsOnlyOneBatchBeforeInterruption()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        using var store = new SqliteCaptureProcessingStore(fixture.Options);
        using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        using var telemetry = new CaptureProcessingTelemetry();
        using var interruption = new CancellationTokenSource();
        var persistence = new CaptureProcessingPersistence(fixture.Options, store, storage, telemetry,
            NullLogger<CaptureProcessingPersistence>.Instance, new InterruptAfterCenteredPruneBatch(interruption));
        var index = new SqliteCenteredViews(store, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        using var connection = await fixture.OpenAsync().ConfigureAwait(false);
        var token = TestContext.CancellationToken;
        var captured = new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
        var root = fixture.Options.Value.RawIngressRoot;
        var scope = ViewScope();
        var unprunedPage = fixture.Options.Value.DerivedProductLifecycle.ReconciliationBatchSize + 1;
        for (var order = 1; order <= unprunedPage + CaptureProcessingPersistence.CenteredViewPruneBatchSize; order++)
        {
            var definition = RetentionViewDefinition(scope, order, captured);
            await SeedCenteredRetentionCaptureAsync(connection, root, definition, captured, false, token).ConfigureAwait(false);
            await index.RegisterBaseAsync(definition, [], token).ConfigureAwait(false);
        }

        var held = new HashSet<string>();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            persistence.ExpireOutputsAsync(root, captured, held, token, interruption.Token).AsTask()).ConfigureAwait(false);
        const string countBases = "SELECT COUNT(*) FROM processing_centered_view_bases;";
        Assert.AreEqual((long)unprunedPage, await ScalarAsync(connection, countBases).ConfigureAwait(false));
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;").ConfigureAwait(false));

        await persistence.ExpireOutputsAsync(root, captured, held, token).ConfigureAwait(false);
        Assert.AreEqual(0L, await ScalarAsync(connection, countBases).ConfigureAwait(false));
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CenteredViewRetentionLetsACompetingIngressWriterCommitBetweenBatches()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        using var store = new SqliteCaptureProcessingStore(fixture.Options);
        using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        using var telemetry = new CaptureProcessingTelemetry();
        using var outbox = new SqliteArtifactOutbox();
        using var calibration = new SqliteCalibrationLibraryStore(Mock.Of<IRawCaptureIngress>(), fixture.Options, TimeProvider.System);
        var log = new CenteredRetentionLog();
        var index = new SqliteCenteredViews(store, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        using var connection = await fixture.OpenAsync().ConfigureAwait(false);
        var root = fixture.Options.Value.RawIngressRoot;
        var captured = DateTimeOffset.UtcNow.AddDays(-15);
        var scope = ViewScope();
        var token = TestContext.CancellationToken;
        var ingressHolds = new Mock<IRawIngressRetentionHolds>();
        ingressHolds.Setup(holds => holds.GetRetentionHoldsAsync(root, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RawIngressRetentionHold>());
        var capacity = Mock.Of<IStorageCapacityProvider>(provider =>
            provider.GetCapacity(root) == new StorageCapacity(1000, 500));
        var lastBaseId = "";
        RawIngressPaths? lastPaths = null;

        for (var order = 1; order <= CaptureProcessingPersistence.CenteredViewPruneBatchSize + 2; order++)
        {
            var definition = RetentionViewDefinition(scope, order, captured);
            await SeedCenteredRetentionCaptureAsync(connection, root, definition, captured, false, token).ConfigureAwait(false);
            var view = await index.RegisterBaseAsync(definition, [], token).ConfigureAwait(false);
            if (string.CompareOrdinal(view.Base.BaseId, lastBaseId) > 0)
            {
                lastBaseId = view.Base.BaseId;
                lastPaths = RawIngressFileStore.GetPaths(root, captured, definition.Reference.Input.Raw.Artifact.ArtifactId);
            }
        }
        Assert.IsNotNull(lastPaths);

        // The normal processing hold protects the newest 100 raw captures independently of age.
        var recent = DateTimeOffset.UtcNow;
        for (var order = 1001; order <= 1100; order++)
        {
            var definition = RetentionViewDefinition(scope, order, recent);
            await SeedCenteredRetentionCaptureAsync(connection, root, definition, recent, true, token).ConfigureAwait(false);
            var paths = RawIngressFileStore.GetPaths(root, recent, definition.Reference.Input.Raw.Artifact.ArtifactId);
            await File.WriteAllBytesAsync(paths.SidecarAbsolutePath, [1], token).ConfigureAwait(false);
        }

        var checkpoint = new StartWriterAfterCenteredPruneBatch(WriteCaptureAsync);
        var persistence = new CaptureProcessingPersistence(fixture.Options, store, storage, telemetry, log, checkpoint);
        var holds = new CompositeProcessingRetentionHolds(
            persistence, new CameraAgentClearReferenceLoader(fixture.Options), calibration);
        using var service = new RetentionBackgroundService(Mock.Of<ICameraAgentConfigurationAccessor>(), fixture.Options,
            TimeProvider.System, outbox, capacity, new StoragePressureState(), NullLogger<RetentionBackgroundService>.Instance,
            ingressHolds.Object, holds);
        await service.ApplyRetentionAsync(RetentionBackgroundServiceTests.CreateConfig(root), token).ConfigureAwait(false);
        var writer = checkpoint.Writer;
        Assert.IsNotNull(writer);
        Assert.AreEqual(2L, await writer.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false),
            "The competing ingress writer must commit while another delete batch is still pending.");
        Assert.IsEmpty(log.Warnings);
        Assert.AreEqual(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM processing_centered_view_bases;")
            .ConfigureAwait(false));
        using var survivor = connection.CreateCommand();
        survivor.CommandText = "SELECT base_id FROM processing_centered_view_bases;";
        Assert.AreEqual(lastBaseId, (string?)await survivor.ExecuteScalarAsync(token).ConfigureAwait(false),
            "A raw payload restored during the lock handoff must keep its view.");
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;").ConfigureAwait(false));

        async Task<long> WriteCaptureAsync()
        {
            var gate = RawIngressLifecycleLock.ForRoot(root);
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var storageGate = StorageLifecycleLock.ForRoot(root);
                await storageGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    return await CommitCaptureAsync().ConfigureAwait(false);
                }
                finally
                {
                    storageGate.Release();
                }
            }
            finally
            {
                gate.Release();
            }
        }

        async Task<long> CommitCaptureAsync()
        {
            var options = new SqliteConnectionStringBuilder
            {
                DataSource = fixture.DatabasePath,
                DefaultTimeout = new CameraAgentHostOptions().RawIngressSqliteBusyTimeoutSeconds
            };
            using var writerConnection = new SqliteConnection(options.ConnectionString);
            await writerConnection.OpenAsync(token).ConfigureAwait(false);
            var accepted = RetentionViewDefinition(scope, 2000, recent);
            await SeedCenteredRetentionCaptureAsync(writerConnection, root, accepted, recent, false, token)
                .ConfigureAwait(false);

            var payload = lastPaths!.PayloadAbsolutePath;
            Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
            await File.WriteAllBytesAsync(payload, [1], token).ConfigureAwait(false);
            return await ScalarAsync(writerConnection, "SELECT COUNT(*) FROM processing_centered_view_bases;").ConfigureAwait(false);
        }
    }

    private sealed class StartWriterAfterCenteredPruneBatch(Func<Task<long>> startWriter) : ICaptureProcessingFaultInjector
    {
        internal Task<long>? Writer { get; private set; }

        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (point == CaptureProcessingFaultPoint.AfterCenteredViewPruneBatchCommit)
                Writer ??= startWriter();
        }
    }

    [TestMethod]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The dropped index names are fixed schema constants.")]
    public async Task CenteredViewRetentionIndexesAdoptThePreviousShapeAtomicallyAndSurviveRawRestart()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        var token = TestContext.CancellationToken;
        using var store = new SqliteCaptureProcessingStore(fixture.Options);
        var index = new SqliteCenteredViews(store, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        var view = await index.RegisterBaseAsync(ViewDefinition(ViewScope(), 101), [], token).ConfigureAwait(false);
        using var connection = await fixture.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        foreach (var name in CenteredViewRetentionSchema.IndexNames)
        {
            command.CommandText = $"DROP INDEX {name};";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        using (var failed = new SqliteCaptureProcessingStore(fixture.Options,
            faultInjector: new ThrowOnceFaultInjector(CaptureProcessingFaultPoint.BeforeSchemaCommit)))
            await Assert.ThrowsExactlyAsync<InjectedProcessingSchemaException>(() => failed.InitializeAsync(token).AsTask())
                .ConfigureAwait(false);
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'ix_processing_centered_view_intervals_end';")
            .ConfigureAwait(false));

        using var reopened = new SqliteCaptureProcessingStore(fixture.Options);
        var readback = new SqliteCenteredViews(reopened, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        Assert.IsNotNull(await readback.ReadRevisionAsync(view.Base.BaseId, view.Revision.RevisionId, token).ConfigureAwait(false));
        await new SqliteRawCaptureJournal(fixture.DatabasePath, 1).InitializeAsync(token).ConfigureAwait(false);
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name = $name;";
        var parameter = command.Parameters.AddWithValue("$name", "");
        foreach (var name in CenteredViewRetentionSchema.IndexNames)
        {
            parameter.Value = name;
            Assert.AreEqual(1L, await command.ExecuteScalarAsync(token).ConfigureAwait(false));
        }
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

    private static async Task SeedWindowCheckpointAsync(SqliteConnection connection, CenteredView view,
        bool pending, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO processing_centered_windows (window_sha256, deadline_utc_ticks, registration_deadline_utc_ticks,
                checkpoint_json, reference_capture_id, closure_json, terminal)
            VALUES ($window, 1, 2, X'7B7D', $capture, X'7B7D', 1);
            INSERT INTO processing_centered_view_window_work (window_sha256, live_base_id, retry_utc)
            VALUES ($window, $base, $retry);
            """;
        command.Parameters.AddWithValue("$window", view.Base.BaseId);
        command.Parameters.AddWithValue("$capture", view.Base.Definition.Reference.Capture.CaptureId.ToString("N"));
        command.Parameters.AddWithValue("$base", view.Base.BaseId);
        command.Parameters.AddWithValue("$retry", pending ? 1L : DBNull.Value);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task SeedCenteredRetentionCaptureAsync(SqliteConnection connection, string root,
        CenteredViewBaseDefinition definition, DateTimeOffset capturedUtc, bool retainedPayload, CancellationToken token,
        bool rawHold = false)
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
                $artifact || $artifact, 1, $payload, $sidecar, X'7B7D', $utc, $utc, $utc, 'committed', $hold);
            """;
        command.Parameters.AddWithValue("$capture", capture.CaptureId.ToString("N"));
        command.Parameters.AddWithValue("$artifact", artifactId.ToString("N"));
        command.Parameters.AddWithValue("$agent", capture.AgentId);
        command.Parameters.AddWithValue("$sequence", capture.CaptureSequence);
        command.Parameters.AddWithValue("$payload", paths.PayloadRelativePath);
        command.Parameters.AddWithValue("$sidecar", paths.SidecarRelativePath);
        command.Parameters.AddWithValue("$utc", capturedUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$hold", rawHold ? 1 : 0);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        if (retainedPayload)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.PayloadAbsolutePath)!);
            await File.WriteAllBytesAsync(paths.PayloadAbsolutePath, new byte[1], token).ConfigureAwait(false);
        }
    }

    private sealed class InterruptAfterCenteredPruneBatch(CancellationTokenSource interruption) : ICaptureProcessingFaultInjector
    {
        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (point == CaptureProcessingFaultPoint.AfterCenteredViewPruneBatchCommit)
                interruption.Cancel();
        }
    }

    private sealed class CenteredRetentionLog : ILogger<CaptureProcessingPersistence>
    {
        internal List<Exception?> Warnings { get; } = [];
        internal List<string> Reports { get; } = [];
        internal List<(int Id, string Message)> WarningEvents { get; } = [];
        internal List<int> ReportIds { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(exception);
                WarningEvents.Add((eventId.Id, formatter(state, exception)));
            }
            if (logLevel == LogLevel.Information)
            {
                Reports.Add(formatter(state, exception));
                ReportIds.Add(eventId.Id);
            }
        }
    }
}
