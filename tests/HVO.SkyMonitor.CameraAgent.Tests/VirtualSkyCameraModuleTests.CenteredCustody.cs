using System.Diagnostics.Metrics;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SQLitePCL;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    [TestMethod]
    public async Task CenteredCustody_FullSensorOverlapPreservesOrdinaryMeasurementAndDelayedRegistration()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-full-custody");
        var token = CancellationToken.None;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        var interval = TimeSpan.FromSeconds(2);
        var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.BayerRggb16);
        var sensor = CreateConfig(CameraPixelFormat.BayerRggb16, 3096, 2080);
        profile = profile with
        {
            Rig = profile.Rig with
            {
                Sensor = sensor.Rig.Sensor,
                Optics = sensor.Rig.Optics,
                Pipeline = profile.Rig.Pipeline with
                {
                    CadenceMode = CaptureCadenceMode.MinimumStartInterval,
                    CaptureInterval = interval
                }
            }
        };
        try
        {
            using var provider = CenteredRuntimeProvider(root, clock);
            var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            var module = new VirtualSkyCameraModule(deviceClock, CenteredRuntimeCatalog(start), new ProjectedSceneStore(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var camera = module.ConfigureAwait(false);
            await module.InitializeAsync(active, token).ConfigureAwait(false);
            var coordinator = provider.GetRequiredService<CenteredWindowCoordinator>();
            var registration = provider.GetRequiredService<CenteredWindowRegistrationWorker>();
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            using var connection = await OpenCenteredCustodyJournalAsync(root, token).ConfigureAwait(false);
            var exposures = new List<ExposureKey>();
            string? window103 = null;
            for (var index = 0; index < 8; index++)
            {
                var attempt = await CaptureCenteredRuntimeAttemptAsync(ingress, module, deviceClock, clock,
                    active, start + index * interval, 1, token).ConfigureAwait(false);
                var receipt = await ingress.AcceptCenteredAsync(active, attempt.Submission, attempt.Reservation, token)
                    .ConfigureAwait(false);
                Assert.AreEqual(12_879_360L, receipt.Manifest.Descriptor.Layout.ByteLength);
                var key = attempt.Reservation.Pending.Key;
                exposures.Add(new(key.Camera, key.RunId, receipt.Manifest.Descriptor.Capture.CaptureId));
                await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);

                // The cadence's measurement competes for custody before the previous batch can release it.
                await registration.RegisterPendingAsync(token).ConfigureAwait(false);
                await coordinator.CloseReadyAsync(token).ConfigureAwait(false);
                if (index == 4)
                {
                    var pending = await store.ReadCenteredWindowByReferenceAsync(exposures[2].CaptureId, token).ConfigureAwait(false);
                    Assert.IsNotNull(pending);
                    Assert.IsTrue(pending.Closure.CanRegister);
                    Assert.IsNull(pending.Publication);
                    CollectionAssert.AreEqual(exposures.Select(item => item.CaptureId).ToArray(),
                        pending.Closure.Slots.Select(slot => slot.Input!.Key.CaptureId).ToArray());
                    window103 = pending.Closure.Schedule.Identity;
                    var owners = await ReadCenteredCustodyOwnersAsync(connection, exposures[4].CaptureId, token).ConfigureAwait(false);
                    Assert.Contains(window103, owners);
                    Assert.HasCount(3, owners);
                }
                if (index == 5)
                {
                    var settled = await store.ReadCenteredWindowByReferenceAsync(exposures[2].CaptureId, token).ConfigureAwait(false);
                    Assert.IsNotNull(settled?.Publication, settled?.FailureReason);
                    Assert.IsTrue(settled.IsTerminal);
                    CollectionAssert.AreEquivalent(settled.Publication.RegistrationArtifactIds.ToArray(),
                        (await ReadCenteredRegistrationArtifactIdsAsync(root, exposures[2].CaptureId, token).ConfigureAwait(false)).ToArray());
                    var owners = await ReadCenteredCustodyOwnersAsync(connection, exposures[4].CaptureId, token).ConfigureAwait(false);
                    Assert.DoesNotContain(window103!, owners);
                    foreach (var survivor in exposures.Skip(3).Take(2))
                    {
                        var surviving = await store.ReadCenteredWindowStateAsync(survivor.CaptureId, token).ConfigureAwait(false);
                        Assert.IsNotNull(surviving?.Checkpoint);
                        Assert.Contains(surviving.Checkpoint.Schedule.Identity, owners);
                    }
                }
            }

            var products = provider.GetRequiredService<IExposureProductReader>();
            var published = await products.ReadProductsAfterAsync(exposures[0].Camera, 0, 128, token).ConfigureAwait(false);
            CollectionAssert.AreEquivalent(exposures.ToArray(), published.Where(product =>
                product.Slot.SchemaVersion == ExposureMeasurementProducts.SchemaVersion && product.Product is not null)
                .Select(product => product.Exposure).ToArray());
            var linear105 = await products.ReadLinearAsync(exposures[4], token).ConfigureAwait(false);
            Assert.IsNotNull(linear105?.Product);
            var custody = provider.GetRequiredService<CenteredExposureInputCustody>();
            var lease = await custody.OpenLinearAsync(linear105.Product, interval, token).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            await using var retained = lease.ConfigureAwait(false);
            Assert.AreEqual(linear105.Product.PayloadSha256, PayloadChecksum.ComputeSha256(lease.Input.Linear.Payload.Span));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CenteredCustody_ReportsBudgetRefusalAndRetriesBeforeCutoff()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-custody-retry");
        var token = CancellationToken.None;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
        using var metrics = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name == CenteredWindowRuntimeJournal.HoldRefusalMetricName)
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        long refusals = 0;
        metrics.SetMeasurementEventCallback<long>((_, count, _, _) => Interlocked.Add(ref refusals, count));
        metrics.Start();
        try
        {
            using var provider = CenteredRuntimeProvider(root, clock);
            var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            var module = new VirtualSkyCameraModule(deviceClock, CenteredRuntimeCatalog(start), new ProjectedSceneStore(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var camera = module.ConfigureAwait(false);
            await module.InitializeAsync(active, token).ConfigureAwait(false);
            var attempt = await CaptureCenteredRuntimeAttemptAsync(ingress, module, deviceClock, clock, active, start, 1, token)
                .ConfigureAwait(false);
            var rawBytes = attempt.Submission.Result.Frame!.Layout!.ByteLength;
            using var connection = await OpenCenteredCustodyJournalAsync(root, token).ConfigureAwait(false);
            using var fill = connection.CreateCommand();
            fill.CommandText = """
                INSERT INTO centered_exposure_holds(owner_id, capture_id, output_identity, bytes, expires_unix_ms)
                VALUES ('budget-fixture-owner', 'budget-fixture-capture', 'budget-fixture-output', $bytes, $expires);
                """;
            fill.Parameters.AddWithValue("$bytes", (512L << 20) - (3 * rawBytes +
                SqliteCaptureProcessingStore.CenteredWindowMetadataReserveBytes) + 1);
            fill.Parameters.AddWithValue("$expires", start.AddMinutes(1).ToUnixTimeMilliseconds());
            await fill.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            var receipt = await ingress.AcceptCenteredAsync(active, attempt.Submission, attempt.Reservation, token).ConfigureAwait(false);
            var capture = receipt.Manifest.Descriptor.Capture.CaptureId;
            Assert.IsEmpty(await ReadCenteredCustodyOwnersAsync(connection, capture, token).ConfigureAwait(false));
            Assert.IsGreaterThan(0L, Interlocked.Read(ref refusals));

            // Free exactly one byte, leaving the shared reservation at the original limit.
            fill.CommandText = "UPDATE centered_exposure_holds SET bytes = bytes - 1 WHERE owner_id = 'budget-fixture-owner';";
            await fill.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            Assert.IsTrue(await store.RecoverUnobservedCenteredEvidenceAsync(token).ConfigureAwait(false));
            Assert.HasCount(1, await ReadCenteredCustodyOwnersAsync(connection, capture, token).ConfigureAwait(false));
            Assert.IsFalse(await store.RecoverUnobservedCenteredEvidenceAsync(token).ConfigureAwait(false));

            // Standard measurement opens and releases an independent linear lease at the full budget.
            await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);
            Assert.HasCount(1, await ReadCenteredCustodyOwnersAsync(connection, capture, token).ConfigureAwait(false));
            Assert.AreEqual(1L, await ReadCenteredTestHoldCountAsync(root, capture, token).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CenteredCustody_DispatchAndAdmissionKeepTheirIndexedObserverMembershipOnLongRuns()
    {
        var shorter = await ObserveCenteredCustodyWorkAsync(10_000).ConfigureAwait(false);
        var longer = await ObserveCenteredCustodyWorkAsync(250_000).ConfigureAwait(false);
        TestContext.WriteLine($"SQLite VM work, rounded up to 100 steps: dispatch {shorter.Dispatch}/{longer.Dispatch}, " +
            $"admission {shorter.Admission}/{longer.Admission}.");
        Assert.IsLessThanOrEqualTo(shorter.Dispatch + 100, longer.Dispatch);
        Assert.IsLessThanOrEqualTo(shorter.Admission + 100, longer.Admission);
    }

    private static async Task<(int Dispatch, int Admission)> ObserveCenteredCustodyWorkAsync(int history)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-custody-range");
        var token = CancellationToken.None;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        try
        {
            using var provider = CenteredRuntimeProvider(root, clock);
            var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
            var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            var module = new VirtualSkyCameraModule(deviceClock, CenteredRuntimeCatalog(start), new ProjectedSceneStore(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var camera = module.ConfigureAwait(false);
            await module.InitializeAsync(active, token).ConfigureAwait(false);
            var captures = new List<Guid>();
            Guid run = default;
            for (var slot = 1; slot <= 3; slot++)
            {
                var attempt = await CaptureCenteredRuntimeAttemptAsync(ingress, module, deviceClock, clock, active,
                    start.AddSeconds(2 * (slot - 1)), 1, token).ConfigureAwait(false);
                run = attempt.Reservation.Pending.Key.RunId;
                var receipt = await ingress.AcceptCenteredAsync(active, attempt.Submission, attempt.Reservation, token).ConfigureAwait(false);
                captures.Add(receipt.Manifest.Descriptor.Capture.CaptureId);
            }

            using var connection = await OpenCenteredCustodyJournalAsync(root, token).ConfigureAwait(false);
            using var seed = connection.CreateCommand();
            seed.CommandText = """
                UPDATE processing_centered_attempt_calendar SET slot = slot + $history WHERE run_id = $run;
                WITH RECURSIVE slots(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM slots WHERE n < $history)
                INSERT INTO processing_centered_attempt_calendar(attempt_id, run_id, slot, start_ticks, interval_ticks,
                    integration_ticks, cadence, dispatched_ticks)
                SELECT printf('history-%d', n), $run, n, 1, 1, 0, 'history', 1 FROM slots;
                """;
            seed.Parameters.AddWithValue("$history", history);
            seed.Parameters.AddWithValue("$run", run.ToString("N"));
            await seed.ExecuteNonQueryAsync(token).ConfigureAwait(false);

            var fourth = await CaptureCenteredRuntimeAttemptAsync(ingress, module, deviceClock, clock, active,
                start.AddSeconds(6), 1, token).ConfigureAwait(false);
            var changed = fourth.Reservation.Pending.Key;
            Assert.AreEqual(run, changed.RunId);
            using var windowsQuery = connection.CreateCommand();
            windowsQuery.CommandText = """
                SELECT window_sha256 FROM processing_centered_attempt_calendar
                WHERE run_id = $run AND slot > $history ORDER BY slot;
                """;
            windowsQuery.Parameters.AddWithValue("$history", history);
            windowsQuery.Parameters.AddWithValue("$run", run.ToString("N"));
            var windows = new List<string>();
            using (var rows = await windowsQuery.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                while (await rows.ReadAsync(token).ConfigureAwait(false))
                    windows.Add(rows.GetString(0));
            }
            Assert.HasCount(4, windows);

            var dispatch = await MeasureCenteredHoldLookupAsync(connection, changed, clock.UtcNow, token).ConfigureAwait(false);
            (string, Guid)[] expectedDispatch = [(windows[3], captures[1]), (windows[3], captures[2])];
            CollectionAssert.AreEqual(expectedDispatch, dispatch.Pairs.ToArray());
            foreach (var (window, capture) in expectedDispatch)
                Assert.Contains(window, await ReadCenteredCustodyOwnersAsync(connection, capture, token).ConfigureAwait(false));

            var accepted = await ingress.AcceptCenteredAsync(active, fourth.Submission, fourth.Reservation, token).ConfigureAwait(false);
            var capture4 = accepted.Manifest.Descriptor.Capture.CaptureId;
            var admission = await MeasureCenteredHoldLookupAsync(connection, changed, clock.UtcNow, token).ConfigureAwait(false);
            (string, Guid)[] expectedAdmission =
                [(windows[1], capture4), (windows[2], capture4), .. expectedDispatch, (windows[3], capture4)];
            CollectionAssert.AreEqual(expectedAdmission, admission.Pairs.ToArray());
            CollectionAssert.AreEquivalent(new[] { windows[1], windows[2], windows[3] },
                await ReadCenteredCustodyOwnersAsync(connection, capture4, token).ConfigureAwait(false));
            return (dispatch.Work, admission.Work);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(List<(string, Guid)> Pairs, int Work)> MeasureCenteredHoldLookupAsync(
        SqliteConnection connection, ExposureAttemptKey changed, DateTimeOffset now, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + CenteredWindowRuntimeJournal.RefreshHoldsSql;
        command.Parameters.AddWithValue("$attempt", changed.AttemptId.ToString("N"));
        command.Parameters.AddWithValue("$now", now.UtcTicks);
        var plan = new List<string>();
        using (var rows = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            while (await rows.ReadAsync(token).ConfigureAwait(false))
                plan.Add(rows.GetString(3));
        }
        Assert.IsTrue(plan.Any(detail => detail.Contains("SEARCH observer", StringComparison.Ordinal) &&
            detail.Contains("(run_id=? AND slot>? AND slot<?)", StringComparison.Ordinal)), string.Join(Environment.NewLine, plan));

        const int instructionsPerSample = 100;
        var samples = 0;
        var pairs = new List<(string, Guid)>();
        raw.sqlite3_progress_handler(connection.Handle, instructionsPerSample, _ => { samples++; return 0; }, null);
        try
        {
            command.CommandText = CenteredWindowRuntimeJournal.RefreshHoldsSql;
            using var rows = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await rows.ReadAsync(token).ConfigureAwait(false))
                pairs.Add((rows.GetString(0), Guid.ParseExact(rows.GetString(1), "N")));
        }
        finally
        {
            raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
        }
        var work = (samples + 1) * instructionsPerSample;
        Assert.IsLessThanOrEqualTo(2_000, work);
        return (pairs, work);
    }

    private static async Task<SqliteConnection> OpenCenteredCustodyJournalAsync(string root, CancellationToken token)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        try
        {
            await connection.OpenAsync(token).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static async Task<string[]> ReadCenteredCustodyOwnersAsync(
        SqliteConnection connection, Guid capture, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT owner.window_sha256
            FROM centered_exposure_holds hold
            JOIN processing_centered_window_holds owner ON owner.owner_id = hold.owner_id
            WHERE hold.capture_id = $capture;
            """;
        command.Parameters.AddWithValue("$capture", capture.ToString("N"));
        var owners = new List<string>();
        using var rows = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await rows.ReadAsync(token).ConfigureAwait(false))
            owners.Add(rows.GetString(0));
        return owners.ToArray();
    }
}
