using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture;

/// <summary>
/// Retention and cleanup of bulk capture data are held while capture admission is not running (#1314), which is
/// how an installer upgrade or rollback protects what the previous image needs while the swap is undecided.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CaptureMaintenanceHoldTests
{
    [TestMethod]
    public async Task HeldByAsync_RunningAdmission_HoldsNothing()
    {
        using var root = new MaintenanceHoldRoot();
        using var admission = await root.AdmissionAsync(paused: false).ConfigureAwait(false);

        Assert.IsNull(await CaptureMaintenanceHold.HeldByAsync(admission, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task HeldByAsync_DurablePauseSeenByARestartedImage_HoldsMaintenance()
    {
        // The candidate image starts on the journal the previous image paused, before anything resumes it.
        using var root = new MaintenanceHoldRoot();
        using var admission = await root.AdmissionAsync(paused: true).ConfigureAwait(false);

        Assert.AreEqual(
            CaptureAdmissionState.Paused,
            await CaptureMaintenanceHold.HeldByAsync(admission, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task HeldByAsync_ResumedAdmission_ReleasesTheHold()
    {
        using var root = new MaintenanceHoldRoot();
        using var admission = await root.AdmissionAsync(paused: true).ConfigureAwait(false);

        await admission.ResumeAsync("hold-resume", null, "test", null, CancellationToken.None).ConfigureAwait(false);

        Assert.IsNull(await CaptureMaintenanceHold.HeldByAsync(admission, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task HeldByAsync_WithoutAdmission_HoldsNothing()
        => Assert.IsNull(await CaptureMaintenanceHold.HeldByAsync(null, CancellationToken.None).ConfigureAwait(false));
}

/// <summary>A raw-ingress root whose capture admission a test can leave running or durably paused.</summary>
internal sealed class MaintenanceHoldRoot : IDisposable
{
    private readonly List<IDisposable> owned = [];

    public MaintenanceHoldRoot()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hvo-maintenance-hold-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>
    /// A coordinator initialized from the durable journal, as a freshly started image sees it. With
    /// <paramref name="paused"/>, an earlier coordinator paused admission first and was then disposed.
    /// </summary>
    public async Task<CaptureAdmissionCoordinator> AdmissionAsync(bool paused)
    {
        if (paused)
        {
            using var firstTelemetry = new CaptureControlTelemetry();
            using var first = Create(firstTelemetry);
            await first.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await first.PauseAsync("hold-pause", null, "installer-lifecycle:test", null, CancellationToken.None).ConfigureAwait(false);
        }
        var telemetry = new CaptureControlTelemetry();
        owned.Add(telemetry);
        var admission = Create(telemetry);
        await admission.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(paused ? CaptureAdmissionState.Paused : CaptureAdmissionState.Running, admission.Snapshot.State);
        return admission;
    }

    private CaptureAdmissionCoordinator Create(CaptureControlTelemetry telemetry)
        => new(new JournalIngress(Path), Options.Create(new CameraAgentHostOptions { RawIngressRoot = Path }), TimeProvider.System, telemetry);

    public void Dispose()
    {
        foreach (var disposable in owned) disposable.Dispose();
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }

    private sealed class JournalIngress(string root) : IRawCaptureIngress
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken)
            => new(new SqliteRawCaptureJournal(System.IO.Path.Combine(root, "journal", "raw-ingress.db"), 1)
                .InitializeAsync(cancellationToken));

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration, CaptureLoopSubmission submission, CancellationToken cancellationToken)
            => ValueTask.FromResult<RawCaptureReceipt?>(null);
    }
}
