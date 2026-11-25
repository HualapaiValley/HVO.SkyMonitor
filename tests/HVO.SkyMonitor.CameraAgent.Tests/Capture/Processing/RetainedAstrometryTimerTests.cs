using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Components;
using HVO.SkyMonitor.CameraAgent.Tests.Services;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task CurrentSkyTimerRecoversAnEarlierFitWhileSelectedLayersKeepRetrying()
    {
        var root = CreateTestRoot();
        try
        {
            var token = CancellationToken.None;
            var timeout = TimeSpan.FromSeconds(8);
            var fixture = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            using var gate = new CaptureAstrometryExecutionGate();
            var persistence = CreatePersistence(fixture.Fixture.Options, store, storage, telemetry);
            var older = await ProduceLatestReadFixtureAsync(fixture, store, persistence, telemetry, gate).ConfigureAwait(false);
            var reference = await MeasuredAstrometryFixture.CreateAtAsync(20).ConfigureAwait(false);
            var next = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true,
                captureSequence: 61, reference: reference, prior: (1, 1)).ConfigureAwait(false);
            var newest = await ProduceLatestReadFixtureAsync(next, store, persistence, telemetry, gate).ConfigureAwait(false);
            var baseArtifact = await ProduceRetainedDisplayBaseAsync(next, persistence, telemetry).ConfigureAwait(false);
            var captureId = newest.Capture.CaptureId;
            using var artifacts = new CameraAgentArtifactService(fixture.Fixture.Options, store, new CameraAgentPreviewEncoder());
            var layerRead = new AsyncLocal<bool>();
            (TaskCompletionSource Entered, TaskCompletionSource Release)? checkpoint = null;
            var wrapper = new Mock<ICameraAgentArtifactService>(MockBehavior.Strict);
            wrapper.Setup(service => service.OpenContentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(async (Guid id, CancellationToken cancellation) =>
                {
                    if (layerRead.Value && id == newest.ArtifactId && checkpoint is { } pending)
                    {
                        pending.Entered.TrySetResult();
                        await pending.Release.Task.WaitAsync(cancellation).ConfigureAwait(false);
                    }
                    return await artifacts.OpenContentAsync(id, cancellation).ConfigureAwait(false);
                });
            using var reader = CreateUnpooledAstrometryReader(store, wrapper.Object);
            var layers = new CameraAgentLayeredPresentationService(store, artifacts, persistence: persistence, astrometry: reader);
            var service = CameraAgentOperatorUiServiceTests.CreateAuthorizedRetainedAstrometryService(reader, layers);

            var original = (await service.GetRetainedAstrometryAsync(captureId, token).ConfigureAwait(false)).Value!;
            var earlier = CameraAgentLatestAstrometry.FromVerified(
                (await service.GetRetainedAstrometryAsync(older.Capture.CaptureId, token).ConfigureAwait(false)).Value!);
            Assert.IsNotNull(earlier);
            var frame = original.Solution!.Assessment.Frame;
            var raw = RawIngressFileStore.GetPaths(root, frame.ExposureStartUtc, frame.SourceArtifactId);
            var write = File.Open(raw.PayloadAbsolutePath, FileMode.Append, FileAccess.Write);
            await using (write.ConfigureAwait(false))
                await write.WriteAsync(new byte[1], token).ConfigureAwait(false);
            var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
            var reconciled = await new RawIngressReconciler(root, journal)
                .RunAsync(_ => ValueTask.FromResult(default(CaptureMaintenanceEntry)), token).ConfigureAwait(false);
            Assert.AreEqual(1, reconciled.Quarantined);

            using var context = new BunitContext();
            context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js")
                .Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
            var ui = CurrentSkyPageTests.Configure(context);
            var clock = new CurrentSkyPageTests.ManualTimeProvider(DateTimeOffset.UtcNow);
            context.Services.AddSingleton<TimeProvider>(clock);
            var image = CurrentSkyPageTests.AstrometryImage(original);
            var combined = CurrentSkyPageTests.WithStructuredBase().Stages.Single(slot => slot.Stage == CameraAgentPresentationStage.Combined);
            image = image with
            {
                StructuredLayersAvailable = true,
                Stages = image.Stages.Select(slot => slot.Stage == CameraAgentPresentationStage.Combined
                    ? combined with { DisplayArtifactId = baseArtifact }
                    : slot).ToArray()
            };
            ui.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(image));
            ui.AstrometryPublicationHandler = service.ReadAstrometryPublicationStateAsync;
            ui.AstrometryHandler = service.GetRetainedAstrometryAsync;
            var selectedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var selectedCompleted = new TaskCompletionSource<OperatorUiResult<CameraAgentLayeredPresentation>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var historyCompleted = new TaskCompletionSource<OperatorUiResult<CameraAgentLatestAstrometryRead>>(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.PresentationHandler = ui.CurrentPresentationHandler = async (id, cancellation) =>
            {
                layerRead.Value = true;
                selectedStarted.TrySetResult();
                var result = await service.GetCurrentLayeredPresentationAsync(id, cancellation).ConfigureAwait(false);
                selectedCompleted.TrySetResult(result);
                return result;
            };
            ui.LatestAstrometrySelectionHandler = async (cancellation, previous, selected) =>
            {
                var pending = checkpoint;
                // Force the reachable ordering only when the page has already started selected work.
                if (pending is { } active && selectedStarted.Task.IsCompleted)
                    await active.Entered.Task.WaitAsync(cancellation).ConfigureAwait(false);
                var latest = await service.GetLatestAcceptedAstrometryAsync(cancellation, previous, selected).ConfigureAwait(false);
                historyCompleted.TrySetResult(latest);
                return latest;
            };

            // Establish the unavailable headline and the still-renderable selected frame with real I/O.
            using (var inaccessible = File.Open(Path.Combine(root, older.PayloadRelativePath), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                using var page = context.Render<CurrentSkyPage>();
                await CheckSelectedAsync().ConfigureAwait(false);
                var initial = await historyCompleted.Task.WaitAsync(timeout).ConfigureAwait(false);
                Assert.AreEqual(false, initial.Value?.Completed);
                page.WaitForAssertion(() =>
                {
                    Assert.IsTrue(clock.HasTimer(TimeSpan.FromSeconds(15)));
                    StringAssert.Contains(page.Find(".latest-measured-solution").TextContent, "temporarily unavailable", StringComparison.Ordinal);
                }, timeout);

                // Release the older file before advancing the actual page's next periodic refresh.
                await inaccessible.DisposeAsync().ConfigureAwait(false);
                for (var tick = 1; tick <= 2; tick++)
                {
                    selectedStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    selectedCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    historyCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    var hold = (Entered: new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                        Release: new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                    checkpoint = hold;
                    clock.Advance(TimeSpan.FromSeconds(15));
                    OperatorUiResult<CameraAgentLatestAstrometryRead> result;
                    try
                    {
                        result = await historyCompleted.Task.WaitAsync(timeout).ConfigureAwait(false);
                        if (!result.IsSuccess)
                        {
                            Assert.IsTrue(hold.Entered.Task.IsCompleted, "Selected reconstruction owns the shared permit.");
                            var busy = await service.GetRetainedAstrometryAsync(older.Capture.CaptureId, token).ConfigureAwait(false);
                            Assert.AreEqual("read-capacity", busy.Value?.Reason);
                            Console.WriteLine($"tick={tick} historyCompleted={result.Value?.Completed} selectedPermit={busy.Value?.Reason}");
                        }
                    }
                    finally { hold.Release.TrySetResult(); }
                    await CheckSelectedAsync().ConfigureAwait(false);
                    Assert.AreEqual(earlier, (await reader.ReadLatestAcceptedAsync(token).ConfigureAwait(false)).Summary);
                    page.WaitForAssertion(() => Assert.IsTrue(page.Find(".pixel-sky-calculator button").HasAttribute("disabled")), timeout);
                    Assert.IsTrue(result.IsSuccess, $"Tick {tick} failed to recover from same-page selected raw permit contention.");
                    page.WaitForAssertion(() => StringAssert.Contains(page.Find(".latest-measured-solution").TextContent,
                        earlier.SolutionIdentitySha256, StringComparison.Ordinal), timeout);
                }
            }

            async Task CheckSelectedAsync()
            {
                var result = await selectedCompleted.Task.WaitAsync(timeout).ConfigureAwait(false);
                Assert.IsTrue(result.IsSuccess);
                Assert.AreEqual("retained-evidence-invalid", result.Value?.Astrometry?.Reason);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
