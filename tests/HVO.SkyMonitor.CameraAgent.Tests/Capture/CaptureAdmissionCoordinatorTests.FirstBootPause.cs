using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture;

/// <summary>
/// The installer-owned first-boot pause and atomic first resume of a Silo-layout clean install (#1966; cutover contract
/// 8.1 and 8.4, fault matrix "Operator no-op pause before the Silo first resume").
/// </summary>
public sealed partial class CaptureAdmissionCoordinatorTests
{
    [TestMethod]
    public async Task FirstBootPauseIsSeededBeforeAdmissionAndSurvivesRestartAsync()
    {
        var root = CreateRoot();
        var id = Guid.NewGuid();
        var seed = new FirstBootPauseIdentity(id);
        try
        {
            using (var first = await CoordinatorFixture.CreateAsync(root, firstBootPauseId: id).ConfigureAwait(false))
            {
                Assert.AreEqual(CaptureAdmissionState.Paused, first.Coordinator.Snapshot.State);
                Assert.AreEqual(1L, first.Coordinator.Snapshot.Version);
                var status = await first.Coordinator.ReadFirstBootPauseAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(status);
                Assert.IsTrue(status.Seeded);
                Assert.IsTrue(status.InForce);
                Assert.IsFalse(status.Resumed);
                Assert.AreEqual("paused", status.State);
            }

            // Restart (and an owner bootstrap, which never issues a capture command) leaves the same single pause.
            using var restarted = await CoordinatorFixture.CreateAsync(root, firstBootPauseId: id).ConfigureAwait(false);
            Assert.AreEqual(CaptureAdmissionState.Paused, restarted.Coordinator.Snapshot.State);
            Assert.AreEqual(1L, restarted.Coordinator.Snapshot.Version);
            using var verify = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarLongAsync(
                verify, "SELECT COUNT(*) FROM capture_control_commands;").ConfigureAwait(false));
            Assert.AreEqual(seed.PauseKey, await ScalarStringAsync(
                verify, "SELECT idempotency_key FROM capture_control_commands;").ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarLongAsync(
                verify, "SELECT changed FROM capture_control_commands;").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarLongAsync(
                verify, "SELECT expected_version FROM capture_control_commands;").ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarLongAsync(
                verify, "SELECT result_version FROM capture_control_commands;").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task OperatorResumeIsRefusedWhileFirstBootPauseIsInForceAsync()
    {
        var root = CreateRoot();
        try
        {
            using var fixture = await CoordinatorFixture.CreateAsync(root, firstBootPauseId: Guid.NewGuid()).ConfigureAwait(false);

            await Assert.ThrowsExactlyAsync<CaptureControlConflictException>(async () =>
                await fixture.Coordinator.ResumeAsync("ui-resume", 1, "owner-1", null, CancellationToken.None)
                    .ConfigureAwait(false)).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<CaptureControlConflictException>(async () =>
                await fixture.Coordinator.ResumeAsync("ui-resume-unversioned", null, "owner-1", null, CancellationToken.None)
                    .ConfigureAwait(false)).ConfigureAwait(false);

            Assert.AreEqual(CaptureAdmissionState.Paused, fixture.Coordinator.Snapshot.State);
            var status = await fixture.Coordinator.ReadFirstBootPauseAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(status!.InForce);
            using var verify = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarLongAsync(
                verify, "SELECT COUNT(*) FROM capture_control_commands;").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task FirstResumeIsAtomicIdempotentAndNeverReseedsAsync()
    {
        var root = CreateRoot();
        var id = Guid.NewGuid();
        var seed = new FirstBootPauseIdentity(id);
        try
        {
            using (var fixture = await CoordinatorFixture.CreateAsync(root, firstBootPauseId: id).ConfigureAwait(false))
            {
                // A stale expected version is refused before the seed check can admit anything.
                await Assert.ThrowsExactlyAsync<CaptureControlConflictException>(async () =>
                    await fixture.Coordinator.ResumeAsync(seed.ResumeKey, 0, seed.Actor, FirstBootPauseIdentity.ResumeReason,
                        CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

                var resumed = await fixture.Coordinator.ResumeAsync(
                    seed.ResumeKey, 1, seed.Actor, FirstBootPauseIdentity.ResumeReason, CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(resumed.Changed);
                Assert.AreEqual(CaptureAdmissionState.Running, resumed.State);
                Assert.AreEqual(2L, resumed.Version);

                var replay = await fixture.Coordinator.ResumeAsync(
                    seed.ResumeKey, 1, seed.Actor, FirstBootPauseIdentity.ResumeReason, CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(replay.Replayed);
                Assert.AreEqual(2L, replay.Version);

                var status = await fixture.Coordinator.ReadFirstBootPauseAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(status!.Resumed);
                Assert.IsFalse(status.InForce);

                // After the first resume the operator's ordinary controls apply again.
                var paused = await fixture.Coordinator.PauseAsync("ui-pause", 2, "owner-1", "clouds", CancellationToken.None)
                    .ConfigureAwait(false);
                var operatorResume = await fixture.Coordinator.ResumeAsync(
                    "ui-resume", paused.Version, "owner-1", null, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureAdmissionState.Running, operatorResume.State);
            }

            using var restarted = await CoordinatorFixture.CreateAsync(root, firstBootPauseId: id).ConfigureAwait(false);
            Assert.AreEqual(CaptureAdmissionState.Running, restarted.Coordinator.Snapshot.State);
            using var verify = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarLongAsync(
                verify, $"SELECT COUNT(*) FROM capture_control_commands WHERE target_state = 'paused' AND actor LIKE 'installer-first-boot:%';")
                .ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LaterOperatorPauseRefusesFirstResumeAndHandsResumeToOperatorAsync(bool noOpSameVersion)
    {
        var root = CreateRoot();
        var id = Guid.NewGuid();
        var seed = new FirstBootPauseIdentity(id);
        try
        {
            using var fixture = await CoordinatorFixture.CreateAsync(root, firstBootPauseId: id).ConfigureAwait(false);
            var operatorPause = await fixture.Coordinator.PauseAsync(
                "ui-pause", noOpSameVersion ? 1 : null, "owner-1", "hold for inspection", CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsFalse(operatorPause.Changed);
            Assert.AreEqual(1L, operatorPause.Version);

            var refusal = await Assert.ThrowsExactlyAsync<CaptureControlConflictException>(async () =>
                await fixture.Coordinator.ResumeAsync(seed.ResumeKey, 1, seed.Actor, FirstBootPauseIdentity.ResumeReason,
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            StringAssert.Contains(refusal.Message, "superseded", StringComparison.Ordinal);
            Assert.AreEqual(CaptureAdmissionState.Paused, fixture.Coordinator.Snapshot.State);
            Assert.AreEqual(1L, fixture.Coordinator.Snapshot.Version);
            var status = await fixture.Coordinator.ReadFirstBootPauseAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(status!.Seeded);
            Assert.IsFalse(status.InForce);
            Assert.IsFalse(status.Resumed);

            // The operator who placed the later pause now owns the decision to resume.
            var resumed = await fixture.Coordinator.ResumeAsync("ui-resume", 1, "owner-1", null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual(CaptureAdmissionState.Running, resumed.State);
            await Assert.ThrowsExactlyAsync<CaptureControlConflictException>(async () =>
                await fixture.Coordinator.ResumeAsync(seed.ResumeKey, 2, seed.Actor, FirstBootPauseIdentity.ResumeReason,
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task NoFirstBootPauseLeavesFreshStateRunningAndReportsNoneAsync()
    {
        var root = CreateRoot();
        try
        {
            using var fixture = await CoordinatorFixture.CreateAsync(root).ConfigureAwait(false);
            Assert.AreEqual(CaptureAdmissionState.Running, fixture.Coordinator.Snapshot.State);
            Assert.AreEqual(0L, fixture.Coordinator.Snapshot.Version);
            Assert.IsNull(fixture.Coordinator.FirstBootPause);
            Assert.IsNull(await fixture.Coordinator.ReadFirstBootPauseAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.IsNull(new FirstBootPauseOptions { Id = Guid.Empty }.Identity);
        }
        finally
        {
            DeleteRoot(root);
        }
    }
    [TestMethod]
    public async Task SchemaCreationCommitsOnlyTheSeededPauseEvenWhenInterruptedAsync()
    {
        var root = CreateRoot();
        var seed = new FirstBootPauseIdentity(Guid.NewGuid());
        var path = Path.Combine(root, "journal", "raw-ingress.db");
        try
        {
            // Interrupted after the schema and seed statements ran but before commit: nothing is durable.
            await Assert.ThrowsExactlyAsync<InjectedSchemaFaultException>(async () =>
                await new SqliteRawCaptureJournal(path, 1, faultInjector: new OneShotSchemaFault(RawIngressFaultPoint.BeforeMigrationCommit),
                    firstBootPause: seed).InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            using (var interrupted = await OpenAsync(root).ConfigureAwait(false))
            {
                Assert.AreEqual(0L, await ScalarLongAsync(interrupted, "PRAGMA user_version;").ConfigureAwait(false));
                Assert.AreEqual(0L, await ScalarLongAsync(
                    interrupted, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'capture_control_state';").ConfigureAwait(false));
            }

            // The retry creates the schema and the pause together; the first committed control state is the pause.
            await new SqliteRawCaptureJournal(path, 1, firstBootPause: seed).InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using var verify = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual("paused", await ScalarStringAsync(verify, "SELECT state FROM capture_control_state;").ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarLongAsync(verify, "SELECT version FROM capture_control_state;").ConfigureAwait(false));
            Assert.AreEqual(seed.PauseKey, await ScalarStringAsync(
                verify, "SELECT idempotency_key FROM capture_control_commands;").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM raw_capture_sequences;").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task FirstBootProfileOverACommittedRunningStateFailsClosedAsync()
    {
        var root = CreateRoot();
        try
        {
            // A journal created without the seed commits running/0; a first-boot profile then refuses to open admission.
            await CreateJournal(root).InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            {
                using var fixture = await CoordinatorFixture.CreateAsync(root, firstBootPauseId: Guid.NewGuid()).ConfigureAwait(false);
            }).ConfigureAwait(false);
            using var verify = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(0L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM capture_control_commands;").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task InstallerLifecycleResumeNeverLiftsAPauseItDoesNotOwnAsync(bool withExpectedVersion)
    {
        var root = CreateRoot();
        var operation = Guid.NewGuid();
        var installerActor = $"installer-lifecycle:{operation:D}";
        try
        {
            using var fixture = await CoordinatorFixture.CreateAsync(root, firstBootPauseId: Guid.NewGuid()).ConfigureAwait(false);

            // While the seed is in force, the ordinary lifecycle resume is refused.
            await Assert.ThrowsExactlyAsync<CaptureControlConflictException>(async () =>
                await fixture.Coordinator.ResumeAsync($"lifecycle:{operation:D}:resume", withExpectedVersion ? 1 : null,
                    installerActor, null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            // After a same-version operator pause supersedes the seed, it is still refused: it owns no pause.
            _ = await fixture.Coordinator.PauseAsync("ui-pause", 1, "owner-1", "hold", CancellationToken.None).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<CaptureControlConflictException>(async () =>
                await fixture.Coordinator.ResumeAsync($"lifecycle:{operation:D}:resume-2", withExpectedVersion ? 1 : null,
                    installerActor, null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            // A no-op installer pause on an already paused instance owns nothing either.
            var noOp = await fixture.Coordinator.PauseAsync($"lifecycle:{operation:D}:pause", null, installerActor, null,
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(noOp.Changed);
            await Assert.ThrowsExactlyAsync<CaptureControlConflictException>(async () =>
                await fixture.Coordinator.ResumeAsync($"lifecycle:{operation:D}:resume-3", withExpectedVersion ? 1 : null,
                    installerActor, null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(CaptureAdmissionState.Paused, fixture.Coordinator.Snapshot.State);
            Assert.AreEqual(1L, fixture.Coordinator.Snapshot.Version);

            // The operator who paused resumes; then an installer pause it owns may be lifted by that installer alone.
            var running = await fixture.Coordinator.ResumeAsync("ui-resume", 1, "owner-1", null, CancellationToken.None).ConfigureAwait(false);
            var second = Guid.NewGuid();
            var secondActor = $"installer-lifecycle:{second:D}";
            using var pausing = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var owned = await fixture.Coordinator.PauseAsync($"lifecycle:{second:D}:pause", running.Version, secondActor, null,
                pausing.Token).ConfigureAwait(false);
            Assert.IsTrue(owned.Changed);
            var resumed = await fixture.Coordinator.ResumeAsync($"lifecycle:{second:D}:resume", withExpectedVersion ? owned.Version : null,
                secondActor, null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureAdmissionState.Running, resumed.State);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private sealed class InjectedSchemaFaultException : Exception
    {
        public InjectedSchemaFaultException()
        {
        }

        public InjectedSchemaFaultException(string message)
            : base(message)
        {
        }

        public InjectedSchemaFaultException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private sealed class OneShotSchemaFault(RawIngressFaultPoint target) : IRawIngressFaultInjector
    {
        private int _injected;

        public bool IsEnabled(RawIngressFaultPoint point) => point == target;

        public void Inject(RawIngressFaultPoint point)
        {
            if (point == target && Interlocked.Exchange(ref _injected, 1) == 0) throw new InjectedSchemaFaultException();
        }
    }
}
