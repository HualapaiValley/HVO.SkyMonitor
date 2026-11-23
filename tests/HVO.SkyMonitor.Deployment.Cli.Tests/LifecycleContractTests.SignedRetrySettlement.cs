using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [DataRow("accepted")]
    [DataRow("owner-after-acceptance")]
    [DataRow("owner-after-crash")]
    [DataRow("before-acceptance")]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AutomaticSignedRecovery_ReconcilesTheAcceptedPauseBeforeDrained(string scenario)
    {
        using var fixture = await AutomaticSignedFixtureAsync();
        using var release = CandidateRelease(fixture, out var candidate);
        using var crashRoot = new TemporaryDirectory();
        var lifecycle = new AutomaticSignedLifecycleClient(fixture.Paths) { SupportsOwnedResume = true };
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity);
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with { Release = release.Folder };
        Task<LifecycleResult> RunAsync(bool resume) => CameraAgentLifecycleManager.ExecuteAsync(
            request with { Resume = resume }, fixture.Runner, _ => lifecycle, _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null);

        CrashImage? cut = null;
        void Capture()
        {
            var copy = Path.Combine(crashRoot.Path, scenario);
            CopyTree(fixture.Root, copy);
            cut = new CrashImage(scenario, copy, fixture.Runner.ActiveImageId, fixture.Runner.Running);
        }

        if (scenario == "before-acceptance")
        {
            CameraAgentLifecycleManager.JournalRecorded = state =>
            {
                if (state is { Phase: LifecycleOperationPhase.Prepared, MutationStarted: true, PreMutationContinuity: null })
                    Capture();
            };
        }
        else
        {
            lifecycle.AfterPauseAccepted = () =>
            {
                if (scenario == "owner-after-acceptance")
                    lifecycle.AcceptOwnerCommand("paused", "owner-before-drain-record", changed: false);
                Capture();
            };
        }
        try
        {
            Assert.AreEqual("completed", (await RunAsync(resume: false)).Outcome);
        }
        finally
        {
            ResetCrashHooks();
            lifecycle.AfterPauseAccepted = null;
        }

        Assert.IsNotNull(cut);
        RestoreCrashImage(fixture, cut);
        var interrupted = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.AreEqual(LifecycleOperationPhase.Prepared, interrupted.Phase);
        Assert.IsTrue(interrupted.MutationStarted);
        Assert.IsNull(interrupted.PreMutationContinuity);
        Assert.IsNull(interrupted.SignedRecoveryPauseCommandId);
        Assert.IsNull(interrupted.StateSnapshot);
        if (scenario == "owner-after-crash")
            lifecycle.AcceptOwnerCommand("paused", "owner-before-drain-record", changed: false);

        var accepted = scenario == "accepted";
        var stopped = fixture.Runner.ComposeStopCount;
        var generic = lifecycle.GenericResumeCalls;
        var raw = RawIngressPath(fixture.Paths);
        var before = await File.ReadAllBytesAsync(raw);
        var boundary = lifecycle.ReadBoundary();
        var candidateStarts = 0;
        fixture.Runner.OnComposeUp = image =>
        {
            if (image != candidate) return;
            candidateStarts++;
            owner.RejectNextVerification = true;
        };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => RunAsync(resume: true));

        var final = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.AreEqual(accepted ? 1 : 0, candidateStarts);
        Assert.AreEqual(1, lifecycle.PauseRequests, "The dispatched first attempt is the only installer Pause.");
        Assert.AreEqual(generic, lifecycle.GenericResumeCalls);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        if (accepted)
        {
            Assert.AreEqual(LifecycleOperationPhase.Restored, final.Phase);
            Assert.AreEqual(lifecycle.PauseCommandId, final.SignedRecoveryPauseCommandId);
            Assert.AreEqual("Running", lifecycle.ReadBoundary().CaptureState);
            Assert.HasCount(1, lifecycle.OwnedRequests);
            Assert.IsNotNull(final.StateSnapshotRestoredUtc);
        }
        else
        {
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(raw));
            Assert.AreEqual(boundary, lifecycle.ReadBoundary());
            Assert.AreEqual(stopped, fixture.Runner.ComposeStopCount);
            Assert.IsNull(final.SignedRecoveryPauseCommandId);
            Assert.IsEmpty(lifecycle.OwnedRequests);
        }
    }

    [TestMethod]
    [DataRow("quiet")]
    [DataRow("quiet-committed-cut")]
    [DataRow("owner-during-verification")]
    [DataRow("owner-after-commit")]
    [DataRow("owner-after-lost-ack")]
    [DataRow("unsupported")]
    [DataRow("unsupported-settlement-cut")]
    [DataRow("owner-settlement-cut")]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AutomaticSignedRecovery_SuccessfulRetrySettlesOnlyItsDurablyOwnedResume(string scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        using var fixture = await AutomaticSignedFixtureAsync();
        using var release = CandidateRelease(fixture, out var candidate);
        using var crashRoot = new TemporaryDirectory();
        bool unsupported = scenario.StartsWith("unsupported", StringComparison.Ordinal);
        var lifecycle = new AutomaticSignedLifecycleClient(fixture.Paths)
        {
            SupportsOwnedResume = !unsupported,
            LoseAcknowledgement = scenario == "owner-after-lost-ack"
        };
        var request = fixture.Request(LifecycleOperationKind.Upgrade) with { Release = release.Folder };
        var ownerOnCandidate = scenario is "owner-during-verification" or "owner-settlement-cut";
        var retry = false;
        var candidateChecks = 0;
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity)
        {
            InspectVerification = _ =>
            {
                if (retry && fixture.Runner.ActiveImageId == candidate && ++candidateChecks == 2 && ownerOnCandidate)
                    lifecycle.AcceptOwnerCommand("paused", "owner-during-second-proof", changed: false);
            }
        };
        Task<LifecycleResult> RunAsync(bool resume) => CameraAgentLifecycleManager.ExecuteAsync(
            request with { Resume = resume }, fixture.Runner, _ => lifecycle, _ => owner,
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
        retry = true;

        var interrupted = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        var generic = lifecycle.GenericResumeCalls;
        var restart = fixture.Runner.ComposeRestartCount;
        bool beforeDispatch = scenario is "quiet-committed-cut" or "owner-after-commit";
        bool afterDisposition = scenario is "owner-settlement-cut" or "unsupported-settlement-cut";
        var ids = new List<Guid>();
        lifecycle.BeforeCommittedResume = () =>
        {
            var committed = CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None)
                .GetAwaiter().GetResult()!;
            Assert.AreEqual(LifecycleOperationPhase.Committed, committed.Phase);
            Assert.IsNotNull(committed.RestoreResumeCommandId);
            Assert.AreEqual(interrupted.SignedRecoveryPauseCommandId, committed.SignedRecoveryPauseCommandId);
            ids.Add(committed.RestoreResumeCommandId.Value);
            if (beforeDispatch && ids.Count == 1) throw new IOException("synthetic committed cut before owned resume");
        };
        CameraAgentLifecycleManager.JournalRecorded = state =>
        {
            if (afterDisposition && state.Phase == LifecycleOperationPhase.Committed && ReadSignedRetryOutcome(fixture.Paths) is not null)
                throw new IOException("synthetic committed cut after admission disposition");
        };
        LifecycleResult result;
        try
        {
            if (beforeDispatch || afterDisposition || lifecycle.LoseAcknowledgement)
            {
                await Assert.ThrowsExactlyAsync<InstallerException>(() => RunAsync(resume: true));
                var committed = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
                Assert.AreEqual(LifecycleOperationPhase.Committed, committed.Phase);
                Assert.AreEqual(ids.Single(), committed.RestoreResumeCommandId);
                Assert.AreEqual(afterDisposition, ReadSignedRetryOutcome(fixture.Paths) is not null);
            }
            else
            {
                result = await RunAsync(resume: true);
                await AssertResultAsync(result);
                return;
            }
        }
        finally
        {
            ResetCrashHooks();
        }
        if (scenario is "owner-after-commit" or "owner-after-lost-ack")
            lifecycle.AcceptOwnerCommand("paused", "owner-on-committed-candidate", changed: lifecycle.LoseAcknowledgement);
        if (scenario == "unsupported-settlement-cut")
            lifecycle.SupportsOwnedResume = true;
        if (scenario == "quiet-committed-cut")
        {
            var manifestPath = Path.Combine(release.Folder, "release.json");
            var sumsPath = Path.Combine(release.Folder, "SHA256SUMS");
            var originalManifest = await File.ReadAllBytesAsync(manifestPath);
            var originalSums = await File.ReadAllBytesAsync(sumsPath);
            var beforeMismatch = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
            release.RewriteText(static text => text + "\n");
            var refusal = await Assert.ThrowsExactlyAsync<InstallerException>(() => RunAsync(resume: true));
            StringAssert.Contains(refusal.Message, "original verified release checksums", StringComparison.Ordinal);
            CollectionAssert.AreEqual(beforeMismatch, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
            Assert.HasCount(1, ids);
            Assert.IsEmpty(lifecycle.OwnedRequests);
            await File.WriteAllBytesAsync(manifestPath, originalManifest);
            await File.WriteAllBytesAsync(sumsPath, originalSums);
        }

        var ups = fixture.Runner.ComposeUpCount;
        var stops = fixture.Runner.ComposeStopCount;
        result = await RunAsync(resume: true);
        Assert.AreEqual(ups, fixture.Runner.ComposeUpCount);
        Assert.AreEqual(stops, fixture.Runner.ComposeStopCount);
        Assert.HasCount(afterDisposition ? 1 : 2, ids);
        Assert.IsTrue(ids.All(id => id == ids[0]));
        await AssertResultAsync(result);

        async Task AssertResultAsync(LifecycleResult settled)
        {
            var expected = unsupported ? "completed-admission-preserved-unsupported-atomic-resume"
                : scenario is "quiet" or "quiet-committed-cut" ? "completed" : "completed-admission-preserved";
            Assert.AreEqual(expected, settled.Outcome);
            Assert.AreEqual(candidate, settled.Image!.ImageId);
            Assert.AreEqual(candidate, fixture.Runner.ActiveImageId);
            Assert.AreEqual(restart + 1, fixture.Runner.ComposeRestartCount);
            Assert.AreEqual(1, lifecycle.PauseRequests);
            Assert.AreEqual(generic, lifecycle.GenericResumeCalls);
            Assert.AreEqual(expected == "completed" ? "Running" : "Paused", lifecycle.ReadBoundary().CaptureState);
            Assert.AreEqual(unsupported ? 0 : lifecycle.LoseAcknowledgement ? 2 : 1, lifecycle.OwnedRequests.Count);
            Assert.IsTrue(lifecycle.OwnedRequests.All(id => id == ids[0]));
            var journal = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
            Assert.AreEqual(expected, ReadSignedRetryOutcome(fixture.Paths));
            Assert.AreEqual(LifecycleOperationPhase.Completed, journal!.Phase);
            Assert.IsNull(journal.RestoreResumeCommandId);
            Assert.AreEqual(interrupted.SignedRecoveryPauseCommandId, journal.SignedRecoveryPauseCommandId);
            using var distribution = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(fixture.Paths.DeploymentStateRoot, AcquiredImage.EvidenceFileName)));
            Assert.AreEqual(candidate, distribution.RootElement.GetProperty("imageId").GetString());
            using var programs = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(fixture.Paths.DeploymentStateRoot, PathPrograms.RecordName)));
            Assert.AreEqual(candidate, programs.RootElement.GetProperty("imageId").GetString());
            var calls = lifecycle.CommittedCapabilityChecks;
            var after = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
            await Assert.ThrowsExactlyAsync<InstallerException>(() => RunAsync(resume: true));
            Assert.AreEqual(calls, lifecycle.CommittedCapabilityChecks);
            Assert.AreEqual(generic, lifecycle.GenericResumeCalls);
            CollectionAssert.AreEqual(after, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
        }
    }

    private static string? ReadSignedRetryOutcome(InstallationPaths paths)
    {
        using var journal = JsonDocument.Parse(File.ReadAllText(paths.LifecycleStatePath));
        return journal.RootElement.TryGetProperty("signedRetryAdmissionOutcome", out var outcome) ? outcome.GetString() : null;
    }
}
