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
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Integration")]
    [DataRow("layer")]
    [DataRow("standalone")]
    [DataRow("unrelated")]
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
            var unrelated = scenario == "unrelated";
            var pendingStandalone = scenario == "standalone";
            var fixture = unrelated ? first : second;
            var selected = unrelated ? older : newest;
            var captureId = selected.Capture.CaptureId;
            var baseArtifactId = await ProduceRetainedDisplayBaseAsync(fixture, persistence, telemetry).ConfigureAwait(false);

            using var artifacts = new CameraAgentArtifactService(first.Fixture.Options, store, new CameraAgentPreviewEncoder());
            using var layerReader = new CameraAgentRetainedAstrometryService(store, artifacts);
            using var latestReader = new CameraAgentRetainedAstrometryService(store, artifacts);
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
            ui.LatestAstrometrySelectionHandler = (cancellation, previous, assessment) =>
                service.GetLatestAcceptedAstrometryAsync(cancellation, previous, assessment);
            var page = context.Render<CurrentSkyPage>();
            var timeout = TimeSpan.FromSeconds(8);
            page.WaitForAssertion(() =>
            {
                StringAssert.Contains(page.Find(".latest-measured-solution").TextContent, newest.ArtifactId.ToString(), StringComparison.Ordinal);
                Assert.AreEqual(1, standaloneReads);
                Assert.AreEqual(pendingStandalone, page.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
            }, timeout);
            if (!pendingStandalone)
            {
                await page.Find(".pixel-sky-calculator input").ChangeAsync("280").ConfigureAwait(false);
                await page.FindAll(".pixel-sky-calculator input")[1].ChangeAsync("240").ConfigureAwait(false);
                await page.Find("form.pixel-sky-calculator").SubmitAsync().ConfigureAwait(false);
                StringAssert.Contains(page.Find(".astrometry-calculator-result").TextContent, "mean of date", StringComparison.Ordinal);
                await page.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
                Assert.IsTrue(heldLayer.IsPending);
            }
            else Assert.IsTrue(heldStandalone.IsPending);

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
            await page.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
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
            await page.Find("button[title='Show Processed image']").ClickAsync().ConfigureAwait(false);
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
                var reloadButton = page.FindAll("button.refresh-measured-evidence").SingleOrDefault();
                if (reloadButton is not null && !reloadButton.HasAttribute("disabled"))
                    await reloadButton.ClickAsync().ConfigureAwait(false);
                await page.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
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
                await page.Find(".pixel-sky-calculator input").ChangeAsync("280").ConfigureAwait(false);
                await page.FindAll(".pixel-sky-calculator input")[1].ChangeAsync("240").ConfigureAwait(false);
                await page.Find("form.pixel-sky-calculator").SubmitAsync().ConfigureAwait(false);
                StringAssert.Contains(page.Find(".astrometry-calculator-result").TextContent, "mean of date", StringComparison.Ordinal);
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
