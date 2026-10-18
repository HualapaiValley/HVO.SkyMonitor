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

    private static RawCaptureIngress CreateIngress(
        string root,
        IProjectedSceneStagingReconciler? staging,
        out RawIngressTelemetry telemetry)
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
            projectedSceneStaging: staging);
    }
}
