using System.Threading.Tasks.Sources;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Components;
using HVO.SkyMonitor.CameraAgent.Tests.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Integration")]
    [DataRow("layer")]
    [DataRow("standalone")]
    [DataRow("unrelated")]
    [DataRow("unrelated-materialized")]
    [DataRow("repair")]
    public async Task CurrentSkyRejectsOutstandingWorkAfterRetainedFitInvalidation(string scenario)
    {
        var root = CreateTestRoot();
        try
        {
            var token = CancellationToken.None;
            var first = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(first.Fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            using var gate = new CaptureAstrometryExecutionGate();
            var persistence = CreatePersistence(first.Fixture.Options, store, storage, telemetry);
            var older = await ProduceLatestReadFixtureAsync(first, store, persistence, telemetry, gate).ConfigureAwait(false);
            var second = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true, captureSequence: 61,
                reference: await MeasuredAstrometryFixture.CreateAtAsync(20).ConfigureAwait(false), prior: (1, 1)).ConfigureAwait(false);
            var newest = await ProduceLatestReadFixtureAsync(second, store, persistence, telemetry, gate).ConfigureAwait(false);
            var holdMaterializedHistory = scenario == "unrelated-materialized";
            var unrelated = scenario == "unrelated" || holdMaterializedHistory;
            var pendingStandalone = scenario == "standalone";
            var fixture = unrelated ? first : second;
            var selected = unrelated ? older : newest;
            var captureId = selected.Capture.CaptureId;
            var baseArtifactId = await ProduceRetainedDisplayBaseAsync(fixture, persistence, telemetry).ConfigureAwait(false);

            using var artifacts = new CameraAgentArtifactService(first.Fixture.Options, store, new CameraAgentPreviewEncoder());
            using var layerReader = new CameraAgentRetainedAstrometryService(store, artifacts);
            var historyRequests = 0;
            var historyHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseHistory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var preEvictionResponse = new TaskCompletionSource<OperatorUiResult<CameraAgentLatestAstrometryRead>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            ICameraAgentArtifactService? historyOverride = null;
            if (scenario == "unrelated")
            {
                var checkpoint = new Mock<ICameraAgentArtifactService>(MockBehavior.Strict);
                checkpoint.Setup(reader => reader.OpenContentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .Returns(async (Guid id, CancellationToken cancellation) =>
                    {
                        if (Volatile.Read(ref historyRequests) == 2 && id == newest.Artifact.SourceArtifactIds[0])
                        {
                            // The history scan has already chosen its candidate snapshot and acquired the raw permit.
                            historyHeld.TrySetResult();
                            await releaseHistory.Task.WaitAsync(cancellation).ConfigureAwait(false);
                        }
                        return await artifacts.OpenContentAsync(id, cancellation).ConfigureAwait(false);
                    });
                historyOverride = checkpoint.Object;
            }
            using var latestReader = historyOverride is null
                ? new CameraAgentRetainedAstrometryService(store, artifacts)
                : CreateUnpooledAstrometryReader(store, historyOverride);
            var layers = new CameraAgentLayeredPresentationService(store, artifacts, persistence: persistence, astrometry: layerReader);
            var service = CameraAgentOperatorUiServiceTests.CreateAuthorizedRetainedAstrometryService(latestReader, layers);
            // Keep the deliberately interleaved selected and headline reads from competing for
            // one bounded raw reader. Both still verify the same persistent journal and files.
            var selectedService = CameraAgentOperatorUiServiceTests.CreateAuthorizedRetainedAstrometryService(layerReader);
            var selectedFit = await selectedService.GetRetainedAstrometryAsync(captureId, token).ConfigureAwait(false);
            var enriched = await service.GetCurrentLayeredPresentationAsync(captureId, token).ConfigureAwait(false);
            var olderFit = CameraAgentLatestAstrometry.FromVerified(await layerReader.GetAsync(older.Capture.CaptureId, token).ConfigureAwait(false));
            Assert.IsNotNull(olderFit);
            Assert.IsTrue(selectedFit.IsSuccess);
            Assert.IsNotNull(selectedFit.Value?.Mapping);
            Assert.IsTrue(enriched.IsSuccess);
            Assert.AreEqual(baseArtifactId, enriched.Value?.Astrometry?.CompatibleBaseArtifactId);
            Assert.HasCount(2, enriched.Value!.Layers);

            var image = CurrentSkyPageTests.AstrometryImage(selectedFit.Value);
            var structured = CurrentSkyPageTests.WithStructuredBase();
            var rawSlot = image.Stages.Single(slot => slot.Stage == CameraAgentPresentationStage.Raw);
            image = image with
            {
                StructuredLayersAvailable = true,
                Stages = structured.Stages.Select(slot => slot.Stage switch
                {
                    CameraAgentPresentationStage.Raw => rawSlot,
                    CameraAgentPresentationStage.Combined => slot with { DisplayArtifactId = baseArtifactId },
                    _ => slot
                }).ToArray()
            };
            using var context = new BunitContext();
            context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js")
                .Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
            var ui = CurrentSkyPageTests.Configure(context);
            context.Services.AddSingleton<TimeProvider>(new CurrentSkyPageTests.ManualTimeProvider(OperatorUiTestData.Now));
            ui.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(image));
            var heldLayer = new HeldDisplayRead<CameraAgentLayeredPresentation>();
            var heldStandalone = new HeldDisplayRead<CameraAgentRetainedAstrometry>();
            var layerReads = 0;
            var standaloneReads = 0;
            var freshReads = 0;
            CancellationToken heldToken = default;
            ui.PresentationHandler = (id, cancellation) =>
            {
                if (++layerReads == 1 || pendingStandalone && layerReads == 2)
                    return ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Failure(OperatorUiResultKind.NotFound, "Pending layers"));
                if (layerReads != 2) return service.GetLayeredPresentationAsync(id, cancellation);
                heldToken = cancellation;
                return heldLayer.Read();
            };
            ui.CurrentPresentationHandler = (id, cancellation) =>
            {
                freshReads++;
                return service.GetCurrentLayeredPresentationAsync(id, cancellation);
            };
            ui.AstrometryHandler = (id, cancellation) =>
            {
                if (++standaloneReads != 1 || !pendingStandalone) return selectedService.GetRetainedAstrometryAsync(id, cancellation);
                heldToken = cancellation;
                return heldStandalone.Read();
            };
            ui.AstrometryPublicationHandler = service.ReadAstrometryPublicationStateAsync;
            var selectedFitRendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.LatestAstrometrySelectionHandler = async (cancellation, previous, assessment) =>
            {
                var request = Interlocked.Increment(ref historyRequests);
                // Applying a headline advances the observation revision. Install the initial selected fit
                // before that can supersede its verified observation on the renderer.
                if (request == 1 && !pendingStandalone)
                    await selectedFitRendered.Task.WaitAsync(cancellation).ConfigureAwait(false);
                var response = await service.GetLatestAcceptedAstrometryAsync(cancellation, previous, assessment).ConfigureAwait(false);
                if (unrelated && request == 2)
                {
                    preEvictionResponse.TrySetResult(response);
                    if (holdMaterializedHistory)
                    {
                        historyHeld.TrySetResult();
                        await releaseHistory.Task.WaitAsync(cancellation).ConfigureAwait(false);
                    }
                }
                return response;
            };
            var page = context.Render<CurrentSkyPage>();
            var timeout = TimeSpan.FromSeconds(8);

            // Resolve each event target on the renderer so background renders cannot retire its handler first.
            Task RefreshAsync() => page.InvokeAsync(() => page.Find("button.refresh-link").ClickAsync());
            Task CalculatePixelToSkyAsync() => page.InvokeAsync(async () =>
            {
                await page.Find(".pixel-sky-calculator input").ChangeAsync("280").ConfigureAwait(true);
                await page.FindAll(".pixel-sky-calculator input")[1].ChangeAsync("240").ConfigureAwait(true);
                await page.Find("form.pixel-sky-calculator").SubmitAsync().ConfigureAwait(true);
                StringAssert.Contains(page.Find(".astrometry-calculator-result").TextContent, "mean of date", StringComparison.Ordinal);
            });

            try
            {
                await page.WaitForAssertionAsync(() =>
                {
                    Assert.AreEqual(1, standaloneReads);
                    Assert.AreEqual(pendingStandalone, page.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
                    if (pendingStandalone) Assert.IsTrue(heldStandalone.IsPending);
                }, timeout).ConfigureAwait(false);
            }
            finally { selectedFitRendered.TrySetResult(); }
            await page.WaitForAssertionAsync(() =>
                StringAssert.Contains(page.Find(".latest-measured-solution").TextContent, newest.ArtifactId.ToString(), StringComparison.Ordinal),
                timeout).ConfigureAwait(false);
            if (!pendingStandalone)
            {
                await CalculatePixelToSkyAsync().ConfigureAwait(false);
                await RefreshAsync().ConfigureAwait(false);
                Assert.IsTrue(heldLayer.IsPending);
                if (unrelated) await historyHeld.Task.WaitAsync(timeout).ConfigureAwait(false);
            }

            var reconciler = new DerivedProductReconciler(root, store);
            var asset = Path.Combine(root, newest.PayloadRelativePath);
            var sidecar = Path.Combine(root, newest.SidecarRelativePath);
            byte[]? originalPayload = null;
            byte[]? originalSidecar = null;
            if (scenario == "repair")
            {
                originalPayload = await File.ReadAllBytesAsync(asset, token).ConfigureAwait(false);
                originalSidecar = await File.ReadAllBytesAsync(sidecar, token).ConfigureAwait(false);
                var corrupt = (byte[])originalPayload.Clone();
                corrupt[0] ^= 1;
                await File.WriteAllBytesAsync(asset, corrupt, token).ConfigureAwait(false);
                Assert.AreEqual(1, (await reconciler.RunAsync(token).ConfigureAwait(false)).Quarantined);
                Assert.AreEqual("Quarantined", (await store.ReadOutputByArtifactIdAsync(newest.ArtifactId, token).ConfigureAwait(false))?.AvailabilityState);
            }
            else
            {
                await store.PlanLifecycleOperationAsync(new(
                    Guid.NewGuid().ToString("N"), "delete", newest.OutputIdentitySha256, newest.PayloadRelativePath,
                    newest.SidecarRelativePath, "processing-quarantine/expired-display-fit", "retention-expired", 0), token).ConfigureAwait(false);
                _ = await reconciler.RunAsync(token).ConfigureAwait(false);
                Assert.IsNull(await store.ReadOutputByArtifactIdAsync(newest.ArtifactId, token).ConfigureAwait(false));
            }
            Assert.IsFalse(File.Exists(asset));
            try
            {
                await RefreshAsync().ConfigureAwait(false);
                if (unrelated) Assert.AreEqual(2, Volatile.Read(ref historyRequests));
            }
            finally { releaseHistory.TrySetResult(); }
            if (unrelated)
            {
                var response = await preEvictionResponse.Task.WaitAsync(timeout).ConfigureAwait(false);
                Assert.IsNotNull(response.Value);
                Assert.AreEqual(holdMaterializedHistory, response.Value.Completed);
                Assert.AreEqual(holdMaterializedHistory ? newest.ArtifactId : (Guid?)null, response.Value.Summary?.ArtifactId);
                Assert.AreEqual(!holdMaterializedHistory, response.Value.PreviousSummaryEvicted);
                Assert.IsFalse(response.Value.SelectedAssessmentInvalidated);
            }
            page.WaitForAssertion(() =>
            {
                var headline = page.Find(".latest-measured-solution").TextContent;
                StringAssert.Contains(headline, olderFit.ArtifactId.ToString(), StringComparison.Ordinal);
                StringAssert.Contains(headline, olderFit.SolutionIdentitySha256, StringComparison.Ordinal);
                StringAssert.Contains(headline, olderFit.MidpointUtc.ToString("O"), StringComparison.Ordinal);
                StringAssert.Contains(headline, unrelated ? "For the selected capture" : "For a different capture", StringComparison.Ordinal);
                Assert.AreEqual(!unrelated, page.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
                Assert.AreEqual(unrelated ? 1 : 0, page.FindAll(".astrometry-calculator-result").Count);
            }, timeout);

            if (unrelated) Assert.AreEqual(3, Volatile.Read(ref historyRequests));

            // Deliver the already captured payload on the renderer, even if its token was canceled.
            // This exercises the continuation guards rather than depending on a cooperative service.
            await page.InvokeAsync(() =>
            {
                if (pendingStandalone) heldStandalone.Complete(selectedFit);
                else heldLayer.Complete(enriched);
            }).ConfigureAwait(false);
            if (pendingStandalone)
            {
                Assert.IsTrue(page.Find(".pixel-sky-calculator button").HasAttribute("disabled"),
                    "Completing pre-invalidation standalone work must not restore an invalidated mapping.");
            }
            await page.InvokeAsync(() => page.Find("button[title='Show Processed image']").ClickAsync()).ConfigureAwait(false);
            if (!pendingStandalone && !heldToken.IsCancellationRequested)
            {
                // A live response did not hit the cancellation guard. Wait until the component has applied
                // the image projection so the following assertion observes its actual rendered tools.
                page.WaitForElement(".sky-layer-canvas img", timeout);
            }
            Assert.AreEqual(!unrelated, page.Find(".pixel-sky-calculator button").HasAttribute("disabled"),
                "Completing pre-invalidation layer work must not restore an invalidated mapping.");
            foreach (var layer in enriched.Value.Layers)
                Assert.AreEqual(unrelated, page.FindAll($"g[id='{layer.DomGroupId}']").Count != 0,
                    "The outstanding enriched response must not restore invalidated measured overlays.");
            Assert.AreEqual(!unrelated, heldToken.IsCancellationRequested, "Only the invalidated source's pending work is canceled.");
            if (unrelated) return;
            Assert.IsEmpty(page.FindAll(".astrometry-calculator-result"));

            if (originalPayload is not null)
            {
                await File.WriteAllBytesAsync(asset, originalPayload, token).ConfigureAwait(false);
                await File.WriteAllBytesAsync(sidecar, originalSidecar!, token).ConfigureAwait(false);
                // Release the verified original bytes through the journal's availability transition.
                // The fresh reader still checks the complete retained lineage and hashes.
                await store.SetOutputAvailabilityAsync(newest.OutputIdentitySha256, "Available", null, token).ConfigureAwait(false);
                await page.InvokeAsync(async () =>
                {
                    var reloadButton = page.FindAll("button.refresh-measured-evidence").SingleOrDefault();
                    if (reloadButton is not null && !reloadButton.HasAttribute("disabled"))
                        await reloadButton.ClickAsync().ConfigureAwait(true);
                }).ConfigureAwait(false);
                await RefreshAsync().ConfigureAwait(false);
                page.WaitForAssertion(() =>
                {
                    Assert.IsFalse(page.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
                    StringAssert.Contains(page.Find(".latest-measured-solution").TextContent, newest.ArtifactId.ToString(), StringComparison.Ordinal);
                    foreach (var layer in enriched.Value.Layers)
                        Assert.HasCount(1, page.FindAll($"g[id='{layer.DomGroupId}']"));
                    Assert.IsEmpty(page.FindAll(".astrometry-calculator-result"));
                    Assert.IsTrue(page.FindAll(".pixel-sky-calculator input")
                        .All(input => string.IsNullOrEmpty(input.GetAttribute("value"))));
                }, timeout);
                await CalculatePixelToSkyAsync().ConfigureAwait(false);
            }
            Assert.IsTrue(freshReads > 0, "The selected capture needs a read begun after invalidation.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class HeldDisplayRead<T> : IValueTaskSource<OperatorUiResult<T>>
    {
        private Action<object?>? _continuation;
        private object? _state;
        private OperatorUiResult<T>? _result;
        public bool IsPending => _continuation is not null && _result is null;
        public ValueTask<OperatorUiResult<T>> Read() => new(this, 0);
        public ValueTaskSourceStatus GetStatus(short token) => _result is null ? ValueTaskSourceStatus.Pending : ValueTaskSourceStatus.Succeeded;
        public OperatorUiResult<T> GetResult(short token) => _result!;
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            _continuation = continuation;
            _state = state;
        }
        public void Complete(OperatorUiResult<T> result)
        {
            Assert.IsNotNull(_continuation);
            _result = result;
            _continuation(_state);
        }
    }
}
