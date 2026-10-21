using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task Expiration_PendingPauseFinishesOneJournaledDeletionAndResumesRemainingOutputs()
    {
        using var root = new MaintenanceHoldRoot();
        using var telemetry = new CaptureProcessingTelemetry();
        var options = (await CreateFixtureAsync(root.Path).ConfigureAwait(false)).Options;
        using var store = new SqliteCaptureProcessingStore(options);
        using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        var persistence = CreatePersistence(options, store, storage, telemetry);
        var outputs = new List<DurableProcessingOutput>();
        for (var sequence = 1; sequence <= 2; sequence++)
        {
            var fixture = await CreateFixtureAsync(root.Path, sequence, $"raw-{sequence}", new byte[8]).ConfigureAwait(false);
            var producer = new ProducingStep();
            var graph = new CaptureProcessingGraph([
                new CaptureProcessingGraphNode(producer.Name, producer, [], true, producer.RecipeName,
                    producer.OutputRole, producer.OutputVariant, new string('A', 64))]);
            var produced = await FrameProcessingWorker.ProcessGraphItemAsync(fixture.Item, graph, persistence, telemetry,
                1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, produced.Outcome, produced.Reason);
            var node = await store.ReadNodeAsync(fixture.Manifest.Descriptor.Capture.CaptureId,
                producer.Name, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(node);
            outputs.Add(node.Outputs.Single());
        }
        using var admission = await root.AdmissionAsync(paused: false).ConfigureAwait(false);
        using var entry = await CaptureMaintenanceHold.EnterAsync(admission, CancellationToken.None).ConfigureAwait(false);
        Task<CaptureControlCommandResult>? pause = null;
        var holds = new Mock<IReadOnlySet<string>>(MockBehavior.Strict);
        holds.Setup(value => value.Contains(It.IsAny<string>())).Callback(() =>
        {
            // Pause while the first candidate is active; its file moves, SQLite commit and tombstone cleanup must finish.
            pause ??= admission.PauseAsync("expiration-pause", null, "test", null, CancellationToken.None);
        }).Returns(false);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await persistence.ExpireOutputsAsync(root.Path, DateTimeOffset.MaxValue, holds.Object,
                CancellationToken.None, entry.InterruptionToken).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.IsNotNull(pause);
        Assert.IsFalse(pause.IsCompleted, "The lease must still hold the acknowledgement barrier.");
        Assert.AreEqual(1, outputs.Count(output => File.Exists(Path.Combine(root.Path, output.PayloadRelativePath))));
        foreach (var output in outputs)
            Assert.AreEqual(File.Exists(Path.Combine(root.Path, output.PayloadRelativePath)),
                File.Exists(Path.Combine(root.Path, output.SidecarRelativePath)), "A pause cannot split a product pair.");
        Assert.IsEmpty((await store.ReadActionableLifecyclePageAsync(null, 16, CancellationToken.None).ConfigureAwait(false)).Items,
            "The active deletion must complete every journal phase before yielding.");
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(Path.Combine(root.Path, "processing-deletion-tombstones")).Any());
        entry.Dispose();
        await pause.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await admission.ResumeAsync("expiration-resume", null, "test", null, CancellationToken.None).ConfigureAwait(false);
        using var resumed = await CaptureMaintenanceHold.EnterAsync(admission, CancellationToken.None).ConfigureAwait(false);
        var none = new HashSet<string>(StringComparer.Ordinal);
        Assert.AreEqual(2, await persistence.ExpireOutputsAsync(root.Path, DateTimeOffset.MaxValue, none,
            CancellationToken.None, resumed.InterruptionToken).ConfigureAwait(false));
        Assert.AreEqual(0, await persistence.ExpireOutputsAsync(root.Path, DateTimeOffset.MaxValue, none,
            CancellationToken.None, resumed.InterruptionToken).ConfigureAwait(false));
        Assert.IsFalse(outputs.Any(output => File.Exists(Path.Combine(root.Path, output.PayloadRelativePath))));
        Assert.IsEmpty((await store.ReadProcessingEvidencePageAsync(null, 16, CancellationToken.None).ConfigureAwait(false)).Items);
    }
}
