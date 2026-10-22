using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using Microsoft.Data.Sqlite;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class RawIngressMaintenanceInterruptionTests
{
    [TestMethod]
    public async Task ReconcileAsync_PauseAtEveryDirectorySyncResumesMixedBatchesAfterReopen()
    {
        if (!OperatingSystem.IsLinux()) return;
        var totalSyncs = await RunBoundaryAsync(0).ConfigureAwait(false);
        for (var boundary = 1; boundary <= totalSyncs; boundary++)
            await RunBoundaryAsync(boundary).ConfigureAwait(false);
        Console.WriteLine($"Verified {totalSyncs} interruption positions across planned and discovered batches, plus a control.");
    }

    private static async Task<int> RunBoundaryAsync(int boundary)
    {
        using var hold = new Capture.MaintenanceHoldRoot();
        using var admission = await hold.AdmissionAsync(paused: false).ConfigureAwait(false);
        var db = Path.Combine(hold.Path, "journal", "raw-ingress.db");
        var journal = new SqliteRawCaptureJournal(db, 1);
        await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var raw = Path.Combine(hold.Path, "frames", "2026", "01", "01", "Raw");
        var quarantine = Path.Combine(hold.Path, "quarantine", "planned");
        Directory.CreateDirectory(raw);
        Directory.CreateDirectory(quarantine);
        // The first pair was fully moved before a crash, but its durable plan is unfinished.
        await File.WriteAllBytesAsync(Path.Combine(quarantine, "planned.bin"), new byte[] { 7 }).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(quarantine, "planned.json"), "invalid-planned").ConfigureAwait(false);
        await journal.PlanQuarantineAsync(new RawIngressPlannedQuarantine("planned", "frames/2026/01/01/Raw/planned.bin",
            "frames/2026/01/01/Raw/planned.json", "quarantine/planned", "invalid-v2-sidecar", 16), CancellationToken.None).ConfigureAwait(false);
        for (var index = 0; index < 2; index++)
        {
            await File.WriteAllBytesAsync(Path.Combine(raw, $"new-{index}.bin"), new byte[] { (byte)index }).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(raw, $"new-{index}.json"), $"invalid-{index}").ConfigureAwait(false);
        }
        var syncs = 0;
        var completedAtPause = -1;
        Task? pause = null;
        var reconciler = new RawIngressReconciler(hold.Path, journal, directorySyncRecorder: () =>
        {
            syncs++;
            if (boundary != syncs) return;
            completedAtPause = Completed(db);
            pause = admission.PauseAsync($"pause-{boundary}", null, "test", null, CancellationToken.None);
            Assert.IsFalse(pause.IsCompleted, "An active operation cannot release the barrier before its journal completion.");
        });
        var first = await reconciler.RunAsync(_ => ValueTask.FromResult(hold.Gate.EnterIfInitialized()), CancellationToken.None).ConfigureAwait(false);
        if (boundary == 0)
        {
            Assert.AreEqual(3, first.Quarantined);
            Assert.AreEqual(3, Completed(db));
            return syncs;
        }
        Assert.IsNotNull(pause, $"Boundary {boundary} was not reached.");
        await pause.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var expected = completedAtPause + 1;
        Assert.AreEqual(expected, first.Quarantined, $"Boundary {boundary} started another operation.");
        Assert.AreEqual(3 - expected, first.MaintenanceDeferred);
        Assert.AreEqual(expected, Completed(db));
        Assert.IsEmpty(await journal.ReadPlannedQuarantinesAsync(CancellationToken.None).ConfigureAwait(false));
        var held = await reconciler.RunAsync(_ => ValueTask.FromResult(hold.Gate.EnterIfInitialized()), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, held.Quarantined);
        Assert.AreEqual(3 - expected, held.MaintenanceDeferred);
        await admission.ResumeAsync($"resume-{boundary}", null, "test", null, CancellationToken.None).ConfigureAwait(false);
        var reopened = new SqliteRawCaptureJournal(db, 1);
        await reopened.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var next = new RawIngressReconciler(hold.Path, reopened);
        var resumed = await next.RunAsync(_ => ValueTask.FromResult(hold.Gate.EnterIfInitialized()), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(3 - expected, resumed.Quarantined);
        Assert.AreEqual(0, resumed.MaintenanceDeferred);
        Assert.AreEqual(3, Completed(db), "Resume must not duplicate journaled quarantine operations.");
        Assert.IsEmpty(await reopened.ReadPlannedQuarantinesAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.IsFalse(Directory.EnumerateFiles(raw).Any());
        var evidence = Directory.EnumerateFiles(Path.Combine(hold.Path, "quarantine"), "*", SearchOption.AllDirectories).ToArray();
        Assert.HasCount(6, evidence);
        CollectionAssert.AreEqual(new byte[] { 7 }, await File.ReadAllBytesAsync(evidence.Single(p => Path.GetFileName(p) == "planned.bin")).ConfigureAwait(false));
        Assert.AreEqual("invalid-planned", await File.ReadAllTextAsync(evidence.Single(p => Path.GetFileName(p) == "planned.json")).ConfigureAwait(false));
        for (var index = 0; index < 2; index++)
        {
            CollectionAssert.AreEqual(new byte[] { (byte)index }, await File.ReadAllBytesAsync(evidence.Single(p => Path.GetFileName(p) == $"new-{index}.bin")).ConfigureAwait(false));
            Assert.AreEqual($"invalid-{index}", await File.ReadAllTextAsync(evidence.Single(p => Path.GetFileName(p) == $"new-{index}.json")).ConfigureAwait(false));
        }
        var finished = await next.RunAsync(_ => ValueTask.FromResult(hold.Gate.EnterIfInitialized()), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, finished.Quarantined);
        Console.WriteLine($"Boundary {boundary}: acknowledged after {expected}; resumed {resumed.Quarantined}; durable operations 3; evidence files 6.");
        return syncs;
    }

    private static int Completed(string db)
    {
        using var connection = new SqliteConnection($"Data Source={db}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM raw_ingress_reconciliation WHERE outcome = 'quarantined' AND operation_state = 'completed'";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}

public sealed partial class ProjectedSceneStagingStoreTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InitializeAsync_ResumeBeforeReadinessPublicationRetriesDeferredMaintenance(bool quarantine)
    {
        if (!quarantine && !OperatingSystem.IsLinux()) return;
        using var hold = new Capture.MaintenanceHoldRoot();
        using var admission = await hold.AdmissionAsync(paused: true).ConfigureAwait(false);
        var root = quarantine ? Path.Combine(hold.Path, "frames", "2026", "01", "01", "Raw") : Path.Combine(hold.Path, "staging", "projected-scenes");
        Directory.CreateDirectory(root);
        for (var index = 0; index < 3; index++)
            await File.WriteAllTextAsync(Path.Combine(root, quarantine ? $"{index}.bin" : $".{index}.tmp"), "incomplete").ConfigureAwait(false);
        using var staging = CreateStore(hold.Path);
        using var lifecycle = new ProjectedSceneStageLifecycleCoordinator();
        var state = new RawIngressState(TimeProvider.System);
        using var telemetry = new RawIngressTelemetry(state);
        var logger = new InitializationReadinessLogger();
        using var ingress = new RawCaptureIngress(Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = hold.Path,
            RawIngressReserveBytes = 0,
            RawIngressSqliteBusyTimeoutSeconds = 1
        }), new FileSystemStorageCapacityProvider(), state, TimeProvider.System, telemetry, logger,
            new NullRawIngressFaultInjector(), projectedSceneStaging: staging, projectedSceneLifecycle: lifecycle, maintenanceGate: hold.Gate);
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        await admission.ResumeAsync("first-resume", null, "test", null, CancellationToken.None).ConfigureAwait(false);
        Task? pause = null;
        var deletes = 0;
        staging.BeforeDirectorySyncForTest = _ =>
        {
            deletes++;
            if (deletes == 1) pause = admission.PauseAsync("second-pause", null, "test", null, CancellationToken.None);
        };
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (quarantine && instrument.Meter.Name == RawIngressTelemetry.MeterName && instrument.Name == "camera_agent.ingress.sqlite.transactions")
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, listenerState) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key != "operation" || !Equals(tag.Value, "reconciliation-plan")) continue;
                deletes++;
                if (deletes == 1) pause = admission.PauseAsync("second-pause", null, "test", null, CancellationToken.None);
            }
        });
        listener.Start();
        // Event 2040 gates initialization after its lease releases and before _initialized=true.
        var beforeReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        logger.OnInitialized = () =>
        {
            beforeReady.TrySetResult();
            finishReady.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        };
        var initializing = Task.Run(async () => await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false));
        try
        {
            await beforeReady.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.IsNotNull(pause);
            await pause.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await admission.ResumeAsync("racing-resume", null, "test", null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            finishReady.TrySetResult();
            await initializing.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            logger.OnInitialized = null;
        }
        Console.WriteLine($"quarantine={quarantine}; admission={admission.Snapshot.State}; operations={deletes}; remaining={Directory.EnumerateFiles(root).Count()}");
        Assert.AreEqual(1, deletes);
        Assert.AreEqual(CaptureAdmissionState.Running, admission.Snapshot.State);
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(3, deletes, "The Running notification before initialization completion was lost, leaving deferred work unprocessed on the next pass.");
        Assert.IsFalse(Directory.EnumerateFiles(root).Any());
    }

    private sealed class InitializationReadinessLogger : ILogger<RawCaptureIngress>
    {
        internal Action? OnInitialized { get; set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 2040) OnInitialized?.Invoke();
        }
    }
}
