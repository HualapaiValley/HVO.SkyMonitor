using System.Threading.Tasks.Sources;
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
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task CurrentSkyRejectsLateRetainedLayersAndShowsExactRevalidatedFit()
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
            var capture = newest.Capture.CaptureId;
            var displayArtifact = await ProduceRetainedDisplayBaseAsync(next, persistence, telemetry).ConfigureAwait(false);

            using var artifacts = new CameraAgentArtifactService(fixture.Fixture.Options, store, new CameraAgentPreviewEncoder());
            using var selectedReader = new CameraAgentRetainedAstrometryService(store, artifacts);
            using var latestReader = new CameraAgentRetainedAstrometryService(store, artifacts);
            var layers = new CameraAgentLayeredPresentationService(store, artifacts, persistence: persistence, astrometry: selectedReader);
            var service = CameraAgentOperatorUiServiceTests.CreateAuthorizedRetainedAstrometryService(latestReader, layers);
            var selectedService = CameraAgentOperatorUiServiceTests.CreateAuthorizedRetainedAstrometryService(selectedReader);
            var originalLayerRead = await service.GetCurrentLayeredPresentationAsync(capture, token).ConfigureAwait(false);
            Assert.IsTrue(originalLayerRead.IsSuccess);
            var originalLayers = originalLayerRead.Value;
            Assert.IsNotNull(originalLayers);
            var original = originalLayers.Astrometry;
            Assert.IsNotNull(original);
            Assert.AreEqual(displayArtifact, original.CompatibleBaseArtifactId);
            var originalSummary = CameraAgentLatestAstrometry.FromVerified(original);
            var olderSummary = CameraAgentLatestAstrometry.FromVerified(
                await selectedReader.GetAsync(older.Capture.CaptureId, token).ConfigureAwait(false));
            Assert.IsNotNull(originalSummary);
            Assert.IsNotNull(olderSummary);
            var frame = original.Solution!.Assessment.Frame;
            var paths = RawIngressFileStore.GetPaths(root, frame.ExposureStartUtc, frame.SourceArtifactId);
            var payload = await File.ReadAllBytesAsync(paths.PayloadAbsolutePath, token).ConfigureAwait(false);
            var sidecar = await File.ReadAllBytesAsync(paths.SidecarAbsolutePath, token).ConfigureAwait(false);
            var layerIds = new[]
            {
                originalLayers.Layers.Single(layer => layer.Kind == AstrometricDiagnosticPresentation.AssociationsLayer).DomGroupId,
                originalLayers.Layers.Single(layer => layer.Kind == AstrometricDiagnosticPresentation.ResidualsLayer).DomGroupId
            };

            var heldLayer = new DeferredRetainedLayerRead();
            var rawQuarantined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var armLayer = 0;
            var awaitQuarantine = 0;
            var heldCancellation = CancellationToken.None;
            var allowFreshLayers = 0;
            using var context = new BunitContext();
            context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js")
                .Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
            var ui = CurrentSkyPageTests.Configure(context);
            var image = CurrentSkyPageTests.AstrometryImage(original);
            var combined = CurrentSkyPageTests.WithStructuredBase().Stages.Single(slot => slot.Stage == CameraAgentPresentationStage.Combined);
            image = image with
            {
                StructuredLayersAvailable = true,
                Stages = image.Stages.Select(slot => slot.Stage == CameraAgentPresentationStage.Combined
                    ? combined with { DisplayArtifactId = displayArtifact }
                    : slot).ToArray()
            };
            ui.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(image));
            var standaloneLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.AstrometryHandler = async (id, cancellation) =>
            {
                var result = await selectedService.GetRetainedAstrometryAsync(id, cancellation).ConfigureAwait(false);
                standaloneLoaded.TrySetResult();
                return result;
            };
            ui.PresentationHandler = (id, cancellation) =>
            {
                if (Interlocked.Exchange(ref armLayer, 0) != 0)
                {
                    Assert.AreEqual(capture, id);
                    heldCancellation = cancellation;
                    return heldLayer.Read();
                }
                return Volatile.Read(ref allowFreshLayers) != 0 ? service.GetLayeredPresentationAsync(id, cancellation)
                    : ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Failure(OperatorUiResultKind.NotFound, "Pending layers"));
            };
            ui.CurrentPresentationHandler = service.GetCurrentLayeredPresentationAsync;
            ui.LatestAstrometrySelectionHandler = async (cancellation, previous, selected) =>
            {
                await standaloneLoaded.Task.WaitAsync(cancellation).ConfigureAwait(false);
                if (Interlocked.Exchange(ref awaitQuarantine, 0) != 0)
                {
                    Assert.AreEqual(originalSummary, previous);
                    Assert.AreEqual(new CameraAgentAstrometryAssessmentIdentity(capture, originalSummary.ArtifactId,
                        originalSummary.SolutionIdentitySha256), selected);
                    await rawQuarantined.Task.WaitAsync(cancellation).ConfigureAwait(false);
                }
                return await service.GetLatestAcceptedAstrometryAsync(cancellation, previous, selected).ConfigureAwait(false);
            };

            var page = context.Render<CurrentSkyPage>();
            page.WaitForAssertion(() =>
            {
                AssertHeadline(originalSummary, "For the selected capture");
                AssertCalculators(enabled: true);
            }, timeout);
            await CalculatePixelToSkyAsync().ConfigureAwait(false);
            Assert.HasCount(1, page.FindAll(".astrometry-calculator-result"));

            await page.Find("button[title='Show Processed image']").ClickAsync().ConfigureAwait(false);
            Volatile.Write(ref awaitQuarantine, 1);
            Volatile.Write(ref armLayer, 1);
            var refresh = page.Find("button.refresh-link").ClickAsync();
            await heldLayer.Awaiting.Task.WaitAsync(timeout).ConfigureAwait(false);
            try
            {
                await File.WriteAllBytesAsync(paths.PayloadAbsolutePath, [.. payload, 0], token).ConfigureAwait(false);
                var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
                var reconciled = await new RawIngressReconciler(root, journal)
                    .RunAsync(_ => ValueTask.FromResult(default(CaptureMaintenanceEntry)), token).ConfigureAwait(false);
                Assert.AreEqual(1, reconciled.Quarantined);
            }
            finally { rawQuarantined.TrySetResult(); }
            await refresh.ConfigureAwait(false);
            page.WaitForAssertion(AssertWithdrawn, timeout);

            // Complete off the renderer so the read continuation can dispatch before the barrier.
            await Task.Run(() => heldLayer.Complete(originalLayerRead), token).ConfigureAwait(false);
            await page.InvokeAsync(static () => { }).ConfigureAwait(false);
            Assert.IsTrue(heldLayer.ResponseConsumed);
            AssertWithdrawn();
            Assert.IsTrue(heldCancellation.IsCancellationRequested, "The invalidated layer request must be retired.");

            using var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
            var ingressState = new RawIngressState(TimeProvider.System);
            using var ingressTelemetry = new RawIngressTelemetry(ingressState);
            using var ingress = ActivatorUtilities.CreateInstance<RawCaptureIngress>(services, fixture.Fixture.Options,
                new FileSystemStorageCapacityProvider(), ingressState, TimeProvider.System, ingressTelemetry,
                NullLogger<RawCaptureIngress>.Instance, new NullRawIngressFaultInjector());
            var evidence = await ingress.ReadEvidencePageAsync(10, null, token).ConfigureAwait(false);
            var target = evidence.Items.Single(item => item.Target.CaptureId == capture).Target;
            Assert.AreEqual("quarantined", target.State);
            await File.WriteAllBytesAsync(paths.PayloadAbsolutePath, payload, token).ConfigureAwait(false);
            await File.WriteAllBytesAsync(paths.SidecarAbsolutePath, sidecar, token).ConfigureAwait(false);
            var repair = await ingress.ReleaseEvidenceAsync(target, RawEvidenceReleaseAction.Revalidate,
                "verify-restored-current-fit", "owner", "evidence-restored", token).ConfigureAwait(false);
            Assert.IsFalse(repair.Duplicate);
            Assert.AreEqual(RawEvidenceReleaseAction.Revalidate, repair.Audit.Action);
            Volatile.Write(ref allowFreshLayers, 1);

            await page.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
            page.WaitForAssertion(() =>
            {
                AssertHeadline(originalSummary, "For the selected capture");
                var provenance = page.Find(".astrometry-provenance").TextContent;
                StringAssert.Contains(provenance, capture.ToString(), StringComparison.Ordinal);
                StringAssert.Contains(provenance, frame.SourceArtifactId.ToString(), StringComparison.Ordinal);
                StringAssert.Contains(provenance, frame.SourceDescriptorSha256, StringComparison.Ordinal);
                StringAssert.Contains(provenance, frame.SourcePayloadSha256, StringComparison.Ordinal);
                StringAssert.Contains(provenance, originalSummary.SolutionIdentitySha256, StringComparison.Ordinal);
                AssertCalculators(enabled: true);
                Assert.IsEmpty(page.FindAll(".astrometry-calculator-result"));
                Assert.IsFalse(page.Find("button.restore-layers").HasAttribute("disabled"));
                Assert.IsFalse(page.Find(".sky-layer-save button").HasAttribute("disabled"));
                foreach (var id in layerIds)
                {
                    Assert.HasCount(1, page.FindAll($"g[id='{id}']"));
                    Assert.IsFalse(page.Find($"input[data-layer-target='{id}']").HasAttribute("disabled"));
                }
            }, timeout);
            await CalculatePixelToSkyAsync().ConfigureAwait(false);
            await page.Find("form.sky-pixel-calculator").SubmitAsync().ConfigureAwait(false);
            StringAssert.Contains(page.Find(".astrometry-calculator-result").TextContent,
                "source pixels.", StringComparison.Ordinal);

            void AssertWithdrawn()
            {
                AssertHeadline(olderSummary, "For a different capture");
                AssertCalculators(enabled: false);
                Assert.IsEmpty(page.FindAll(".astrometry-provenance"));
                Assert.IsEmpty(page.FindAll(".astrometry-calculator-result"));
                foreach (var id in layerIds)
                {
                    Assert.IsEmpty(page.FindAll($"g[id='{id}']"));
                    Assert.IsEmpty(page.FindAll($"input[data-layer-target='{id}']"));
                }
            }

            void AssertHeadline(CameraAgentLatestAstrometry summary, string label)
            {
                var headline = page.Find(".latest-measured-solution");
                var text = headline.TextContent;
                Assert.AreEqual($"/gallery/{summary.CaptureId:D}", headline.QuerySelector("a")?.GetAttribute("href"));
                StringAssert.Contains(text, FormattableString.Invariant(
                    $"{summary.RightAscensionHours:0.######} h / {summary.DeclinationDegrees:0.######}°"), StringComparison.Ordinal);
                StringAssert.Contains(text, FormattableString.Invariant($"{summary.RollDegrees:0.######}°"), StringComparison.Ordinal);
                StringAssert.Contains(text, FormattableString.Invariant(
                    $"{summary.LocalScale.XArcsecondsPerPixel:0.####} / {summary.LocalScale.YArcsecondsPerPixel:0.####} arcsec/pixel"), StringComparison.Ordinal);
                StringAssert.Contains(text, label, StringComparison.Ordinal);
                StringAssert.Contains(text, summary.ArtifactId.ToString(), StringComparison.Ordinal);
                StringAssert.Contains(text, summary.SolutionIdentitySha256, StringComparison.Ordinal);
                StringAssert.Contains(text, summary.MidpointUtc.ToString("O"), StringComparison.Ordinal);
            }

            void AssertCalculators(bool enabled)
            {
                var controls = page.FindAll(".pixel-sky-calculator input, .pixel-sky-calculator select, .pixel-sky-calculator button");
                Assert.HasCount(7, controls);
                Assert.IsTrue(controls.All(control => control.HasAttribute("disabled") != enabled),
                    "Both calculators must follow the selected capture's verified mapping.");
            }

            async Task CalculatePixelToSkyAsync()
            {
                await page.Find(".pixel-sky-calculator input").ChangeAsync("280").ConfigureAwait(false);
                await page.FindAll(".pixel-sky-calculator input")[1].ChangeAsync("240").ConfigureAwait(false);
                await page.Find("form.pixel-sky-calculator").SubmitAsync().ConfigureAwait(false);
                StringAssert.Contains(page.Find(".astrometry-calculator-result").TextContent,
                    originalSummary.MidpointUtc.ToString("O"), StringComparison.Ordinal);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class DeferredRetainedLayerRead : IValueTaskSource<OperatorUiResult<CameraAgentLayeredPresentation>>
    {
        private Action<object?>? _continuation;
        private object? _state;
        private OperatorUiResult<CameraAgentLayeredPresentation>? _result;

        internal bool ResponseConsumed { get; private set; }
        internal TaskCompletionSource Awaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ValueTask<OperatorUiResult<CameraAgentLayeredPresentation>> Read() => new(this, 0);
        public ValueTaskSourceStatus GetStatus(short token) => _result is null ? ValueTaskSourceStatus.Pending : ValueTaskSourceStatus.Succeeded;
        public OperatorUiResult<CameraAgentLayeredPresentation> GetResult(short token)
        {
            ResponseConsumed = true;
            return _result!;
        }

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            _continuation = continuation;
            _state = state;
            Awaiting.TrySetResult();
        }

        internal void Complete(OperatorUiResult<CameraAgentLayeredPresentation> result)
        {
            Assert.IsNotNull(_continuation);
            _result = result;
            _continuation(_state);
        }
    }
}
