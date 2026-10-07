using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

/// <summary>
/// Durable edge work that outlives an upgrade. Standard work planned under a node plan the running code no longer
/// builds, or whose live execution expired, is abandoned rather than quarantined: the ordered lane and ingress keep
/// moving, the raw stays held until its upload completes, and a scene-required upload stops waiting for a scene that
/// will never be committed. Genuinely corrupt evidence still quarantines. Outcomes are compared by name so the same
/// file runs against the pre-fix head to show the hazard.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class EdgeUpgradeLaneTests
{
    internal const string SupersededPlan = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public async Task WorkPendingUnderAnEarlierPlanIsAbandonedAndItsRawStillUploads()
    {
        var root = CreateRoot("plan-superseded");
        try
        {
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            using var provider = CreateProvider(root, clock);
            var fixture = await StartAsync(provider, root).ConfigureAwait(false);
            var first = await fixture.AcceptAsync(0).ConfigureAwait(false);
            // Work accepted before the upgrade carries the plan the earlier build compiled.
            await ExecuteAsync(root, "UPDATE processing_execution_nodes SET plan_sha256 = $plan;",
                ("$plan", SupersededPlan)).ConfigureAwait(false);

            var abandoned = await fixture.ProcessStandardAsync().ConfigureAwait(false);
            Assert.AreEqual("Abandoned", abandoned.Handled.Outcome.ToString(), abandoned.Handled.Reason);
            Assert.AreEqual("plan-superseded", abandoned.Handled.Reason);
            Assert.AreEqual("Abandoned", abandoned.Acknowledged.ToString());
            Assert.AreEqual(("abandoned", "plan-superseded"),
                await ReadLaneAsync(root, first, "standard").ConfigureAwait(false));
            Assert.AreEqual(("Expired", "processing.plan-superseded"),
                await ReadExecutionAsync(root, first).ConfigureAwait(false));
            Assert.AreEqual(0L, await CountUnreleasedPinsAsync(root, first).ConfigureAwait(false));
            Assert.AreEqual(0L, (await fixture.Store.ReadOperationalStateAsync(CancellationToken.None)
                .ConfigureAwait(false)).TerminalCount, "Abandoned work is not a terminal processing failure.");
            Assert.IsTrue(await fixture.IsHeldAsync(first).ConfigureAwait(false),
                "The raw stays held until its upload completes.");

            var second = await fixture.AcceptAsync(1).ConfigureAwait(false);
            Assert.AreEqual(RawIngressAvailability.Accepting, provider.GetRequiredService<RawIngressState>().Snapshot.Availability);
            var processed = await fixture.ProcessStandardAsync().ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, processed.Handled.Outcome, processed.Handled.Reason);
            Assert.AreEqual(second, processed.CaptureId, "The ordered lane moves past the abandoned head.");

            await fixture.UploadAndAssertCompletedAsync(first).ConfigureAwait(false);
            Assert.IsFalse(await fixture.IsHeldAsync(first).ConfigureAwait(false), "The hold releases once the raw is uploaded.");
            Assert.IsNotNull(await fixture.Outbox.ReadAsync(root, fixture.Receipts[first].Manifest.IdempotencyKey,
                CancellationToken.None).ConfigureAwait(false), "The raw itself is queued for upload.");
            await fixture.UploadAndAssertCompletedAsync(second).ConfigureAwait(false);
            Assert.IsNotNull(await fixture.Persistence.FindCommittedSceneUploadAsync(
                fixture.Receipts[second].Manifest.Descriptor, CancellationToken.None).ConfigureAwait(false));
            Assert.IsFalse(await fixture.IsHeldAsync(second).ConfigureAwait(false));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    public async Task ExpiredLiveWorkIsAbandonedWithoutBlockingIngress()
    {
        var root = CreateRoot("processing-expired");
        try
        {
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            using var provider = CreateProvider(root, clock, liveMaximumQueueAgeSeconds: 10);
            var fixture = await StartAsync(provider, root).ConfigureAwait(false);
            var first = await fixture.AcceptAsync(0).ConfigureAwait(false);
            clock.Now = clock.Now.AddSeconds(11);

            Assert.IsNull(await fixture.Ingress.ClaimAsync(fixture.Standard, "expiry-test", fixture.Configuration,
                CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(("abandoned", "processing-expired"),
                await ReadLaneAsync(root, first, "standard").ConfigureAwait(false));
            Assert.AreEqual(("Expired", "processing.live-maximum-age"),
                await ReadExecutionAsync(root, first).ConfigureAwait(false));
            Assert.AreEqual(0L, await CountUnreleasedPinsAsync(root, first).ConfigureAwait(false));
            Assert.IsTrue(await fixture.IsHeldAsync(first).ConfigureAwait(false));

            var second = await fixture.AcceptAsync(1).ConfigureAwait(false);
            var processed = await fixture.ProcessStandardAsync().ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, processed.Handled.Outcome, processed.Handled.Reason);
            Assert.AreEqual(second, processed.CaptureId);
            await fixture.UploadAndAssertCompletedAsync(first).ConfigureAwait(false);
            Assert.IsFalse(await fixture.IsHeldAsync(first).ConfigureAwait(false));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    public async Task StartupAbandonsStandardWorkAnEarlierVersionQuarantinedForExpiry()
    {
        var root = CreateRoot("expired-conversion");
        try
        {
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            Guid first;
            Guid evidenceInvalid;
            RawCaptureReceipt firstReceipt;
            using (var provider = CreateProvider(root, clock, liveMaximumQueueAgeSeconds: 10))
            {
                var fixture = await StartAsync(provider, root).ConfigureAwait(false);
                first = await fixture.AcceptAsync(0).ConfigureAwait(false);
                evidenceInvalid = await fixture.AcceptAsync(1).ConfigureAwait(false);
                firstReceipt = fixture.Receipts[first];
                clock.Now = clock.Now.AddSeconds(11);
                Assert.IsNull(await fixture.Ingress.ClaimAsync(fixture.Standard, "expiry-test", fixture.Configuration,
                    CancellationToken.None).ConfigureAwait(false));
            }
            // The state an earlier version left behind: expired work quarantined, holding the lane and the raw.
            // A quarantine for genuinely invalid evidence must survive the conversion.
            await ExecuteAsync(root, """
                UPDATE capture_lane_work SET state = 'quarantined', failure_reason = 'processing-expired'
                WHERE lane_name = 'standard' AND raw_capture_row_id = (SELECT raw_capture_row_id FROM raw_captures WHERE capture_id = $first);
                UPDATE capture_lane_work SET state = 'quarantined', failure_reason = 'evidence-invalid'
                WHERE lane_name = 'standard' AND raw_capture_row_id = (SELECT raw_capture_row_id FROM raw_captures WHERE capture_id = $other);
                UPDATE raw_captures SET retention_hold = 1;
                """, ("$first", first.ToString("N")), ("$other", evidenceInvalid.ToString("N"))).ConfigureAwait(false);
            SqliteConnection.ClearAllPools();

            using (var restarted = CreateProvider(root, clock, liveMaximumQueueAgeSeconds: 10))
            {
                var fixture = await StartAsync(restarted, root).ConfigureAwait(false);
                fixture.Receipts[first] = firstReceipt;
                Assert.AreEqual(("abandoned", "processing-expired"),
                    await ReadLaneAsync(root, first, "standard").ConfigureAwait(false));
                Assert.AreEqual(("quarantined", "evidence-invalid"),
                    await ReadLaneAsync(root, evidenceInvalid, "standard").ConfigureAwait(false));
                Assert.IsTrue(await fixture.IsHeldAsync(first).ConfigureAwait(false), "The upload lane still holds the raw.");
                var upload = await fixture.Ingress.ClaimAsync(fixture.Upload, "conversion-test", fixture.Configuration,
                    CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(upload);
                Assert.AreEqual(first, upload.Context.RawCapture.Manifest.Descriptor.Capture.CaptureId);
                var uploaded = await fixture.UploadHandler.HandleAsync(upload.Context, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, uploaded.Outcome, uploaded.Reason);
                await fixture.Ingress.CompleteAsync(upload, CancellationToken.None).ConfigureAwait(false);
                Assert.IsFalse(await fixture.IsHeldAsync(first).ConfigureAwait(false));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    public async Task CorruptRawEvidenceStillQuarantinesAndRefusesCaptures()
    {
        var root = CreateRoot("evidence-corrupt");
        try
        {
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            using var provider = CreateProvider(root, clock);
            var fixture = await StartAsync(provider, root).ConfigureAwait(false);
            var first = await fixture.AcceptAsync(0).ConfigureAwait(false);
            // The committed sidecar no longer matches the journaled manifest.
            var sidecar = Path.ChangeExtension(fixture.Receipts[first].StoredFrame.AbsolutePath, ".json");
            var bytes = await File.ReadAllBytesAsync(sidecar).ConfigureAwait(false);
            await File.WriteAllBytesAsync(sidecar, [.. bytes, (byte)' ']).ConfigureAwait(false);

            var result = await fixture.ProcessStandardAsync().ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.TerminalFailure, result.Handled.Outcome, result.Handled.Reason);
            Assert.AreEqual(CaptureLaneHandlerOutcome.TerminalFailure, result.Acknowledged);
            Assert.AreEqual("quarantined", (await ReadLaneAsync(root, first, "standard").ConfigureAwait(false)).State);
            // Restore the sidecar so ingress initialization can inspect holds; the quarantine is already durable.
            await File.WriteAllBytesAsync(sidecar, bytes).ConfigureAwait(false);
            Assert.IsTrue(await fixture.IsHeldAsync(first).ConfigureAwait(false));
            await Assert.ThrowsExactlyAsync<CaptureLaneBackpressureException>(async () =>
                await fixture.AcceptAsync(1).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    public async Task SupersededPlanOverCorruptRawPayloadStillQuarantines()
    {
        var root = CreateRoot("plan-superseded-corrupt");
        try
        {
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            using var provider = CreateProvider(root, clock);
            var fixture = await StartAsync(provider, root).ConfigureAwait(false);
            var first = await fixture.AcceptAsync(0).ConfigureAwait(false);
            await ExecuteAsync(root, "UPDATE processing_execution_nodes SET plan_sha256 = $plan;",
                ("$plan", SupersededPlan)).ConfigureAwait(false);
            // A same-length flip leaves the sidecar and journal checks green, so only payload reconstruction sees it.
            var payloadPath = fixture.Receipts[first].StoredFrame.AbsolutePath;
            var bytes = await File.ReadAllBytesAsync(payloadPath).ConfigureAwait(false);
            var corrupt = bytes.ToArray();
            corrupt[0] ^= 0xFF;
            await File.WriteAllBytesAsync(payloadPath, corrupt).ConfigureAwait(false);

            var result = await fixture.ProcessStandardAsync().ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.TerminalFailure, result.Handled.Outcome, result.Handled.Reason);
            Assert.AreEqual(CaptureLaneHandlerOutcome.TerminalFailure, result.Acknowledged);
            Assert.AreEqual("quarantined", (await ReadLaneAsync(root, first, "standard").ConfigureAwait(false)).State);
            await File.WriteAllBytesAsync(payloadPath, bytes).ConfigureAwait(false);
            Assert.IsTrue(await fixture.IsHeldAsync(first).ConfigureAwait(false));
            await Assert.ThrowsExactlyAsync<CaptureLaneBackpressureException>(async () =>
                await fixture.AcceptAsync(1).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    public async Task SceneRequiredUploadWaitingForAbandonedWorkCompletesWithTheRawOnly()
    {
        var root = CreateRoot("scene-abandoned");
        try
        {
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            using var provider = CreateProvider(root, clock);
            var fixture = await StartAsync(provider, root).ConfigureAwait(false);
            var first = await fixture.AcceptAsync(0).ConfigureAwait(false);
            var upload = await fixture.Ingress.ClaimAsync(fixture.Upload, "raw-first", fixture.Configuration,
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(upload);
            var waiting = await fixture.UploadHandler.HandleAsync(upload.Context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("outbox-scene-pending", waiting.Reason);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Deferred,
                await fixture.Ingress.FailAsync(upload, waiting, CancellationToken.None).ConfigureAwait(false));

            await ExecuteAsync(root, "UPDATE processing_execution_nodes SET plan_sha256 = $plan;",
                ("$plan", SupersededPlan)).ConfigureAwait(false);
            var abandoned = await fixture.ProcessStandardAsync().ConfigureAwait(false);
            Assert.AreEqual("Abandoned", abandoned.Acknowledged.ToString(), abandoned.Handled.Reason);

            clock.Now = clock.Now.AddMinutes(1);
            await fixture.UploadAndAssertCompletedAsync(first).ConfigureAwait(false);
            Assert.IsFalse(await fixture.IsHeldAsync(first).ConfigureAwait(false));
            Assert.AreEqual(1L, (await fixture.Outbox.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false))
                .PendingCount, "Only the raw is queued; no scene was ever committed.");
        }
        finally
        {
            Cleanup(root);
        }
    }

    internal static async Task<UpgradeFixture> StartAsync(ServiceProvider provider, string root)
    {
        var ingress = provider.GetRequiredService<RawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var module = new VirtualSkyCameraModule(TimeProvider.System,
            provider.GetRequiredService<ICelestialCatalog>(), provider.GetRequiredService<IProjectedSceneStore>(),
            stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
        var configuration = CreateConfiguration();
        await module.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
        var lanes = provider.GetRequiredService<CaptureLanePolicy>().Definitions;
        return new UpgradeFixture(
            root,
            ingress,
            module,
            configuration,
            lanes.Single(static lane => lane.Name == "standard"),
            lanes.Single(static lane => lane.Name == "upload"),
            provider.GetRequiredService<StandardCaptureLaneHandler>(),
            provider.GetRequiredService<UploadCaptureLaneHandler>(),
            provider.GetRequiredService<IArtifactOutbox>(),
            provider.GetRequiredService<CaptureProcessingPersistence>(),
            provider.GetRequiredService<SqliteCaptureProcessingStore>());
    }

    internal sealed record UpgradeFixture(
        string Root,
        RawCaptureIngress Ingress,
        VirtualSkyCameraModule Module,
        CameraModuleConfig Configuration,
        CaptureLaneDefinition Standard,
        CaptureLaneDefinition Upload,
        StandardCaptureLaneHandler StandardHandler,
        UploadCaptureLaneHandler UploadHandler,
        IArtifactOutbox Outbox,
        CaptureProcessingPersistence Persistence,
        SqliteCaptureProcessingStore Store)
    {
        internal Dictionary<Guid, RawCaptureReceipt> Receipts { get; } = [];

        internal async Task<Guid> AcceptAsync(int index)
        {
            var request = new CaptureRequest(new DateTimeOffset(2025, 1, 15, 8, index, 0, TimeSpan.Zero),
                TimeSpan.FromSeconds(5), CaptureMode.Still, new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null));
            var capture = await Module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
            var receipt = await Ingress.AcceptAsync(Configuration, new CaptureLoopSubmission(request, capture,
                request.RequestedStartUtc, request.TargetInterval, TimeSpan.Zero), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            var captureId = receipt.Manifest.Descriptor.Capture.CaptureId;
            Receipts[captureId] = receipt;
            return captureId;
        }

        internal async Task<(Guid CaptureId, CaptureLaneHandlerResult Handled, CaptureLaneHandlerOutcome Acknowledged)>
            ProcessStandardAsync()
        {
            var lease = await Ingress.ClaimAsync(Standard, "upgrade-test", Configuration, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(lease);
            var handled = await StandardHandler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
            if (handled.Outcome == CaptureLaneHandlerOutcome.Completed)
            {
                await Ingress.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
                return (lease.Context.RawCapture.Manifest.Descriptor.Capture.CaptureId, handled, handled.Outcome);
            }
            var acknowledged = await Ingress.FailAsync(lease, handled, CancellationToken.None).ConfigureAwait(false);
            return (lease.Context.RawCapture.Manifest.Descriptor.Capture.CaptureId, handled, acknowledged);
        }

        internal async Task UploadAndAssertCompletedAsync(Guid captureId)
        {
            var lease = await Ingress.ClaimAsync(Upload, "upgrade-upload", Configuration, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(lease, "The upload row is claimable.");
            Assert.AreEqual(captureId, lease.Context.RawCapture.Manifest.Descriptor.Capture.CaptureId);
            var uploaded = await UploadHandler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, uploaded.Outcome, uploaded.Reason);
            await Ingress.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(("completed", (string?)null), await ReadLaneAsync(Root, captureId, "upload").ConfigureAwait(false));
        }

        internal async Task<bool> IsHeldAsync(Guid captureId)
        {
            var artifactId = Receipts[captureId].Manifest.Descriptor.Artifact.ArtifactId;
            return (await Ingress.GetRetentionHoldsAsync(Root, CancellationToken.None).ConfigureAwait(false))
                .Any(hold => hold.ArtifactId == artifactId);
        }
    }

    internal static async Task<(string State, string? Reason)> ReadLaneAsync(string root, Guid captureId, string lane)
    {
        using var connection = await OpenAsync(root).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT work.state, work.failure_reason FROM capture_lane_work work
            JOIN raw_captures raw ON raw.raw_capture_row_id = work.raw_capture_row_id
            WHERE raw.capture_id = $capture AND work.lane_name = $lane;
            """;
        command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
        command.Parameters.AddWithValue("$lane", lane);
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        return (reader.GetString(0), await reader.IsDBNullAsync(1).ConfigureAwait(false) ? null : reader.GetString(1));
    }

    private static async Task<(string Status, string? Reason)> ReadExecutionAsync(string root, Guid captureId)
    {
        using var connection = await OpenAsync(root).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, failure_reason FROM processing_executions WHERE capture_id = $capture;";
        command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        return (reader.GetString(0), await reader.IsDBNullAsync(1).ConfigureAwait(false) ? null : reader.GetString(1));
    }

    private static async Task<long> CountUnreleasedPinsAsync(string root, Guid captureId)
    {
        using var connection = await OpenAsync(root).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM processing_execution_input_pins pin
            JOIN processing_executions execution ON execution.execution_id = pin.execution_id
            WHERE execution.capture_id = $capture AND pin.released_flag = 0;
            """;
        command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
        return Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Fixed test SQL with bound parameters.")]
    internal static async Task ExecuteAsync(string root, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = await OpenAsync(root).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        Assert.IsGreaterThan(0, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
    }

    private static async Task<SqliteConnection> OpenAsync(string root)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "journal", "raw-ingress.db"),
            Pooling = false
        }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    internal static string CreateRoot(string name)
        => Path.Combine(Path.GetTempPath(), "hvo-edge-upgrade", name, Guid.NewGuid().ToString("N"));

    internal static void Cleanup(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    internal static ServiceProvider CreateProvider(string root, TimeProvider clock, int? liveMaximumQueueAgeSeconds = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["CameraAgent:RawIngressRoot"] = root,
            ["CameraAgent:RawIngressReserveBytes"] = "0",
            ["CameraAgent:AgentId"] = "edge-upgrade-agent",
            ["CameraAgent:CentralIntegration:Mode"] = "Enabled",
            ["CameraAgent:CaptureDistribution:UploadEnabled"] = "true"
        };
        if (liveMaximumQueueAgeSeconds is { } maximumAge)
        {
            values["CameraAgent:ProcessingGraphs:LiveMaximumQueueAgeSeconds"] =
                maximumAge.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(clock);
        services.AddSingleton<ICelestialCatalog>(new InMemoryCelestialCatalog([]));
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return services.BuildServiceProvider();
    }

    internal static CameraModuleConfig CreateConfiguration()
        => new(
            new ObservatoryLocation(35.347, -113.878, 0, "America/Phoenix"),
            new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(new
            { seed = 63, maximumResults = 1, shotNoiseEnabled = false })),
            new CameraRigConfig(
                new SensorProfile("EdgeUpgradeFixture", 64, 48, 5.86, SensorColorMode.Mono,
                    CameraPixelFormat.Mono16, SensorResponseMode.Monochrome, SensorRecipeVersion: "edge-upgrade-v1"),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye, 32, 24, 23,
                    CalibrationVersion: "edge-upgrade-optics-v1"),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            new CapturePipelineConfig([new("ProjectedScene", "scene", DependsOn: ["$raw"])],
                CapturePipelineSchemaVersions.ExplicitV2, CapturePipelineDependencyPolicy.RejectEnabledDependent),
            AgentId: "edge-upgrade-agent");

    internal sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
