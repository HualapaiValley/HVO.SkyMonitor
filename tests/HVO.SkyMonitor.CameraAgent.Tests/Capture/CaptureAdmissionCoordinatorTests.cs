using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.Fleet.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test helpers receive only constant SQL from this test class.")]
public sealed class CaptureAdmissionCoordinatorTests
{
    [TestMethod]
    public async Task SchemaV6MigrationAddsFreshRunningControlStateAndAuditAsync()
    {
        var root = CreateRoot();
        try
        {
            var journal = CreateJournal(root);
            await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using (var connection = await OpenAsync(root).ConfigureAwait(false))
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    DROP TABLE capture_control_commands;
                    DROP TABLE capture_control_state;
                    PRAGMA user_version = 5;
                    """;
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await CreateJournal(root).InitializeAsync(CancellationToken.None).ConfigureAwait(false);

            using var verify = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(10L, await ScalarLongAsync(verify, "PRAGMA user_version;").ConfigureAwait(false));
            Assert.AreEqual("running", await ScalarStringAsync(
                verify, "SELECT state FROM capture_control_state WHERE state_key = 1;").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarLongAsync(
                verify, "SELECT version FROM capture_control_state WHERE state_key = 1;").ConfigureAwait(false));
            Assert.AreEqual(2L, await ScalarLongAsync(
                verify,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('capture_control_state','capture_control_commands');")
                .ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task PauseAndPauseRequestedSurviveRestartAsPausedAsync()
    {
        var root = CreateRoot();
        try
        {
            using (var fixture = await CoordinatorFixture.CreateAsync(root).ConfigureAwait(false))
            {
                Assert.AreEqual(CaptureAdmissionState.Running, fixture.Coordinator.Snapshot.State);
                var paused = await fixture.Coordinator.PauseAsync(
                    "pause-restart", 0, "owner-1", "maintenance", CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureAdmissionState.Paused, paused.State);
            }

            using (var restarted = await CoordinatorFixture.CreateAsync(root).ConfigureAwait(false))
            {
                Assert.AreEqual(CaptureAdmissionState.Paused, restarted.Coordinator.Snapshot.State);
            }

            using (var connection = await OpenAsync(root).ConfigureAwait(false))
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE capture_control_state
                    SET state = 'pause_requested', version = version + 1;
                    INSERT INTO capture_control_commands(
                        idempotency_key, target_state, actor, payload_sha256, status, changed, requested_unix_ms)
                    VALUES ('interrupted-pause', 'paused', 'owner-1', $hash, 'pending', 1, $now);
                    """;
                command.Parameters.AddWithValue("$hash", new string('A', 64));
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            using (var recovered = await CoordinatorFixture.CreateAsync(root).ConfigureAwait(false))
            {
                Assert.AreEqual(CaptureAdmissionState.Paused, recovered.Coordinator.Snapshot.State);
                using var verify = await OpenAsync(root).ConfigureAwait(false);
                Assert.AreEqual("completed", await ScalarStringAsync(
                    verify,
                    "SELECT status FROM capture_control_commands WHERE idempotency_key = 'interrupted-pause';")
                    .ConfigureAwait(false));
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task CaptureBoundaryCancellationReopensAdmissionAfterInterruptedDrainAsync()
    {
        var root = CreateRoot();
        try
        {
            using var fixture = await CoordinatorFixture.CreateAsync(root).ConfigureAwait(false);
            var admission = await fixture.Coordinator.EnterAsync(CancellationToken.None).ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource();
            var boundary = fixture.Coordinator.ExecuteCaptureBoundaryAsync(
                _ => Task.FromResult(true), cancellation.Token);
            await Task.Delay(50).ConfigureAwait(false);
            Assert.IsFalse(boundary.IsCompleted);

            await cancellation.CancelAsync().ConfigureAwait(false);
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => boundary).ConfigureAwait(false);
            admission.MarkNoPublicationRequired();
            admission.Dispose();

            using (var reopened = await fixture.Coordinator.EnterAsync(CancellationToken.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false))
            {
            }
            Assert.AreEqual(CaptureAdmissionState.Running, fixture.Coordinator.Snapshot.State);

            using var actionCancellation = new CancellationTokenSource();
            var actionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var action = fixture.Coordinator.ExecuteCaptureBoundaryAsync(
                async token =>
                {
                    actionStarted.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                    return true;
                },
                actionCancellation.Token);
            await actionStarted.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            await actionCancellation.CancelAsync().ConfigureAwait(false);
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => action).ConfigureAwait(false);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task CommandsEnforceReplayCollisionVersionAndAuditedNoOpAsync()
    {
        var root = CreateRoot();
        try
        {
            using var fixture = await CoordinatorFixture.CreateAsync(root).ConfigureAwait(false);
            var paused = await fixture.Coordinator.PauseAsync(
                "pause-1", 0, "owner-1", "weather", CancellationToken.None).ConfigureAwait(false);
            var replay = await fixture.Coordinator.PauseAsync(
                "pause-1", 0, "owner-1", "weather", CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(replay.Replayed);
            Assert.AreEqual(paused.State, replay.State);
            Assert.AreEqual(paused.Version, replay.Version);
            Assert.AreEqual(paused.CompletedUtc, replay.CompletedUtc);

            await Assert.ThrowsExactlyAsync<CaptureControlConflictException>(async () =>
                await fixture.Coordinator.PauseAsync(
                    "pause-1", 0, "owner-1", "different", CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<CaptureControlConflictException>(async () =>
                await fixture.Coordinator.ResumeAsync(
                    "resume-wrong-version", paused.Version - 1, "owner-1", null, CancellationToken.None)
                    .ConfigureAwait(false)).ConfigureAwait(false);

            var resumed = await fixture.Coordinator.ResumeAsync(
                "resume-1", paused.Version, "owner-1", "clear", CancellationToken.None).ConfigureAwait(false);
            var historicalReplay = await fixture.Coordinator.PauseAsync(
                "pause-1", 0, "owner-1", "weather", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureAdmissionState.Paused, historicalReplay.State);
            Assert.AreEqual(CaptureAdmissionState.Running, fixture.Coordinator.Snapshot.State);
            using (await fixture.Coordinator.EnterAsync(CancellationToken.None).ConfigureAwait(false))
            {
            }
            var noOp = await fixture.Coordinator.ResumeAsync(
                "resume-no-op", resumed.Version, "owner-1", "already running", CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsFalse(noOp.Changed);
            Assert.AreEqual(resumed.Version, noOp.Version);

            using var verify = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(3L, await ScalarLongAsync(
                verify, "SELECT COUNT(*) FROM capture_control_commands;").ConfigureAwait(false));
            Assert.AreEqual("owner-1", await ScalarStringAsync(
                verify, "SELECT actor FROM capture_control_commands WHERE idempotency_key = 'resume-no-op';")
                .ConfigureAwait(false));
            Assert.AreEqual("already running", await ScalarStringAsync(
                verify, "SELECT reason FROM capture_control_commands WHERE idempotency_key = 'resume-no-op';")
                .ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarLongAsync(
                verify, "SELECT changed FROM capture_control_commands WHERE idempotency_key = 'resume-no-op';")
                .ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task PauseDrainsExposureAndIngressAndAcknowledgementPreventsFurtherExposureAsync()
    {
        var root = CreateRoot();
        try
        {
            using var fixture = await CoordinatorFixture.CreateAsync(root).ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource();
            var module = new ControlledModule();
            var ingress = new ControlledIngress(cancellation);
            var context = new CaptureHostContext(
                CreateConfiguration(), ingress, new NullDistributor());
            var runner = new CameraModuleRunner(
                module,
                context,
                TimeProvider.System,
                NullLogger.Instance,
                fixture.Coordinator);
            var run = runner.RunAsync(cancellation.Token);
            await module.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            var pause = fixture.Coordinator.PauseAsync(
                "pause-drain", 0, "owner-1", null, CancellationToken.None);
            await WaitForStateAsync(fixture.Coordinator, CaptureAdmissionState.PauseRequested).ConfigureAwait(false);
            Assert.IsFalse(pause.IsCompleted);

            module.Release.TrySetResult();
            await ingress.AcceptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Assert.IsFalse(pause.IsCompleted);
            ingress.ReleaseAccept.TrySetResult();
            var paused = await pause.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Assert.AreEqual(CaptureAdmissionState.Paused, paused.State);

            await Task.Delay(100).ConfigureAwait(false);
            Assert.AreEqual(1, module.Attempts);
            using (var blockedCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            {
                await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                    await fixture.Coordinator.EnterAsync(blockedCancellation.Token).ConfigureAwait(false))
                    .ConfigureAwait(false);
            }

            await cancellation.CancelAsync().ConfigureAwait(false);
            await run.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task PauseFailsClosedWhenAdmittedExposureDoesNotPublishDurablyAsync()
    {
        var root = CreateRoot();
        try
        {
            using (var fixture = await CoordinatorFixture.CreateAsync(root).ConfigureAwait(false))
            using (var cancellation = new CancellationTokenSource())
            {
                var module = new ControlledModule();
                var context = new ControlledHostContext(
                    CreateConfiguration(), cancellation, failPublication: true);
                var runner = new CameraModuleRunner(
                    module, context, TimeProvider.System, NullLogger.Instance, fixture.Coordinator);
                var run = runner.RunAsync(cancellation.Token);
                await module.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

                var pause = fixture.Coordinator.PauseAsync(
                    "pause-publication-failure", 0, "owner-1", null, CancellationToken.None);
                await WaitForStateAsync(fixture.Coordinator, CaptureAdmissionState.PauseRequested).ConfigureAwait(false);
                module.Release.TrySetResult();
                await context.IngressStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                context.ReleaseIngress.TrySetResult();

                await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                    await run.ConfigureAwait(false)).ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<CaptureAdmissionUnavailableException>(async () =>
                    await pause.ConfigureAwait(false)).ConfigureAwait(false);
                Assert.AreEqual(CaptureAdmissionState.Unavailable, fixture.Coordinator.Snapshot.State);
                Assert.AreEqual(FleetAvailability.Unavailable, fixture.RuntimeState.Snapshot.Capture.Availability);

                using var verify = await OpenAsync(root).ConfigureAwait(false);
                Assert.AreEqual("pause_requested", await ScalarStringAsync(
                    verify, "SELECT state FROM capture_control_state WHERE state_key = 1;").ConfigureAwait(false));
                Assert.AreEqual("pending", await ScalarStringAsync(
                    verify, "SELECT status FROM capture_control_commands WHERE idempotency_key = 'pause-publication-failure';")
                    .ConfigureAwait(false));
                Assert.AreEqual(0L, await ScalarLongAsync(
                    verify, "SELECT COUNT(*) FROM capture_control_commands WHERE idempotency_key = 'pause-publication-failure' AND completed_unix_ms IS NOT NULL;")
                    .ConfigureAwait(false));
            }

            using var restarted = await CoordinatorFixture.CreateAsync(root).ConfigureAwait(false);
            Assert.AreEqual(CaptureAdmissionState.Paused, restarted.Coordinator.Snapshot.State);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PauseSafelyDrainsAcquisitionFailureWithoutPendingPublicationAsync(bool returnNull)
    {
        var root = CreateRoot();
        try
        {
            using var fixture = await CoordinatorFixture.CreateAsync(root).ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource();
            var module = new FailingControlledModule(returnNull);
            var context = new ControlledHostContext(CreateConfiguration(), cancellation, releaseImmediately: true);
            var runner = new CameraModuleRunner(
                module, context, TimeProvider.System, NullLogger.Instance, fixture.Coordinator);
            var run = runner.RunAsync(cancellation.Token);
            await module.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            var pause = fixture.Coordinator.PauseAsync(
                returnNull ? "pause-null-result" : "pause-module-failure",
                0,
                "owner-1",
                null,
                CancellationToken.None);
            await WaitForStateAsync(fixture.Coordinator, CaptureAdmissionState.PauseRequested).ConfigureAwait(false);
            module.Release.TrySetResult();

            var paused = await pause.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Assert.AreEqual(CaptureAdmissionState.Paused, paused.State);
            Assert.IsFalse(context.IngressStarted.Task.IsCompleted);
            await cancellation.CancelAsync().ConfigureAwait(false);
            await run.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task PausedBeforeRunnerBlocksExposureAndResumeCommitsRunningBeforeAdmissionAsync()
    {
        var root = CreateRoot();
        try
        {
            using var fixture = await CoordinatorFixture.CreateAsync(root).ConfigureAwait(false);
            var paused = await fixture.Coordinator.PauseAsync(
                "pause-before", 0, "owner-1", null, CancellationToken.None).ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource();
            var module = new ControlledModule(releaseImmediately: true);
            var context = new ControlledHostContext(CreateConfiguration(), cancellation, releaseImmediately: true);
            var runner = new CameraModuleRunner(
                module, context, TimeProvider.System, NullLogger.Instance, fixture.Coordinator);
            var run = runner.RunAsync(cancellation.Token);
            await Task.Delay(100).ConfigureAwait(false);
            Assert.AreEqual(0, module.Attempts);

            await fixture.Coordinator.ResumeAsync(
                "resume-before", paused.Version, "owner-1", null, CancellationToken.None).ConfigureAwait(false);
            await module.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            using var verify = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual("running", await ScalarStringAsync(
                verify, "SELECT state FROM capture_control_state WHERE state_key = 1;").ConfigureAwait(false));

            await cancellation.CancelAsync().ConfigureAwait(false);
            await run.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task CommandBoundsAreValidatedBeforePersistenceAsync()
    {
        var root = CreateRoot();
        try
        {
            using var fixture = await CoordinatorFixture.CreateAsync(root).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<CaptureControlValidationException>(async () =>
                await fixture.Coordinator.PauseAsync(
                    new string('k', 129), null, "owner", null, CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<CaptureControlValidationException>(async () =>
                await fixture.Coordinator.PauseAsync(
                    "bounded", null, new string('a', 129), null, CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<CaptureControlValidationException>(async () =>
                await fixture.Coordinator.PauseAsync(
                    "bounded", null, "owner", new string('r', 513), CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task WaitForStateAsync(
        CaptureAdmissionCoordinator coordinator,
        CaptureAdmissionState expected)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(2);
        while (coordinator.Snapshot.State != expected && DateTimeOffset.UtcNow < timeout)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
        Assert.AreEqual(expected, coordinator.Snapshot.State);
    }

    private static CameraModuleConfig CreateConfiguration()
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("Test"),
            new CameraRigConfig(
                new SensorProfile("Test", 1, 1, 1, SensorColorMode.Mono, CameraPixelFormat.Mono8),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.Zero, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), 0, 0)));

    private static SqliteRawCaptureJournal CreateJournal(string root)
        => new(Path.Combine(root, "journal", "raw-ingress.db"), 1);

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-capture-control-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task<SqliteConnection> OpenAsync(string root)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private sealed class CoordinatorFixture : IDisposable
    {
        private readonly CaptureControlTelemetry _telemetry;

        private CoordinatorFixture(
            CaptureAdmissionCoordinator coordinator,
            CaptureControlTelemetry telemetry,
            FleetRuntimeState runtimeState)
        {
            Coordinator = coordinator;
            _telemetry = telemetry;
            RuntimeState = runtimeState;
        }

        internal CaptureAdmissionCoordinator Coordinator { get; }

        internal FleetRuntimeState RuntimeState { get; }

        internal static async Task<CoordinatorFixture> CreateAsync(string root)
        {
            var telemetry = new CaptureControlTelemetry();
            var runtimeState = new FleetRuntimeState(TimeProvider.System);
            var coordinator = new CaptureAdmissionCoordinator(
                new InitializingIngress(root),
                Options.Create(new CameraAgentHostOptions
                {
                    RawIngressRoot = root,
                    RawIngressSqliteBusyTimeoutSeconds = 1
                }),
                TimeProvider.System,
                telemetry,
                runtimeState);
            await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            return new CoordinatorFixture(coordinator, telemetry, runtimeState);
        }

        public void Dispose()
        {
            Coordinator.Dispose();
            _telemetry.Dispose();
        }
    }

    private sealed class InitializingIngress(string root) : IRawCaptureIngress
    {
        public async ValueTask InitializeAsync(CancellationToken cancellationToken)
            => await CreateJournal(root).InitializeAsync(cancellationToken).ConfigureAwait(false);

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<RawCaptureReceipt?>(null);
    }

    private sealed class ControlledModule(bool releaseImmediately = false) : ICameraModule
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = CompletedOrPending(releaseImmediately);
        internal int Attempts { get; private set; }
        public string Id => "controlled";
        public string DisplayName => "Controlled";
        public string ModuleType => "Test";
        public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;
        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
        {
            Attempts++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            return new CaptureResult(
                null,
                new CaptureSetpoint(TimeSpan.FromMilliseconds(1), 0, null, null),
                TimeSpan.Zero,
                CaptureMode.Still,
                false)
            {
                AcquisitionTiming = new CaptureAcquisitionTiming(
                    now.AddMilliseconds(-2), now.AddMilliseconds(-1), now)
            };
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingControlledModule(bool returnNull) : ICameraModule
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "failing-controlled";
        public string DisplayName => "Failing controlled";
        public string ModuleType => "Test";
        public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;
        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return returnNull ? null! : throw new IOException("Injected acquisition failure.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ControlledHostContext(
        CameraModuleConfig configuration,
        CancellationTokenSource cancellation,
        bool releaseImmediately = false,
        bool failPublication = false) : ICaptureHostContext
    {
        internal TaskCompletionSource IngressStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseIngress { get; } = CompletedOrPending(releaseImmediately);
        public CameraModuleConfig Configuration => configuration;

        public async ValueTask PublishAsync(CaptureLoopSubmission submission, CancellationToken cancellationToken)
        {
            IngressStarted.TrySetResult();
            await ReleaseIngress.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (failPublication)
            {
                throw new InvalidDataException("Injected durable publication failure.");
            }
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    private sealed class ControlledIngress(CancellationTokenSource cancellation) : IRawCaptureIngress
    {
        internal TaskCompletionSource AcceptStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseAccept { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
        {
            AcceptStarted.TrySetResult();
            await ReleaseAccept.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await cancellation.CancelAsync().ConfigureAwait(false);
            return null;
        }
    }

    private sealed class NullDistributor : ICaptureDistributor
    {
        public void NotifyCommittedCapture()
        {
        }

        public ValueTask ProcessEphemeralAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private static TaskCompletionSource CompletedOrPending(bool completed)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed)
        {
            source.TrySetResult();
        }
        return source;
    }
}
