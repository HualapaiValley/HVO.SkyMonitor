using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AutomaticSignedRecovery_DoesNotDispatchAnotherPauseAfterCancellationFollowingAcceptance()
    {
        using var fixture = await AutomaticSignedFixtureAsync();
        using var release = CandidateRelease(fixture, out _);
        using var cancel = new CancellationTokenSource();
        var lifecycle = new AutomaticSignedLifecycleClient(fixture.Paths) { SupportsOwnedResume = true };
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity);
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with { Release = release.Folder };
        lifecycle.AfterPauseAccepted = () =>
        {
            cancel.Cancel();
            cancel.Token.ThrowIfCancellationRequested();
        };
        CameraAgentLifecycleManager.JournalRecorded = state =>
        {
            if (state.FailureCode == "signed-upgrade-recovery-pending")
                throw new IOException("Interrupted before confirming the accepted Pause.");
        };
        try
        {
            await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
                request, fixture.Runner, _ => lifecycle, _ => owner, fixture.Uid, fixture.Gid, cancel.Token, error: TextWriter.Null));
        }
        finally
        {
            ResetCrashHooks();
            lifecycle.AfterPauseAccepted = null;
        }
        var pending = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.AreEqual(LifecycleOperationPhase.Restoring, pending.Phase);
        Assert.IsNull(pending.PreMutationContinuity);
        Assert.IsNull(pending.SignedRecoveryPauseCommandId);
        Assert.AreEqual("Paused", lifecycle.ReadBoundary().CaptureState);

        var replay = await CameraAgentLifecycleManager.ExecuteAsync(request with { Resume = true }, fixture.Runner,
            _ => lifecycle, _ => owner, fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null);
        Assert.AreEqual("restored-previous-healthy-admission-resumed", replay.Outcome);
        var settled = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.AreEqual(lifecycle.PauseCommandId, settled.SignedRecoveryPauseCommandId);
        Assert.AreEqual(1, lifecycle.PauseRequests);
        Assert.HasCount(1, lifecycle.OwnedRequests);
        Assert.AreEqual(0, lifecycle.GenericResumeCalls);
        Assert.AreEqual("Running", lifecycle.ReadBoundary().CaptureState);
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AutomaticSignedRecovery_ReplaysAnInterruptedStoreReplacementFromItsAuthorizedPlan()
    {
        using var fixture = await AutomaticSignedFixtureAsync();
        using var release = CandidateRelease(fixture, out var candidate);
        var lifecycle = new AutomaticSignedLifecycleClient(fixture.Paths) { SupportsOwnedResume = true };
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true };
        var fleet = Path.Combine(fixture.Paths.StateRoot, "raw", ".fleet", "fleet-status.db");
        fixture.Runner.OnComposeUp = image =>
        {
            if (image == candidate) ExecuteSql(fleet, "UPDATE fleet_status_metadata SET next_sequence = 7;");
        };
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with { Release = release.Folder };
        Task<LifecycleResult> RunAsync(bool resume) => CameraAgentLifecycleManager.ExecuteAsync(
            request with { Resume = resume }, fixture.Runner, _ => lifecycle, _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null);
        CameraAgentStateSnapshot.StepCompleted = step =>
        {
            if (step == "restore:raw/.fleet/fleet-status.db")
                throw new IOException("Interrupted after replacing Fleet and before counters were carried.");
        };
        try
        {
            var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => RunAsync(resume: false));
            StringAssert.Contains(failure.Message, "Interrupted after replacing Fleet", StringComparison.Ordinal);
        }
        finally
        {
            ResetCrashHooks();
        }
        var interrupted = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.AreEqual(interrupted.StateSnapshot!.SnapshotId, interrupted.AuthorizedSignedRestoreSnapshotId);
        Assert.AreEqual(interrupted.StateSnapshot.SnapshotId, interrupted.StateRestorePlan!.SnapshotId);
        Assert.IsNull(interrupted.StateSnapshotRestoredUtc);
        Assert.IsFalse(fixture.Runner.Running);
        Assert.AreEqual(candidate, fixture.Runner.ActiveImageId);

        Assert.AreEqual("restored-previous-healthy-admission-resumed", (await RunAsync(resume: true)).Outcome);
        var settled = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.AreEqual(interrupted.StateRestorePlan.SnapshotId, settled.StateRestorePlan!.SnapshotId);
        CollectionAssert.AreEqual(interrupted.StateRestorePlan.CurrentLocations.ToArray(), settled.StateRestorePlan.CurrentLocations.ToArray());
        CollectionAssert.AreEqual(interrupted.StateRestorePlan.TargetLocations.ToArray(), settled.StateRestorePlan.TargetLocations.ToArray());
        Assert.AreEqual(interrupted.StateSnapshot, settled.StateSnapshot);
        Assert.IsNull(settled.AuthorizedSignedRestoreSnapshotId);
        Assert.IsNotNull(settled.StateSnapshotRestoredUtc);
        Assert.AreEqual(7, ReadCounter(fixture.Paths, "raw/.fleet/fleet-status.db", "SELECT next_sequence FROM fleet_status_metadata;"));
        Assert.AreEqual(1, lifecycle.PauseRequests);
        Assert.HasCount(1, lifecycle.OwnedRequests);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
    }

    [TestMethod]
    [DataRow("before-start")]
    [DataRow("after-start")]
    [DataRow("after-start-owner-pause")]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AutomaticSignedRecovery_ReplaysThePersistedDispositionAcrossPredecessorStartup(string cut)
    {
        using var fixture = await AutomaticSignedFixtureAsync();
        using var release = CandidateRelease(fixture, out var candidate);
        var lifecycle = new AutomaticSignedLifecycleClient(fixture.Paths) { SupportsOwnedResume = true };
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true };
        var command = fixture.Request(LifecycleOperationKind.Upgrade) with { Release = release.Folder };
        Task<LifecycleResult> RunAsync(bool resume) => CameraAgentLifecycleManager.ExecuteAsync(
            command with { Resume = resume }, fixture.Runner, _ => lifecycle, _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null);

        const string cutMessage = "synthetic interruption at predecessor startup";
        var beforeStart = cut == "before-start";
        LifecycleOperationState? atStartup = null;
        CameraAgentLifecycleManager.JournalRecorded = state =>
        {
            if (beforeStart && state.FailureCode == "signed-upgrade-recovery-pending")
                throw new IOException(cutMessage);
        };
        fixture.Runner.OnComposeUp = image =>
        {
            if (image != fixture.Manifest.Image.ImageId) return;
            atStartup = CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (!beforeStart) throw new IOException(cutMessage);
        };
        try
        {
            var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => RunAsync(resume: false));
            StringAssert.Contains(failure.Message, cutMessage, StringComparison.Ordinal);
        }
        finally
        {
            ResetCrashHooks();
            fixture.Runner.OnComposeUp = null;
        }

        var pending = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.AreEqual("signed-upgrade-recovery-pending", pending.FailureCode);
        Assert.AreEqual(LifecycleOperationPhase.Restoring, pending.Phase);
        Assert.IsNotNull(pending.StateSnapshotRestoredUtc);
        Assert.AreEqual(pending.StateSnapshot!.SnapshotId, pending.StateRestorePlan!.SnapshotId);
        Assert.AreEqual(lifecycle.PauseCommandId, pending.SignedRecoveryPauseCommandId);
        Assert.AreEqual(!beforeStart, fixture.Runner.Running);
        Assert.AreEqual(beforeStart ? candidate : fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        if (!beforeStart) Assert.AreEqual(pending.FailureCode, atStartup?.FailureCode);
        Assert.AreEqual(0, lifecycle.GenericResumeCalls);
        Assert.IsEmpty(lifecycle.OwnedRequests);

        var ups = fixture.Runner.ComposeUpCount;
        var stops = fixture.Runner.ComposeStopCount;
        var restarts = fixture.Runner.ComposeRestartCount;
        if (cut == "after-start-owner-pause")
        {
            lifecycle.AcceptOwnerCommand("paused", "owner-at-start-cut", changed: false);
            byte[] raw = await File.ReadAllBytesAsync(RawIngressPath(fixture.Paths));
            Assert.AreEqual("restored-predecessor-admission-preserved", (await RunAsync(resume: true)).Outcome);
            await AssertLaterLifecycleAdmittedAsync(fixture);
            CollectionAssert.AreEqual(raw, await File.ReadAllBytesAsync(RawIngressPath(fixture.Paths)));
            Assert.AreEqual("Paused", lifecycle.ReadBoundary().CaptureState);
            Assert.AreEqual(pending.PreMutationContinuity!.CaptureVersion, lifecycle.ReadBoundary().CaptureVersion);
        }
        else
        {
            Assert.AreEqual("restored-previous-healthy-admission-resumed", (await RunAsync(resume: true)).Outcome);
            Assert.AreEqual("Running", lifecycle.ReadBoundary().CaptureState);
        }

        var settled = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.AreEqual(cut == "after-start-owner-pause" ? LifecycleOperationPhase.RecoveryAdmissionPreserved : LifecycleOperationPhase.Restored, settled.Phase);
        Assert.AreEqual(pending.StateSnapshot, settled.StateSnapshot);
        Assert.AreEqual(pending.StateSnapshotRestoredUtc, settled.StateSnapshotRestoredUtc);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(ups + (beforeStart ? 1 : 0), fixture.Runner.ComposeUpCount);
        Assert.AreEqual(stops, fixture.Runner.ComposeStopCount);
        Assert.AreEqual(restarts, fixture.Runner.ComposeRestartCount);
        Assert.AreEqual(1, lifecycle.PauseRequests);
        Assert.AreEqual(0, lifecycle.GenericResumeCalls);
        Assert.HasCount(1, lifecycle.OwnedRequests);
    }

    [TestMethod]
    [DataRow("quiet")]
    [DataRow("owner-before-stop")]
    [DataRow("owner-at-stop")]
    [DataRow("owner-on-candidate")]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AutomaticSignedRecovery_ForwardRetryRetainsItsOriginallyOwnedPause(string scenario)
    {
        using var fixture = await AutomaticSignedFixtureAsync();
        using var release = CandidateRelease(fixture, out var candidate);
        using var crashRoot = new TemporaryDirectory();
        var lifecycle = new AutomaticSignedLifecycleClient(fixture.Paths) { SupportsOwnedResume = true };
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity);
        var command = fixture.Request(LifecycleOperationKind.Upgrade) with { Release = release.Folder };
        Task<LifecycleResult> RunAsync(bool resume) => CameraAgentLifecycleManager.ExecuteAsync(
            command with { Resume = resume }, fixture.Runner, _ => lifecycle, _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null);

        var crashImages = new List<CrashImage>();
        CaptureCrashImages(fixture, crashRoot.Path, crashImages);
        try
        {
            Assert.AreEqual("completed", (await RunAsync(resume: false)).Outcome);
        }
        finally
        {
            ResetCrashHooks();
        }
        RestoreCrashImage(fixture, crashImages.First(static image => image.Step == "journal:CandidateVerified"));
        var interrupted = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.AreEqual(lifecycle.PauseCommandId, interrupted.SignedRecoveryPauseCommandId);
        var genericBeforeRetry = lifecycle.GenericResumeCalls;
        var candidateStarts = 0;
        var stopCount = fixture.Runner.ComposeStopCount;
        void OwnerPause() => lifecycle.AcceptOwnerCommand("paused", "owner-on-forward-retry", changed: false);
        CameraAgentLifecycleManager.JournalRecorded = state =>
        {
            // The live drain and its owned receipt were already observed. Accept a new no-op before Compose stop.
            if (scenario == "owner-at-stop" && state.Phase == LifecycleOperationPhase.Drained) OwnerPause();
        };
        fixture.Runner.OnComposeUp = image =>
        {
            if (image == candidate)
            {
                candidateStarts++;
                if (scenario == "owner-on-candidate") OwnerPause();
                owner.RejectNextVerification = true;
            }
            else if (scenario == "owner-before-stop")
            {
                OwnerPause();
            }
        };
        try
        {
            if (scenario == "owner-before-stop")
                Assert.AreEqual("restored-predecessor-admission-preserved", (await RunAsync(resume: true)).Outcome);
            else
                await Assert.ThrowsExactlyAsync<InstallerException>(() => RunAsync(resume: true));
        }
        finally
        {
            ResetCrashHooks();
            fixture.Runner.OnComposeUp = null;
        }

        const string rawStore = "raw/journal/raw-ingress.db";
        var final = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        var candidateRetried = scenario is "quiet" or "owner-on-candidate";
        Assert.AreEqual(candidateRetried ? 1 : 0, candidateStarts);
        Assert.AreEqual(1, lifecycle.PauseRequests);
        Assert.AreEqual(genericBeforeRetry, lifecycle.GenericResumeCalls);
        Assert.AreEqual(lifecycle.PauseCommandId, final.SignedRecoveryPauseCommandId);
        Assert.AreEqual(1, ReadCounter(fixture.Paths, rawStore,
            "SELECT COUNT(*) FROM capture_control_commands WHERE actor LIKE 'installer-lifecycle:%' AND target_state = 'paused' AND changed = 1;"));
        _ = await CameraAgentStateSnapshot.VerifyAsync(fixture.Paths, fixture.InstanceId, interrupted.StateSnapshot!, CancellationToken.None);
        if (scenario == "quiet")
        {
            Assert.AreEqual(LifecycleOperationPhase.Restored, final.Phase);
            Assert.AreEqual("Running", lifecycle.ReadBoundary().CaptureState);
            Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
            Assert.AreNotEqual(interrupted.StateSnapshot, final.StateSnapshot);
            Assert.HasCount(1, lifecycle.OwnedRequests);
        }
        else
        {
            Assert.AreEqual("Paused", lifecycle.ReadBoundary().CaptureState);
            Assert.AreEqual(interrupted.PreMutationContinuity!.CaptureVersion, lifecycle.ReadBoundary().CaptureVersion);
            Assert.AreEqual(1, ReadCounter(fixture.Paths, rawStore,
                "SELECT COUNT(*) FROM capture_control_commands WHERE idempotency_key = 'owner-on-forward-retry' AND changed = 0;"));
            Assert.HasCount(scenario == "owner-at-stop" ? 1 : 0, lifecycle.OwnedRequests);
            Assert.AreEqual(scenario == "owner-on-candidate" ? candidate : fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
            Assert.AreEqual(scenario != "owner-on-candidate", fixture.Runner.Running);
            if (scenario != "owner-on-candidate")
            {
                Assert.AreEqual(LifecycleOperationPhase.RecoveryAdmissionPreserved, final.Phase);
                await AssertLaterLifecycleAdmittedAsync(fixture);
            }
            if (scenario == "owner-before-stop")
                Assert.AreEqual(stopCount + 1, fixture.Runner.ComposeStopCount, "Only the interrupted candidate was stopped.");
        }
    }
}
