extern alias InstallerRuntime;

using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.Deployment.Contracts;
using Fixture = HVO.SkyMonitor.CameraAgent.Tests.Endpoints.InstallerLifecycleControlIntegrationTests.Fixture;
using RuntimeManager = InstallerRuntime::HVO.SkyMonitor.Deployment.CameraAgentLifecycleManager;
using RuntimePaths = InstallerRuntime::HVO.SkyMonitor.Deployment.InstallationPaths;
using RuntimeFiles = InstallerRuntime::HVO.SkyMonitor.Deployment.SafeFileSystem;
using RuntimeJournal = InstallerRuntime::HVO.SkyMonitor.Deployment.InstallerControlJournal;
using RuntimeWorker = InstallerRuntime::HVO.SkyMonitor.Deployment.InstallerControlWorker;
using RuntimeClient = InstallerRuntime::HVO.SkyMonitor.Deployment.InstallerControlHttpClient;
using NativeClient = InstallerRuntime::HVO.SkyMonitor.Deployment.CameraAgentLifecycleClient;
using RuntimeCatalog = InstallerRuntime::HVO.SkyMonitor.Deployment.ProductionCatalog;
using RuntimeException = InstallerRuntime::HVO.SkyMonitor.Deployment.InstallerException;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

public sealed partial class InstallerLifecycleRecoveryIntegrationTests
{
    [TestMethod]
    [DataRow(LifecycleOperationKind.Reinstall, false, LifecycleOperationPhase.Prepared, true)]
    [DataRow(LifecycleOperationKind.Rollback, false, LifecycleOperationPhase.Restoring, true)]
    [DataRow(LifecycleOperationKind.Reinstall, false, LifecycleOperationPhase.Prepared)]
    [DataRow(LifecycleOperationKind.Rollback, false, LifecycleOperationPhase.Restoring)]
    [DataRow(LifecycleOperationKind.Reinstall, false)]
    [DataRow(LifecycleOperationKind.Rollback, false)]
    [DataRow(LifecycleOperationKind.Reinstall, true)]
    [DataRow(LifecycleOperationKind.Rollback, true)]
    public async Task InstallerLifecycleCommittedRecoveryLetsTheRealWorkerSettleBeforeNativeResume(LifecycleOperationKind kind, bool terminal, LifecycleOperationPhase phase = LifecycleOperationPhase.Committed, bool crashBeforePause = false)
    {
        var fixture = new Fixture(false);
        await using var owned = fixture.ConfigureAwait(false);
        await fixture.InitializeAsync().ConfigureAwait(false);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var id = Guid.NewGuid();
        var paths = RuntimePaths.Create(Path.Combine(fixture.Root.FullName, "installer"), id, "synthetic");
        RuntimeFiles.CreateOwnerDirectory(paths.OperationsRoot);
        var journal = new RuntimeJournal(Path.Combine(paths.OperationsRoot, $"control-worker-{id:D}.json"));
        await journal.LoadAsync(watchdog.Token).ConfigureAwait(false);
        using var client = new RuntimeClient(new Uri("http://127.0.0.1"), "test-installation-token", journal.WorkerId, fixture.Handler);
        await client.ExchangeAsync(null, watchdog.Token).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var queued = await fixture.Controls.SubmitAsync(new(Guid.NewGuid(), CameraAgentControlKind.Restart,
            "owner", "interrupted restart", fixture.Capture.Snapshot.Version, now), watchdog.Token).ConfigureAwait(false);
        var draining = queued with
        {
            Phase = CameraAgentControlPhase.Draining,
            OriginalProcessId = fixture.Controls.ProcessId,
            ResumeAfterVerification = true,
            Events = [.. queued.Events, new(CameraAgentControlPhase.Draining, now, "lifecycle.draining")]
        };
        await journal.SaveAsync(draining).ConfigureAwait(false);
        await client.ExchangeAsync(draining, watchdog.Token).ConfigureAwait(false);
        if (!crashBeforePause) await client.DrainAsync(queued.Request, watchdog.Token).ConfigureAwait(false);
        if (terminal) await journal.SaveAsync(draining with
        {
            Phase = CameraAgentControlPhase.Failed,
            Events = [.. draining.Events, new(CameraAgentControlPhase.Failed, DateTimeOffset.UtcNow, "restart.verification-timeout")]
        }).ConfigureAwait(false);
        // Recovery brings a new process back while preserving the native SQLite pause and control projection.
        await fixture.ReloadAsync().ConfigureAwait(false);
        Assert.IsTrue(fixture.Controls.AdmissionHeld);
        var deployment = Guid.NewGuid();
        var native = new NativeClient(new Uri("http://127.0.0.1"), fixture.Handler);
        var paused = crashBeforePause ? null :
            await native.PauseAndDrainAsync(deployment, "test-installation-token", watchdog.Token).ConfigureAwait(false);
        var image = new ImageInstallationIdentity("registry", "test@sha256:" + new string('a', 64),
            "sha256:" + new string('b', 64), "amd64", null, SourceRevision: new string('8', 40),
            Component: "CameraAgent", ConfigurationContract: "cameraagent-install-v1", CatalogContract: "hvo-approved-catalogs-v1");
        var specification = RuntimeCatalog.Get(RuntimeCatalog.DefaultCatalogId);
        var catalog = new CatalogInstallationIdentity(RuntimeCatalog.DefaultCatalogId, specification.PackageVersionPrefix + "1",
            specification.SchemaVersion, specification.PreprocessingVersion, specification.DatabaseSha256,
            specification.DatabaseLength, specification.RowCount, paths.CatalogRoot, new string('a', 64), "local-offline");
        var operation = new LifecycleOperationState(DeploymentSchemaVersions.LifecycleOperation, deployment, kind,
            id, new string('a', 64), phase, InstallationStatus.Running, now, now, image,
            CandidateImage: kind == LifecycleOperationKind.Rollback ? image : null, OriginalCatalog: catalog,
            MutationStarted: true, PostMutationContinuity: paused is null ? null : new("Paused", paused.CaptureVersion, paused.CaptureSequence,
                0, 0, 0, 0, 0, 0, 0, 0, now));
        await RuntimeManager.RecordAsync(paths, operation, watchdog.Token).ConfigureAwait(false);
        Assert.AreEqual(InstallationStatus.Running,
            (await RuntimeManager.ReadOperationAsync(paths.LifecycleStatePath, watchdog.Token).ConfigureAwait(false))!.Status);
        var refusal = await Assert.ThrowsExactlyAsync<RuntimeException>(() =>
            native.ResumeAsync(deployment, "test-installation-token", watchdog.Token)).ConfigureAwait(false);
        StringAssert.Contains(refusal.Message, "409", StringComparison.Ordinal);
        Assert.AreEqual(crashBeforePause ? CaptureAdmissionState.Running : CaptureAdmissionState.Paused, fixture.Capture.Snapshot.State);
        var preserved = await File.ReadAllBytesAsync(paths.LifecycleStatePath).ConfigureAwait(false);
        var messages = new List<string>();
        var worker = new RuntimeWorker(client, journal, token => RuntimeManager.AcquireControlDeploymentAsync(paths, token),
            TimeProvider.System, messages.Add,
            acquireInterruptedRecovery: (receipt, token) => RuntimeManager.AcquireInterruptedControlAsync(paths,
                phase == LifecycleOperationPhase.Prepared ? InstanceLifecycleCondition.Uninstalled : InstanceLifecycleCondition.Installed,
                receipt, client, token));
        if (crashBeforePause)
        {
            var controlBytes = await File.ReadAllBytesAsync(Path.Combine(paths.OperationsRoot, $"control-worker-{id:D}.json")).ConfigureAwait(false);
            await worker.PollAsync(watchdog.Token).ConfigureAwait(false);
            Assert.IsFalse(journal.Receipts.Single().IsTerminal, "A process change alone must not release a Running replacement.");
            Assert.IsTrue(fixture.Controls.AdmissionHeld);
            Assert.AreEqual(CaptureAdmissionState.Running, fixture.Capture.Snapshot.State);
            CollectionAssert.AreEqual(preserved, await File.ReadAllBytesAsync(paths.LifecycleStatePath).ConfigureAwait(false));
            CollectionAssert.AreEqual(controlBytes, await File.ReadAllBytesAsync(Path.Combine(paths.OperationsRoot, $"control-worker-{id:D}.json")).ConfigureAwait(false));
            using (var admissionTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
                await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                {
                    using var unexpected = await fixture.Capture.EnterAsync(admissionTimeout.Token).ConfigureAwait(false);
                    unexpected.MarkNoPublicationRequired();
                }).ConfigureAwait(false);
            // A retried recovery must establish its durable pause before the receipt fence can be released.
            await native.PauseAndDrainAsync(deployment, "test-installation-token", watchdog.Token).ConfigureAwait(false);
            await fixture.ReloadAsync().ConfigureAwait(false);
            Assert.AreEqual(CaptureAdmissionState.Paused, fixture.Capture.Snapshot.State, "The recovery pause must survive reload.");
        }
        await worker.PollAsync(watchdog.Token).ConfigureAwait(false);
        Assert.IsTrue(journal.Receipts.Single().IsTerminal, string.Join("; ", messages));
        Assert.IsEmpty(journal.Unpublished);
        Assert.IsEmpty(journal.Refused);
        Assert.IsFalse(fixture.Controls.AdmissionHeld);
        CollectionAssert.AreEqual(preserved, await File.ReadAllBytesAsync(paths.LifecycleStatePath).ConfigureAwait(false));
        Assert.AreEqual(CaptureAdmissionState.Paused, fixture.Capture.Snapshot.State, "Settlement must not invent a deployment resume.");
        await native.ResumeAsync(deployment, "test-installation-token", watchdog.Token).ConfigureAwait(false);
        await RuntimeManager.CompleteAsync(paths, operation, watchdog.Token).ConfigureAwait(false);
        Assert.AreEqual(InstallationStatus.Completed,
            (await RuntimeManager.ReadOperationAsync(paths.LifecycleStatePath, watchdog.Token).ConfigureAwait(false))!.Status);
        Assert.AreEqual(CaptureAdmissionState.Running, fixture.Capture.Snapshot.State);
        using var admitted = await fixture.Capture.EnterAsync(watchdog.Token).ConfigureAwait(false);
        admitted.MarkNoPublicationRequired();
    }

}
