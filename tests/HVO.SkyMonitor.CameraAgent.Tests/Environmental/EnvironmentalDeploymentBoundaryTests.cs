using System.Collections.Concurrent;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Unit")]
public sealed class EnvironmentalDeploymentBoundaryTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan HarnessDeadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StartupSourcePeriod = TimeSpan.FromMinutes(20);

    [TestMethod]
    [DataRow(true, true)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public async Task ProtectedLocationStartup_SettlesTheOriginalDueSlot(bool readyBeforePoll, bool effectiveAtDue)
    {
        var fixture = new Fixture(Epoch.AddSeconds(-1), Location(Epoch.AddDays(-1)), protectedStartup: true,
            useExplicitEffectiveFrom: effectiveAtDue);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.LoadProvisionalLocationAsync().ConfigureAwait(false);
        if (readyBeforePoll) await fixture.ReconcileLocationAsync().ConfigureAwait(false);

        await fixture.StartAndFireFirstPollAsync().ConfigureAwait(false);
        if (!readyBeforePoll)
        {
            await fixture.WaitForLocationHoldAsync().ConfigureAwait(false);
            await AssertWaitingForLocationAsync(fixture).ConfigureAwait(false);
            fixture.Clock.AdvanceTo(Epoch.AddSeconds(1));
            await fixture.ReconcileLocationAsync().ConfigureAwait(false);
        }

        var active = fixture.ProtectedLocation!.Active;
        Assert.IsNotNull(active);
        var next = Epoch + StartupSourcePeriod;
        await fixture.Clock.WaitForTimerAsync(next).WaitAsync(HarnessDeadline).ConfigureAwait(false);
        var attempts = await fixture.ReadAttemptsAsync().ConfigureAwait(false);
        Assert.HasCount(1, attempts, "Scrub completion must settle the original due slot.");
        var initial = attempts.Single();
        Assert.IsNull(await fixture.ReadLocationHeldPollAsync().ConfigureAwait(false));
        Assert.AreEqual(next, (await fixture.ReadStatesAsync().ConfigureAwait(false)).Single().NextPollUtc);
        if (effectiveAtDue)
        {
            Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced, initial.Disposition);
            var fact = fixture.Publisher.Facts.Single();
            Assert.AreEqual(Epoch, fact.ObservedAtUtc);
            Assert.IsTrue(active.IsEffectiveAt(fact.ObservedAtUtc));
        }
        else
        {
            Assert.AreEqual(EnvironmentalAcquisitionDisposition.Missing, initial.Disposition);
            Assert.AreEqual("deployment-location-not-effective", initial.Reason);
            Assert.IsNull(initial.ObservationId);
            Assert.IsEmpty(fixture.Publisher.Facts);
            Assert.AreEqual(0L, (await fixture.ReadSnapshotAsync().ConfigureAwait(false)).StoredCount);
        }

        fixture.Clock.AdvanceTo(next);
        await fixture.Clock.WaitForTimerAsync(next + StartupSourcePeriod).WaitAsync(HarnessDeadline).ConfigureAwait(false);
        var latest = (await fixture.ReadStatesAsync().ConfigureAwait(false)).Single();
        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced, latest.LastDisposition);
        Assert.AreEqual(next, latest.LastObservedUtc);
        Assert.AreEqual(next + StartupSourcePeriod, latest.NextPollUtc);
        Assert.HasCount(2, await fixture.ReadAttemptsAsync().ConfigureAwait(false));
        Assert.AreEqual(effectiveAtDue ? 2L : 1L, (await fixture.ReadSnapshotAsync().ConfigureAwait(false)).StoredCount);
        Assert.IsFalse(fixture.Logger.Events.Any(static entry => entry.Id == 2522));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProtectedLocationStartup_ShutdownCancelsAWaitingPoll(bool corruptHistory)
    {
        var fixture = new Fixture(Epoch.AddSeconds(-1), Location(Epoch.AddDays(-1)), protectedStartup: true);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.LoadProvisionalLocationAsync().ConfigureAwait(false);
        await fixture.StartAndFireFirstPollAsync().ConfigureAwait(false);
        await fixture.WaitForLocationHoldAsync().ConfigureAwait(false);
        await AssertWaitingForLocationAsync(fixture).ConfigureAwait(false);
        if (corruptHistory)
        {
            var path = Path.Combine(fixture.Root, ".location", "deployment-location.v1.protected");
            await File.WriteAllTextAsync(path, "invalid protected history").ConfigureAwait(false);
            await fixture.ReconcileLocationAsync().ConfigureAwait(false);
            Assert.IsTrue(fixture.RetainedIntegrityRecoveryRequired);
            Assert.AreEqual("invalid protected history", await File.ReadAllTextAsync(path).ConfigureAwait(false));
        }

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Coordinator.AcquireSourceAsync(
            "source-0", EnvironmentalAcquisitionTrigger.BeforeCapture, Epoch, 1, Guid.NewGuid(),
            CancellationToken.None).AsTask().WaitAsync(HarnessDeadline)).ConfigureAwait(false);
        Assert.AreEqual("Deployment location is not initialized.", error.Message);
        await fixture.StopAsync().WaitAsync(HarnessDeadline).ConfigureAwait(false);
        await AssertWaitingForLocationAsync(fixture).ConfigureAwait(false);
        Assert.IsFalse(fixture.Logger.Events.Any(static entry => entry.Id == 2522));
    }

    [TestMethod]
    [DataRow(true, 1, 0, DisplayName = "Healthy stop before first due retains ordinary cadence")]
    [DataRow(false, 1, 0, DisplayName = "Overdue held slot is settled on restart")]
    [DataRow(false, 2, 0, DisplayName = "Two starts before recovery preserve the first held slot")]
    [DataRow(false, 1, 5_000, DisplayName = "Held due time retains sub-millisecond precision")]
    public async Task ProtectedLocationStartup_RestartSeparatesHeldAndOrdinarySchedule(
        bool readyBeforeStop, int restartWindows, int epochOffsetTicks)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-environmental-restart-{Guid.NewGuid():N}");
        var location = Location(Epoch.AddDays(-1));
        var due = Epoch.AddTicks(epochOffsetTicks);
        try
        {
            var initial = new Fixture(due.AddSeconds(-1), location, root: root, protectedStartup: true, startupEpoch: due);
            await using (initial.ConfigureAwait(false))
            {
                await initial.LoadProvisionalLocationAsync().ConfigureAwait(false);
                if (readyBeforeStop)
                {
                    await initial.ReconcileLocationAsync().ConfigureAwait(false);
                    await initial.StartAndWaitForPollAsync(due).ConfigureAwait(false);
                    Assert.IsNull(await initial.ReadLocationHeldPollAsync().ConfigureAwait(false));
                    Assert.IsEmpty(await initial.ReadAttemptsAsync().ConfigureAwait(false));
                }
                else
                {
                    await initial.StartAndFireFirstPollAsync().ConfigureAwait(false);
                    await initial.WaitForLocationHoldAsync().ConfigureAwait(false);
                    await AssertWaitingForLocationAsync(initial).ConfigureAwait(false);
                }
            }

            for (var window = 1; window < restartWindows; window++)
            {
                var stillPending = new Fixture((due + StartupSourcePeriod * window).AddSeconds(1), location, root: root,
                    protectedStartup: true, startupEpoch: due);
                await using (stillPending.ConfigureAwait(false))
                {
                    await stillPending.LoadProvisionalLocationAsync().ConfigureAwait(false);
                    await stillPending.StartAndWaitForScheduleWriteAsync().ConfigureAwait(false);
                    await AssertWaitingForLocationAsync(stillPending).ConfigureAwait(false);
                }
            }

            var currentSlot = due + StartupSourcePeriod * restartWindows;
            var reopened = new Fixture(currentSlot.AddSeconds(1), location, root: root, protectedStartup: true,
                startupEpoch: due);
            await using (reopened.ConfigureAwait(false))
            {
                await reopened.LoadLocationAsync(expectProvisional: !readyBeforeStop).ConfigureAwait(false);
                if (!readyBeforeStop) await reopened.ReconcileLocationAsync().ConfigureAwait(false);
                var next = currentSlot + StartupSourcePeriod;
                await reopened.StartAndWaitForPollAsync(next).ConfigureAwait(false);

                var expected = readyBeforeStop ? currentSlot : due;
                var state = (await reopened.ReadStatesAsync().ConfigureAwait(false)).Single();
                Assert.AreEqual(expected, reopened.Publisher.Facts.Single().ObservedAtUtc);
                Assert.AreEqual(JournalTime(expected), state.LastObservedUtc);
                Assert.AreEqual(JournalTime(next), state.NextPollUtc);
                Assert.IsNull(await reopened.ReadLocationHeldPollAsync().ConfigureAwait(false));
                Assert.HasCount(1, await reopened.ReadAttemptsAsync().ConfigureAwait(false));
                Assert.AreEqual(1L, (await reopened.ReadSnapshotAsync().ConfigureAwait(false)).StoredCount);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ProtectedLocationStartup_HealthySourceDoesNotParseUnrelatedRetiredRuntimeRows()
    {
        var fixture = new Fixture(Epoch, Location(Epoch.AddDays(-1)), protectedStartup: true);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.LoadProvisionalLocationAsync().ConfigureAwait(false);
        await fixture.ReconcileLocationAsync().ConfigureAwait(false);
        await fixture.WriteRetiredRuntimeRowAsync().ConfigureAwait(false);
        await fixture.StartAndWaitForPollAsync(Epoch + StartupSourcePeriod).ConfigureAwait(false);

        Assert.AreEqual(Epoch, fixture.Publisher.Facts.Single().ObservedAtUtc);
        Assert.HasCount(1, await fixture.ReadAttemptsAsync().ConfigureAwait(false));
        Assert.IsFalse(fixture.Logger.Events.Any(static entry => entry.Id == 2526));
    }

    [TestMethod]
    public async Task ProtectedLocationStartup_RestartWithinPeriodDoesNotReacquireASettledHold()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-environmental-settled-{Guid.NewGuid():N}");
        var location = Location(Epoch.AddDays(-1));
        var device = new BlockingSkyQualityMeter();
        device.ReleaseFirstRead.TrySetResult();
        var next = Epoch + StartupSourcePeriod;
        try
        {
            var initial = new Fixture(Epoch.AddSeconds(-1), location, root: root, protectedStartup: true, device: device);
            await using (initial.ConfigureAwait(false))
            {
                await initial.LoadProvisionalLocationAsync().ConfigureAwait(false);
                await initial.StartAndFireFirstPollAsync().ConfigureAwait(false);
                await initial.WaitForLocationHoldAsync().ConfigureAwait(false);
                initial.Clock.AdvanceTo(Epoch.AddSeconds(1));
                await initial.ReconcileLocationAsync().ConfigureAwait(false);
                await initial.Clock.WaitForTimerAsync(next).WaitAsync(HarnessDeadline).ConfigureAwait(false);
                Assert.AreEqual(1L, (await initial.ReadSnapshotAsync().ConfigureAwait(false)).StoredCount);
                Assert.AreEqual(new EnvironmentalLocationPollSlot(Epoch, true),
                    await initial.ReadLocationPollSlotAsync().ConfigureAwait(false));
                Assert.AreEqual(next, (await initial.ReadStatesAsync().ConfigureAwait(false)).Single().NextPollUtc);
            }

            var reopened = new Fixture(Epoch.AddSeconds(2), location, root: root, protectedStartup: true, device: device);
            await using (reopened.ConfigureAwait(false))
            {
                await reopened.LoadLocationAsync(expectProvisional: false).ConfigureAwait(false);
                await reopened.StartAndWaitForPollAsync(next).ConfigureAwait(false);
                Assert.AreEqual(1L, (await reopened.ReadSnapshotAsync().ConfigureAwait(false)).StoredCount);
                Assert.HasCount(1, await reopened.ReadAttemptsAsync().ConfigureAwait(false));

                await Assert.ThrowsAsync<InvalidDataException>(() => reopened.Coordinator
                    .AcquireScheduledSourceAsync("source-0", Epoch, locationHeld: true, CancellationToken.None).AsTask()
                    .WaitAsync(HarnessDeadline)).ConfigureAwait(false);
                Assert.AreEqual(1L, (await reopened.ReadSnapshotAsync().ConfigureAwait(false)).StoredCount);
                Assert.HasCount(1, await reopened.ReadAttemptsAsync().ConfigureAwait(false));
                Assert.AreEqual(new EnvironmentalLocationPollSlot(Epoch, true),
                    await reopened.HoldLocationPollAsync().ConfigureAwait(false));

                reopened.Clock.AdvanceTo(next);
                await reopened.Clock.WaitForTimerAsync(next + StartupSourcePeriod).WaitAsync(HarnessDeadline).ConfigureAwait(false);
                Assert.AreEqual(2L, (await reopened.ReadSnapshotAsync().ConfigureAwait(false)).StoredCount);
                Assert.HasCount(2, await reopened.ReadAttemptsAsync().ConfigureAwait(false));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ProtectedLocationStartup_QueuedTriggerFailureDoesNotRepeatACommittedHeldPoll()
    {
        var fixture = new Fixture(Epoch.AddSeconds(-1), Location(Epoch.AddDays(-1)), protectedStartup: true);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Publisher.BeforeHeldCommit = cancellationToken =>
        {
            entered.TrySetResult();
            return release.Task.WaitAsync(cancellationToken);
        };
        await fixture.LoadProvisionalLocationAsync().ConfigureAwait(false);
        await fixture.StartAndFireFirstPollAsync().ConfigureAwait(false);
        await fixture.WaitForLocationHoldAsync().ConfigureAwait(false);
        await fixture.ReconcileLocationAsync().ConfigureAwait(false);
        await entered.Task.WaitAsync(HarnessDeadline).ConfigureAwait(false);
        try
        {
            var queued = await fixture.Coordinator.AcquireSourceAsync("source-0", EnvironmentalAcquisitionTrigger.AfterCapture,
                Epoch.AddDays(-2), 1, Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(EnvironmentalAcquisitionDisposition.Coalesced, queued.Disposition);
        }
        finally
        {
            release.TrySetResult();
        }

        await fixture.Clock.WaitForTimerAsync(Epoch + StartupSourcePeriod).WaitAsync(HarnessDeadline).ConfigureAwait(false);
        Assert.AreEqual(1, fixture.Publisher.HeldCommitAttempts);
        Assert.AreEqual(1L, (await fixture.ReadSnapshotAsync().ConfigureAwait(false)).StoredCount);
        var periodic = (await fixture.ReadAttemptsAsync().ConfigureAwait(false))
            .Single(static attempt => attempt.Trigger == EnvironmentalAcquisitionTrigger.Periodic);
        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced, periodic.Disposition);
        Assert.IsNull(await fixture.ReadLocationHeldPollAsync().ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProtectedLocationStartup_BusyCaptureDoesNotTakeOwnershipOfAHeldPoll(bool refuseCoalescedAttempt)
    {
        var device = new BlockingSkyQualityMeter();
        var fixture = new Fixture(Epoch.AddSeconds(1), Location(Epoch.AddDays(-1)), protectedStartup: true, device: device);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.LoadProvisionalLocationAsync().ConfigureAwait(false);
        await fixture.ReconcileLocationAsync().ConfigureAwait(false);
        await fixture.PersistLocationHoldAsync().ConfigureAwait(false);
        if (refuseCoalescedAttempt) await fixture.RefuseCoalescedAttemptAsync().ConfigureAwait(false);

        var captureId = Guid.NewGuid();
        var capture = fixture.Coordinator.AcquireSourceAsync("source-0", EnvironmentalAcquisitionTrigger.AfterCapture,
            Epoch.AddSeconds(1), 1, captureId, CancellationToken.None).AsTask();
        try
        {
            await device.FirstRead.Task.WaitAsync(HarnessDeadline).ConfigureAwait(false);
            var poll = fixture.Coordinator.AcquireScheduledSourceAsync("source-0", Epoch, locationHeld: true,
                CancellationToken.None).AsTask();
            if (refuseCoalescedAttempt)
            {
                await Assert.ThrowsAsync<SqliteException>(() => poll.WaitAsync(HarnessDeadline)).ConfigureAwait(false);
                Assert.AreEqual(Epoch, await fixture.ReadLocationHeldPollAsync().ConfigureAwait(false));
            }
            else
            {
                var receipt = await poll.WaitAsync(HarnessDeadline).ConfigureAwait(false);
                Assert.AreEqual(EnvironmentalAcquisitionDisposition.Coalesced, receipt.Disposition);
                Assert.IsNull(await fixture.ReadLocationHeldPollAsync().ConfigureAwait(false));
            }
        }
        finally
        {
            device.ReleaseFirstRead.TrySetResult();
        }

        var captureReceipt = await capture.WaitAsync(HarnessDeadline).ConfigureAwait(false);
        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced, captureReceipt.Disposition);
        if (refuseCoalescedAttempt)
            await fixture.StartAndWaitForPollAsync(Epoch + StartupSourcePeriod).ConfigureAwait(false);

        Assert.AreEqual(refuseCoalescedAttempt ? 2L : 1L, (await fixture.ReadSnapshotAsync().ConfigureAwait(false)).StoredCount);
        var attempts = await fixture.ReadAttemptsAsync().ConfigureAwait(false);
        Assert.HasCount(2, attempts);
        var captured = attempts.Single(static attempt => attempt.Trigger == EnvironmentalAcquisitionTrigger.AfterCapture);
        Assert.AreEqual(captureId, captured.CaptureId);
        Assert.AreEqual(captureReceipt.ObservationId, captured.ObservationId);
        var periodic = attempts.Single(static attempt => attempt.Trigger == EnvironmentalAcquisitionTrigger.Periodic);
        Assert.AreEqual(refuseCoalescedAttempt ? EnvironmentalAcquisitionDisposition.Produced : EnvironmentalAcquisitionDisposition.Coalesced,
            periodic.Disposition);
        Assert.IsNull(await fixture.ReadLocationHeldPollAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ProtectedLocationStartup_CoalescedDispositionSurvivesShutdownBeforeCaptureDrains()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-environmental-coalesced-{Guid.NewGuid():N}");
        var location = Location(Epoch.AddDays(-1));
        var device = new BlockingSkyQualityMeter();
        var captureAt = Epoch.AddSeconds(1);
        var next = Epoch + StartupSourcePeriod;
        try
        {
            var fixture = new Fixture(captureAt, location, root: root, protectedStartup: true, device: device);
            EnvironmentalAcquisitionAttemptRecord original;
            await using (fixture.ConfigureAwait(false))
            {
                await fixture.LoadProvisionalLocationAsync().ConfigureAwait(false);
                await fixture.ReconcileLocationAsync().ConfigureAwait(false);
                await fixture.PersistLocationHoldAsync().ConfigureAwait(false);

                using var shutdown = new CancellationTokenSource();
                var capture = fixture.Coordinator.AcquireSourceAsync("source-0", EnvironmentalAcquisitionTrigger.AfterCapture,
                    captureAt, 1, Guid.NewGuid(), shutdown.Token).AsTask();
                try
                {
                    await device.FirstRead.Task.WaitAsync(HarnessDeadline).ConfigureAwait(false);
                    await fixture.StartAndWaitForPollAsync(next).ConfigureAwait(false);
                    await fixture.StopAsync().WaitAsync(HarnessDeadline).ConfigureAwait(false);
                }
                finally
                {
                    await shutdown.CancelAsync().ConfigureAwait(false);
                }
                await Assert.ThrowsAsync<OperationCanceledException>(() => capture.WaitAsync(HarnessDeadline)).ConfigureAwait(false);
                Assert.AreEqual(new EnvironmentalLocationPollSlot(Epoch, true),
                    await fixture.ReadLocationPollSlotAsync().ConfigureAwait(false));
                Assert.AreEqual(0L, (await fixture.ReadSnapshotAsync().ConfigureAwait(false)).StoredCount);
                original = (await fixture.ReadAttemptsAsync().ConfigureAwait(false)).Single();
                Assert.AreEqual(EnvironmentalAcquisitionTrigger.Periodic, original.Trigger);
                Assert.AreEqual(EnvironmentalAcquisitionDisposition.Coalesced, original.Disposition);
            }

            var reopened = new Fixture(next.AddSeconds(1), location, root: root, protectedStartup: true, device: device);
            await using var reopenedLifetime = reopened.ConfigureAwait(false);
            await reopened.LoadLocationAsync(expectProvisional: false).ConfigureAwait(false);
            await reopened.StartAndWaitForPollAsync(next + StartupSourcePeriod).ConfigureAwait(false);
            Assert.AreEqual(1L, (await reopened.ReadSnapshotAsync().ConfigureAwait(false)).StoredCount);
            var attempts = await reopened.ReadAttemptsAsync().ConfigureAwait(false);
            Assert.HasCount(2, attempts);
            Assert.IsTrue(attempts.Contains(original));
            Assert.AreEqual(EnvironmentalAcquisitionTrigger.Periodic,
                attempts.Single(static attempt => attempt.Disposition == EnvironmentalAcquisitionDisposition.Produced).Trigger);
            CollectionAssert.AreEqual(new[] { captureAt, next }, device.RequestedTimes.ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ProtectedLocationStartup_AttemptWriteFailureRollsBackTheFactAndRecordsAnExplicitDisposition()
    {
        var fixture = new Fixture(Epoch.AddSeconds(-1), Location(Epoch.AddDays(-1)), protectedStartup: true);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.LoadProvisionalLocationAsync().ConfigureAwait(false);
        await fixture.StartAndFireFirstPollAsync().ConfigureAwait(false);
        await fixture.WaitForLocationHoldAsync().ConfigureAwait(false);
        await fixture.RefuseProducedAttemptAsync().ConfigureAwait(false);
        await fixture.ReconcileLocationAsync().ConfigureAwait(false);

        await fixture.Clock.WaitForTimerAsync(Epoch + StartupSourcePeriod).WaitAsync(HarnessDeadline).ConfigureAwait(false);
        var receipt = (await fixture.ReadAttemptsAsync().ConfigureAwait(false)).Single();
        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Failed, receipt.Disposition);
        Assert.AreEqual("journal-unavailable", receipt.Reason);
        Assert.AreEqual(1, fixture.Publisher.HeldCommitAttempts);
        Assert.AreEqual(0L, (await fixture.ReadSnapshotAsync().ConfigureAwait(false)).StoredCount);
        Assert.IsNull(await fixture.ReadLocationHeldPollAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ProtectedLocationStartup_TransientSettlementFailureRetriesTheSameFact()
    {
        var fixture = new Fixture(Epoch.AddSeconds(-1), Location(Epoch.AddDays(-1)), protectedStartup: true);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        fixture.Publisher.BeforeHeldCommit = _ => fixture.Publisher.HeldCommitAttempts == 1
            ? Task.FromException(new SqliteException("test write contention", SQLitePCL.raw.SQLITE_BUSY))
            : Task.CompletedTask;
        await fixture.LoadProvisionalLocationAsync().ConfigureAwait(false);
        await fixture.StartAndFireFirstPollAsync().ConfigureAwait(false);
        await fixture.WaitForLocationHoldAsync().ConfigureAwait(false);
        await fixture.ReconcileLocationAsync().ConfigureAwait(false);

        var retry = Epoch.AddMilliseconds(100);
        await fixture.Clock.WaitForTimerAsync(retry).WaitAsync(HarnessDeadline).ConfigureAwait(false);
        fixture.Clock.AdvanceTo(retry);
        await fixture.Clock.WaitForTimerAsync(Epoch + StartupSourcePeriod).WaitAsync(HarnessDeadline).ConfigureAwait(false);
        Assert.HasCount(2, fixture.Publisher.HeldFacts);
        Assert.AreSame(fixture.Publisher.HeldFacts.First(), fixture.Publisher.HeldFacts.Last());
        Assert.AreEqual(1L, (await fixture.ReadSnapshotAsync().ConfigureAwait(false)).StoredCount);
        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced,
            (await fixture.ReadAttemptsAsync().ConfigureAwait(false)).Single().Disposition);
        Assert.IsNull(await fixture.ReadLocationHeldPollAsync().ConfigureAwait(false));
    }

    private static DateTimeOffset JournalTime(DateTimeOffset value)
        => DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());

    private static async Task AssertWaitingForLocationAsync(Fixture fixture)
    {
        Assert.IsNull(fixture.ProtectedLocation!.Active);
        var pending = (await fixture.ReadStatesAsync().ConfigureAwait(false)).Single();
        Assert.AreEqual(JournalTime(fixture.StartupEpoch), pending.NextPollUtc, "A due source must retain its slot while the protected location is unavailable.");
        Assert.AreEqual(fixture.StartupEpoch, await fixture.ReadLocationHeldPollAsync().ConfigureAwait(false));
        Assert.IsNull(pending.LastDisposition);
        Assert.IsEmpty(await fixture.ReadAttemptsAsync().ConfigureAwait(false));
        Assert.AreEqual(0L, (await fixture.ReadSnapshotAsync().ConfigureAwait(false)).StoredCount);
        Assert.IsFalse(fixture.Logger.Events.Any(static entry => entry.Id == 2522));
    }

    [TestMethod]
    [DataRow(-1, 0, null)]
    [DataRow(0, 30, EnvironmentalAcquisitionDisposition.Produced)]
    [DataRow(1, 30, EnvironmentalAcquisitionDisposition.Missing)]
    [DataRow(29_999, 30, EnvironmentalAcquisitionDisposition.Missing)]
    [DataRow(30_000, 60, EnvironmentalAcquisitionDisposition.Produced)]
    public async Task NewDeploymentStartup_RecordsTheFloorSlotAndPreservesTheNextBoundary(
        int startupMilliseconds,
        int nextSeconds,
        EnvironmentalAcquisitionDisposition? expectedInitialDisposition)
    {
        var startup = Epoch.AddMilliseconds(startupMilliseconds);
        var fixture = new Fixture(startup, Location(startup));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var next = Epoch.AddSeconds(nextSeconds);

        await fixture.StartAndWaitForPollAsync(next).ConfigureAwait(false);
        var states = await fixture.ReadStatesAsync().ConfigureAwait(false);
        var attempts = await fixture.ReadAttemptsAsync().ConfigureAwait(false);
        var state = states.Single();

        Assert.AreEqual(expectedInitialDisposition, state.LastDisposition);
        Assert.AreEqual(next, state.NextPollUtc);
        Assert.HasCount(expectedInitialDisposition.HasValue ? 1 : 0, attempts);
        if (expectedInitialDisposition == EnvironmentalAcquisitionDisposition.Missing)
        {
            Assert.AreEqual("deployment-location-not-effective", state.LastReason);
            Assert.IsNull(state.LastObservationId);
            Assert.IsNull(state.LastObservedUtc);
            Assert.IsEmpty(fixture.Publisher.Facts);
            Assert.AreEqual(0L, (await fixture.ReadSnapshotAsync().ConfigureAwait(false)).StoredCount);
        }

        fixture.Clock.AdvanceTo(next);
        await fixture.Clock.WaitForTimerAsync(next.AddSeconds(30)).WaitAsync(HarnessDeadline).ConfigureAwait(false);
        state = (await fixture.ReadStatesAsync().ConfigureAwait(false)).Single();

        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced, state.LastDisposition);
        Assert.AreEqual(next, state.LastObservedUtc);
        Assert.AreEqual(next.AddSeconds(30), state.NextPollUtc);
        Assert.AreEqual(0, state.ConsecutiveFailures);
        Assert.IsTrue(fixture.Publisher.Facts.All(fact => fixture.Location.IsEffectiveAt(fact.ObservedAtUtc)));
        Assert.IsFalse(fixture.Logger.Events.Any(static entry => entry.Id == 2522));
    }

    [TestMethod]
    public async Task FutureEffectiveDeployment_RecordsMissingSlotsUntilTheFirstValidBoundary()
    {
        var fixture = new Fixture(Epoch.AddMilliseconds(1), Location(Epoch.AddSeconds(31)));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.StartAndWaitForPollAsync(Epoch.AddSeconds(30)).ConfigureAwait(false);

        fixture.Clock.AdvanceTo(Epoch.AddSeconds(30));
        await fixture.Clock.WaitForTimerAsync(Epoch.AddSeconds(60)).WaitAsync(HarnessDeadline).ConfigureAwait(false);
        var attempts = await fixture.ReadAttemptsAsync().ConfigureAwait(false);
        Assert.HasCount(2, attempts);
        Assert.IsTrue(attempts.All(static attempt =>
            attempt.Disposition == EnvironmentalAcquisitionDisposition.Missing &&
            attempt.Reason == "deployment-location-not-effective" && attempt.ObservationId is null));
        Assert.IsEmpty(fixture.Publisher.Facts);

        fixture.Clock.AdvanceTo(Epoch.AddSeconds(60));
        await fixture.Clock.WaitForTimerAsync(Epoch.AddSeconds(90)).WaitAsync(HarnessDeadline).ConfigureAwait(false);
        var state = (await fixture.ReadStatesAsync().ConfigureAwait(false)).Single();
        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced, state.LastDisposition);
        Assert.AreEqual(Epoch.AddSeconds(60), state.LastObservedUtc);
        Assert.HasCount(1, fixture.Publisher.Facts);
    }

    [TestMethod]
    public async Task ExpiredDeployment_RecordsMissingAtTheExclusiveEndWithoutPublishing()
    {
        var fixture = new Fixture(Epoch, Location(Epoch, Epoch.AddSeconds(30)));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.StartAndWaitForPollAsync(Epoch.AddSeconds(30)).ConfigureAwait(false);

        fixture.Clock.AdvanceTo(Epoch.AddSeconds(30));
        await fixture.Clock.WaitForTimerAsync(Epoch.AddSeconds(60)).WaitAsync(HarnessDeadline).ConfigureAwait(false);
        var state = (await fixture.ReadStatesAsync().ConfigureAwait(false)).Single();

        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Missing, state.LastDisposition);
        Assert.AreEqual("deployment-location-not-effective", state.LastReason);
        Assert.AreEqual(Epoch, state.LastObservedUtc, "The prior valid observation remains historical evidence.");
        Assert.HasCount(1, fixture.Publisher.Facts);
        Assert.AreEqual(Epoch.AddSeconds(60), state.NextPollUtc);
    }

    [TestMethod]
    public async Task MissingStartupAndSuccessfulIndependentSources_RemainDurableAcrossRestart()
    {
        var location = Location(Epoch.AddMilliseconds(1));
        var fixture = new Fixture(Epoch.AddMilliseconds(1), location, sourceCount: 2);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.StartAndWaitForPollAsync(Epoch.AddSeconds(30)).ConfigureAwait(false);
        await fixture.StopAsync().ConfigureAwait(false);

        using (var reopened = new SqliteEnvironmentalObservationOutbox())
        {
            var states = await reopened.ReadSourceStatesAsync(fixture.Root, CancellationToken.None).ConfigureAwait(false);
            var attempts = await reopened.ReadAttemptsAsync(fixture.Root, 10, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(2, states);
            Assert.HasCount(2, attempts);
            Assert.IsTrue(states.All(static state =>
                state.LastDisposition == EnvironmentalAcquisitionDisposition.Missing &&
                state.NextPollUtc == Epoch.AddSeconds(30)));
            Assert.AreEqual(0L, (await reopened.GetLocalSnapshotAsync(fixture.Root, CancellationToken.None)
                .ConfigureAwait(false)).StoredCount);
        }

        var restart = new Fixture(Epoch.AddSeconds(30), location, sourceCount: 2, root: fixture.Root);
        await using var restartLifetime = restart.ConfigureAwait(false);
        await restart.StartAndWaitForPollAsync(Epoch.AddSeconds(60)).ConfigureAwait(false);
        var recovered = await restart.ReadStatesAsync().ConfigureAwait(false);
        var history = await restart.ReadAttemptsAsync().ConfigureAwait(false);
        Assert.HasCount(2, recovered);
        Assert.HasCount(4, history);
        Assert.HasCount(2, restart.Publisher.Facts);
        Assert.AreEqual(2L, (await restart.ReadSnapshotAsync().ConfigureAwait(false)).StoredCount);
        Assert.IsTrue(recovered.All(static state =>
            state.LastDisposition == EnvironmentalAcquisitionDisposition.Produced &&
            state.LastObservedUtc == Epoch.AddSeconds(30) && state.ConsecutiveFailures == 0));
    }

    [TestMethod]
    public async Task CaptureTriggeredAcquisition_PreservesLocationValidationAndValidPublication()
    {
        var fixture = new Fixture(Epoch.AddMilliseconds(1), Location(Epoch.AddMilliseconds(1)));
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        var captureId = Guid.NewGuid();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await fixture.Coordinator.AcquireSourceAsync(
                "source-0", EnvironmentalAcquisitionTrigger.BeforeCapture, Epoch, 1, captureId,
                CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        var receipt = await fixture.Coordinator.AcquireSourceAsync(
            "source-0", EnvironmentalAcquisitionTrigger.BeforeCapture, Epoch.AddMilliseconds(1), 1, captureId,
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced, receipt.Disposition);
        Assert.HasCount(1, fixture.Publisher.Facts);
        Assert.HasCount(1, await fixture.ReadAttemptsAsync().ConfigureAwait(false));
    }

    private static DeploymentLocationSnapshot Location(DateTimeOffset from, DateTimeOffset? until = null)
        => DeploymentLocationSnapshot.Create(
            "location", 1, "test", null, from, until,
            35.5599378, -113.9119818, 520, "America/Phoenix");

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly SqliteEnvironmentalObservationOutbox _store;
        private readonly EnvironmentalAcquisitionTelemetry _telemetry;
        private readonly EnvironmentalAcquisitionService _service;
        private readonly CancellationTokenSource _applicationStarted = new();
        private readonly CancellationTokenSource _applicationStopping = new();
        private readonly FileCameraAgentConfigurationLoader? _loader;
        private readonly RetainedIntegrityScrubService? _scrub;
        private readonly SignalingScheduleStore? _scheduleWitness;
        private readonly string _configurationPath;
        private readonly bool _ownsRoot;
        private bool _started;
        private readonly int _sourceCount;

        public Fixture(DateTimeOffset startup, DeploymentLocationSnapshot location, int sourceCount = 1, string? root = null,
            bool protectedStartup = false, bool useExplicitEffectiveFrom = true, DateTimeOffset? startupEpoch = null,
            IEnvironmentalDevice? device = null)
        {
            Root = root ?? Path.Combine(Path.GetTempPath(), $"hvo-environmental-boundary-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            _ownsRoot = root is null;
            _sourceCount = sourceCount;
            Clock = new ManualClock(startup);
            Location = location;
            StartupEpoch = startupEpoch ?? Epoch;
            var services = new ServiceCollection();
            if (device is not null) services.AddSingleton(device);
            _services = services.BuildServiceProvider();
            _store = new SqliteEnvironmentalObservationOutbox(timeProvider: Clock);
            Publisher = new RecordingPublisher(_store, Root);
            _telemetry = new EnvironmentalAcquisitionTelemetry(Clock);
            _configurationPath = Path.Combine(Root, "cameraagent.json");
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = Root,
                ConfigFilePath = _configurationPath,
                CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled },
                Observatory = new ObservatoryLocation(35.5599378, -113.9119818, 520, "America/Phoenix"),
                DeploymentLocation = new DeploymentLocationOptions
                {
                    LocationId = location.LocationId,
                    EffectiveFromUtc = useExplicitEffectiveFrom ? location.EffectiveFromUtc : null
                },
                EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions
                {
                    Enabled = true,
                    MaximumConcurrency = 4,
                    QueueCapacity = 4,
                    Sources = Enumerable.Range(0, sourceCount).Select(index => new EnvironmentalSourceConfiguration
                    {
                        Id = $"source-{index}",
                        Type = device is null ? "VirtualEnvironment" : "SkyQualityMeter",
                        Kind = device is null ? EnvironmentalObservationKind.AirTemperature : EnvironmentalObservationKind.SkyQuality,
                        Required = true,
                        Triggers = protectedStartup
                            ? [EnvironmentalAcquisitionTrigger.Periodic, EnvironmentalAcquisitionTrigger.BeforeCapture, EnvironmentalAcquisitionTrigger.AfterCapture]
                            : [EnvironmentalAcquisitionTrigger.Periodic, EnvironmentalAcquisitionTrigger.BeforeCapture],
                        ScheduleEpochUtc = StartupEpoch,
                        PeriodSeconds = protectedStartup ? (int)StartupSourcePeriod.TotalSeconds : 30,
                        ValidForSeconds = 120,
                        StaleAfterSeconds = 45,
                        Options = device is null
                            ? CaptureContractJson.SerializeToElement(new VirtualEnvironmentalSourceOptions(1118 + index, Epoch, 12.5, null))
                            : CaptureContractJson.SerializeToElement(new EnvironmentalDeviceOptions("device"))
                    }).ToArray()
                }
            });
            var factory = new EnvironmentalSourceFactory(_services,
                [new EnvironmentalSourceRegistration(
                    "VirtualEnvironment", typeof(VirtualEnvironmentalSource), typeof(VirtualEnvironmentalSourceOptions)),
                 new EnvironmentalSourceRegistration(
                    "SkyQualityMeter", typeof(SkyQualityMeterSource), typeof(EnvironmentalDeviceOptions))]);
            if (protectedStartup)
            {
                var lifetime = new Mock<IHostApplicationLifetime>();
                lifetime.SetupGet(host => host.ApplicationStarted).Returns(_applicationStarted.Token);
                lifetime.SetupGet(host => host.ApplicationStopping).Returns(_applicationStopping.Token);
                ProtectedLocation = new ProtectedDeploymentLocationStore(options,
                    new DataProtectionDeploymentLocationProtector(options), Clock, NullLogger<ProtectedDeploymentLocationStore>.Instance);
                _loader = new FileCameraAgentConfigurationLoader(options, NullLogger<FileCameraAgentConfigurationLoader>.Instance,
                    ProtectedLocation, hostLifetime: lifetime.Object, timeProvider: Clock);
                _scrub = new RetainedIntegrityScrubService(_loader, NullLogger<RetainedIntegrityScrubService>.Instance,
                    Clock, locations: ProtectedLocation, hostLifetime: lifetime.Object);
                _scheduleWitness = new SignalingScheduleStore(_store);
            }
            Coordinator = new EnvironmentalAcquisitionCoordinator(
                factory, Publisher, (IDeploymentLocationStore?)ProtectedLocation ?? new FixedLocationStore(location),
                options, Clock, _store, _telemetry);
            _service = new EnvironmentalAcquisitionService(
                Coordinator, (IEnvironmentalAcquisitionStateStore?)_scheduleWitness ?? _store,
                new NoopRetentionStore(), _telemetry, Clock, options, Logger);
        }

        public string Root { get; }
        public ManualClock Clock { get; }
        public DateTimeOffset StartupEpoch { get; }
        public DeploymentLocationSnapshot Location { get; }
        public ProtectedDeploymentLocationStore? ProtectedLocation { get; }
        public bool RetainedIntegrityRecoveryRequired => _loader!.RetainedIntegrityRecoveryRequired;
        public RecordingPublisher Publisher { get; }
        public RecordingLogger Logger { get; } = new();
        public EnvironmentalAcquisitionCoordinator Coordinator { get; }

        public async Task StartAndWaitForPollAsync(DateTimeOffset nextPoll)
        {
            await StartAsync().ConfigureAwait(false);
            await Clock.WaitForTimerAsync(nextPoll, _sourceCount).WaitAsync(HarnessDeadline).ConfigureAwait(false);
        }

        private async Task StartAsync()
        {
            await _service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            _started = true;
        }

        public Task LoadProvisionalLocationAsync() => LoadLocationAsync(expectProvisional: true);

        public async Task LoadLocationAsync(bool expectProvisional)
        {
            if (!File.Exists(_configurationPath))
                File.Copy(Path.Combine(AppContext.BaseDirectory, "cameraagent.sample.json"), _configurationPath);
            var configuration = await _loader!.LoadAsync(CancellationToken.None).WaitAsync(HarnessDeadline).ConfigureAwait(false);
            Assert.AreEqual(Location.LocationId, configuration.DeploymentLocation?.LocationId);
            if (expectProvisional)
            {
                Assert.IsNull(ProtectedLocation!.Active);
                Assert.IsTrue(ProtectedLocation.RequiresDeploymentLocationReconciliation);
                Assert.IsFalse(ProtectedLocation.RecentRetainedEvidenceReady);
            }
            else
            {
                Assert.IsNotNull(ProtectedLocation!.Active);
            }
            await _scrub!.StartAsync(CancellationToken.None).ConfigureAwait(false);
        }

        public async Task ReconcileLocationAsync()
        {
            await _applicationStarted.CancelAsync().ConfigureAwait(false);
            await _loader!.DrainRetainedIntegrityScrubAsync(CancellationToken.None).WaitAsync(HarnessDeadline).ConfigureAwait(false);
        }

        public async Task StartAndFireFirstPollAsync()
        {
            // The worker registers this timer only after durably recording the due slot.
            await StartAndWaitForPollAsync(StartupEpoch).ConfigureAwait(false);
            Clock.AdvanceTo(StartupEpoch);
        }

        public async Task StartAndWaitForScheduleWriteAsync()
        {
            await StartAsync().ConfigureAwait(false);
            await _scheduleWitness!.ScheduleWritten.Task.WaitAsync(HarnessDeadline).ConfigureAwait(false);
        }

        public Task WaitForLocationHoldAsync()
            => _scheduleWitness!.HoldWritten.Task.WaitAsync(HarnessDeadline);

        public async ValueTask<DateTimeOffset?> ReadLocationHeldPollAsync()
        {
            var slot = await ReadLocationPollSlotAsync().ConfigureAwait(false);
            return slot is { Settled: false } ? slot.DueUtc : null;
        }

        public ValueTask<EnvironmentalLocationPollSlot?> ReadLocationPollSlotAsync()
            => ((IEnvironmentalLocationHeldPollStore)_store).ReadLocationHeldPollAsync(
                Root, Coordinator.Sources.Single(), CancellationToken.None);

        public ValueTask<EnvironmentalLocationPollSlot> HoldLocationPollAsync()
            => ((IEnvironmentalLocationHeldPollStore)_store).HoldLocationPollAsync(
                Root, Coordinator.Sources.Single(), StartupEpoch, CancellationToken.None);

        public async Task PersistLocationHoldAsync()
        {
            var source = Coordinator.Sources.Single();
            await _store.UpdateSourceScheduleAsync(Root, source, StartupEpoch, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(new EnvironmentalLocationPollSlot(StartupEpoch, false), await HoldLocationPollAsync().ConfigureAwait(false));
        }

        public async Task RefuseCoalescedAttemptAsync()
            => await ExecuteJournalStatementAsync("""
                CREATE TRIGGER test_refuse_coalesced_attempt
                BEFORE INSERT ON environmental_acquisition_attempts
                WHEN NEW.trigger = 'Periodic' AND NEW.outcome = 'Coalesced'
                BEGIN SELECT RAISE(ABORT, 'test coalesced attempt persistence failure'); END;
                """).ConfigureAwait(false);

        public async Task WriteRetiredRuntimeRowAsync()
            => await ExecuteJournalStatementAsync("""
                INSERT INTO environmental_source_runtime(source_id, observation_kind, required, consecutive_failures)
                VALUES('retired-source', 'RetiredKind', 0, 0);
                """).ConfigureAwait(false);

        public async Task RefuseProducedAttemptAsync()
            => await ExecuteJournalStatementAsync("""
                CREATE TRIGGER test_refuse_produced_attempt
                BEFORE INSERT ON environmental_acquisition_attempts WHEN NEW.outcome = 'Produced'
                BEGIN SELECT RAISE(ABORT, 'test attempt persistence failure'); END;
                """).ConfigureAwait(false);

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
            Justification = "Only fixed test fixture statements are passed to this helper.")]
        private async Task ExecuteJournalStatementAsync(string statement)
        {
            await _store.InitializeAsync(Root, CancellationToken.None).ConfigureAwait(false);
            var path = Path.Combine(Root, ".environment", "environmental-observation-outbox.db");
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        public ValueTask<IReadOnlyList<EnvironmentalSourceRuntimeState>> ReadStatesAsync()
            => _store.ReadSourceStatesAsync(Root, CancellationToken.None);

        public ValueTask<IReadOnlyList<EnvironmentalAcquisitionAttemptRecord>> ReadAttemptsAsync()
            => _store.ReadAttemptsAsync(Root, 100, CancellationToken.None);

        public ValueTask<LocalEnvironmentalObservationSnapshot> ReadSnapshotAsync()
            => _store.GetLocalSnapshotAsync(Root, CancellationToken.None);

        public async Task StopAsync()
        {
            if (_started)
            {
                await _service.StopAsync(CancellationToken.None).ConfigureAwait(false);
                _started = false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            await _applicationStopping.CancelAsync().ConfigureAwait(false);
            if (_scrub is not null)
            {
                await _scrub.StopAsync(CancellationToken.None).ConfigureAwait(false);
                _scrub.Dispose();
                ProtectedLocation!.Dispose();
            }
            _applicationStarted.Dispose();
            _applicationStopping.Dispose();
            _service.Dispose();
            Coordinator.Dispose();
            _telemetry.Dispose();
            _store.Dispose();
            await _services.DisposeAsync().ConfigureAwait(false);
            if (_ownsRoot)
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class BlockingSkyQualityMeter : IEnvironmentalDevice
    {
        private int _reads;
        public TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<DateTimeOffset> RequestedTimes { get; } = new();

        public async ValueTask<EnvironmentalDeviceSample> ReadAsync(string deviceId, string command, DateTimeOffset receivedAtUtc,
            CancellationToken cancellationToken)
        {
            RequestedTimes.Enqueue(receivedAtUtc);
            var read = Interlocked.Increment(ref _reads);
            if (read == 1)
            {
                FirstRead.TrySetResult();
                await ReleaseFirstRead.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            var sampled = Epoch.AddMilliseconds(read);
            return new EnvironmentalDeviceSample(System.Text.Encoding.ASCII.GetBytes(EnvironmentalHardwareAdapterTests.Sqm),
                sampled, false, ReceivedAtUtc: sampled);
        }
    }

    private sealed class SignalingScheduleStore(SqliteEnvironmentalObservationOutbox inner)
        : IEnvironmentalAcquisitionStateStore, IEnvironmentalLocationHeldPollStore
    {
        public TaskCompletionSource ScheduleWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HoldWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask UpdateSourceScheduleAsync(string root, EnvironmentalSourceDescriptor source,
            DateTimeOffset nextPollUtc, CancellationToken cancellationToken)
        {
            await inner.UpdateSourceScheduleAsync(root, source, nextPollUtc, cancellationToken).ConfigureAwait(false);
            ScheduleWritten.TrySetResult();
        }

        public ValueTask<EnvironmentalLocationPollSlot?> ReadLocationHeldPollAsync(string root, EnvironmentalSourceDescriptor source,
            CancellationToken cancellationToken)
            => ((IEnvironmentalLocationHeldPollStore)inner).ReadLocationHeldPollAsync(root, source, cancellationToken);

        public async ValueTask<EnvironmentalLocationPollSlot> HoldLocationPollAsync(string root, EnvironmentalSourceDescriptor source,
            DateTimeOffset due, CancellationToken cancellationToken)
        {
            var slot = await ((IEnvironmentalLocationHeldPollStore)inner).HoldLocationPollAsync(
                root, source, due, cancellationToken).ConfigureAwait(false);
            HoldWritten.TrySetResult();
            return slot;
        }

        public ValueTask<bool> RecordCaptureRegimeAsync(string root, long captureSequence, Guid captureId,
            CaptureSolarRegime regime, DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
            => inner.RecordCaptureRegimeAsync(root, captureSequence, captureId, regime, observedAtUtc, cancellationToken);

        public ValueTask RecordAttemptAsync(string root, EnvironmentalSourceDescriptor source, EnvironmentalAcquisitionReceipt receipt,
            long? captureSequence, Guid? captureId, CancellationToken cancellationToken)
            => inner.RecordAttemptAsync(root, source, receipt, captureSequence, captureId, cancellationToken);

        public ValueTask<IReadOnlyList<EnvironmentalSourceRuntimeState>> ReadSourceStatesAsync(string root, CancellationToken cancellationToken)
            => inner.ReadSourceStatesAsync(root, cancellationToken);

        public ValueTask<IReadOnlyList<EnvironmentalAcquisitionAttemptRecord>> ReadAttemptsAsync(string root, int maximumResults,
            CancellationToken cancellationToken)
            => inner.ReadAttemptsAsync(root, maximumResults, cancellationToken);
    }

    private sealed class RecordingPublisher(SqliteEnvironmentalObservationOutbox store, string root)
        : IEnvironmentalObservationPublisher, IEnvironmentalHeldPollPublisher
    {
        public ConcurrentQueue<EnvironmentalObservationFactV1> Facts { get; } = new();
        public ConcurrentQueue<EnvironmentalObservationFactV1> HeldFacts { get; } = new();
        public Func<CancellationToken, Task>? BeforeHeldCommit { get; set; }
        public int HeldCommitAttempts => HeldFacts.Count;

        public async ValueTask<EnvironmentalAcquisitionReceipt> PublishHeldPollAsync(
            EnvironmentalObservationFactV1 fact, EnvironmentalHeldPoll poll, CancellationToken cancellationToken)
        {
            HeldFacts.Enqueue(fact);
            if (BeforeHeldCommit is { } before)
                await before(cancellationToken).ConfigureAwait(false);
            var result = await store.CommitHeldPollAsync(root, fact, poll, cancellationToken).ConfigureAwait(false);
            Facts.Enqueue(fact);
            return result.HeldPollReceipt!;
        }

        public async ValueTask<EnvironmentalObservationPublishResult> PublishAsync(
            EnvironmentalObservationFactV1 fact, CancellationToken cancellationToken = default)
        {
            var result = await store.CommitLocalAsync(root, fact, cancellationToken).ConfigureAwait(false);
            Facts.Enqueue(fact);
            return new EnvironmentalObservationPublishResult(
                result.Disposition == LocalEnvironmentalObservationCommitDisposition.Committed
                    ? EnvironmentalObservationPublishDisposition.Enqueued
                    : EnvironmentalObservationPublishDisposition.Duplicate,
                null);
        }
    }

    private sealed class FixedLocationStore(DeploymentLocationSnapshot active) : IDeploymentLocationStore
    {
        public DeploymentLocationSnapshot? Active => active;
        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(DeploymentLocationSeed seed, CancellationToken cancellationToken)
            => ValueTask.FromResult(active);
        public DeploymentLocationSnapshot Resolve(CaptureLocationProvenance provenance, DateTimeOffset? effectiveUtc = null) => active;
    }

    private sealed class NoopRetentionStore : ILocalEnvironmentalRetentionStore
    {
        public ValueTask<LocalEnvironmentalRetentionResult> RetainLocalAsync(
            string root, DateTimeOffset recordedBeforeUtc, int maximumResults, CancellationToken cancellationToken)
            => ValueTask.FromResult(new LocalEnvironmentalRetentionResult(0, 0, 0, 0));
    }

    private sealed class RecordingLogger : ILogger<EnvironmentalAcquisitionService>
    {
        public ConcurrentQueue<(int Id, Exception? Exception)> Events { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Events.Enqueue((eventId.Id, exception));
    }

    private sealed class ManualClock(DateTimeOffset startup) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly List<(DateTimeOffset Due, int Count, TaskCompletionSource Completion)> _waiters = [];
        private DateTimeOffset _now = startup;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => GetUtcNow().UtcTicks;
        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) { return _now; }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new ManualTimer(this, callback, state);
                timer.Change(dueTime, period);
                _timers.Add(timer);
                foreach (var waiter in _waiters.Where(waiter =>
                    _timers.Count(timer => timer.Due == waiter.Due) >= waiter.Count).ToArray())
                {
                    _waiters.Remove(waiter);
                    waiter.Completion.TrySetResult();
                }
                return timer;
            }
        }

        public Task WaitForTimerAsync(DateTimeOffset due, int count = 1)
        {
            lock (_gate)
            {
                if (_timers.Count(timer => timer.Due == due) >= count) { return Task.CompletedTask; }
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((due, count, completion));
                return completion.Task;
            }
        }

        public void AdvanceTo(DateTimeOffset instant)
        {
            ManualTimer[] due;
            lock (_gate)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(instant, _now);
                _now = instant;
                due = _timers.Where(timer => timer.Due <= instant).ToArray();
                foreach (var timer in due) { timer.Due = null; }
            }
            foreach (var timer in due) { timer.Fire(); }
        }

        private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due { get; set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    if (period != Timeout.InfiniteTimeSpan) { throw new NotSupportedException("Only one-shot delays are used by this fixture."); }
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                    return true;
                }
            }
            public void Fire() => callback(state);
            public void Dispose()
            {
                lock (owner._gate) { owner._timers.Remove(this); Due = null; }
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
