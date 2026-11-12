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
    public async Task StandaloneDrain_BrowserCancellationAfterClaim_EndsCancelledNotSucceeded()
    {
        using var fixture = new Fixture(CameraAgentControlKind.Drain);
        fixture.Client.AfterDrain = () => fixture.Client.RecordCancellation("owner-drain-cancel", "Cancel the claimed drain");
        var receipt = await fixture.RunAsync();

        Assert.AreEqual(CameraAgentControlPhase.Cancelled, receipt.Phase);
        Assert.AreNotEqual(CameraAgentControlPhase.Succeeded, receipt.Phase);
        Assert.AreNotEqual("lifecycle.drained-paused", receipt.Events[^1].Code);
        Assert.AreEqual("drain.cancelled-paused", receipt.Events[^1].Code);
        CollectionAssert.AreEqual(new[] { CameraAgentControlPhase.Queued, CameraAgentControlPhase.Draining, CameraAgentControlPhase.Cancelled },
            receipt.Events.Select(item => item.Phase).ToArray());
        Assert.AreEqual("owner-drain-cancel", receipt.Cancellation?.Actor);
        Assert.AreEqual("Cancel the claimed drain", receipt.Cancellation?.Reason);
        Assert.IsTrue(receipt.IsValid());
        // A cancelled claimed Drain keeps the documented standalone-Drain outcome: paused, with no resume or restart.
        Assert.AreEqual("Paused", fixture.Client.CaptureState);
        CollectionAssert.AreEqual(DrainEffects, fixture.Client.Effects);
        Assert.IsFalse(receipt.ResumeRequested);
        Assert.IsFalse(receipt.ResumeConfirmed);
        Assert.IsFalse(receipt.RestartRequested);
        Assert.AreEqual(8L, receipt.PausedVersion);
        Assert.IsTrue(fixture.Client.TerminalDelivered);
        await fixture.ReloadAndAssertAsync(receipt);
        var reloaded = await fixture.ReloadAsync();
        Assert.AreEqual(receipt.Cancellation, reloaded.Cancellation);
        Assert.AreEqual(receipt.PausedVersion, reloaded.PausedVersion);
    }

    [TestMethod]
    [DataRow("claim-published")]
    [DataRow("drain-in-progress")]
    [DataRow("post-drain-boundary")]
    public async Task StandaloneDrain_CancellationFromClaimToDrainEnd_IsObservedAtThePostDrainBoundary(string recordedAt)
    {
        using var fixture = new Fixture(CameraAgentControlKind.Drain);
        switch (recordedAt)
        {
            case "claim-published":
                fixture.Client.OnExchange = published =>
                {
                    if (published?.Phase == CameraAgentControlPhase.Draining && !fixture.Client.Effects.Contains("drain"))
                        fixture.Client.RecordCancellation("owner", "Cancelled as the claim was published");
                };
                break;
            case "drain-in-progress":
                fixture.Client.AfterDrain = () => fixture.Client.RecordCancellation("owner", "Cancelled before the drain returned");
                break;
            default:
                fixture.Client.OnExchange = published =>
                {
                    if (published?.Phase == CameraAgentControlPhase.Draining && fixture.Client.Effects.Contains("drain"))
                        fixture.Client.RecordCancellation("owner", "Cancelled as the drain ended");
                };
                break;
        }
        var receipt = await fixture.RunAsync();

        Assert.AreEqual(CameraAgentControlPhase.Cancelled, receipt.Phase);
        Assert.AreEqual("drain.cancelled-paused", receipt.Events[^1].Code);
        Assert.AreEqual("owner", receipt.Cancellation?.Actor);
        Assert.AreEqual("Paused", fixture.Client.CaptureState);
        CollectionAssert.AreEqual(DrainEffects, fixture.Client.Effects);
        // Initial read, claim publication, the single post-drain boundary read, then terminal publication. The Restart path
        // checks at its first post-drain read too; no extra read is needed before the Drain outcome is decided.
        CollectionAssert.AreEqual(new CameraAgentControlPhase?[] { CameraAgentControlPhase.Queued, CameraAgentControlPhase.Draining,
            CameraAgentControlPhase.Draining, CameraAgentControlPhase.Cancelled }, fixture.Client.Exchanged);
        await fixture.ReloadAndAssertAsync(receipt);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StandaloneDrain_AtTheDrainBound_EndsDrainTimeoutAndStaysPaused(bool cancelledDuringDrain)
    {
        using var fixture = new Fixture(CameraAgentControlKind.Drain);
        fixture.Client.HungDrain = true;
        if (cancelledDuringDrain) fixture.Client.AfterDrain = () => fixture.Client.RecordCancellation("owner", "Cancelled while draining");
        var receipt = await fixture.RunAsync(Budgets with { Drain = TimeSpan.FromMilliseconds(30) });

        // The bound wins over a cancellation recorded during the drain; neither outcome resumes a standalone Drain.
        Assert.AreEqual(CameraAgentControlPhase.Failed, receipt.Phase);
        Assert.AreEqual("drain.timeout", receipt.Events[^1].Code);
        Assert.AreEqual("Paused", fixture.Client.CaptureState);
        CollectionAssert.AreEqual(DrainEffects, fixture.Client.Effects);
        Assert.IsFalse(receipt.ResumeRequested);
        await fixture.ReloadAndAssertAsync(receipt);
    }

    [TestMethod]
    [DataRow(false, "lifecycle.failed-check-capture")]
    [DataRow(true, "lifecycle.timeout")]
    public async Task StandaloneDrain_UnreadablePostDrainBoundary_FailsClosedAndStaysPaused(bool timeout, string code)
    {
        using var fixture = new Fixture(CameraAgentControlKind.Drain);
        fixture.Client.FailPostDrainRead = timeout;
        fixture.Client.AfterDrain = () => fixture.Client.RecordCancellation("owner", "Unobservable cancellation");
        var receipt = await fixture.RunAsync();

        // A completed drain whose cancellation state cannot be read is neither Succeeded nor relabelled as a drain timeout.
        Assert.AreEqual(CameraAgentControlPhase.Failed, receipt.Phase);
        Assert.AreEqual(code, receipt.Events[^1].Code);
        Assert.AreEqual("Paused", fixture.Client.CaptureState);
        CollectionAssert.AreEqual(DrainEffects, fixture.Client.Effects);
        Assert.IsFalse(receipt.ResumeRequested);
        await fixture.ReloadAndAssertAsync(receipt);
    }

    [TestMethod]
    [DataRow(CameraAgentControlKind.Drain)]
    [DataRow(CameraAgentControlKind.Restart)]
    public async Task DrainedBoundaryAndVerificationDeadline_AreJournaledBeforeAnyExchangePublishesThem(CameraAgentControlKind kind)
    {
        using var fixture = new Fixture(kind);
        // What the journal held at the moment each crash-sensitive field first reached CameraAgent.
        var boundary = new List<(CameraAgentControlReceipt Published, CameraAgentControlReceipt Journal)>();
        var deadline = new List<(CameraAgentControlReceipt Published, CameraAgentControlReceipt Journal)>();
        fixture.Client.OnExchange = published =>
        {
            if (published is null) return;
            if (boundary.Count == 0 && published.PausedVersion is not null) boundary.Add((published, fixture.ReadDurable()));
            if (deadline.Count == 0 && published.VerificationDeadlineUtc is not null) deadline.Add((published, fixture.ReadDurable()));
        };
        var receipt = await fixture.RunAsync();

        Assert.AreEqual(CameraAgentControlPhase.Succeeded, receipt.Phase);
        Assert.HasCount(1, boundary, "The drained boundary must be published to CameraAgent.");
        Assert.AreEqual(CameraAgentControlPhase.Draining, boundary[0].Published.Phase, "The drained boundary is first published by the post-drain read.");
        Assert.AreEqual(8L, boundary[0].Journal.PausedVersion, "PausedVersion must be journaled before CameraAgent can retain it.");
        Assert.AreEqual(boundary[0].Published.PausedVersion, boundary[0].Journal.PausedVersion);
        Assert.AreEqual(boundary[0].Published.DrainedCaptureSequence, boundary[0].Journal.DrainedCaptureSequence);
        if (kind == CameraAgentControlKind.Restart)
        {
            Assert.HasCount(1, deadline, "A Restart publishes its verification deadline before the stop call.");
            Assert.AreEqual(CameraAgentControlPhase.Draining, deadline[0].Published.Phase, "The deadline is first published by the pre-stop cancellation read.");
            Assert.IsNotNull(deadline[0].Journal.VerificationDeadlineUtc, "The verification deadline must be journaled before CameraAgent can retain it.");
            Assert.AreEqual(deadline[0].Published.VerificationDeadlineUtc, deadline[0].Journal.VerificationDeadlineUtc);
        }
        else Assert.IsEmpty(deadline, "A standalone Drain has no verification deadline.");
    }

    [TestMethod]
    [DataRow(CameraAgentControlKind.Restart, "boundary", true)]
    [DataRow(CameraAgentControlKind.Restart, "deadline", true)]
    [DataRow(CameraAgentControlKind.Restart, "refused", true)]
    [DataRow(CameraAgentControlKind.Restart, "boundary", false)]
    [DataRow(CameraAgentControlKind.Drain, "boundary", true)]
    [DataRow(CameraAgentControlKind.Drain, "boundary", false)]
    public async Task JournalWriteFailure_NeverPublishesTheUnjournaledFieldAndIsReportedAsAJournalFailure(
        CameraAgentControlKind kind, string field, bool persistent)
    {
        using var fixture = new Fixture(kind);
        if (field == "refused") fixture.Client.Failure = "restart-refused";
        bool Carries(CameraAgentControlReceipt receipt) => field switch
        {
            "boundary" => receipt.PausedVersion is not null,
            "deadline" => receipt.VerificationDeadlineUtc is not null,
            _ => receipt.RestartRefused
        };
        // The first write carrying the field fails. A persistent fault fails every later write too; a transient one does not.
        var failedWrites = new List<CameraAgentControlReceipt>();
        fixture.FailJournalWrite = next =>
        {
            if (failedWrites.Count > 0 ? !persistent : !Carries(next)) return false;
            failedWrites.Add(next);
            return true;
        };

        if (persistent)
        {
            // No terminal receipt can be journaled either, so the installer stops; a later worker settles from the journal.
            await Assert.ThrowsAsync<IOException>(() => fixture.RunAsync());
            Assert.IsNotEmpty(failedWrites);
            Assert.IsTrue(Carries(failedWrites[0]), "The first failed write carried the field.");
            // Not even the failure path's owned-pause restore may publish the field the journal lacks.
            Assert.IsEmpty(fixture.PublicationViolations, string.Join(" | ", fixture.PublicationViolations));
            var durable = await fixture.ReloadAsync();
            Assert.IsFalse(durable.IsTerminal);
            Assert.IsFalse(Carries(durable));
            Assert.DoesNotContain("resume", fixture.Client.Effects);
            Assert.AreEqual("Paused", fixture.Client.CaptureState);
            return;
        }

        var terminal = await fixture.RunAsync(); // RunAsync also fails on any publication the journal did not hold.
        Assert.HasCount(1, failedWrites);
        Assert.IsTrue(Carries(failedWrites[0]), "The failed write carried the field.");
        Assert.AreEqual(CameraAgentControlPhase.Failed, terminal.Phase);
        Assert.AreEqual("lifecycle.failed-check-capture", terminal.Events[^1].Code, "A journal write failure is not a drain failure.");
        // A Restart restores its owned pause from journaled evidence; a standalone Drain stays paused.
        Assert.AreEqual(kind == CameraAgentControlKind.Restart, terminal.ResumeConfirmed);
        Assert.AreEqual(kind == CameraAgentControlKind.Restart ? "Running" : "Paused", fixture.Client.CaptureState);
        Assert.DoesNotContain("restart", fixture.Client.Effects);
        await fixture.ReloadAndAssertAsync(terminal);
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
    [DataRow(false)]
    [DataRow(true)]
    public async Task RestartingPublicationFailurePersistsNonIssuanceBeforeOwnedRestore(bool timeout)
    {
        using var fixture = new Fixture();
        fixture.Client.FailRestartingPublication = timeout;
        fixture.Client.BeforeResume = receipt =>
        {
            var durable = JsonSerializer.Deserialize(File.ReadAllText(fixture.JournalPath), DeploymentJsonContext.Default.InstallerControlJournalState)!.Receipts.Single();
            Assert.IsTrue(durable.RestartRequested);
            Assert.IsTrue(durable.RestartNotIssued);
            Assert.IsFalse(durable.RestartMayHaveOccurred);
            Assert.IsTrue(durable.ResumeRequested);
            Assert.AreEqual(receipt.PausedVersion, durable.PausedVersion);
        };
        var receipt = await fixture.RunAsync();
        Assert.AreEqual(CameraAgentControlPhase.Failed, receipt.Phase);
        Assert.IsTrue(receipt.RestartNotIssued);
        Assert.IsTrue(receipt.ResumeConfirmed);
        Assert.AreEqual("Running", fixture.Client.CaptureState);
        CollectionAssert.AreEqual(RestoreEffects, fixture.Client.Effects);
        await fixture.ReloadAndAssertAsync(receipt);
    }

    [TestMethod]
    [DataRow("draining", true)]
    [DataRow("not-issued", true)]
    [DataRow("refused", true)]
    [DataRow("ambiguous", false)]
    public async Task InterruptedSettlementRestoresOnlyDurablyProvenPreStopWork(string boundary, bool shouldRestore)
    {
        using var fixture = new Fixture();
        var initial = await fixture.Client.ExchangeAsync(null, CancellationToken.None);
        fixture.Client.CaptureState = "Paused";
        fixture.Client.Version = 8;
        fixture.Client.Effects.Add("drain");
        var queued = Queued();
        var receipt = queued with
        {
            Phase = boundary == "draining" ? CameraAgentControlPhase.Draining : CameraAgentControlPhase.Restarting,
            OriginalProcessId = initial.ProcessId,
            ResumeAfterVerification = true,
            PausedVersion = 8,
            DrainedCaptureSequence = 42,
            RestartRequested = boundary != "draining",
            RestartNotIssued = boundary == "not-issued",
            RestartRefused = boundary == "refused",
            Events = [.. queued.Events, new(CameraAgentControlPhase.Draining, DateTimeOffset.UtcNow, "lifecycle.draining")]
        };
        if (receipt.Phase == CameraAgentControlPhase.Restarting)
            receipt = receipt with { Events = [.. receipt.Events, new(CameraAgentControlPhase.Restarting, DateTimeOffset.UtcNow, "restart.requesting")] };
        var journal = new InstallerControlJournal(fixture.JournalPath);
        await journal.SaveAsync(receipt);
        var terminal = await new InstallerControlVerifier(fixture.Client, journal.SaveAsync, TimeProvider.System, Budgets).SettleInterruptedAsync(receipt);
        Assert.AreEqual(CameraAgentControlPhase.Failed, terminal.Phase);
        Assert.AreEqual(shouldRestore, terminal.ResumeConfirmed);
        Assert.AreEqual(shouldRestore ? "Running" : "Paused", fixture.Client.CaptureState);
        Assert.AreEqual(shouldRestore ? 1 : 0, fixture.Client.Effects.Count(effect => effect == "resume"));
        Assert.DoesNotContain("restart", fixture.Client.Effects);
        await fixture.ReloadAndAssertAsync(terminal);
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

    [TestMethod]
    [DataRow(false, "lifecycle.failed-check-capture")]
    [DataRow(true, "lifecycle.timeout")]
    public async Task PreRestartReadFailureDoesNotReportDrainTimeout(bool timeout, string code)
    {
        using var fixture = new Fixture();
        fixture.Client.FailPostDrainRead = timeout;
        var receipt = await fixture.RunAsync();
        Assert.AreEqual(code, receipt.Events[^1].Code);
        Assert.IsTrue(receipt.ResumeConfirmed);
        Assert.DoesNotContain("restart", fixture.Client.Effects);
    }

    [TestMethod]
    public async Task LongDrainRenewsWorkerLeaseUntilTheDrainCompletes()
    {
        using var fixture = new Fixture(CameraAgentControlKind.Drain);
        var drain = new TaskCompletionSource<LifecycleContinuity>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.PendingDrain = drain.Task;
        var heartbeats = 0;
        fixture.Client.OnExchange = receipt =>
        {
            if (receipt?.Phase != CameraAgentControlPhase.Draining || !fixture.Client.Effects.Contains("drain")) return;
            if (++heartbeats == 3)
                drain.SetResult(new("Paused", 8, 42, 0, 0, 0, 0, 0, 0, 0, 0));
        };
        var terminal = await fixture.RunAsync();
        Assert.AreEqual(CameraAgentControlPhase.Succeeded, terminal.Phase);
        Assert.IsGreaterThanOrEqualTo(3, heartbeats);
        CollectionAssert.AreEqual(DrainEffects, fixture.Client.Effects);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RepeatedPreStopTransportFailuresKeepRecoveryPendingAcrossWorkerReload(bool duringResume)
    {
        using var fixture = new Fixture();
        fixture.Client.AfterDrain = () => fixture.Client.WindowSafe = false;
        var remaining = 3;
        fixture.Client.OnExchange = receipt =>
        {
            if (!duringResume && receipt is { IsTerminal: false } && fixture.Client.Effects.Contains("drain") && remaining-- > 0)
                throw new HttpRequestException("Injected repeated restore read outage.");
        };
        fixture.Client.BeforeResume = _ =>
        {
            if (duringResume && remaining-- > 0) throw new HttpRequestException("Injected repeated resume outage.");
        };
        var pending = await fixture.RunAsync();
        Assert.IsFalse(pending.IsTerminal);
        Assert.AreEqual("Paused", fixture.Client.CaptureState);
        for (var poll = 0; poll < 4; poll++)
        {
            var journal = new InstallerControlJournal(fixture.JournalPath);
            await journal.LoadAsync(CancellationToken.None);
            await new InstallerControlWorker(fixture.Client, journal, _ => Task.FromResult<IDisposable>(new RecoveryLease()),
                TimeProvider.System, _ => { }, Budgets).PollAsync(CancellationToken.None);
        }
        var settled = await fixture.ReloadAsync();
        Assert.IsTrue(settled.IsTerminal);
        Assert.IsTrue(settled.ResumeConfirmed);
        Assert.AreEqual("Running", fixture.Client.CaptureState);
        Assert.DoesNotContain("restart", fixture.Client.Effects);
        Assert.AreEqual(1, fixture.Client.Effects.Count(effect => effect == "resume"));
    }

    private sealed class RecoveryLease : IDisposable { public void Dispose() { } }

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

        // Every exchange publication that did not match the journal at that moment. CameraAgent accepts only a successor
        // that keeps each published field, so a crash after an unjournaled publication strands the projection.
        internal List<string> PublicationViolations { get; } = [];

        internal Fixture(CameraAgentControlKind kind = CameraAgentControlKind.Restart)
        {
            _queued = Queued(kind);
            Client.BeforeEffect = (phase) =>
            {
                var durable = JsonSerializer.Deserialize(File.ReadAllText(JournalPath), DeploymentJsonContext.Default.InstallerControlJournalState)!;
                Assert.AreEqual(phase, durable.Receipts.Single().Phase, "The durable boundary must precede the effect.");
            };
            Client.BeforePublish = published =>
            {
                var durable = ReadDurable();
                if (!SameEvidence(durable, published))
                    PublicationViolations.Add($"{published.Phase}/{published.Events[^1].Code} published {Describe(published)} while the journal held {Describe(durable)}");
            };
        }

        internal CameraAgentControlReceipt ReadDurable() =>
            JsonSerializer.Deserialize(File.ReadAllText(JournalPath), DeploymentJsonContext.Default.InstallerControlJournalState)!.Receipts.Single();

        private static bool SameEvidence(CameraAgentControlReceipt durable, CameraAgentControlReceipt published) =>
            durable with { Events = Array.Empty<CameraAgentControlEvent>() } == published with { Events = Array.Empty<CameraAgentControlEvent>() } &&
            durable.Events.SequenceEqual(published.Events);

        private static string Describe(CameraAgentControlReceipt receipt) =>
            $"[phase={receipt.Phase} events={receipt.Events.Count} paused={receipt.PausedVersion} sequence={receipt.DrainedCaptureSequence} " +
            $"deadline={receipt.VerificationDeadlineUtc:O} restartRequested={receipt.RestartRequested} refused={receipt.RestartRefused} " +
            $"notIssued={receipt.RestartNotIssued} resumeRequested={receipt.ResumeRequested} resumeConfirmed={receipt.ResumeConfirmed} " +
            $"verified={receipt.VerifiedProcessId} cancelled={receipt.Cancellation is not null}]";

        // Returns true for a journal write that must fail, as a full disk or read-only filesystem would.
        internal Func<CameraAgentControlReceipt, bool>? FailJournalWrite { get; set; }

        internal async Task<CameraAgentControlReceipt> RunAsync(InstallerControlBudgets? budgets = null, TimeProvider? clock = null,
            CancellationToken cancellationToken = default)
        {
            var journal = new InstallerControlJournal(JournalPath);
            await journal.SaveAsync(_queued);
            Task Persist(CameraAgentControlReceipt next) =>
                FailJournalWrite?.Invoke(next) == true ? throw new IOException("Injected journal write failure.") : journal.SaveAsync(next);
            var receipt = await new InstallerControlVerifier(Client, Persist, clock ?? TimeProvider.System, budgets ?? Budgets)
                .ExecuteAsync(_queued, cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.IsEmpty(PublicationViolations, "Journal each receipt before an exchange publishes it: " + string.Join(" | ", PublicationViolations));
            return receipt;
        }

        internal async Task<CameraAgentControlReceipt> ReloadAsync()
        {
            var reloaded = new InstallerControlJournal(JournalPath);
            await reloaded.LoadAsync(CancellationToken.None);
            return reloaded.Receipts.Single();
        }

        internal async Task ReloadAndAssertAsync(CameraAgentControlReceipt expected)
        {
            var actual = await ReloadAsync();
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
        internal Task<LifecycleContinuity>? PendingDrain;
        internal bool? FailRestartingPublication, FailPostDrainRead;
        private bool _publicationFailed, _postDrainReadFailed;
        internal Action<CameraAgentControlReceipt>? BeforeResume;
        internal Action? AfterDrain;
        internal Action<CameraAgentControlPhase>? BeforeEffect;
        internal Action<CameraAgentControlReceipt?>? OnExchange;
        internal Action<CameraAgentControlReceipt>? BeforePublish;
        internal readonly List<CameraAgentControlPhase?> Exchanged = [];
        // Mirrors CameraAgentControlStore: a recorded cancellation is retained on the projection the exchange returns.
        internal CameraAgentControlCancellation? Cancellation;
        private readonly Guid _original = Guid.NewGuid();
        private readonly Guid _new = Guid.NewGuid();
        private bool _restarted;

        internal void RecordCancellation(string actor, string reason)
        {
            Cancellation ??= new(actor, DateTimeOffset.UtcNow, reason);
            CancelRequested = true;
        }

        public Task<CameraAgentControlSnapshot> ExchangeAsync(CameraAgentControlReceipt? receipt, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Exchanged.Add(receipt?.Phase);
            if (receipt is not null) BeforePublish?.Invoke(receipt);
            OnExchange?.Invoke(receipt);
            if (receipt?.Phase == CameraAgentControlPhase.Restarting && FailRestartingPublication is { } timeout && !_publicationFailed)
            {
                _publicationFailed = true;
                if (timeout) throw new TimeoutException("Injected restart publication timeout.");
                throw new HttpRequestException("Injected restart publication failure.");
            }
            if (receipt?.Phase == CameraAgentControlPhase.Draining && Effects.Contains("drain") && FailPostDrainRead is { } readTimeout &&
                !_postDrainReadFailed)
            {
                _postDrainReadFailed = true;
                if (readTimeout) throw new TimeoutException("Injected post-drain boundary read timeout.");
                throw new HttpRequestException("Injected post-drain boundary read failure.");
            }
            if (receipt is not null && Cancellation is not null) receipt = receipt with { Cancellation = Cancellation };
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
            return PendingDrain ?? (HungDrain ? new TaskCompletionSource<LifecycleContinuity>().Task : Task.FromResult(Boundary() with
            { OutboxPending = Failure == "drain-backlog" ? 1 : 0, QueuesObserved = Failure != "unobserved-queues" }));
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
            BeforeEffect?.Invoke(receipt.Phase);
            BeforeResume?.Invoke(receipt);
            Effects.Add("resume");
            CaptureState = "Running";
            return Task.CompletedTask;
        }

        private LifecycleContinuity Boundary() => new("Paused", Version, 42, 0, 0, 0, 0, 0, 0, 0, 0);
    }
}
