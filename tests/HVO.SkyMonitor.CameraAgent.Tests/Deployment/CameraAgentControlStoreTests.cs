using HVO.SkyMonitor.CameraAgent.Common.Deployment;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Tests.Automation;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Deployment;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentControlStoreTests
{
    private DirectoryInfo _root = null!;
    private MutableTimeProvider _clock = null!;
    private static readonly Guid Worker = new("ea0691e3-f6c4-482b-a911-49be1cdb95e2");

    [TestInitialize]
    public void Initialize()
    {
        _root = Directory.CreateTempSubdirectory("hvo-1711-controls-");
        _clock = new(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
    }

    [TestCleanup]
    public void Cleanup() => _root.Delete(true);

    [TestMethod]
    public async Task DrainIsUnavailableUntilInstallerRegisters_HeartbeatExpiresFailClosed()
    {
        using var controls = Create();
        Assert.IsFalse(controls.Available);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controls.SubmitAsync(Request(), CancellationToken.None)).ConfigureAwait(false);
        await RegisterAsync(controls).ConfigureAwait(false);
        Assert.IsTrue(controls.Available);
        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.IsFalse(controls.Available);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controls.SubmitAsync(Request(), CancellationToken.None)).ConfigureAwait(false);
        Assert.IsEmpty((await controls.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Receipts);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("line\nbreak")]
    [DataRow("\u001b[31mreason")]
    public async Task InvalidReasonIsRejectedWithoutDurableIntent(string reason)
    {
        using var controls = Create();
        await RegisterAsync(controls).ConfigureAwait(false);
        await Assert.ThrowsAsync<ArgumentException>(() => controls.SubmitAsync(Request() with { Reason = reason }, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsEmpty((await controls.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Receipts);
    }

    [TestMethod]
    public async Task ReplayPreservesOriginalRequestTime_ChangedReasonConflicts_SecondPendingRequestIsRefused()
    {
        using var controls = Create();
        await RegisterAsync(controls).ConfigureAwait(false);
        var request = Request();
        var first = await controls.SubmitAsync(request, CancellationToken.None).ConfigureAwait(false);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var replayed = await controls.SubmitAsync(request with { RequestedUtc = _clock.GetUtcNow() }, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(first.Request, replayed.Request);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controls.SubmitAsync(request with { Reason = "different" }, CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controls.SubmitAsync(Request(), CancellationToken.None)).ConfigureAwait(false);
        Assert.HasCount(1, (await controls.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Receipts);
    }

    [TestMethod]
    public async Task CancellationAndIntentSurviveHostRestart_CapabilityRequiresFreshRegistration()
    {
        var request = Request();
        using (var controls = Create())
        {
            await RegisterAsync(controls).ConfigureAwait(false);
            await controls.SubmitAsync(request, CancellationToken.None).ConfigureAwait(false);
            await controls.CancelAsync(request.OperationId, CancellationToken.None).ConfigureAwait(false);
        }
        using var restarted = Create();
        Assert.IsFalse(restarted.Available);
        var (current, cancelled) = await restarted.ExchangeAsync(new(Worker), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(request, current!.Request);
        Assert.IsTrue(cancelled);
        Assert.IsTrue(restarted.Available);
    }

    [TestMethod]
    public async Task WorkerIdentityCannotStealALiveLease()
    {
        using var controls = Create();
        await RegisterAsync(controls).ConfigureAwait(false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controls.ExchangeAsync(new(Guid.NewGuid()), CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task TerminalReceiptIsDurableAndImmutable_RetryIsIdempotent()
    {
        var request = Request();
        CameraAgentControlReceipt terminal;
        using (var controls = Create())
        {
            await RegisterAsync(controls).ConfigureAwait(false);
            var queued = await controls.SubmitAsync(request, CancellationToken.None).ConfigureAwait(false);
            terminal = queued with
            {
                Phase = CameraAgentControlPhase.Failed,
                Events = [.. queued.Events, new(CameraAgentControlPhase.Failed, _clock.GetUtcNow(), "drain.timeout")]
            };
            await controls.ExchangeAsync(new(Worker, terminal), CancellationToken.None).ConfigureAwait(false);
            await controls.ExchangeAsync(new(Worker, terminal), CancellationToken.None).ConfigureAwait(false);
            await Assert.ThrowsAsync<InvalidOperationException>(() => controls.ExchangeAsync(new(Worker,
                terminal with { Phase = CameraAgentControlPhase.Queued }), CancellationToken.None)).ConfigureAwait(false);
        }
        using var restarted = Create();
        var receipts = (await restarted.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Receipts;
        Assert.HasCount(1, receipts);
        Assert.AreEqual(terminal.Request, receipts[0].Request);
        Assert.AreEqual(CameraAgentControlPhase.Failed, receipts[0].Phase);
    }

    [TestMethod]
    public async Task ReceiptCannotChangeTheActorOrClaimUnverifiedRestartSuccess()
    {
        using var controls = Create();
        await RegisterAsync(controls).ConfigureAwait(false);
        var queued = await controls.SubmitAsync(Request() with { Kind = CameraAgentControlKind.Restart }, CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controls.ExchangeAsync(new(Worker,
            queued with { Request = queued.Request with { Actor = "forged" } }), CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controls.ExchangeAsync(new(Worker,
            queued with
            {
                Phase = CameraAgentControlPhase.Succeeded,
                Events = [.. queued.Events, new(CameraAgentControlPhase.Succeeded, _clock.GetUtcNow(), "restart.verified")]
            }), CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RestartMechanismRequiresBoundInstallerReceiptAndExactPauseVersion_CancellationWithdrawsIt()
    {
        using var controls = Create();
        await RegisterAsync(controls).ConfigureAwait(false);
        var queued = await controls.SubmitAsync(Request() with { Kind = CameraAgentControlKind.Restart }, CancellationToken.None).ConfigureAwait(false);
        var restarting = queued with
        {
            Phase = CameraAgentControlPhase.Restarting,
            PausedVersion = 9,
            Events = [.. queued.Events, new(CameraAgentControlPhase.Draining, _clock.GetUtcNow(), "lifecycle.draining"),
                new(CameraAgentControlPhase.Restarting, _clock.GetUtcNow(), "restart.requesting")]
        };
        await controls.ExchangeAsync(new(Worker, restarting), CancellationToken.None).ConfigureAwait(false);
        var command = new CameraAgentControlRestart(Worker, queued.Request.OperationId, 9);
        Assert.IsTrue(await controls.MayRestartAsync(command, 9, CancellationToken.None).ConfigureAwait(false));
        Assert.IsFalse(await controls.MayRestartAsync(command, 10, CancellationToken.None).ConfigureAwait(false));
        Assert.IsFalse(await controls.MayRestartAsync(command with { WorkerId = Guid.NewGuid() }, 9, CancellationToken.None).ConfigureAwait(false));
        await controls.CancelAsync(queued.Request.OperationId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(await controls.MayRestartAsync(command, 9, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CorruptOrLinkedStateFailsClosed()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root.FullName, ".lifecycle-control"));
        var path = Path.Combine(folder.FullName, "state.json");
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":99,\"receipts\":[],\"cancelledOperationId\":null}").ConfigureAwait(false);
        using (var controls = Create())
            await Assert.ThrowsAsync<InvalidDataException>(() => controls.ReadAsync(CancellationToken.None)).ConfigureAwait(false);
        if (!OperatingSystem.IsLinux()) return;
        File.Delete(path);
        File.CreateSymbolicLink(path, Path.Combine(_root.FullName, "outside.json"));
        using var linked = Create();
        await Assert.ThrowsAsync<SkyMonitor.Storage.FileSystem.FileSystemFaultException>(() => linked.ReadAsync(CancellationToken.None)).ConfigureAwait(false);
    }

    private CameraAgentControlRequest Request() => new(Guid.NewGuid(), CameraAgentControlKind.Drain, "owner",
        "planned maintenance", 7, _clock.GetUtcNow());
    private CameraAgentControlStore Create() => new(Options.Create(new CameraAgentHostOptions { RawIngressRoot = _root.FullName }), _clock);
    private static Task<(CameraAgentControlReceipt? Current, bool Cancelled)> RegisterAsync(CameraAgentControlStore controls) => controls.ExchangeAsync(new(Worker), CancellationToken.None);
}
