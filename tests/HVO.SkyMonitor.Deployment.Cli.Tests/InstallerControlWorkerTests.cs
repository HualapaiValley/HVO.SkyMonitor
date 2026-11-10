using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class InstallerControlWorkerTests
{
    [TestMethod]
    [DataRow(400)]
    [DataRow(409)]
    public async Task RefusedReceiptIsDurablyQuarantinedAndDoesNotBlockHeartbeatsOrLaterWork(int status)
    {
        using var fixture = new Fixture();
        var refused = Queued() with { Phase = CameraAgentControlPhase.Failed };
        refused = refused with { Events = [.. refused.Events, new(CameraAgentControlPhase.Failed, DateTimeOffset.UtcNow, "lifecycle.failed")] };
        await fixture.Journal.SaveAsync(refused);
        fixture.Client.RefusedId = refused.Request.OperationId;
        fixture.Client.RefusalStatus = status;
        fixture.Client.Current = Queued();
        var next = fixture.Client.Current.Request.OperationId;
        await fixture.Worker.PollAsync(CancellationToken.None);
        await fixture.Worker.PollAsync(CancellationToken.None);
        Assert.AreEqual(2, fixture.Client.Heartbeats);
        Assert.AreEqual(1, fixture.Client.Refusals);
        Assert.AreEqual(CameraAgentControlPhase.Succeeded, fixture.Journal.Receipts.Single(item => item.Request.OperationId == next).Phase);
        var reloaded = new InstallerControlJournal(fixture.Path);
        await reloaded.LoadAsync(CancellationToken.None);
        Assert.AreEqual(status, reloaded.Refused.Single().StatusCode);
        Assert.AreEqual(refused.Request.OperationId, reloaded.Refused.Single().OperationId);
        Assert.IsEmpty(reloaded.Unpublished);
        StringAssert.Contains(fixture.Messages.Single(), "publication=quarantined", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task InstallerBusyRecordsTerminalReceiptWithoutAnyCaptureEffect_AndKeepsPolling()
    {
        using var fixture = new Fixture(busy: true);
        fixture.Client.Current = Queued();
        await fixture.Worker.RunAsync(true, CancellationToken.None);
        var receipt = fixture.Journal.Receipts.Single();
        Assert.AreEqual("lifecycle.installer-busy", receipt.Events[^1].Code);
        Assert.AreEqual(0, fixture.Client.Drains);
        Assert.IsEmpty(fixture.Journal.Unpublished);
        await fixture.Worker.PollAsync(CancellationToken.None);
        Assert.AreEqual(2, fixture.Client.Heartbeats);
    }

    [TestMethod]
    public async Task ActiveProjectionWithoutInstallerAuthorityIsFailedWithoutReplayingEffects()
    {
        using var fixture = new Fixture();
        var queued = Queued();
        fixture.Client.Current = queued with
        {
            Phase = CameraAgentControlPhase.Draining,
            Events = [.. queued.Events, new(CameraAgentControlPhase.Draining, DateTimeOffset.UtcNow, "lifecycle.draining")]
        };
        await fixture.Worker.RunAsync(true, CancellationToken.None);
        Assert.AreEqual("lifecycle.projection-orphaned", fixture.Journal.Receipts.Single().Events[^1].Code);
        Assert.AreEqual(0, fixture.Client.Drains);
        Assert.AreEqual(0, fixture.Client.Restarts);
    }

    [TestMethod]
    public async Task InterruptedRestartPublishesFailureWithoutRequestingAnotherStop()
    {
        using var fixture = new Fixture();
        var queued = Queued();
        var interrupted = queued with
        {
            Phase = CameraAgentControlPhase.Restarting,
            RestartRequested = true,
            Events = [.. queued.Events, new(CameraAgentControlPhase.Draining, DateTimeOffset.UtcNow, "lifecycle.draining"),
                new(CameraAgentControlPhase.Restarting, DateTimeOffset.UtcNow, "restart.requesting")]
        };
        await fixture.Journal.SaveAsync(interrupted);
        fixture.Client.Current = interrupted;
        await fixture.Worker.RunAsync(true, CancellationToken.None);
        Assert.AreEqual("lifecycle.installer-interrupted", fixture.Journal.Receipts.Single().Events[^1].Code);
        Assert.AreEqual(0, fixture.Client.Restarts);
        Assert.IsEmpty(fixture.Journal.Unpublished);
    }

    [TestMethod]
    public async Task TerminalReceiptIsRepublishedWhenProjectionStillHasQueuedRequest()
    {
        using var fixture = new Fixture();
        var queued = Queued();
        var terminal = queued with
        {
            Phase = CameraAgentControlPhase.Failed,
            Events = [.. queued.Events, new(CameraAgentControlPhase.Failed, DateTimeOffset.UtcNow, "lifecycle.failed")]
        };
        await fixture.Journal.SaveAsync(terminal);
        await fixture.Journal.AcknowledgeAsync(queued.Request.OperationId);
        fixture.Client.Current = queued;
        await fixture.Worker.PollAsync(CancellationToken.None);
        Assert.AreEqual(1, fixture.Client.TerminalPublications);
        Assert.AreEqual(0, fixture.Client.Drains);
    }

    private static CameraAgentControlReceipt Queued()
    {
        var request = new CameraAgentControlRequest(Guid.NewGuid(), CameraAgentControlKind.Drain, "owner", "maintenance", 0, DateTimeOffset.UtcNow);
        return new(request, CameraAgentControlPhase.Queued, [new(CameraAgentControlPhase.Queued, request.RequestedUtc, "lifecycle.queued")]);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("hvo-1739-worker-");
        internal string Path => System.IO.Path.Combine(_root.FullName, "worker.json");
        internal InstallerControlJournal Journal { get; }
        internal Client Client { get; } = new();
        internal List<string> Messages { get; } = [];
        internal InstallerControlWorker Worker { get; }
        internal Fixture(bool busy = false)
        {
            Journal = new(Path);
            Worker = new(Client, Journal, _ => busy ? throw InstallerException.Refused("busy") : Task.FromResult<IDisposable>(new Lease()),
                TimeProvider.System, Messages.Add);
        }
        public void Dispose() => _root.Delete(true);
    }
    private sealed class Lease : IDisposable { public void Dispose() { } }
    private sealed class Client : IInstallerControlClient
    {
        internal CameraAgentControlReceipt? Current;
        internal Guid? RefusedId;
        internal int RefusalStatus = 409, Refusals, Heartbeats, Drains, Restarts, TerminalPublications;
        private readonly Guid _process = Guid.NewGuid();
        public Task<CameraAgentControlSnapshot> ExchangeAsync(CameraAgentControlReceipt? receipt, CancellationToken cancellationToken)
        {
            if (receipt is null) Heartbeats++;
            else if (receipt.Request.OperationId == RefusedId) { Refusals++; throw new InstallerControlRefusalException(RefusalStatus); }
            else if (receipt.IsTerminal) { TerminalPublications++; Current = null; }
            else Current = receipt;
            return Task.FromResult(new CameraAgentControlSnapshot(Current, false, _process, Drains > 0 ? "Paused" : "Running", 0, true, true, true));
        }
        public Task<LifecycleContinuity> DrainAsync(CameraAgentControlRequest request, CancellationToken cancellationToken)
        { Drains++; return Task.FromResult(new LifecycleContinuity("Paused", 1, 42, 0, 0, 0, 0, 0, 0, 0, 0)); }
        public Task RestartAsync(CameraAgentControlReceipt receipt, CancellationToken cancellationToken) { Restarts++; return Task.CompletedTask; }
        public Task<LifecycleContinuity> ConfirmDrainedAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task ResumeAsync(CameraAgentControlReceipt receipt, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}
