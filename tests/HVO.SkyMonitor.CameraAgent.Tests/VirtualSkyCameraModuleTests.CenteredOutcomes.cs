using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    [TestMethod]
    public async Task CenteredLateAdmissionResolvesTheOriginalMissingCenterWithoutPublishing()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-late-center");
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
        var module = new VirtualSkyCameraModule(deviceClock, CenteredRuntimeCatalog(start), new ProjectedSceneStore());
        await using var camera = module.ConfigureAwait(false);
        try
        {
            string identity;
            Guid capture;
            string closureIdentity;
            DateTimeOffset cutoff;
            byte[] frozenBytes;
            using (var initial = CenteredRuntimeProvider(root, clock))
            {
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(initial, profile).ConfigureAwait(false);
                await module.InitializeAsync(active, token).ConfigureAwait(false);
                var ingress = initial.GetRequiredService<RawCaptureIngress>();
                var attempt = await CaptureCenteredRuntimeAttemptAsync(ingress, module, deviceClock, clock, active, start, 1, token)
                    .ConfigureAwait(false);
                (identity, cutoff) = await ReadOutcomeWindowAsync(root, attempt.Reservation.Pending.Key, token).ConfigureAwait(false);
                clock.UtcNow = cutoff.AddMilliseconds(1);
                await initial.GetRequiredService<CenteredWindowCoordinator>().CloseDueAsync(token).ConfigureAwait(false);
                var store = initial.GetRequiredService<SqliteCaptureProcessingStore>();
                var original = await store.ReadCenteredWindowDispositionAsync(identity, token).ConfigureAwait(false);
                Assert.IsNotNull(original);
                Assert.IsTrue(original.IsTerminal);
                Assert.AreEqual(cutoff, original.Closure.EvidenceCutoffUtc);
                Assert.AreEqual(CenteredWindowSlotState.MissingExposure, original.Closure.Center.State);
                closureIdentity = original.Closure.Identity;
                frozenBytes = await ReadUnpublishedClosureAsync(root, identity, token).ConfigureAwait(false);

                var receipt = await ingress.AcceptCenteredAsync(active, attempt.Submission, attempt.Reservation, token).ConfigureAwait(false);
                capture = receipt.Manifest.Descriptor.Capture.CaptureId;
                await ProcessCenteredRuntimeExposureAsync(initial, active, token).ConfigureAwait(false);
                await AssertFrozenDecisionAsync(initial).ConfigureAwait(false);
            }
            using var reopened = CenteredRuntimeProvider(root, clock);
            await reopened.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            await AssertFrozenDecisionAsync(reopened).ConfigureAwait(false);

            async Task AssertFrozenDecisionAsync(ServiceProvider provider)
            {
                await provider.GetRequiredService<CenteredWindowCoordinator>().CloseDueAsync(token).ConfigureAwait(false);
                await provider.GetRequiredService<CenteredWindowRegistrationWorker>().RegisterPendingAsync(token).ConfigureAwait(false);
                var reader = provider.GetRequiredService<ICenteredWindowReader>();
                var snapshot = await reader.ReadAsync(capture, token).ConfigureAwait(false);
                Assert.IsNotNull(snapshot);
                Assert.AreEqual(capture, snapshot.ReferenceCaptureId);
                Assert.AreEqual(identity, snapshot.WindowIdentity);
                Assert.AreEqual(closureIdentity, snapshot.ClosureIdentity);
                Assert.AreEqual(cutoff, snapshot.EvidenceCutoffUtc);
                Assert.IsTrue(snapshot.IsTerminal);
                Assert.AreEqual(CenteredWindowSlotState.MissingExposure, snapshot.Slots[2].State);
                Assert.IsNull(snapshot.Slots[2].Exposure);
                Assert.IsNull(snapshot.EvidenceArtifactId);
                Assert.IsEmpty(snapshot.ContributingCaptureIds);
                var byIdentity = await provider.GetRequiredService<SqliteCaptureProcessingStore>()
                    .ReadCenteredWindowDispositionAsync(identity, token).ConfigureAwait(false);
                Assert.IsNotNull(byIdentity);
                Assert.AreEqual(closureIdentity, byIdentity.Closure.Identity);
                CollectionAssert.AreEqual(byIdentity.Closure.Slots.Select(slot => slot.State).ToArray(),
                    snapshot.Slots.Select(slot => slot.State!.Value).ToArray());
                CollectionAssert.AreEqual(frozenBytes, await ReadUnpublishedClosureAsync(root, identity, token).ConfigureAwait(false));
                Assert.IsNull(await reader.ReadAsync(Guid.NewGuid(), token).ConfigureAwait(false));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CenteredShutterRefusalIsFrozenOnlyWhenCommittedByCutoff(bool afterCutoff)
    {
        const string reason = "schedule.start-window-closed";
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-shutter-refusal");
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
        var module = new VirtualSkyCameraModule(deviceClock, CenteredRuntimeCatalog(start), new ProjectedSceneStore());
        await using var camera = module.ConfigureAwait(false);
        try
        {
            string identity;
            Guid observer;
            DateTimeOffset cutoff;
            ExposureAttemptKey rejected;
            byte[] frozen;
            using (var initial = CenteredRuntimeProvider(root, clock))
            {
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(initial, profile).ConfigureAwait(false);
                await module.InitializeAsync(active, token).ConfigureAwait(false);
                var ingress = initial.GetRequiredService<RawCaptureIngress>();
                var first = await CaptureCenteredRuntimeAttemptAsync(ingress, module, deviceClock, clock, active, start, 1, token)
                    .ConfigureAwait(false);
                var receipt = await ingress.AcceptCenteredAsync(active, first.Submission, first.Reservation, token).ConfigureAwait(false);
                observer = receipt.Manifest.Descriptor.Capture.CaptureId;
                await ProcessCenteredRuntimeExposureAsync(initial, active, token).ConfigureAwait(false);
                var second = await CaptureCenteredRuntimeAttemptAsync(ingress, module, deviceClock, clock, active,
                    start.AddSeconds(2), 1, token).ConfigureAwait(false);
                rejected = second.Reservation.Pending.Key;
                (identity, cutoff) = await ReadOutcomeWindowAsync(root, rejected, token).ConfigureAwait(false);
                if (afterCutoff)
                    clock.UtcNow = cutoff.AddMilliseconds(1);
                await ingress.RefuseCenteredStartAsync(rejected, token).ConfigureAwait(false);

                using (var db = await OpenOutcomeJournalAsync(root, token).ConfigureAwait(false))
                using (var command = db.CreateCommand())
                {
                    command.CommandText = """
                        SELECT expired_ticks, expired_reason FROM processing_centered_attempt_calendar WHERE attempt_id = $attempt;
                        """;
                    command.Parameters.AddWithValue("$attempt", rejected.AttemptId.ToString("N"));
                    using var rows = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                    Assert.IsTrue(await rows.ReadAsync(token).ConfigureAwait(false));
                    Assert.AreEqual(clock.UtcNow.UtcTicks, rows.GetInt64(0));
                    Assert.AreEqual(reason, rows.GetString(1));
                }
                await Assert.ThrowsExactlyAsync<RawIngressConflictException>(() =>
                    ingress.AcceptCenteredAsync(active, second.Submission, second.Reservation, token)).ConfigureAwait(false);
                clock.UtcNow = cutoff.AddMilliseconds(1);
                await initial.GetRequiredService<CenteredWindowCoordinator>().CloseDueAsync(token).ConfigureAwait(false);
                frozen = await ReadUnpublishedClosureAsync(root, identity, token).ConfigureAwait(false);
            }
            using var reopened = CenteredRuntimeProvider(root, clock);
            await reopened.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var outcome = await reopened.GetRequiredService<SqliteCaptureProcessingStore>()
                .ReadCenteredWindowDispositionAsync(identity, token).ConfigureAwait(false);
            Assert.IsNotNull(outcome);
            Assert.IsTrue(outcome.IsTerminal);
            Assert.AreEqual(cutoff, outcome.Closure.EvidenceCutoffUtc);
            Assert.AreEqual(afterCutoff ? CenteredWindowSlotState.MissingExposure : CenteredWindowSlotState.FailedAttempt,
                outcome.Closure.Center.State);
            Assert.AreEqual(afterCutoff ? null : reason, outcome.Closure.Center.Reason);
            Assert.IsNull(outcome.Closure.Center.Input);
            if (!afterCutoff)
                Assert.AreEqual(rejected, outcome.Closure.Center.Attempt);
            var snapshot = await reopened.GetRequiredService<ICenteredWindowReader>().ReadAsync(observer, token).ConfigureAwait(false);
            Assert.IsNotNull(snapshot);
            Assert.AreEqual(outcome.Closure.Center.State, snapshot.Slots[3].State);
            Assert.AreEqual(outcome.Closure.Center.Reason, snapshot.Slots[3].Reason);
            CollectionAssert.AreEqual(frozen, await ReadUnpublishedClosureAsync(root, identity, token).ConfigureAwait(false));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<(string Identity, DateTimeOffset Deadline)> ReadOutcomeWindowAsync(string root,
        ExposureAttemptKey attempt, CancellationToken token)
    {
        using var db = await OpenOutcomeJournalAsync(root, token).ConfigureAwait(false);
        using var query = db.CreateCommand();
        query.CommandText = """
            SELECT window.window_sha256, window.deadline_utc_ticks
            FROM processing_centered_attempt_calendar calendar
            JOIN processing_centered_windows window ON window.window_sha256 = calendar.window_sha256
            WHERE calendar.attempt_id = $attempt;
            """;
        query.Parameters.AddWithValue("$attempt", attempt.AttemptId.ToString("N"));
        using var rows = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        Assert.IsTrue(await rows.ReadAsync(token).ConfigureAwait(false));
        return (rows.GetString(0), new(rows.GetInt64(1), TimeSpan.Zero));
    }

    private static async Task<byte[]> ReadUnpublishedClosureAsync(string root, string identity, CancellationToken token)
    {
        using var db = await OpenOutcomeJournalAsync(root, token).ConfigureAwait(false);
        using var query = db.CreateCommand();
        query.CommandText = """
            SELECT closure_json FROM processing_centered_windows
            WHERE window_sha256 = $window AND reference_capture_id IS NULL AND publication_json IS NULL AND terminal = 1;
            """;
        query.Parameters.AddWithValue("$window", identity);
        var result = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
        Assert.IsInstanceOfType<byte[]>(result);
        return (byte[])result;
    }

    private static async Task<SqliteConnection> OpenOutcomeJournalAsync(string root, CancellationToken token)
    {
        var db = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        await db.OpenAsync(token).ConfigureAwait(false);
        return db;
    }
}
