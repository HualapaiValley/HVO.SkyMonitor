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
    [DataRow("refresh-layer", false)]
    [DataRow("first-layer", false)]
    [DataRow("first-layer", true)]
    [DataRow("first-standalone", false)]
    [DataRow("prefetch", false)]
    [DataRow("selected-during-latest", false)]
    [DataRow("selected-during-latest", true)]
    [DataRow("positive-after-latest", false)]
    [DataRow("positive-after-latest", true)]
    [DataRow("point-withdraws-headline", false)]
    [DataRow("incoming-point-before-headline", false)]
    [DataRow("incoming-point-after-headline", false)]
    [DataRow("renewed-before-history", false)]
    [DataRow("renewed-after-history", false)]
    [DataRow("superseding-unavailable", false)]
    [DataRow("superseding-repair", false)]
    [DataRow("repeated-quarantine", false)]
    [DataRow("repeated-repair", false)]
    [DataRow("repeated-superseded", false)]
    public async Task CurrentSkyRejectsLateRetainedLayersAndShowsExactRevalidatedFit(string delivery, bool quarantineAssociations)
    {
        var root = CreateTestRoot();
        try
        {
            var token = CancellationToken.None;
            var timeout = TimeSpan.FromSeconds(8);
            var pointWithdrawsHeadline = delivery == "point-withdraws-headline";
            var incomingPointFirst = delivery == "incoming-point-before-headline";
            var incomingPoint = incomingPointFirst || delivery == "incoming-point-after-headline";
            var renewedBeforeHistory = delivery == "renewed-before-history";
            var renewedAfterHistory = delivery == "renewed-after-history";
            var renewedSelection = renewedBeforeHistory || renewedAfterHistory;
            var repeatedObservation = delivery is "repeated-quarantine" or "repeated-repair" or "repeated-superseded";
            var repeatedRepair = delivery == "repeated-repair";
            var repeatedSuperseded = delivery == "repeated-superseded";
            var refreshLayer = delivery == "refresh-layer" || pointWithdrawsHeadline || renewedSelection || repeatedObservation;
            var firstStandalone = delivery == "first-standalone";
            var prefetch = delivery == "prefetch" || incomingPoint;
            var supersedingRepair = delivery == "superseding-repair";
            var superseding = supersedingRepair || delivery == "superseding-unavailable";
            var selectedDuringLatest = delivery == "selected-during-latest" || superseding;
            var positiveAfterLatest = delivery == "positive-after-latest";
            var fixture = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Fixture.Options);
            using var gate = new CaptureAstrometryExecutionGate();
            var persistence = CreatePersistence(fixture.Fixture.Options, store, telemetry);
            var older = await ProduceLatestReadFixtureAsync(fixture, store, persistence, telemetry, gate).ConfigureAwait(false);
            using var artifacts = new CameraAgentArtifactService(fixture.Fixture.Options, store, new CameraAgentPreviewEncoder());
            using var latestReader = new CameraAgentRetainedAstrometryService(store, artifacts);
            var initialOlderHistory = incomingPointFirst
                ? await CameraAgentOperatorUiServiceTests.CreateAuthorizedRetainedAstrometryService(latestReader)
                    .GetLatestAcceptedAstrometryAsync(token).ConfigureAwait(false)
                : null;
            var reference = await MeasuredAstrometryFixture.CreateAtAsync(20).ConfigureAwait(false);
            var next = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true,
                captureSequence: 61, reference: reference, prior: (1, 1)).ConfigureAwait(false);
            var newest = await ProduceLatestReadFixtureAsync(next, store, persistence, telemetry, gate).ConfigureAwait(false);
            var capture = newest.Capture.CaptureId;
            var displayArtifact = await ProduceRetainedDisplayBaseAsync(next, persistence, telemetry).ConfigureAwait(false);

            using var selectedReader = new CameraAgentRetainedAstrometryService(store, artifacts);
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
            var olderEvidence = await selectedReader.GetAsync(older.Capture.CaptureId, token).ConfigureAwait(false);
            var olderSummary = CameraAgentLatestAstrometry.FromVerified(olderEvidence);
            var originalStandalone = firstStandalone
                ? await selectedService.GetRetainedAstrometryAsync(capture, token).ConfigureAwait(false)
                : null;
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

            var heldLayer = new DeferredRetainedRead<CameraAgentLayeredPresentation>();
            var heldStandalone = new DeferredRetainedRead<CameraAgentRetainedAstrometry>();
            var heldPositive = new DeferredRetainedRead<CameraAgentAstrometryPublicationState>();
            OperatorUiResult<CameraAgentAstrometryPublicationState>? positiveObservation = null;
            var armPositive = positiveAfterLatest ? 1 : 0;
            var rawQuarantined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var latestAwaitingQuarantine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var armLayer = delivery == "first-layer" || selectedDuringLatest || positiveAfterLatest ? 1 : 0;
            var armStandalone = firstStandalone ? 1 : 0;
            var awaitQuarantine = refreshLayer || incomingPoint ? 0 : 1;
            var initialHistory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseInitialHistory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fallbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var negativeHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseNegative = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var heldFallbackPoint = new DeferredRetainedRead<CameraAgentAstrometryPublicationState>();
            var heldSelectionPoint = new DeferredRetainedRead<CameraAgentAstrometryPublicationState>();
            var releaseRepairedLayers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            OperatorUiResult<CameraAgentAstrometryPublicationState>? fallbackPoint = null;
            OperatorUiResult<CameraAgentAstrometryPublicationState>? selectionPoint = null;
            var armFallbackPoint = 0;
            var armSelectionPoint = 0;
            var armRepairedLayers = 0;
            var latestRequests = 0;
            var holdNegative = renewedBeforeHistory || superseding ? 1 : 0;
            var offerStaleReplacement = renewedBeforeHistory ? 1 : 0;
            var heldCancellation = CancellationToken.None;
            var allowFreshLayers = 0;
            using var context = new BunitContext();
            context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js")
                .Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
            var ui = CurrentSkyPageTests.Configure(context);
            var clock = incomingPoint || renewedSelection || superseding || repeatedObservation ? new CurrentSkyPageTests.ManualTimeProvider(DateTimeOffset.UtcNow) : null;
            if (clock is not null) context.Services.AddSingleton<TimeProvider>(clock);
            var rejectedPublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var selectedIdentity = new CameraAgentAstrometryAssessmentIdentity(capture, originalSummary.ArtifactId, originalSummary.SolutionIdentitySha256);
            var olderIdentity = new CameraAgentAstrometryAssessmentIdentity(
                olderSummary.CaptureId, olderSummary.ArtifactId, olderSummary.SolutionIdentitySha256);
            var available = await selectedService.ReadAstrometryPublicationStateAsync(selectedIdentity, token).ConfigureAwait(false);
            Assert.IsTrue(available.IsSuccess);
            Assert.AreEqual(CameraAgentAstrometryPublicationState.Available, available.Value);
            var differentSolution = selectedIdentity with { SolutionIdentitySha256 = olderSummary.SolutionIdentitySha256 };
            var unmatched = await selectedService.ReadAstrometryPublicationStateAsync(differentSolution, token).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentAstrometryPublicationState.Indeterminate, unmatched.Value);
            ui.AstrometryPublicationHandler = async (identity, cancellation) =>
            {
                var observation = await selectedService.ReadAstrometryPublicationStateAsync(identity, cancellation).ConfigureAwait(false);
                if (identity == olderIdentity && Interlocked.Exchange(ref armFallbackPoint, 0) == 1)
                {
                    Assert.IsTrue(observation.IsSuccess);
                    Assert.AreEqual(CameraAgentAstrometryPublicationState.Available, observation.Value);
                    fallbackPoint = observation;
                    return await heldFallbackPoint.Read().ConfigureAwait(false);
                }
                if (identity == selectedIdentity && Interlocked.Exchange(ref armSelectionPoint, 0) == 1)
                {
                    Assert.IsTrue(observation.IsSuccess);
                    Assert.AreEqual(repeatedRepair ? CameraAgentAstrometryPublicationState.Available
                        : CameraAgentAstrometryPublicationState.Invalidated, observation.Value);
                    Console.WriteLine($"delivery={delivery} history selected point materialized={observation.Value} identity={identity}");
                    selectionPoint = observation;
                    if (repeatedSuperseded) return await heldSelectionPoint.Read().ConfigureAwait(false);
                }
                if (identity == selectedIdentity && Interlocked.Exchange(ref armPositive, 0) == 1)
                {
                    Assert.IsTrue(observation.IsSuccess);
                    Assert.AreEqual(CameraAgentAstrometryPublicationState.Available, observation.Value);
                    positiveObservation = observation;
                    return await heldPositive.Read().ConfigureAwait(false);
                }
                if (rawQuarantined.Task.IsCompleted && identity == selectedIdentity &&
                    observation is { IsSuccess: true, Value: CameraAgentAstrometryPublicationState.Invalidated })
                {
                    Console.WriteLine($"delivery={delivery} publication={observation.Value} capture={identity.CaptureId} artifact={identity.ArtifactId} hash={identity.SolutionIdentitySha256}");
                    rejectedPublication.TrySetResult();
                    if (Interlocked.Exchange(ref holdNegative, 0) == 1)
                    {
                        negativeHeld.TrySetResult();
                        await releaseNegative.Task.WaitAsync(cancellation).ConfigureAwait(false);
                    }
                }
                return observation;
            };
            var image = CurrentSkyPageTests.AstrometryImage(original);
            var combined = CurrentSkyPageTests.WithStructuredBase().Stages.Single(slot => slot.Stage == CameraAgentPresentationStage.Combined);
            image = image with
            {
                StructuredLayersAvailable = true,
                Stages = image.Stages.Select(slot => slot.Stage == CameraAgentPresentationStage.Combined
                    ? combined with { DisplayArtifactId = displayArtifact }
                    : slot).ToArray()
            };
            var shown = prefetch ? CurrentSkyPageTests.AstrometryImage(olderEvidence) : image;
            ui.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(shown));
            var standaloneLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.AstrometryHandler = (id, cancellation) => Interlocked.Exchange(ref armStandalone, 0) == 1
                ? heldStandalone.Read() : ReadSelectedAsync(id, cancellation);

            async ValueTask<OperatorUiResult<CameraAgentRetainedAstrometry>> ReadSelectedAsync(Guid id, CancellationToken cancellation)
            {
                var result = await selectedService.GetRetainedAstrometryAsync(id, cancellation).ConfigureAwait(false);
                standaloneLoaded.TrySetResult();
                return result;
            }
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
            ui.CurrentPresentationHandler = async (id, cancellation) =>
            {
                if (incomingPoint && id == capture && Volatile.Read(ref allowFreshLayers) == 0)
                    return OperatorUiResult<CameraAgentLayeredPresentation>.Failure(
                        OperatorUiResultKind.NotFound, "Replacement layers are pending.");
                if (id == capture && Interlocked.Exchange(ref offerStaleReplacement, 0) == 1)
                    return originalLayerRead;
                if (id == capture && Interlocked.Exchange(ref armRepairedLayers, 0) == 1)
                    await releaseRepairedLayers.Task.WaitAsync(cancellation).ConfigureAwait(false);
                return await service.GetCurrentLayeredPresentationAsync(id, cancellation).ConfigureAwait(false);
            };
            ui.LatestAstrometrySelectionHandler = async (cancellation, previous, selected) =>
            {
                if (refreshLayer || prefetch)
                    await standaloneLoaded.Task.WaitAsync(cancellation).ConfigureAwait(false);
                var request = Interlocked.Increment(ref latestRequests);
                if (incomingPointFirst && request == 1) return initialOlderHistory!;
                if (repeatedSuperseded && request == 3)
                {
                    fallbackStarted.TrySetResult();
                    await releaseFallback.Task.WaitAsync(cancellation).ConfigureAwait(false);
                }
                if (incomingPoint && request == (incomingPointFirst ? 2 : 1))
                {
                    var firstResult = await service.GetLatestAcceptedAstrometryAsync(cancellation, previous, selected).ConfigureAwait(false);
                    Assert.AreEqual(incomingPointFirst ? olderSummary : null, previous);
                    Assert.IsTrue(firstResult.IsSuccess);
                    Assert.AreEqual(originalSummary, firstResult.Value?.Summary);
                    Console.WriteLine($"delivery={delivery} history={request} materialized={firstResult.Value!.Summary!.ArtifactId} selected={selected?.ArtifactId}");
                    initialHistory.TrySetResult();
                    if (incomingPointFirst) await releaseInitialHistory.Task.WaitAsync(cancellation).ConfigureAwait(false);
                    Console.WriteLine($"delivery={delivery} history={request} returned={firstResult.Value.Summary.ArtifactId}");
                    return firstResult;
                }
                if (incomingPoint && request == (incomingPointFirst ? 3 : 2))
                {
                    fallbackStarted.TrySetResult();
                    await releaseFallback.Task.WaitAsync(cancellation).ConfigureAwait(false);
                }
                if (superseding && request == 2)
                {
                    Console.WriteLine($"delivery={delivery} second explicit history request dispatched");
                    if (!supersedingRepair)
                        return new(OperatorUiResultKind.Unavailable, new(false), "The history service is temporarily unavailable.");
                }
                var overlapsQuarantine = Interlocked.Exchange(ref awaitQuarantine, 0) != 0;
                if (overlapsQuarantine)
                {
                    Assert.AreEqual(refreshLayer ? originalSummary : null, previous);
                    if (!prefetch)
                    {
                        Assert.AreEqual(refreshLayer ? new CameraAgentAstrometryAssessmentIdentity(capture, originalSummary.ArtifactId,
                            originalSummary.SolutionIdentitySha256) : null, selected);
                    }
                    else if (selected is not null) Assert.AreEqual(older.Capture.CaptureId, selected.CaptureId);
                    latestAwaitingQuarantine.TrySetResult();
                    await rawQuarantined.Task.WaitAsync(cancellation).ConfigureAwait(false);
                    if (pointWithdrawsHeadline) await releaseFallback.Task.WaitAsync(cancellation).ConfigureAwait(false);
                    if (repeatedObservation) await releaseInitialHistory.Task.WaitAsync(cancellation).ConfigureAwait(false);
                }
                var result = await service.GetLatestAcceptedAstrometryAsync(cancellation, previous, selected).ConfigureAwait(false);
                if (overlapsQuarantine && (selectedDuringLatest || positiveAfterLatest || renewedSelection || repeatedObservation))
                {
                    Assert.IsTrue(result.IsSuccess);
                    Assert.AreEqual(olderSummary, result.Value?.Summary);
                    Assert.AreEqual(renewedSelection || repeatedObservation, result.Value!.PreviousSummaryInvalidated);
                    Assert.AreEqual(renewedSelection || repeatedObservation, result.Value.SelectedAssessmentInvalidated);
                    Console.WriteLine($"delivery={delivery} history={request} returned={result.Value.Summary!.ArtifactId} previous-invalid={result.Value.PreviousSummaryInvalidated} selected-invalid={result.Value.SelectedAssessmentInvalidated}");
                }
                return result;
            };

            var page = context.Render<CurrentSkyPage>();
            Task? refresh = null;
            if (refreshLayer)
            {
                page.WaitForAssertion(() =>
                {
                    AssertHeadline(originalSummary, "For the selected capture");
                    AssertCalculators(enabled: true);
                }, timeout);
                await CalculatePixelToSkyAsync().ConfigureAwait(false);
                Assert.HasCount(1, page.FindAll(".astrometry-calculator-result"));
            }

            if (prefetch)
            {
                page.WaitForAssertion(() => AssertCalculators(enabled: true), timeout);
                if (incomingPoint)
                {
                    if (incomingPointFirst)
                    {
                        page.WaitForAssertion(() => AssertHeadline(olderSummary, "For the selected capture"), timeout);
                        await page.InvokeAsync(() => page.Find("button.refresh-link").ClickAsync()).ConfigureAwait(false);
                    }
                    await initialHistory.Task.WaitAsync(timeout).ConfigureAwait(false);
                    if (!incomingPointFirst)
                        page.WaitForAssertion(() => AssertHeadline(originalSummary, "For a different capture"), timeout);
                    await CalculatePixelToSkyAsync(olderSummary).ConfigureAwait(false);
                }
                shown = image;
            }
            else if (!pointWithdrawsHeadline)
                await page.Find("button[title='Show Processed image']").ClickAsync().ConfigureAwait(false);
            if (refreshLayer || prefetch)
            {
                if (refreshLayer) Volatile.Write(ref awaitQuarantine, 1);
                Volatile.Write(ref armLayer, 1);
                refresh = page.InvokeAsync(() => page.Find("button.refresh-link").ClickAsync());
            }
            await (firstStandalone ? heldStandalone.Awaiting : heldLayer.Awaiting).Task.WaitAsync(timeout).ConfigureAwait(false);
            if (!incomingPoint) await latestAwaitingQuarantine.Task.WaitAsync(timeout).ConfigureAwait(false);
            if (renewedSelection)
            {
                if (renewedAfterHistory) Volatile.Write(ref armPositive, 1);
                await Task.Run(() => heldLayer.Complete(originalLayerRead), token).ConfigureAwait(false);
                if (renewedAfterHistory)
                    await heldPositive.Awaiting.Task.WaitAsync(timeout).ConfigureAwait(false);
                else
                    page.WaitForAssertion(() =>
                    {
                        AssertHeadline(originalSummary, "For the selected capture");
                        AssertCalculators(enabled: true);
                        foreach (var id in layerIds) Assert.HasCount(1, page.FindAll($"g[id='{id}']"));
                    }, timeout);
                Console.WriteLine($"delivery={delivery} layer delivered before quarantine groups={page.FindAll("g[data-layer-kind^='MeasuredAstrometric']").Count}");
            }
            if (selectedDuringLatest || positiveAfterLatest)
            {
                await Task.Run(() => heldLayer.Complete(originalLayerRead), token).ConfigureAwait(false);
                if (positiveAfterLatest)
                {
                    await heldPositive.Awaiting.Task.WaitAsync(timeout).ConfigureAwait(false);
                    AssertCalculators(enabled: false);
                    Assert.IsNotNull(positiveObservation);
                }
                else
                {
                    page.WaitForAssertion(() =>
                    {
                        AssertCalculators(enabled: true);
                        foreach (var id in layerIds) Assert.HasCount(1, page.FindAll($"g[id='{id}']"));
                        Assert.IsNull(page.Find(".latest-measured-solution").QuerySelector("a"));
                    }, timeout);
                    if (!supersedingRepair) await CalculatePixelToSkyAsync().ConfigureAwait(false);
                }
            }
            try
            {
                if (quarantineAssociations)
                {
                    var associations = await store.ReadOutputByArtifactIdAsync(newest.Artifact.SourceArtifactIds[1], token).ConfigureAwait(false);
                    Assert.IsNotNull(associations);
                    var associationPath = Path.Combine(root, associations.PayloadRelativePath);
                    var reconciler = new DerivedProductReconciler(root, store);
                    var heldAssociation = Path.Combine(root, "held-associations");
                    File.Move(associationPath, heldAssociation);
                    Assert.AreEqual(1, (await reconciler.RunAsync(token).ConfigureAwait(false)).Missing);
                    var pending = await selectedService.ReadAstrometryPublicationStateAsync(selectedIdentity, token).ConfigureAwait(false);
                    Assert.AreEqual(CameraAgentAstrometryPublicationState.Indeterminate, pending.Value);
                    File.Move(heldAssociation, associationPath);
                    await reconciler.RunAsync(token).ConfigureAwait(false);
                    var restored = await selectedService.ReadAstrometryPublicationStateAsync(selectedIdentity, token).ConfigureAwait(false);
                    Assert.AreEqual(CameraAgentAstrometryPublicationState.Available, restored.Value);

                    var corrupt = await File.ReadAllBytesAsync(associationPath, token).ConfigureAwait(false);
                    corrupt[0] ^= 1;
                    await File.WriteAllBytesAsync(associationPath, corrupt, token).ConfigureAwait(false);
                    var reconciled = await reconciler.RunAsync(token).ConfigureAwait(false);
                    Assert.AreEqual(1, reconciled.Quarantined);
                    Assert.AreEqual("Quarantined", (await store.ReadOutputByArtifactIdAsync(associations.ArtifactId, token).ConfigureAwait(false))?.AvailabilityState);
                }
                else
                {
                    await QuarantineRawAsync().ConfigureAwait(false);
                }
            }
            finally { rawQuarantined.TrySetResult(); }
            if (refreshLayer) await refresh!.ConfigureAwait(false);
            if (repeatedObservation)
            {
                await VerifyRepeatedObservationAsync().ConfigureAwait(false);
                return;
            }
            if (incomingPoint)
            {
                try
                {
                    await Task.Run(() => heldLayer.Complete(originalLayerRead), token).ConfigureAwait(false);
                    await rejectedPublication.Task.WaitAsync(timeout).ConfigureAwait(false);
                    await refresh!.ConfigureAwait(false);
                    releaseInitialHistory.TrySetResult();
                    await fallbackStarted.Task.WaitAsync(timeout).ConfigureAwait(false);
                    await page.InvokeAsync(() =>
                    {
                        Console.WriteLine($"delivery={delivery} fallback held headline={page.Find(".latest-measured-solution").TextContent}");
                        if (incomingPointFirst) AssertHeadline(olderSummary, "For the selected capture");
                        else Assert.IsNull(page.Find(".latest-measured-solution").QuerySelector("time"));
                        AssertCalculators(enabled: true);
                        Assert.HasCount(1, page.FindAll(".astrometry-calculator-result"));
                        StringAssert.Contains(page.Find(".astrometry-provenance").TextContent, older.Capture.CaptureId.ToString(), StringComparison.Ordinal);
                    }).ConfigureAwait(false);
                }
                finally
                {
                    releaseInitialHistory.TrySetResult();
                    releaseFallback.TrySetResult();
                }
            }
            if (renewedBeforeHistory)
            {
                try
                {
                    await negativeHeld.Task.WaitAsync(timeout).ConfigureAwait(false);
                    await page.InvokeAsync(() =>
                    {
                        var headline = page.Find(".latest-measured-solution").TextContent;
                        Assert.IsFalse(headline.Contains(originalSummary.ArtifactId.ToString(), StringComparison.Ordinal));
                        Console.WriteLine($"delivery={delivery} exact recheck held headline={headline} calculator-disabled={page.Find(".pixel-sky-calculator button").HasAttribute("disabled")}");
                        AssertCalculators(enabled: false);
                        Assert.IsEmpty(page.FindAll(".astrometry-calculator-result"));
                        foreach (var id in layerIds) Assert.IsEmpty(page.FindAll($"g[id='{id}']"));
                    }).ConfigureAwait(false);
                }
                finally { releaseNegative.TrySetResult(); }
            }
            if (superseding)
            {
                try
                {
                    await negativeHeld.Task.WaitAsync(timeout).ConfigureAwait(false);
                    if (supersedingRepair) await RepairRawAsync().ConfigureAwait(false);
                    await page.InvokeAsync(() => page.Find("button.refresh-link").ClickAsync()).ConfigureAwait(false);
                    Console.WriteLine($"delivery={delivery} newer refresh returned while invalid point held, history-requests={Volatile.Read(ref latestRequests)}");
                }
                finally { releaseNegative.TrySetResult(); }
                if (!supersedingRepair)
                {
                    page.WaitForAssertion(() =>
                    {
                        Console.WriteLine($"delivery={delivery} history-requests={Volatile.Read(ref latestRequests)} calculator-disabled={page.Find(".pixel-sky-calculator button").HasAttribute("disabled")}");
                        AssertCalculators(enabled: false);
                        AssertWithdrawn();
                        StringAssert.Contains(page.Find(".latest-measured-solution").TextContent, "Showing the previously verified result", StringComparison.Ordinal);
                    }, timeout);
                }
                else
                    page.WaitForAssertion(() =>
                    {
                        AssertHeadline(originalSummary, "For the selected capture");
                        AssertCalculators(enabled: true);
                    }, timeout);
                Assert.AreEqual(2, Volatile.Read(ref latestRequests));
            }
            if (pointWithdrawsHeadline)
            {
                try
                {
                    AssertHeadline(originalSummary, "For the selected capture");
                    AssertCalculators(enabled: true);
                    await Task.Run(() => heldLayer.Complete(originalLayerRead), token).ConfigureAwait(false);
                    await rejectedPublication.Task.WaitAsync(timeout).ConfigureAwait(false);
                    page.WaitForAssertion(() =>
                    {
                        var headline = page.Find(".latest-measured-solution");
                        StringAssert.Contains(headline.TextContent, "temporarily unavailable", StringComparison.Ordinal);
                        Assert.IsNull(headline.QuerySelector("dl"));
                        Assert.IsNull(headline.QuerySelector("time"));
                        AssertCalculators(enabled: false);
                        Assert.IsEmpty(page.FindAll(".astrometry-calculator-result"));
                    }, timeout);
                }
                finally { releaseFallback.TrySetResult(); }
            }
            if (!supersedingRepair) page.WaitForAssertion(AssertWithdrawn, timeout);

            // Complete off the renderer so the read continuation can dispatch before the barrier.
            if (positiveAfterLatest || renewedAfterHistory)
            {
                await Task.Run(() => heldPositive.Complete(positiveObservation!), token).ConfigureAwait(false);
                Assert.IsTrue(heldPositive.ResponseConsumed);
            }
            else if (!selectedDuringLatest && !pointWithdrawsHeadline && !incomingPoint && !renewedSelection)
            {
                await Task.Run(() =>
                {
                    if (firstStandalone) heldStandalone.Complete(originalStandalone!);
                    else heldLayer.Complete(originalLayerRead);
                }, token).ConfigureAwait(false);
            }
            if (prefetch) await refresh!.ConfigureAwait(false);
            if (!refreshLayer) await rejectedPublication.Task.WaitAsync(timeout).ConfigureAwait(false);
            await page.InvokeAsync(static () => { }).ConfigureAwait(false);
            Assert.IsTrue(firstStandalone ? heldStandalone.ResponseConsumed : heldLayer.ResponseConsumed);
            if (!supersedingRepair) page.WaitForAssertion(AssertWithdrawn, timeout);
            if (!supersedingRepair && (selectedDuringLatest || positiveAfterLatest))
            {
                await page.Find("form.pixel-sky-calculator").SubmitAsync().ConfigureAwait(false);
                Assert.IsEmpty(page.FindAll(".astrometry-calculator-result"));
            }
            // The positively renewed layer already completed before quarantine in this ordering.
            if (refreshLayer && !renewedBeforeHistory)
                Assert.IsTrue(heldCancellation.IsCancellationRequested, "The invalidated layer request must be retired.");
            if (quarantineAssociations) return; // Raw cases below exercise registered durable repair and exact recovery.

            if (!supersedingRepair) await RepairRawAsync().ConfigureAwait(false);

            if (clock is not null)
            {
                Assert.IsTrue(clock.HasTimer(TimeSpan.FromSeconds(15)));
                clock.Advance(TimeSpan.FromSeconds(15));
                page.WaitForAssertion(() => AssertHeadline(originalSummary, "For the selected capture"), timeout);
            }
            else
                await page.InvokeAsync(() => page.Find("button.refresh-link").ClickAsync()).ConfigureAwait(false);
            if (prefetch || pointWithdrawsHeadline)
                await page.InvokeAsync(() => page.Find("button[title='Show Processed image']").ClickAsync()).ConfigureAwait(false);
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

            async Task VerifyRepeatedObservationAsync()
            {
                await Task.Run(() => heldLayer.Complete(originalLayerRead), token).ConfigureAwait(false);
                await rejectedPublication.Task.WaitAsync(timeout).ConfigureAwait(false);
                page.WaitForAssertion(() =>
                {
                    Assert.IsNull(page.Find(".latest-measured-solution").QuerySelector("time"));
                    AssertCalculators(enabled: false);
                }, timeout);

                Volatile.Write(ref armFallbackPoint, 1);
                Volatile.Write(ref armRepairedLayers, 1);
                releaseInitialHistory.TrySetResult();
                await heldFallbackPoint.Awaiting.Task.WaitAsync(timeout).ConfigureAwait(false);
                await RepairRawAsync().ConfigureAwait(false);
                releaseRepairedLayers.TrySetResult();

                page.WaitForAssertion(() =>
                {
                    AssertSelected(originalSummary);
                    foreach (var id in layerIds) Assert.HasCount(1, page.FindAll($"g[id='{id}']"));
                }, timeout);
                await CalculatePixelToSkyAsync().ConfigureAwait(false);
                Assert.HasCount(1, page.FindAll(".astrometry-calculator-result"));
                if (!repeatedRepair) await QuarantineRawAsync().ConfigureAwait(false);
                Volatile.Write(ref armSelectionPoint, 1);
                await Task.Run(() => heldFallbackPoint.Complete(fallbackPoint!), token).ConfigureAwait(false);

                if (repeatedSuperseded)
                {
                    await heldSelectionPoint.Awaiting.Task.WaitAsync(timeout).ConfigureAwait(false);
                    var later = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true,
                        captureSequence: 121, reference: await MeasuredAstrometryFixture.CreateAtAsync(40).ConfigureAwait(false),
                        prior: (61, 1)).ConfigureAwait(false); // Quarantined 61 is no longer inspected by recovery.
                    var latest = await ProduceLatestReadFixtureAsync(later, store, persistence, telemetry, gate).ConfigureAwait(false);
                    var evidence = await selectedReader.GetAsync(latest.Capture.CaptureId, token).ConfigureAwait(false);
                    var replacement = CameraAgentLatestAstrometry.FromVerified(evidence);
                    Assert.IsNotNull(replacement);
                    var replacementImage = CurrentSkyPageTests.AstrometryImage(evidence);
                    var replacementCapture = replacementImage.DisplayCapture! with { CaptureSequence = latest.CaptureSequence };
                    shown = replacementImage with { DisplayCapture = replacementCapture, LatestCapture = replacementCapture };
                    await page.InvokeAsync(() => page.Find("button.refresh-link").ClickAsync()).ConfigureAwait(false);
                    await page.InvokeAsync(() => page.Find("button[title='Show Raw image']").ClickAsync()).ConfigureAwait(false);
                    page.WaitForAssertion(() => AssertSelected(replacement), timeout);
                    await CalculatePixelToSkyAsync(replacement).ConfigureAwait(false);

                    await Task.Run(() => heldSelectionPoint.Complete(selectionPoint!), token).ConfigureAwait(false);
                    await fallbackStarted.Task.WaitAsync(timeout).ConfigureAwait(false);
                    await page.InvokeAsync(() =>
                    {
                        AssertHeadline(olderSummary, "For a different capture");
                        AssertSelected(replacement);
                        Assert.HasCount(1, page.FindAll(".astrometry-calculator-result"));
                    }).ConfigureAwait(false);
                    releaseFallback.TrySetResult();
                    page.WaitForAssertion(() => AssertHeadline(replacement, "For the selected capture"), timeout);
                    return;
                }

                page.WaitForAssertion(() => AssertHeadline(olderSummary, "For a different capture"), timeout);
                Assert.IsNotNull(selectionPoint, "The completed history must have checked the republished fit.");
                await page.InvokeAsync(() =>
                {
                    var cached = page.FindAll(".astrometry-calculator-result").Count;
                    var groups = layerIds.Sum(id => page.FindAll($"g[id='{id}']").Count);
                    var enabled = !page.Find(".pixel-sky-calculator button").HasAttribute("disabled");
                    Console.WriteLine($"delivery={delivery} history finished; selected point={selectionPoint.Value}; calculator={enabled}; groups={groups}; cached={cached}");
                }).ConfigureAwait(false);

                if (repeatedRepair)
                {
                    AssertSelected(originalSummary);
                    Assert.HasCount(1, page.FindAll(".astrometry-calculator-result"));
                    foreach (var id in layerIds) Assert.HasCount(1, page.FindAll($"g[id='{id}']"));
                    await CalculatePixelToSkyAsync().ConfigureAwait(false);
                    return;
                }

                var cachedResult = page.FindAll(".astrometry-calculator-result").SingleOrDefault()?.TextContent;
                await page.InvokeAsync(() => page.Find(".pixel-sky-calculator input").ChangeAsync("281")).ConfigureAwait(false);
                await page.InvokeAsync(() => page.Find("form.pixel-sky-calculator").SubmitAsync()).ConfigureAwait(false);
                var newResult = page.FindAll(".astrometry-calculator-result").SingleOrDefault()?.TextContent;
                Console.WriteLine($"delivery={delivery} cached={cachedResult is not null}; new-result={newResult is not null && newResult != cachedResult}");
                Assert.IsNull(cachedResult, "A second exact quarantine must remove the cached calculation.");
                Assert.IsNull(newResult, "A new calculation must not use the twice-quarantined fit.");
                AssertWithdrawn();
            }

            void AssertSelected(CameraAgentLatestAstrometry summary)
            {
                AssertCalculators(enabled: true);
                var provenance = page.Find(".astrometry-provenance").TextContent;
                StringAssert.Contains(provenance, summary.CaptureId.ToString(), StringComparison.Ordinal);
                StringAssert.Contains(provenance, summary.SolutionIdentitySha256, StringComparison.Ordinal);
            }

            async Task QuarantineRawAsync()
            {
                await File.WriteAllBytesAsync(paths.PayloadAbsolutePath, [.. payload, 0], token).ConfigureAwait(false);
                var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
                var reconciled = await new RawIngressReconciler(root, journal)
                    .RunAsync(_ => ValueTask.FromResult(default(CaptureMaintenanceEntry)), token).ConfigureAwait(false);
                Assert.AreEqual(1, reconciled.Quarantined);
            }

            async Task RepairRawAsync()
            {
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
            }

            void AssertWithdrawn()
            {
                AssertHeadline(olderSummary, prefetch ? "For the selected capture" : "For a different capture");
                AssertCalculators(enabled: prefetch);
                if (prefetch)
                    StringAssert.Contains(page.Find(".astrometry-provenance").TextContent, older.Capture.CaptureId.ToString(), StringComparison.Ordinal);
                else Assert.IsEmpty(page.FindAll(".astrometry-provenance"));
                Assert.HasCount(incomingPoint ? 1 : 0, page.FindAll(".astrometry-calculator-result"));
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

            async Task CalculatePixelToSkyAsync(CameraAgentLatestAstrometry? summary = null)
            {
                await page.InvokeAsync(() => page.Find(".pixel-sky-calculator input").ChangeAsync("280")).ConfigureAwait(false);
                await page.InvokeAsync(() => page.FindAll(".pixel-sky-calculator input")[1].ChangeAsync("240")).ConfigureAwait(false);
                await page.InvokeAsync(() => page.Find("form.pixel-sky-calculator").SubmitAsync()).ConfigureAwait(false);
                StringAssert.Contains(page.Find(".astrometry-calculator-result").TextContent,
                    (summary ?? originalSummary).MidpointUtc.ToString("O"), StringComparison.Ordinal);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class DeferredRetainedRead<T> : IValueTaskSource<OperatorUiResult<T>>
    {
        private Action<object?>? _continuation;
        private object? _state;
        private OperatorUiResult<T>? _result;

        internal bool ResponseConsumed { get; private set; }
        internal TaskCompletionSource Awaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ValueTask<OperatorUiResult<T>> Read() => new(this, 0);
        public ValueTaskSourceStatus GetStatus(short token) => _result is null ? ValueTaskSourceStatus.Pending : ValueTaskSourceStatus.Succeeded;
        public OperatorUiResult<T> GetResult(short token)
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

        internal void Complete(OperatorUiResult<T> result)
        {
            Assert.IsNotNull(_continuation);
            _result = result;
            _continuation(_state);
        }
    }
}
