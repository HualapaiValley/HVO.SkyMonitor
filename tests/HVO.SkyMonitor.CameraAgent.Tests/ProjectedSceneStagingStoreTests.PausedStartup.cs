using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>
/// Raw-ingress startup reconciles projected-scene stages before capture admission reads its persisted state. A
/// durably paused journal holds that cleanup too (#1314), as a candidate image starts on the journal the installer
/// paused and must not delete stages the previous image still owns.
/// </summary>
public sealed partial class ProjectedSceneStagingStoreTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RawIngressStartup_DurablePauseHoldsStageCleanupWhileARunningJournalPrunes(bool paused)
    {
        var root = CreateRoot();
        try
        {
            var stageKey = new string('7', 64);
            using (var staging = CreateStore(root))
            {
                var scene = await CreateSceneAsync(FixtureUtc, 10).ConfigureAwait(false);
                await staging.StageAsync(stageKey, new string('B', 64), scene, CancellationToken.None).ConfigureAwait(false);
            }
            // The previous image ran on this journal and, when paused, the installer paused it before stopping it.
            using (var previous = CreateIngress(root, staging: null, out var previousTelemetry))
            using (previousTelemetry)
            using (var controlTelemetry = new CaptureControlTelemetry())
            using (var admission = new CaptureAdmissionCoordinator(
                       previous, Options.Create(new CameraAgentHostOptions { RawIngressRoot = root }), TimeProvider.System, controlTelemetry))
            {
                await admission.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                if (paused)
                    await admission.PauseAsync("startup-hold", null, "installer-lifecycle:test", null, CancellationToken.None).ConfigureAwait(false);
            }

            // The candidate starts: raw ingress initializes with the real staging store, which prunes unowned stages.
            using var stagingStore = CreateStore(root);
            using (var candidate = CreateIngress(root, stagingStore, out var candidateTelemetry))
            using (candidateTelemetry)
            {
                await candidate.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            }

            var remaining = await stagingStore.ReadAsync(stageKey, CancellationToken.None).ConfigureAwait(false);
            if (paused) Assert.IsNotNull(remaining, "A durably paused startup deleted an unowned projected-scene stage.");
            else Assert.IsNull(remaining, "A running startup no longer reconciles unowned projected-scene stages.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RawIngressStartup_DeferredStagesYieldToSecondPauseAndResumeWithoutRepeatingDeletes()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Directory synchronization is a Linux durability boundary.");
        using var hold = new Capture.MaintenanceHoldRoot();
        using var admission = await hold.AdmissionAsync(paused: true).ConfigureAwait(false);
        var stageRoot = Path.Combine(hold.Path, "staging", "projected-scenes");
        Directory.CreateDirectory(stageRoot);
        for (var index = 0; index < 3; index++)
            await File.WriteAllTextAsync(Path.Combine(stageRoot, $".{index}.tmp"), "incomplete").ConfigureAwait(false);
        using var staging = CreateStore(hold.Path);
        using var lifecycle = new ProjectedSceneStageLifecycleCoordinator();
        using var ingress = CreateIngress(hold.Path, staging, out var telemetry, hold.Gate, lifecycle);
        using var ownedTelemetry = telemetry;
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(3, Directory.EnumerateFiles(stageRoot).ToArray());
        await admission.ResumeAsync("startup-resume", null, "test", null, CancellationToken.None).ConfigureAwait(false);

        Task? pause = null;
        var syncs = 0;
        staging.BeforeDirectorySyncForTest = _ =>
        {
            syncs++;
            if (syncs != 1) return;
            pause = admission.PauseAsync("startup-second-pause", null, "test", null, CancellationToken.None);
            Assert.IsFalse(pause.IsCompleted, "The active deletion must finish its directory sync before acknowledgement.");
        };
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(pause);
        await pause.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual(1, syncs, "A second pause must wait for one deletion, not the entire deferred startup pass.");
        Assert.HasCount(2, Directory.EnumerateFiles(stageRoot).ToArray());
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, syncs, "Acknowledged pause must remain a deletion barrier.");

        await admission.ResumeAsync("startup-second-resume", null, "test", null, CancellationToken.None).ConfigureAwait(false);
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(3, syncs, "Every deferred stage must be deleted and synced exactly once.");
        Assert.IsFalse(Directory.EnumerateFiles(stageRoot).Any());
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(3, syncs);
    }

    [TestMethod]
    public async Task RawIngressStartup_SecondPauseInterruptsDeferredStageLifecycleWait()
    {
        using var hold = new Capture.MaintenanceHoldRoot();
        using var admission = await hold.AdmissionAsync(paused: true).ConfigureAwait(false);
        var stageRoot = Path.Combine(hold.Path, "staging", "projected-scenes");
        Directory.CreateDirectory(stageRoot);
        await File.WriteAllTextAsync(Path.Combine(stageRoot, ".waiting.tmp"), "incomplete").ConfigureAwait(false);
        using var staging = CreateStore(hold.Path);
        using var lifecycle = new ProjectedSceneStageLifecycleCoordinator();
        using var ingress = CreateIngress(hold.Path, staging, out var telemetry, hold.Gate, lifecycle);
        using var ownedTelemetry = telemetry;
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        await admission.ResumeAsync("startup-wait-resume", null, "test", null, CancellationToken.None).ConfigureAwait(false);
        var stageLease = await lifecycle.AcquireReconciliationLeaseAsync(CancellationToken.None).ConfigureAwait(false);
        var initialization = ingress.InitializeAsync(CancellationToken.None).AsTask();
        Task? pause = null;
        try
        {
            Assert.IsFalse(initialization.IsCompleted);
            pause = admission.PauseAsync("startup-wait-pause", null, "test", null, CancellationToken.None);
            await initialization.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await pause.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.HasCount(1, Directory.EnumerateFiles(stageRoot).ToArray());
        }
        finally
        {
            stageLease.Dispose();
            await initialization.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (pause is not null) await pause.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        await admission.ResumeAsync("startup-wait-retry", null, "test", null, CancellationToken.None).ConfigureAwait(false);
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(Directory.EnumerateFiles(stageRoot).Any());
    }

    private static RawCaptureIngress CreateIngress(
        string root,
        IProjectedSceneStagingReconciler? staging,
        out RawIngressTelemetry telemetry,
        CaptureMaintenanceGate? maintenanceGate = null,
        ProjectedSceneStageLifecycleCoordinator? lifecycle = null)
    {
        var state = new RawIngressState(TimeProvider.System);
        telemetry = new RawIngressTelemetry(state);
        return new RawCaptureIngress(
            Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressReserveBytes = 0,
                RawIngressSqliteBusyTimeoutSeconds = 1
            }),
            new FileSystemStorageCapacityProvider(),
            state,
            TimeProvider.System,
            telemetry,
            NullLogger<RawCaptureIngress>.Instance,
            new NullRawIngressFaultInjector(),
            projectedSceneStaging: staging,
            projectedSceneLifecycle: lifecycle,
            maintenanceGate: maintenanceGate);
    }
}
