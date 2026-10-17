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
    public async Task EnterAsync_RunningAdmission_AdmitsThePass()
    {
        using var root = new MaintenanceHoldRoot();
        using var admission = await root.AdmissionAsync(paused: false).ConfigureAwait(false);

        using var entry = await CaptureMaintenanceHold.EnterAsync(admission, CancellationToken.None).ConfigureAwait(false);

        Assert.IsFalse(entry.Held);
        Assert.IsNotNull(entry.Lease);
    }

    [TestMethod]
    public async Task EnterAsync_DurablePauseSeenByARestartedImage_HoldsTheDeletions()
    {
        // The candidate image starts on the journal the previous image paused, before anything resumes it.
        using var root = new MaintenanceHoldRoot();
        using var admission = await root.AdmissionAsync(paused: true).ConfigureAwait(false);

        using var entry = await CaptureMaintenanceHold.EnterAsync(admission, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CaptureAdmissionState.Paused, entry.HeldBy);
        Assert.IsNull(entry.Lease);
    }

    [TestMethod]
    public async Task EnterAsync_ResumedAdmission_ReleasesTheHold()
    {
        using var root = new MaintenanceHoldRoot();
        using var admission = await root.AdmissionAsync(paused: true).ConfigureAwait(false);

        await admission.ResumeAsync("hold-resume", null, "test", null, CancellationToken.None).ConfigureAwait(false);

        using var entry = await CaptureMaintenanceHold.EnterAsync(admission, CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(entry.Held);
    }

    [TestMethod]
    public async Task EnterAsync_WithoutAdmission_HoldsNothing()
    {
        using var entry = await CaptureMaintenanceHold.EnterAsync(null, CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(entry.Held);
    }

    [TestMethod]
    public async Task PauseAsync_IsAcknowledgedOnlyAfterEveryAdmittedPassHasFinished()
    {
        // The deletion barrier itself: an admitted pass holds the pause until it releases its lease, and once the gate
        // closes for the pause no new pass is admitted, so nothing can delete after the acknowledgement.
        using var root = new MaintenanceHoldRoot();
        using var admission = await root.AdmissionAsync(paused: false).ConfigureAwait(false);
        var entry = await CaptureMaintenanceHold.EnterAsync(admission, CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(entry.Held);

        var pause = admission.PauseAsync("barrier", null, "installer-lifecycle:test", null, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false);
        Assert.IsFalse(pause.IsCompleted);
        using (var late = await CaptureMaintenanceHold.EnterAsync(admission, CancellationToken.None).ConfigureAwait(false))
        {
            Assert.IsTrue(late.Held, "A pass was admitted after the pause closed the gate.");
        }

        entry.Dispose();
        Assert.AreEqual(CaptureAdmissionState.Paused, (await pause.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)).State);
    }
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
