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
    public async Task WorkerEntryClassifiesOnlyMatchingRollbackPartialCommit(bool matchingImage, bool matchingInstance)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidate = fixture.Manifest.Image with { ImageId = "sha256:" + new string('9', 64) };
        var operation = await CameraAgentLifecycleManager.BeginAsync(fixture.Request(LifecycleOperationKind.Rollback),
            fixture.Paths, LifecycleOperationKind.Rollback, fixture.Manifest, CancellationToken.None);
        await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation with
        {
            InstanceId = matchingInstance ? fixture.InstanceId : Guid.NewGuid(),
            Phase = LifecycleOperationPhase.CandidateVerified, MutationStarted = true, CandidateImage = candidate
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
        Assert.AreEqual(matchingImage && matchingInstance,
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
    public async Task WorkerEntryObservesLaterInstallWithoutRestart()
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
                await SafeFileSystem.WriteJsonAtomicAsync(fixture.Paths.ManifestPath,
                    fixture.Manifest with { LifecycleCondition = InstanceLifecycleCondition.Installed },
                    DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
            // A capture-version mismatch distinguishes admitted execution from installer-busy without issuing a pause.
            return EntrySnapshot(Queued());
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() => CameraAgentLifecycleManager.RunControlWorkerAsync(
            WorkerRequest(fixture) with { Once = false }, handler, stop.Token));
        Assert.AreEqual(2, terminalCodes.Count);
        Assert.AreEqual("lifecycle.installer-busy", terminalCodes[0]);
        Assert.AreEqual("lifecycle.capture-conflict", terminalCodes[1]);
        Assert.AreEqual(2, heartbeats);
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
