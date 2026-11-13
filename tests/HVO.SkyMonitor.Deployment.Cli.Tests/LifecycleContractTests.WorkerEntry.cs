using System.Net;
using System.Net.Http.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true, "kind")]
    [DataRow(true, true, "phase")]
    [DataRow(true, true, "completed")]
    [DataRow(true, true, "unmutated")]
    public async Task WorkerEntryClassifiesOnlyMatchingRollbackPartialCommit(bool matchingImage, bool matchingInstance,
        string boundary = "valid")
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidate = fixture.Manifest.Image with { ImageId = "sha256:" + new string('9', 64) };
        var operation = await CameraAgentLifecycleManager.BeginAsync(fixture.Request(LifecycleOperationKind.Rollback),
            fixture.Paths, LifecycleOperationKind.Rollback, fixture.Manifest, CancellationToken.None);
        await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation with
        {
            InstanceId = matchingInstance ? fixture.InstanceId : Guid.NewGuid(),
            Kind = boundary == "kind" ? LifecycleOperationKind.Upgrade : LifecycleOperationKind.Rollback,
            Phase = boundary == "phase" ? LifecycleOperationPhase.Restoring : boundary == "completed"
                ? LifecycleOperationPhase.Completed : boundary == "unmutated" ? LifecycleOperationPhase.Prepared : LifecycleOperationPhase.CandidateVerified,
            Status = boundary == "completed" ? InstallationStatus.Completed : InstallationStatus.Running,
            MutationStarted = boundary is not ("completed" or "unmutated"), CandidateImage = candidate
        }, CancellationToken.None);
        await SafeFileSystem.WriteJsonAtomicAsync(fixture.Paths.ManifestPath,
            fixture.Manifest with { Image = matchingImage ? candidate : candidate with { ImageId = "sha256:" + new string('7', 64) } },
            DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
        var (journal, path, _) = await PendingControlAsync(fixture, terminal: false);
        var evidence = await File.ReadAllBytesAsync(path);
        var deployment = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
        var manifest = await File.ReadAllBytesAsync(fixture.Paths.ManifestPath);
        var result = await File.ReadAllBytesAsync(fixture.Paths.ResultPath);
        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            CameraAgentLifecycleManager.RunControlWorkerAsync(WorkerRequest(fixture), CancellationToken.None));
        Assert.AreEqual(matchingImage && matchingInstance && boundary == "valid",
            failure.Message.Contains("lifecycle.rollback-partial-commit", StringComparison.Ordinal));
        CollectionAssert.AreEqual(evidence, await File.ReadAllBytesAsync(path));
        CollectionAssert.AreEqual(deployment, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
        CollectionAssert.AreEqual(manifest, await File.ReadAllBytesAsync(fixture.Paths.ManifestPath));
        CollectionAssert.AreEqual(result, await File.ReadAllBytesAsync(fixture.Paths.ResultPath));
        Assert.IsFalse(journal.Receipts.Single().IsTerminal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(InstanceLifecycleCondition.Installed)]
    [DataRow(InstanceLifecycleCondition.Uninstalled)]
    public async Task WorkerEntryRunsOneAuthenticatedHeartbeat(InstanceLifecycleCondition condition)
    {
        using var fixture = await LifecycleFixture.CreateAsync(condition);
        var calls = 0;
        using var handler = new WorkerEntryHandler(async request =>
        {
            Assert.AreEqual("lifecycle-token", request.Headers.GetValues("X-HVO-Installation-Token").Single());
            Assert.AreEqual("/api/internal/deployment/lifecycle/control/exchange", request.RequestUri!.AbsolutePath);
            var exchange = await request.Content!.ReadFromJsonAsync(CameraAgentControlJsonContext.Default.CameraAgentControlExchange);
            Assert.IsNotNull(exchange);
            Assert.AreNotEqual(Guid.Empty, exchange.WorkerId);
            calls++;
            return EntrySnapshot(null);
        });
        await CameraAgentLifecycleManager.RunControlWorkerAsync(WorkerRequest(fixture), handler, CancellationToken.None);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("installed")]
    [DataRow("listener")]
    [DataRow("token")]
    public async Task WorkerEntryObservesLaterInstallWithoutRestart(string change)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Uninstalled);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var heartbeats = 0;
        var terminalCodes = new List<string>();
        var terminalIds = new HashSet<Guid>();
        var requested = DateTimeOffset.UtcNow;
        CameraAgentControlReceipt Queued() => new(
            new(Guid.NewGuid(), CameraAgentControlKind.Drain, "owner", "maintenance", 0, requested),
            CameraAgentControlPhase.Queued, [new(CameraAgentControlPhase.Queued, requested, "lifecycle.queued")]);
        using var handler = new WorkerEntryHandler(async request =>
        {
            var exchange = await request.Content!.ReadFromJsonAsync(CameraAgentControlJsonContext.Default.CameraAgentControlExchange);
            if (exchange!.Receipt is { } receipt)
            {
                if (receipt.IsTerminal && terminalIds.Add(receipt.Request.OperationId))
                {
                    terminalCodes.Add(receipt.Events[^1].Code);
                    if (terminalCodes.Count == 2) await stop.CancelAsync();
                }
                return EntrySnapshot(receipt);
            }
            heartbeats++;
            if (heartbeats == 2)
            {
                var current = fixture.Manifest with { LifecycleCondition = InstanceLifecycleCondition.Installed };
                if (change == "listener")
                {
                    current = current with { Port = current.Port + 1 };
                    var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
                    await SafeFileSystem.WriteJsonAtomicAsync(fixture.Paths.ResultPath,
                        result with { Url = new Uri($"http://127.0.0.1:{current.Port}") },
                        DeploymentJsonContext.Default.InstallationResult, CancellationToken.None);
                }
                if (change == "token") current = current with { LifecycleControlTokenSha256 = new string('8', 64) };
                await SafeFileSystem.WriteJsonAtomicAsync(fixture.Paths.ManifestPath, current,
                    DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
            }
            // A capture-version mismatch distinguishes admitted execution from installer-busy without issuing a pause.
            return EntrySnapshot(Queued());
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() => CameraAgentLifecycleManager.RunControlWorkerAsync(
            WorkerRequest(fixture) with { Once = false }, handler, stop.Token));
        Assert.AreEqual(2, terminalCodes.Count);
        Assert.AreEqual("lifecycle.installer-busy", terminalCodes[0]);
        Assert.AreEqual(change == "installed" ? "lifecycle.capture-conflict" : "lifecycle.installer-busy", terminalCodes[1]);
        Assert.AreEqual(2, heartbeats);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(LifecycleOperationKind.Rollback, InstanceLifecycleCondition.Installed)]
    [DataRow(LifecycleOperationKind.Reinstall, InstanceLifecycleCondition.Uninstalled)]
    [DataRow(LifecycleOperationKind.Reinstall, InstanceLifecycleCondition.Uninstalled, true)]
    public async Task WorkerEntryRecoversRetainedReceiptsAfterRefresh(LifecycleOperationKind kind, InstanceLifecycleCondition condition,
        bool completeInstall = false)
    {
        using var fixture = await LifecycleFixture.CreateAsync(condition);
        var operation = await CameraAgentLifecycleManager.BeginAsync(fixture.Request(kind), fixture.Paths, kind,
            fixture.Manifest, CancellationToken.None);
        var candidate = fixture.Manifest.Image with { ImageId = "sha256:" + new string('9', 64) };
        await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation with
        {
            Phase = kind == LifecycleOperationKind.Rollback ? LifecycleOperationPhase.CandidateVerified : LifecycleOperationPhase.Prepared,
            MutationStarted = kind == LifecycleOperationKind.Rollback,
            CandidateImage = kind == LifecycleOperationKind.Rollback ? candidate : null
        }, CancellationToken.None);
        var (journal, path, original) = await PendingControlAsync(fixture, terminal: false);
        original = original with { OriginalProcessId = Guid.NewGuid(), ResumeAfterVerification = true };
        await journal.SaveAsync(original);
        var deployment = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
        var result = await File.ReadAllBytesAsync(fixture.Paths.ResultPath);
        var pending = await File.ReadAllBytesAsync(path);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var reads = 0;
        var publications = 0;
        var process = Guid.NewGuid();
        using var handler = new WorkerEntryHandler(async request =>
        {
            Assert.AreEqual("/api/internal/deployment/lifecycle/control/exchange", request.RequestUri!.AbsolutePath);
            var exchange = await request.Content!.ReadFromJsonAsync(CameraAgentControlJsonContext.Default.CameraAgentControlExchange);
            if (exchange!.Receipt is { IsTerminal: true }) publications++;
            if (exchange.Receipt is null)
            {
                reads++;
                if (reads == 2)
                {
                    CollectionAssert.AreEqual(pending, await File.ReadAllBytesAsync(path));
                    if (kind == LifecycleOperationKind.Rollback)
                        await SafeFileSystem.WriteJsonAtomicAsync(fixture.Paths.ManifestPath,
                            fixture.Manifest with { Image = candidate }, DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
                    if (completeInstall)
                    {
                        await SafeFileSystem.WriteJsonAtomicAsync(fixture.Paths.ManifestPath,
                            fixture.Manifest with { LifecycleCondition = InstanceLifecycleCondition.Installed },
                            DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
                        await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation with
                        {
                            Phase = LifecycleOperationPhase.Completed, Status = InstallationStatus.Completed, MutationStarted = false
                        }, CancellationToken.None);
                        deployment = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
                    }
                }
                if (publications >= 2) await stop.CancelAsync();
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new CameraAgentControlSnapshot(publications > 0 ? null : original, false,
                    process, reads == 1 ? "Running" : "Paused", 1, true, true, true),
                    CameraAgentControlJsonContext.Default.CameraAgentControlSnapshot)
            };
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() => CameraAgentLifecycleManager.RunControlWorkerAsync(
            WorkerRequest(fixture) with { Once = false }, handler, stop.Token));
        Assert.IsTrue(publications >= 2, "Refreshed interrupted acquisition must retain safe settlement access.");
        var reloaded = new InstallerControlJournal(path);
        await reloaded.LoadAsync(CancellationToken.None);
        Assert.IsTrue(reloaded.Receipts.Single().IsTerminal);
        Assert.IsEmpty(reloaded.Unpublished);
        Assert.IsEmpty(reloaded.Refused);
        Assert.AreEqual("lifecycle.installer-interrupted", reloaded.Receipts.Single().Events[^1].Code);
        CollectionAssert.AreEqual(deployment, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
        CollectionAssert.AreEqual(result, await File.ReadAllBytesAsync(fixture.Paths.ResultPath));
    }

    private static LifecycleWorkerRequest WorkerRequest(LifecycleFixture fixture) =>
        new(fixture.InstanceId, fixture.Root, true, false) { AllowTestProductRoot = true };

    private static HttpResponseMessage EntrySnapshot(CameraAgentControlReceipt? receipt) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new CameraAgentControlSnapshot(receipt, false, Guid.NewGuid(), "Paused", 1, true, true, true),
            CameraAgentControlJsonContext.Default.CameraAgentControlSnapshot)
    };

    private sealed class WorkerEntryHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> exchange) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => exchange(request);
    }
}
