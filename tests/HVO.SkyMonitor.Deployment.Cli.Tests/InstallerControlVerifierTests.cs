using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class InstallerControlVerifierTests
{
    private static readonly string[] RestartEffects = ["drain", "restart", "verify", "resume"];
    private static readonly string[] DrainEffects = ["drain"];
    private static readonly string[] RestoreEffects = ["drain", "resume"];
    private static readonly InstallerControlBudgets Budgets = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2),
        TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(1));

    [TestMethod]
    public async Task Success_JournalsBeforeEffectsAndVerifiesNewProcessAndContinuityBeforeResume()
    {
        using var fixture = new Fixture();
        var receipt = await fixture.RunAsync();
        Assert.AreEqual(CameraAgentControlPhase.Succeeded, receipt.Phase);
        Assert.AreNotEqual(receipt.OriginalProcessId, receipt.VerifiedProcessId);
        CollectionAssert.AreEqual(RestartEffects, fixture.Client.Effects);
        CollectionAssert.AreEqual(new[] { CameraAgentControlPhase.Queued, CameraAgentControlPhase.Draining,
            CameraAgentControlPhase.Restarting, CameraAgentControlPhase.Verifying, CameraAgentControlPhase.Succeeded },
            receipt.Events.Select(item => item.Phase).ToArray());
        Assert.AreEqual("restart.verified-resumed", receipt.Events[^1].Code);
        await fixture.ReloadAndAssertAsync(receipt);
    }

    [TestMethod]
    public async Task OriginallyPaused_RemainsPausedAfterVerifiedRestart()
    {
        using var fixture = new Fixture();
        fixture.Client.CaptureState = "Paused";
        var receipt = await fixture.RunAsync();
        Assert.AreEqual(CameraAgentControlPhase.Succeeded, receipt.Phase);
        Assert.IsFalse(receipt.ResumeAfterVerification);
        Assert.DoesNotContain("resume", fixture.Client.Effects);
    }

    [TestMethod]
    public async Task DrainOnly_FinishesAcceptedWorkAndLeavesCapturePausedWithoutRestart()
    {
        using var fixture = new Fixture(CameraAgentControlKind.Drain);
        var receipt = await fixture.RunAsync();
        Assert.AreEqual(CameraAgentControlPhase.Succeeded, receipt.Phase);
        CollectionAssert.AreEqual(DrainEffects, fixture.Client.Effects);
        Assert.AreEqual("Paused", fixture.Client.CaptureState);
        Assert.AreEqual("lifecycle.drained-paused", receipt.Events[^1].Code);
    }

    [TestMethod]
    public async Task NeverReturns_WritesTerminalFailureInInstallerJournalWithNoCameraAgentReceiptDelivery()
    {
        using var fixture = new Fixture();
        fixture.Client.NeverReturns = true;
        var receipt = await fixture.RunAsync(Budgets with { Verify = TimeSpan.FromMilliseconds(70), Read = TimeSpan.FromMilliseconds(10) });
        Assert.AreEqual(CameraAgentControlPhase.Failed, receipt.Phase);
        Assert.AreEqual("restart.verification-timeout", receipt.Events[^1].Code);
        Assert.AreEqual("Paused", fixture.Client.CaptureState);
        Assert.DoesNotContain("resume", fixture.Client.Effects);
        Assert.AreEqual(1, fixture.Client.Effects.Count(item => item == "restart"));
        Assert.IsFalse(fixture.Client.TerminalDelivered);
        await fixture.ReloadAndAssertAsync(receipt);
        var json = await File.ReadAllTextAsync(fixture.JournalPath);
        StringAssert.Contains(json, "verification-timeout", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task UnchangedProcessCannotCountAsRestart_EvenWhenHealthyAndDrained()
    {
        using var fixture = new Fixture();
        fixture.Client.KeepProcessIdentity = true;
        var receipt = await fixture.RunAsync(Budgets with { Verify = TimeSpan.FromMilliseconds(50) });
        Assert.AreEqual("restart.verification-timeout", receipt.Events[^1].Code);
        Assert.DoesNotContain("resume", fixture.Client.Effects);
    }

    [TestMethod]
    public async Task BackwardWallClockCorrectionCannotExtendVerificationWindow()
    {
        using var fixture = new Fixture();
        var clock = new CorrectedClock();
        fixture.Client.NeverReturns = true;
        fixture.Client.AfterDrain = () => clock.Corrected = true;
        var receipt = await fixture.RunAsync(Budgets with { Verify = TimeSpan.FromMilliseconds(50), Read = TimeSpan.FromMilliseconds(10) }, clock: clock);
        Assert.AreEqual("restart.verification-timeout", receipt.Events[^1].Code);
        Assert.IsTrue(receipt.IsValid(), "Observed UTC corrections do not invalidate ordered phase evidence.");
        await fixture.ReloadAndAssertAsync(receipt);
    }

    [TestMethod]
    public async Task LostRestartAcknowledgement_VerifiesSuccessAndNeverSendsSecondRestart()
    {
        using var fixture = new Fixture();
        fixture.Client.LostAcknowledgement = true;
        var receipt = await fixture.RunAsync();
        Assert.AreEqual(CameraAgentControlPhase.Succeeded, receipt.Phase);
        Assert.AreEqual(1, fixture.Client.Effects.Count(item => item == "restart"));
    }

    [TestMethod]
    public async Task HungDrain_IgnoresCancellationButCannotEscapeTheDrainDeadline()
    {
        using var fixture = new Fixture();
        fixture.Client.HungDrain = true;
        var receipt = await fixture.RunAsync(Budgets with { Drain = TimeSpan.FromMilliseconds(30) });
        Assert.AreEqual(CameraAgentControlPhase.Failed, receipt.Phase);
        Assert.AreEqual("drain.timeout", receipt.Events[^1].Code);
        Assert.DoesNotContain("restart", fixture.Client.Effects);
        Assert.IsTrue(receipt.ResumeConfirmed);
        Assert.AreEqual("Running", fixture.Client.CaptureState);
        await fixture.ReloadAndAssertAsync(receipt);
    }

    [TestMethod]
    public async Task CancellationAfterPause_RestoresOriginalRunningStateWithoutRestart()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Client.AfterDrain = cancellation.Cancel;
        var receipt = await fixture.RunAsync(cancellationToken: cancellation.Token);
        Assert.AreEqual(CameraAgentControlPhase.Cancelled, receipt.Phase);
        Assert.AreEqual("Running", fixture.Client.CaptureState);
        CollectionAssert.AreEqual(RestoreEffects, fixture.Client.Effects);
        await fixture.ReloadAndAssertAsync(receipt);
    }

    [TestMethod]
    public async Task BrowserCancellationDuringDrain_IsObservedBeforeRestart()
    {
        using var fixture = new Fixture();
        fixture.Client.AfterDrain = () => fixture.Client.CancelRequested = true;
        var receipt = await fixture.RunAsync();
        Assert.AreEqual(CameraAgentControlPhase.Cancelled, receipt.Phase);
        Assert.DoesNotContain("restart", fixture.Client.Effects);
        Assert.IsTrue(receipt.ResumeConfirmed);
    }

    [TestMethod]
    [DataRow(false, true, "restart.unsupervised")]
    [DataRow(true, false, "restart.capture-window")]
    public async Task UnsafeRestart_IsSkippedWithoutPausing(bool supervised, bool windowSafe, string code)
    {
        using var fixture = new Fixture();
        fixture.Client.Supervised = supervised;
        fixture.Client.WindowSafe = windowSafe;
        var receipt = await fixture.RunAsync();
        Assert.AreEqual(CameraAgentControlPhase.Skipped, receipt.Phase);
        Assert.AreEqual(code, receipt.Events[^1].Code);
        Assert.IsEmpty(fixture.Client.Effects);
    }

    [TestMethod]
    public async Task ExpectedVersionConflict_DoesNotOverrideAnOperatorCommand()
    {
        using var fixture = new Fixture();
        fixture.Client.Version = 99;
        var receipt = await fixture.RunAsync();
        Assert.AreEqual("lifecycle.capture-conflict", receipt.Events[^1].Code);
        Assert.IsEmpty(fixture.Client.Effects);
    }

    [TestMethod]
    public async Task WindowOpensWhileDraining_RestoresOriginalRunningStateWithoutRestart()
    {
        using var fixture = new Fixture();
        fixture.Client.AfterDrain = () => fixture.Client.WindowSafe = false;
        var receipt = await fixture.RunAsync();
        Assert.AreEqual("restart.boundary-changed", receipt.Events[^1].Code);
        Assert.AreEqual("Running", fixture.Client.CaptureState);
        Assert.DoesNotContain("restart", fixture.Client.Effects);
    }

    [TestMethod]
    [DataRow("unobserved-queues")]
    [DataRow("drain-backlog")]
    [DataRow("pause-lost")]
    [DataRow("sequence-rewind")]
    [DataRow("version-changed")]
    [DataRow("verification-backlog")]
    public async Task UnprovenBoundary_BlocksSuccessAndNeverResumes(string failure)
    {
        using var fixture = new Fixture();
        fixture.Client.Failure = failure;
        var receipt = await fixture.RunAsync();
        Assert.AreEqual(CameraAgentControlPhase.Failed, receipt.Phase);
        Assert.AreEqual(failure is "unobserved-queues" or "drain-backlog", receipt.ResumeConfirmed);
    }

    [TestMethod]
    public async Task DefiniteRestartRefusalRestoresOriginalOwnedPause()
    {
        using var fixture = new Fixture();
        fixture.Client.Failure = "restart-refused";
        var receipt = await fixture.RunAsync();
        Assert.AreEqual(CameraAgentControlPhase.Failed, receipt.Phase);
        Assert.AreEqual("restart.refused", receipt.Events[^1].Code);
        Assert.IsTrue(receipt.RestartRefused);
        Assert.IsTrue(receipt.ResumeConfirmed);
        Assert.AreEqual("Running", fixture.Client.CaptureState);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RefusedUnclaimedIntentIsCancelledWithoutAnyCaptureEffect(bool beforeInitialExchange)
    {
        using var fixture = new Fixture();
        fixture.Client.Failure = beforeInitialExchange ? "initial-claim-refused" : "claim-refused";
        var receipt = await fixture.RunAsync();
        Assert.AreEqual(CameraAgentControlPhase.Cancelled, receipt.Phase);
        Assert.AreEqual("lifecycle.claim-refused", receipt.Events[^1].Code);
        Assert.IsEmpty(fixture.Client.Effects);
    }

    [TestMethod]
    public async Task JournalPublicationFailure_BlocksTheEffectItWouldAuthorize()
    {
        var client = new FakeClient();
        var verifier = new InstallerControlVerifier(client, _ => throw new IOException("unwritable journal"), TimeProvider.System, Budgets);
        await Assert.ThrowsAsync<IOException>(() => verifier.ExecuteAsync(Queued(), CancellationToken.None));
        Assert.IsEmpty(client.Effects);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task JournalRejectsSymlinkAndKeepsOutsideEvidenceUnchanged(bool dangling)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Root.FullName, "outside.json");
        if (!dangling) await File.WriteAllTextAsync(outside, "outside evidence");
        File.CreateSymbolicLink(fixture.JournalPath, outside);
        await Assert.ThrowsAsync<InstallerException>(() => new InstallerControlJournal(fixture.JournalPath).LoadAsync(CancellationToken.None));
        if (dangling) Assert.IsFalse(File.Exists(outside));
        else Assert.AreEqual("outside evidence", await File.ReadAllTextAsync(outside));
    }

    private static CameraAgentControlReceipt Queued(CameraAgentControlKind kind = CameraAgentControlKind.Restart)
    {
        var request = new CameraAgentControlRequest(Guid.NewGuid(), kind, "owner", "maintenance", 7, DateTimeOffset.UtcNow);
        return new(request, CameraAgentControlPhase.Queued, [new(CameraAgentControlPhase.Queued, request.RequestedUtc, "lifecycle.queued")]);
    }

    private sealed class Fixture : IDisposable
    {
        internal DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("hvo-1711-verifier-");
        internal string JournalPath => Path.Combine(Root.FullName, "installer.json");
        internal FakeClient Client { get; } = new();
        private readonly CameraAgentControlReceipt _queued;

        internal Fixture(CameraAgentControlKind kind = CameraAgentControlKind.Restart)
        {
            _queued = Queued(kind);
            Client.BeforeEffect = (phase) =>
            {
                var durable = JsonSerializer.Deserialize(File.ReadAllText(JournalPath), DeploymentJsonContext.Default.InstallerControlJournalState)!;
                Assert.AreEqual(phase, durable.Receipts.Single().Phase, "The durable boundary must precede the effect.");
            };
        }

        internal async Task<CameraAgentControlReceipt> RunAsync(InstallerControlBudgets? budgets = null, TimeProvider? clock = null,
            CancellationToken cancellationToken = default)
        {
            var journal = new InstallerControlJournal(JournalPath);
            await journal.SaveAsync(_queued);
            return await new InstallerControlVerifier(Client, journal.SaveAsync, clock ?? TimeProvider.System, budgets ?? Budgets)
                .ExecuteAsync(_queued, cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }

        internal async Task ReloadAndAssertAsync(CameraAgentControlReceipt expected)
        {
            var reloaded = new InstallerControlJournal(JournalPath);
            await reloaded.LoadAsync(CancellationToken.None);
            var actual = reloaded.Receipts.Single();
            Assert.AreEqual(expected.Phase, actual.Phase);
            Assert.AreEqual(expected.Request, actual.Request);
            Assert.AreEqual(expected.Events[^1], actual.Events[^1]);
            Assert.IsTrue(actual.IsTerminal);
        }

        public void Dispose() => Root.Delete(true);
    }

    private sealed class CorrectedClock : TimeProvider
    {
        internal bool Corrected;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow - (Corrected ? TimeSpan.FromHours(1) : TimeSpan.Zero);
    }

    private sealed class FakeClient : IInstallerControlClient
    {
        internal readonly List<string> Effects = [];
        internal string CaptureState = "Running";
        internal long Version = 7;
        internal bool Supervised = true, WindowSafe = true, NeverReturns, KeepProcessIdentity, LostAcknowledgement, HungDrain, CancelRequested, TerminalDelivered;
        internal string? Failure;
        internal Action? AfterDrain;
        internal Action<CameraAgentControlPhase>? BeforeEffect;
        private readonly Guid _original = Guid.NewGuid();
        private readonly Guid _new = Guid.NewGuid();
        private bool _restarted;

        public Task<CameraAgentControlSnapshot> ExchangeAsync(CameraAgentControlReceipt? receipt, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (receipt?.Phase == CameraAgentControlPhase.Queued && Failure == "initial-claim-refused" ||
                receipt?.Phase == CameraAgentControlPhase.Draining && Failure == "claim-refused")
                throw new InstallerControlRefusalException(409);
            if (_restarted && NeverReturns) return new TaskCompletionSource<CameraAgentControlSnapshot>().Task;
            if (receipt is { IsTerminal: true }) TerminalDelivered = true;
            return Task.FromResult(new CameraAgentControlSnapshot(receipt, CancelRequested,
                _restarted && !KeepProcessIdentity ? _new : _original,
                _restarted && Failure == "pause-lost" ? "Running" : CaptureState,
                _restarted && Failure == "version-changed" ? Version + 1 : Version, true, Supervised, WindowSafe, Effects.Contains("drain") ? 8 : null));
        }

        public Task<LifecycleContinuity> DrainAsync(CameraAgentControlRequest request, CancellationToken cancellationToken)
        {
            BeforeEffect?.Invoke(CameraAgentControlPhase.Draining);
            Effects.Add("drain");
            CaptureState = "Paused";
            Version = 8;
            AfterDrain?.Invoke();
            return HungDrain ? new TaskCompletionSource<LifecycleContinuity>().Task : Task.FromResult(Boundary() with
            { OutboxPending = Failure == "drain-backlog" ? 1 : 0, QueuesObserved = Failure != "unobserved-queues" });
        }

        public Task RestartAsync(CameraAgentControlReceipt receipt, CancellationToken cancellationToken)
        {
            BeforeEffect?.Invoke(CameraAgentControlPhase.Restarting);
            Effects.Add("restart");
            if (Failure == "restart-refused") throw new InstallerControlRefusalException(409);
            _restarted = true;
            if (LostAcknowledgement) throw new HttpRequestException("response lost");
            return Task.CompletedTask;
        }

        public Task<LifecycleContinuity> ConfirmDrainedAsync(CancellationToken cancellationToken)
        {
            Effects.Add("verify");
            return Task.FromResult(Boundary() with
            {
                CaptureSequence = Failure == "sequence-rewind" ? 41 : 42,
                LaneLeased = Failure == "verification-backlog" ? 1 : 0
            });
        }

        public Task ResumeAsync(CameraAgentControlReceipt receipt, CancellationToken cancellationToken)
        {
            BeforeEffect?.Invoke(receipt.RestartRefused ? CameraAgentControlPhase.Restarting : receipt.RestartRequested ? CameraAgentControlPhase.Verifying : CameraAgentControlPhase.Draining);
            Effects.Add("resume");
            CaptureState = "Running";
            return Task.CompletedTask;
        }

        private LifecycleContinuity Boundary() => new("Paused", Version, 42, 0, 0, 0, 0, 0, 0, 0, 0);
    }
}
