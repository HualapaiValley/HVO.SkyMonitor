using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class CurrentSkyPageTests
{
    [TestMethod]
    public void CurrentImageLeadsWithIndependentFreshnessAndSystemState()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(
            OperatorUiTestData.CurrentImage(
                CameraAgentPresentationImageFreshness.Delayed,
                CameraAgentPresentationSystemState.Standby)));

        var cut = context.Render<CurrentSkyPage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "Image delayed", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Standby", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Processed sky capture", StringComparison.Ordinal);
            Assert.AreEqual(
                "/api/v1/operations/artifacts/00000000-0000-0000-0000-000000000102/preview",
                cut.Find(".capture-image img").GetAttribute("src"));
        });
    }

    [TestMethod]
    public void CurrentSkyPresentsDurableFactsObservingNightAndCombinedLineage()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var capture = OperatorUiTestData.Capture();
        var combinedId = Guid.Parse("00000000-0000-0000-0000-000000000103");
        capture = capture with
        {
            Artifacts =
            [
                .. capture.Artifacts,
                new CameraAgentGalleryArtifact(combinedId, HVO.SkyMonitor.AgentCore.FrameArtifactRole.Combined, "combine", null, OperatorUiTestData.Now,
                    "application/x-hvo-packed-image", new string('H', 64), 4096,
                    new CameraAgentGalleryRecipe("rolling-mean", "1.0.0", "build-7", new string('C', 64), new string('D', 64)),
                    [capture.Artifacts[0].ArtifactId, Guid.NewGuid(), Guid.NewGuid()], "combine-node")
            ]
        };
        var facts = CameraAgentCurrentSkyFactsProjector.Project(capture, ObservingDayCalendar.Create("America/Phoenix"));
        service.CurrentSkyHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(
            new(OperatorUiTestData.CurrentImage() with { StructuredLayersAvailable = true }, facts, null)));

        var cut = context.Render<CurrentSkyPage>();

        cut.WaitForAssertion(() =>
        {
            var summary = cut.Find(".current-sky-summary").TextContent;
            StringAssert.Contains(summary, "Observing night", StringComparison.Ordinal);
            // 11:59 UTC is 04:59 in Phoenix, inside the night that began at local noon on the 22nd.
            StringAssert.Contains(summary, "2026-07-22 (America/Phoenix)", StringComparison.Ordinal);
            StringAssert.Contains(summary, "rig-test", StringComparison.Ordinal);
            StringAssert.Contains(summary, "1 s", StringComparison.Ordinal);
            StringAssert.Contains(summary, "640 × 480", StringComparison.Ordinal);
            StringAssert.Contains(summary, "Quantified, 25% cover", StringComparison.Ordinal);
            StringAssert.Contains(summary, "Unregistered causal arithmetic mean of 3 source frames, recipe rolling-mean", StringComparison.Ordinal);
            Assert.IsFalse(summary.Contains("registered stack", StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual("/gallery/00000000-0000-0000-0000-000000000001", cut.Find(".layers-link").GetAttribute("href"));
        });

        service.CurrentSkyHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(
            new(OperatorUiTestData.CurrentImage(), null, "Capture facts are temporarily unavailable.")));
        var degraded = context.Render<CurrentSkyPage>();
        degraded.WaitForAssertion(() =>
        {
            StringAssert.Contains(degraded.Find(".facts-unavailable").TextContent, "temporarily unavailable", StringComparison.Ordinal);
            StringAssert.Contains(degraded.Find(".current-sky-summary").TextContent, "Observing nightUnavailable", StringComparison.Ordinal);
            Assert.IsNotNull(degraded.Find(".capture-image img"));
        });
    }

    [TestMethod]
    public void AvailableStageCanBeSelectedAndUnavailableStageCannot()
    {
        using var context = new BunitContext();
        Configure(context);
        var cut = context.Render<CurrentSkyPage>();

        cut.WaitForElement(".capture-image");
        Assert.IsTrue(cut.Find("button[title='Calibrated: This stage was not produced.']").HasAttribute("disabled"));
        // A disabled button is unfocusable, so the reason must be visible text, not only a title.
        var unavailable = cut.Find(".stage-unavailable");
        StringAssert.Contains(unavailable.TextContent, "Calibrated", StringComparison.Ordinal);
        StringAssert.Contains(unavailable.TextContent, "This stage was not produced.", StringComparison.Ordinal);
        Assert.AreEqual(unavailable.Id, cut.Find(".stage-selector").GetAttribute("aria-describedby"));

        cut.Find("button[title='Show Raw image']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("true", cut.Find("button[title='Show Raw image']").GetAttribute("aria-pressed"));
            StringAssert.Contains(
                cut.Find(".capture-image img").GetAttribute("src"),
                "00000000-0000-0000-0000-000000000101",
                StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Showing Raw", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void RefreshFailurePreservesLastSuccessfulImage()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var read = 0;
        service.CurrentImageHandler = _ => ValueTask.FromResult(Interlocked.Increment(ref read) == 1
            ? OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(OperatorUiTestData.CurrentImage())
            : OperatorUiResult<CameraAgentCurrentImagePresentation>.Failure(OperatorUiResultKind.Unavailable, "projection read failed"));
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForElement(".capture-image");

        cut.Find("button.refresh-link").Click();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "projection read failed", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "last successfully read image", StringComparison.Ordinal);
            Assert.IsNotNull(cut.Find(".capture-image img").GetAttribute("src"));
        });
    }

    [TestMethod]
    public async Task FullResolutionLayersToggleAndSaveWithoutChangingSelectedStage()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(WithStructuredBase()));
        var captureId = OperatorUiTestData.CurrentImage().DisplayCapture!.CaptureId;
        var identity = new string('D', 64);
        service.PresentationHandler = (id, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(
            Layered(id, identity)));
        IReadOnlyList<string>? submitted = null;
        service.MaterializationHandler = (id, selected, _) =>
        {
            Assert.AreEqual(captureId, id);
            submitted = selected;
            return ValueTask.FromResult(OperatorUiResult<CameraAgentPresentationMaterializationReceipt>.Success(new(
                id, Guid.NewGuid(), new string('F', 64), new string('A', 64), 1024, false)));
        };
        var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        module.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForElement(".sky-layer-canvas img");

        StringAssert.Contains(cut.Find(".current-sky-hero .sky-layer-canvas img").GetAttribute("src"), "/preview", StringComparison.Ordinal);
        Assert.HasCount(1, cut.FindAll(".current-sky-hero .sky-layer-overlay svg"));
        Assert.IsEmpty(cut.FindAll(".capture-image img"));
        Assert.HasCount(1, cut.FindAll(".sky-layer-controls input[data-layer-target='hvo-layer-0']"));
        Assert.AreEqual("true", cut.Find("button[title='Show Processed image']").GetAttribute("aria-pressed"));
        StringAssert.Contains(cut.Find(".stage-status").TextContent, "processed base image with selected presentation overlays", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".current-sky-summary").TextContent, "Processed base + selected overlays", StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, "Large image view is unavailable for the layered stack", StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll("#current-sky-view-large"));
        Assert.IsEmpty(cut.FindAll(".large-viewer img"));
        await cut.Find("button[title='Show Raw image']").ClickAsync().ConfigureAwait(false);
        Assert.IsEmpty(cut.FindAll(".sky-layer-canvas img"));
        Assert.IsEmpty(cut.FindAll(".sky-layer-overlay svg"));
        Assert.HasCount(1, cut.FindAll(".capture-image img"));
        Assert.HasCount(1, cut.FindAll("#current-sky-view-large"));
        await cut.Find("button[title='Show Processed image']").ClickAsync().ConfigureAwait(false);
        cut.WaitForElement(".sky-layer-canvas img");
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find(".sky-layer-save button").HasAttribute("disabled")));
        await cut.Find(".sky-layer-save button").ClickAsync().ConfigureAwait(false);
        CollectionAssert.AreEqual(new[] { identity }, submitted?.ToArray());
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".sky-layer-result").TextContent, "new immutable artifact", StringComparison.Ordinal));
        StringAssert.Contains(cut.Find(".sky-layer-result a").GetAttribute("href"), "/content", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task GroupedProjectionLayersCountAndRestoreDefaultsWithoutClaimingMeasurements()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(WithStructuredBase()));
        service.PresentationHandler = (id, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(
            Layered(id, new string('D', 64))));
        context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js")
            .Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find(".restore-layers").HasAttribute("disabled")));

        StringAssert.Contains(cut.Find(".scene-panel").TextContent, "Catalog projection", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".scene-panel").TextContent, "not measured associations (#526)", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".scene-panel").TextContent, "Sky context", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".scene-panel").TextContent, "Diagnostics", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".scene-panel").TextContent, "Associations arrive with #525", StringComparison.Ordinal);
        Assert.HasCount(6, cut.FindAll(".unavailable-layers input:disabled"));
        StringAssert.Contains(cut.Find(".inspector-card").TextContent, "astrometric solutions arrive with #523", StringComparison.Ordinal);
        await cut.Find(".sky-layer-controls input").ChangeAsync(false).ConfigureAwait(false);
        Assert.AreEqual("0 selected", cut.Find(".scene-panel header > span").TextContent);
        await cut.Find(".restore-layers").ClickAsync().ConfigureAwait(false);
        Assert.AreEqual("1 selected", cut.Find(".scene-panel header > span").TextContent);
        Assert.IsTrue(cut.Find(".sky-layer-controls input").HasAttribute("checked"));
    }

    [TestMethod]
    public void RecordedLineageUsesArtifactIdsAndDoesNotInventMissingIntegration()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var capture = OperatorUiTestData.Capture();
        var source = Guid.Parse("00000000-0000-0000-0000-000000000101");
        var other = Guid.Parse("00000000-0000-0000-0000-000000000111");
        var facts = CameraAgentCurrentSkyFactsProjector.Project(capture with
        {
            Artifacts = [.. capture.Artifacts, new CameraAgentGalleryArtifact(
                Guid.Parse("00000000-0000-0000-0000-000000000103"), HVO.SkyMonitor.AgentCore.FrameArtifactRole.Combined,
                "combine", null, OperatorUiTestData.Now, "image/jpeg", new string('A', 64), 1024, null,
                [source, other], "combine")]
        }, ObservingDayCalendar.Create("America/Phoenix"));
        service.CurrentSkyHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(
            new(OperatorUiTestData.CurrentImage(), facts, null)));
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForElement(".source-strip");

        Assert.HasCount(2, cut.FindAll(".source-entry"));
        StringAssert.Contains(cut.Find(".stack-lineage").TextContent, "2", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".stack-lineage").TextContent, "not a registered stack", StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll(".lineage-facts dt").Where(static dt => dt.TextContent == "Total integration"));
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".stack-lineage").TextContent, "Source details are unavailable", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WithoutRetainedLayersGroupsRemainVisibleButUnselectable()
    {
        using var context = new BunitContext();
        Configure(context);
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForElement(".scene-panel");
        StringAssert.Contains(cut.Find(".scene-panel").TextContent, "Catalog projection", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".scene-panel").TextContent, "Sky context", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".scene-panel").TextContent, "Diagnostics", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".scene-panel").TextContent, "Measured associations and predictions: unavailable", StringComparison.Ordinal);
        Assert.HasCount(11, cut.FindAll(".scene-panel input:disabled"));
        Assert.IsEmpty(cut.FindAll(".scene-panel input:not(:disabled)"));
    }

    [TestMethod]
    public async Task UnverifiedOrMismatchedLayerPreviewFallsBackAndRetriesOnlyOnManualRefresh()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(WithStructuredBase()));
        service.PresentationHandler = (id, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(id, new string('D', 64))));
        var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        var verification = module.Setup<string>("bindLayerToggles", _ => true);
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => Assert.HasCount(1, module.Invocations.Where(static call => call.Identifier == "bindLayerToggles")));
        var invocation = module.Invocations.Single(static call => call.Identifier == "bindLayerToggles");
        Assert.AreEqual(640, invocation.Arguments[1]);
        Assert.AreEqual(480, invocation.Arguments[2]);
        Assert.IsFalse(cut.Find(".sky-layer-canvas").ClassList.Contains("sky-layer-canvas--verified"));
        Assert.IsTrue(cut.Find(".sky-layer-save button").HasAttribute("disabled"));

        verification.SetResult("mismatch");
        cut.WaitForAssertion(() =>
        {
            Assert.IsEmpty(cut.FindAll(".sky-layer-canvas"));
            Assert.IsNotNull(cut.Find(".capture-image img"));
            StringAssert.Contains(cut.Find(".sky-layer-unavailable").TextContent, "dimensions do not match", StringComparison.Ordinal);
            Assert.IsTrue(cut.Find(".sky-layer-save button").HasAttribute("disabled"));
        });
        var calls = module.Invocations.Count(static call => call.Identifier == "bindLayerToggles");
        await cut.Find("button[title='Show Raw image']").ClickAsync().ConfigureAwait(false);
        await cut.Find("button[title='Show Processed image']").ClickAsync().ConfigureAwait(false);
        Assert.AreEqual(calls, module.Invocations.Count(static call => call.Identifier == "bindLayerToggles"));

        verification.SetResult("valid");
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.HasCount(calls + 1, module.Invocations.Where(static call => call.Identifier == "bindLayerToggles")));
        StringAssert.Contains(cut.Find(".sky-layer-canvas img").GetAttribute("src"), "attempt=1", StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Find(".sky-layer-canvas").ClassList.Contains("sky-layer-canvas--verified")));
        Assert.IsEmpty(cut.FindAll(".sky-layer-unavailable"));
    }

    [TestMethod]
    public async Task LayerPreviewLoadFailureUsesStandardImageUntilManualRefresh()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(WithStructuredBase()));
        service.PresentationHandler = (id, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(id, new string('D', 64))));
        var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        module.Setup<string>("bindLayerToggles", _ => true).SetResult("unavailable");
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".sky-layer-unavailable").TextContent, "could not be verified", StringComparison.Ordinal));
        Assert.IsNotNull(cut.Find(".capture-image img"));
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.HasCount(2, module.Invocations.Where(static call => call.Identifier == "bindLayerToggles")));
    }

    [TestMethod]
    public async Task OptionalLayerFailureRetriesSameCaptureOnRefreshWithoutResettingSelection()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(WithStructuredBase()));
        var reads = 0;
        service.PresentationHandler = (id, _) =>
        {
            return ValueTask.FromResult(Interlocked.Increment(ref reads) == 1
                ? OperatorUiResult<CameraAgentLayeredPresentation>.Failure(OperatorUiResultKind.Unavailable, "Layers unavailable")
                : OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(id, new string('D', 64))));
        };
        context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js")
            .Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Layers unavailable", StringComparison.Ordinal));
        Assert.IsNotNull(cut.Find(".capture-image img"));
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForElement(".sky-layer-canvas img");
        Assert.AreEqual(2, reads);
        Assert.IsEmpty(cut.FindAll(".sky-layer-unavailable"));
        await cut.Find(".sky-layer-controls input").ChangeAsync(false).ConfigureAwait(false);
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        Assert.AreEqual(2, reads);
        Assert.IsFalse(cut.Find(".sky-layer-controls input").HasAttribute("checked"));
    }

    [TestMethod]
    public async Task PendingLayerReadIsNotDuplicatedAndAuthorizationRevocationStopsRetries()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var pending = new TaskCompletionSource<OperatorUiResult<CameraAgentLayeredPresentation>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        service.PresentationHandler = (_, _) =>
        {
            Interlocked.Increment(ref reads);
            return new ValueTask<OperatorUiResult<CameraAgentLayeredPresentation>>(pending.Task);
        };
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => Assert.AreEqual(1, reads));
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        Assert.AreEqual(1, reads);

        pending.SetResult(OperatorUiResult<CameraAgentLayeredPresentation>.Failure(OperatorUiResultKind.Unauthorized, "revoked"));
        Assert.IsTrue(SpinWait.SpinUntil(() =>
            new Uri(context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri).AbsolutePath == "/Account/AccessDenied",
            TimeSpan.FromSeconds(5)));
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        Assert.AreEqual(1, reads);
    }

    [TestMethod]
    public async Task ProcessedLayersPendingThenUnavailableThenLateSuccessNeverShowAnnotatedArtifact()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var presentation = WithStructuredBase();
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(presentation));
        var pending = new TaskCompletionSource<OperatorUiResult<CameraAgentLayeredPresentation>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        service.PresentationHandler = (id, _) => Interlocked.Increment(ref reads) == 1
            ? new ValueTask<OperatorUiResult<CameraAgentLayeredPresentation>>(pending.Task)
            : ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(id, new string('D', 64))));
        context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js")
            .Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => Assert.AreEqual(1, reads));
        AssertBaseOnly(cut, "pending");
        await cut.Find("button[title='Show Raw image']").ClickAsync().ConfigureAwait(false);
        Assert.AreEqual("/api/v1/operations/artifacts/00000000-0000-0000-0000-000000000101/preview",
            cut.Find(".capture-image img").GetAttribute("src"));
        await cut.Find("button[title='Show Processed image']").ClickAsync().ConfigureAwait(false);
        AssertBaseOnly(cut, "pending");

        pending.SetResult(OperatorUiResult<CameraAgentLayeredPresentation>.Failure(OperatorUiResultKind.Unavailable, "Layers unavailable"));
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".sky-layer-unavailable").TextContent, "Layers unavailable", StringComparison.Ordinal));
        cut.WaitForAssertion(() => AssertBaseOnly(cut, "unavailable"));
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Find(".sky-layer-canvas").ClassList.Contains("sky-layer-canvas--verified")));
        Assert.AreEqual(2, reads);
        Assert.AreEqual($"{CombinedDisplayUrl}&attempt=0",
            cut.Find(".sky-layer-canvas img").GetAttribute("src"));
        Assert.IsEmpty(cut.FindAll(".capture-image img"));
    }

    [TestMethod]
    public void ProcessedBaseMissingFailsOpenRatherThanShowingAnnotatedArtifact()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var presentation = WithStructuredBase() with
        {
            Stages = OperatorUiTestData.CurrentImage().Stages
        };
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(presentation));
        service.PresentationHandler = (id, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentLayeredPresentation>.Failure(OperatorUiResultKind.Unavailable, "Layers unavailable"));
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".stage-status").TextContent, "base is unavailable", StringComparison.Ordinal));
        Assert.IsEmpty(cut.FindAll(".capture-image img"));
        Assert.IsEmpty(cut.FindAll("#current-sky-view-large"));
        StringAssert.Contains(cut.Find(".current-sky-summary").TextContent, "Processed base unavailable", StringComparison.Ordinal);
    }

    private const string CombinedDisplayUrl = "/api/v1/operations/artifacts/00000000-0000-0000-0000-000000000104/preview?displayReference=00000000-0000-0000-0000-000000000104";

    private static CameraAgentCurrentImagePresentation WithStructuredBase()
    {
        var source = OperatorUiTestData.CurrentImage();
        return source with
        {
            StructuredLayersAvailable = true,
            Stages = source.Stages.Select(slot => slot.Stage == CameraAgentPresentationStage.Combined
                ? new CameraAgentPresentationSlot(CameraAgentPresentationStage.Combined, "Combined", CameraAgentPresentationSlotAvailability.Available,
                    "Available.", Guid.Parse("00000000-0000-0000-0000-000000000103"), HVO.SkyMonitor.AgentCore.FrameArtifactRole.Combined,
                    null, "application/x-hvo-linear-frame", new Uri(CombinedDisplayUrl, UriKind.Relative),
                    Guid.Parse("00000000-0000-0000-0000-000000000104"), CameraAgentPresentationDisplayBasis.RetainedDerivative,
                    "Retained encoded-preview derivative 00000000-0000-0000-0000-000000000104; one configured display stretch applied when it was produced.",
                    Guid.Parse("00000000-0000-0000-0000-000000000104"), CameraAgentPreviewOperation.EncodeOnly)
                : slot).ToArray()
        };
    }

    // C (...103), D (...104), and A (...102) must remain distinct in both pending and loaded-layer tests.
    private static CameraAgentCurrentImagePresentation WithRetainedCombinedDerivative()
        => WithStructuredBase();

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LiveMeanAndPendingProcessedShareRetainedDerivativeWhileDownloadStaysLinear(bool advertisedLayers)
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(
            WithRetainedCombinedDerivative() with { StructuredLayersAvailable = advertisedLayers }));
        var pending = new TaskCompletionSource<OperatorUiResult<CameraAgentLayeredPresentation>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.PresentationHandler = (_, _) => new ValueTask<OperatorUiResult<CameraAgentLayeredPresentation>>(pending.Task);
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForElement(".capture-image img");

        // Processed selected, layers pending: the unannotated base is the retained derivative, not the linear frame
        // or the annotated artifact, and the download link names the linear Combined frame.
        Assert.AreEqual(CombinedDisplayUrl,
            cut.Find(".capture-image img").GetAttribute("src"));
        StringAssert.Contains(cut.Find(".stage-status").TextContent, "retained display derivative", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".stage-display-policy").TextContent, "Stage artifact 00000000-0000-0000-0000-000000000103", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".stage-display-policy").TextContent, "shown pixels from artifact 00000000-0000-0000-0000-000000000104", StringComparison.Ordinal);
        Assert.AreEqual("/api/v1/operations/artifacts/00000000-0000-0000-0000-000000000103/content",
            cut.Find(".stage-display-policy a").GetAttribute("href"));
        StringAssert.Contains(cut.Find(".current-sky-summary").TextContent, "Display basisRetained display derivative", StringComparison.Ordinal);

        await cut.Find("button[title='Show Combined image']").ClickAsync().ConfigureAwait(false);
        Assert.AreEqual(CombinedDisplayUrl,
            cut.Find(".capture-image img").GetAttribute("src"));
        StringAssert.Contains(cut.Find(".stage-status").TextContent, "Showing Combined (retained display derivative)", StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, "Arithmetic mean, not a sum", StringComparison.Ordinal);

        await cut.Find("button[title='Show Raw image']").ClickAsync().ConfigureAwait(false);
        Assert.AreEqual("/api/v1/operations/artifacts/00000000-0000-0000-0000-000000000101/preview",
            cut.Find(".capture-image img").GetAttribute("src"));
        StringAssert.Contains(cut.Find(".stage-status").TextContent, "Showing Raw (per-image normalization)", StringComparison.Ordinal);
        Assert.AreEqual("/api/v1/operations/artifacts/00000000-0000-0000-0000-000000000101/content",
            cut.Find(".stage-display-policy a").GetAttribute("href"));
        pending.SetResult(OperatorUiResult<CameraAgentLayeredPresentation>.Failure(OperatorUiResultKind.Unavailable, "Layers unavailable"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LoadedProcessedWithAllOverlaysOffDescribesActualDerivativeAndDownloadsLinearSource(bool advertisedLayers)
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var presentation = WithRetainedCombinedDerivative() with { StructuredLayersAvailable = advertisedLayers };
        var combined = presentation.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Combined);
        var annotated = presentation.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Annotated);
        Assert.AreNotEqual(combined.ArtifactId, combined.DisplayArtifactId);
        Assert.AreNotEqual(annotated.ArtifactId, combined.ArtifactId);
        Assert.AreNotEqual(annotated.ArtifactId, combined.DisplayArtifactId);
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(presentation));
        service.PresentationHandler = (id, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(
            Layered(id, new string('D', 64), combined.DisplayArtifactId)));
        context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js")
            .Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForElement(".sky-layer-canvas--verified");

        foreach (var checkbox in cut.FindAll(".sky-layer-controls input[data-layer-target]"))
            await checkbox.ChangeAsync(false).ConfigureAwait(false);

        Assert.AreEqual("0 selected", cut.Find(".scene-panel header > span").TextContent);
        Assert.IsTrue(cut.FindAll(".sky-layer-controls input[data-layer-target]").All(static input => !input.HasAttribute("checked")));
        Assert.AreEqual("true", cut.Find("button[title='Show Processed image']").GetAttribute("aria-pressed"));
        var shown = cut.Find(".sky-layer-canvas img").GetAttribute("src")!;
        Assert.AreEqual($"{combined.PreviewUrl}&attempt=0", shown);
        var policy = cut.Find(".stage-display-policy");
        StringAssert.Contains(policy.TextContent, $"shown pixels from artifact {combined.DisplayArtifactId:D}", StringComparison.Ordinal);
        StringAssert.Contains(policy.TextContent, $"Stage artifact {combined.ArtifactId:D}", StringComparison.Ordinal);
        Assert.IsFalse(policy.TextContent.Contains(annotated.ArtifactId!.Value.ToString("D"), StringComparison.Ordinal));
        Assert.AreEqual($"/api/v1/operations/artifacts/{combined.ArtifactId:D}/content", policy.QuerySelector("a")!.GetAttribute("href"));
        StringAssert.Contains(cut.Find(".current-sky-summary").TextContent, "Display basisRetained display derivative", StringComparison.Ordinal);

        await cut.Find("button[title='Show Combined image']").ClickAsync().ConfigureAwait(false);
        Assert.AreEqual(combined.PreviewUrl!.OriginalString, cut.Find(".capture-image img").GetAttribute("src"));
        await cut.Find("button[title='Show Processed image']").ClickAsync().ConfigureAwait(false);
        cut.WaitForElement(".sky-layer-canvas--verified");
        Assert.AreEqual("0 selected", cut.Find(".scene-panel header > span").TextContent);
        Assert.AreEqual(shown, cut.Find(".sky-layer-canvas img").GetAttribute("src"));
    }

    [TestMethod]
    public async Task RawComparisonUsesOwnArtifactUrlAndExplicitReferencePolicy()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var source = WithRetainedCombinedDerivative();
        var reference = source.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Combined).DisplayArtifactId!.Value;
        var raw = source.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Raw);
        var expected = $"/api/v1/operations/artifacts/{raw.ArtifactId:D}/preview?displayReference={reference:D}";
        source = source with
        {
            Stages = source.Stages.Select(slot => slot.Stage == CameraAgentPresentationStage.Raw ? slot with
            {
                PreviewUrl = new Uri(expected, UriKind.Relative),
                DisplayReferenceId = reference,
                DisplayPolicy = "Capture-bound comparison: same percentile settings, each image's own histogram; not a locked transfer curve."
            } : slot).ToArray()
        };
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(source));
        var cut = context.Render<CurrentSkyPage>();
        await cut.Find("button[title='Show Raw image']").ClickAsync().ConfigureAwait(false);
        Assert.AreEqual(expected, cut.Find(".capture-image img").GetAttribute("src"));
        StringAssert.Contains(cut.Find(".stage-display-policy").TextContent, "not a locked transfer curve", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".current-sky-summary").TextContent, "Capture-bound per-image normalization", StringComparison.Ordinal);
        Assert.AreEqual($"/api/v1/operations/artifacts/{raw.ArtifactId:D}/content", cut.Find(".stage-display-policy a").GetAttribute("href"));
    }

    [TestMethod]
    [DataRow("annotated")]
    [DataRow("linear")]
    [DataRow("unrelated")]
    [DataRow("unresolved")]
    public void LayerManifestMustMatchResolvedCombinedDerivativeBeforeShowingLayers(string scenario)
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var presentation = WithRetainedCombinedDerivative();
        var combined = presentation.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Combined);
        var manifestBase = scenario switch
        {
            "annotated" => presentation.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Annotated).ArtifactId,
            "linear" => combined.ArtifactId,
            "unresolved" => combined.DisplayArtifactId,
            _ => Guid.NewGuid()
        };
        if (scenario == "unresolved")
            presentation = presentation with
            {
                Stages = presentation.Stages.Select(slot => slot.Stage == CameraAgentPresentationStage.Combined
                    ? slot with { DisplayBasis = CameraAgentPresentationDisplayBasis.OwnArtifact } : slot).ToArray()
            };
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(presentation));
        service.PresentationHandler = (id, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(
            Layered(id, new string('D', 64), manifestBase)));
        var cut = context.Render<CurrentSkyPage>();

        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".sky-layer-unavailable").TextContent,
            "manifest base does not match", StringComparison.Ordinal));
        Assert.AreEqual(combined.PreviewUrl!.OriginalString, cut.Find(".capture-image img").GetAttribute("src"));
        Assert.IsEmpty(cut.FindAll(".sky-layer-canvas, .sky-layer-overlay"));
        Assert.IsTrue(cut.Find(".sky-layer-save button").HasAttribute("disabled"));
        Assert.IsEmpty(context.JSInterop.Invocations);
        Assert.IsFalse(cut.Find(".stage-status").TextContent.Contains("with selected presentation overlays", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(false, true, true)]
    [DataRow(false, false, false)]
    [DataRow(true, true, true)]
    [DataRow(true, false, false)]
    public async Task PendingProcessedDistinguishesConfirmedLegacyAbsenceFromRetainedLayerFailure(
        bool advertisedLayers, bool notRetained, bool showsAnnotated)
    {
        var terminalKind = notRetained ? OperatorUiResultKind.NotFound : OperatorUiResultKind.Unavailable;
        using var context = new BunitContext();
        var service = Configure(context);
        var presentation = WithStructuredBase() with { StructuredLayersAvailable = advertisedLayers };
        var annotated = presentation.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Annotated);
        // Complete on the renderer below so the terminal read and its finally-render are observed together.
        var pending = new TaskCompletionSource<OperatorUiResult<CameraAgentLayeredPresentation>>();
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(presentation));
        service.PresentationHandler = (_, _) => new(pending.Task);
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForElement(".capture-image img");
        AssertBaseOnly(cut, "pending");
        await cut.InvokeAsync(() => pending.SetResult(OperatorUiResult<CameraAgentLayeredPresentation>.Failure(terminalKind,
            terminalKind == OperatorUiResultKind.NotFound ? "Structured layers were not retained." : "Retained layer read failed."))).ConfigureAwait(false);
        await cut.WaitForAssertionAsync(() => Assert.AreEqual(showsAnnotated ? annotated.PreviewUrl!.OriginalString : CombinedDisplayUrl,
            cut.Find(".capture-image img").GetAttribute("src")), TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual("true", cut.Find("button[title='Show Processed image']").GetAttribute("aria-pressed"));
        if (showsAnnotated)
        {
            StringAssert.Contains(cut.Find(".stage-display-policy").TextContent, annotated.ArtifactId!.Value.ToString("D"), StringComparison.Ordinal);
            Assert.AreEqual($"/api/v1/operations/artifacts/{annotated.ArtifactId:D}/content", cut.Find(".stage-display-policy a").GetAttribute("href"));
            StringAssert.Contains(cut.Find(".stage-status").TextContent, "retained encoded bytes; no display stretch", StringComparison.Ordinal);
        }
        else
        {
            // Observe the asynchronous finally-render without coupling the assertion to bUnit's render-event subscription.
            for (var attempt = 0; attempt < 500 && cut.Find(".stage-status").TextContent.Contains("layers are pending", StringComparison.Ordinal); attempt++)
                await Task.Delay(10).ConfigureAwait(false);
            AssertBaseOnly(cut, "unavailable");
        }
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        await cut.WaitForAssertionAsync(() => Assert.AreEqual(showsAnnotated ? annotated.PreviewUrl!.OriginalString : CombinedDisplayUrl,
            cut.Find(".capture-image img").GetAttribute("src")), TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsEmpty(cut.FindAll(".sky-layer-overlay"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FreshProcessedWithoutQualifiedDerivativeUsesOwnCombinedUntilConfirmedLayerAbsence(bool notRetained)
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var source = WithStructuredBase();
        var combined = source.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Combined);
        combined = combined with
        {
            DisplayArtifactId = combined.ArtifactId,
            DisplayBasis = CameraAgentPresentationDisplayBasis.OwnArtifact,
            DisplayReferenceId = null,
            DisplayOperation = CameraAgentPreviewOperation.PerImageStretch,
            PreviewUrl = new Uri($"/api/v1/operations/artifacts/{combined.ArtifactId:D}/preview", UriKind.Relative),
            DisplayPolicy = "On-demand per-image normalization, global default; retained comparison reference unavailable."
        };
        var presentation = source with
        {
            StructuredLayersAvailable = false,
            Stages = source.Stages.Select(slot => slot.Stage == combined.Stage ? combined : slot).ToArray()
        };
        var annotated = presentation.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Annotated);
        var pending = new TaskCompletionSource<OperatorUiResult<CameraAgentLayeredPresentation>>();
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(presentation));
        service.PresentationHandler = (_, _) => new(pending.Task);
        var cut = context.Render<CurrentSkyPage>();

        cut.WaitForElement(".capture-image img");
        Assert.AreEqual(combined.PreviewUrl!.OriginalString, cut.Find(".capture-image img").GetAttribute("src"),
            "Unknown layer state must never expose baked annotations, even without qualified D.");
        StringAssert.Contains(cut.Find(".stage-status").TextContent, "processed layers are pending", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".stage-display-policy").TextContent, "global default", StringComparison.Ordinal);
        Assert.AreEqual($"/api/v1/operations/artifacts/{combined.ArtifactId:D}/content", cut.Find(".stage-display-policy a").GetAttribute("href"));
        await cut.InvokeAsync(() => pending.SetResult(OperatorUiResult<CameraAgentLayeredPresentation>.Failure(
            notRetained ? OperatorUiResultKind.NotFound : OperatorUiResultKind.Unavailable,
            notRetained ? "No retained layers." : "Retained layer base is unavailable."))).ConfigureAwait(false);
        for (var attempt = 0; attempt < 500 && cut.Find(".stage-status").TextContent.Contains("layers are pending", StringComparison.Ordinal); attempt++)
            await Task.Delay(10).ConfigureAwait(false);

        var displayed = notRetained ? annotated : combined;
        Assert.AreEqual(displayed.PreviewUrl!.OriginalString, cut.Find(".capture-image img").GetAttribute("src"));
        Assert.AreEqual($"/api/v1/operations/artifacts/{displayed.ArtifactId:D}/content", cut.Find(".stage-display-policy a").GetAttribute("href"));
        Assert.AreEqual("true", cut.Find("button[title='Show Processed image']").GetAttribute("aria-pressed"));
        Assert.IsEmpty(cut.FindAll(".sky-layer-overlay"));
        StringAssert.Contains(cut.Find(".stage-status").TextContent,
            notRetained ? "retained encoded bytes" : "processed layers are unavailable", StringComparison.Ordinal);
        if (!notRetained)
        {
            StringAssert.Contains(cut.Find(".stage-display-policy").TextContent, "global default", StringComparison.Ordinal);
            Assert.IsFalse(cut.Find(".stage-display-policy").TextContent.Contains(annotated.ArtifactId!.Value.ToString("D"), StringComparison.Ordinal));
        }
    }

    [TestMethod]
    [DataRow(HVO.SkyMonitor.AgentCore.CameraPixelFormat.Mono8, false)]
    [DataRow(HVO.SkyMonitor.AgentCore.CameraPixelFormat.Mono8, true)]
    [DataRow(HVO.SkyMonitor.AgentCore.CameraPixelFormat.Rgb24, false)]
    [DataRow(HVO.SkyMonitor.AgentCore.CameraPixelFormat.Rgb24, true)]
    public void EightBitStageLabelsDescribeEncodingNotNormalization(HVO.SkyMonitor.AgentCore.CameraPixelFormat format, bool comparison)
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var projector = new CameraAgentCapturePresentationProjector(Microsoft.Extensions.Options.Options.Create(
            new HVO.SkyMonitor.CameraAgent.Common.Options.CameraAgentHostOptions()));
        var capture = OperatorUiTestData.Capture() with { RawState = "committed" };
        capture = capture with
        {
            Artifacts = capture.Artifacts.Select(artifact => artifact.Role == HVO.SkyMonitor.AgentCore.FrameArtifactRole.Raw
            ? artifact with { PixelFormat = format, MediaType = "application/x-hvo-packed-image" } : artifact).ToArray()
        };
        var raw = projector.Project(capture).Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Raw);
        Assert.AreEqual(CameraAgentPreviewOperation.EncodeOnly, raw.DisplayOperation);
        if (comparison) raw = raw with { DisplayReferenceId = Guid.NewGuid() };
        var presentation = OperatorUiTestData.CurrentImage();
        presentation = presentation with { Stages = presentation.Stages.Select(slot => slot.Stage == raw.Stage ? raw : slot).ToArray() };
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(presentation));
        var cut = context.Render<CurrentSkyPage>();
        cut.Find("button[title='Show Raw image']").Click();
        StringAssert.Contains(cut.Find(".stage-status").TextContent, "encoding only; no display stretch", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".current-sky-summary").TextContent, "Encoding only; no display stretch", StringComparison.Ordinal);
        Assert.IsFalse(cut.Find(".stage-status").TextContent.Contains("normalization", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SameCaptureReferenceOutageRequiresNewBindAndPreservesAllLayersOff()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var original = WithStructuredBase();
        var current = original;
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(current));
        service.PresentationHandler = (id, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(id, new string('D', 64))));
        var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        var binding = module.Setup<string>("bindLayerToggles", _ => true);
        binding.SetResult("valid");
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForElement(".sky-layer-canvas--verified");
        await cut.Find(".sky-layer-controls input[data-layer-target]").ChangeAsync(false).ConfigureAwait(false);
        current = original with
        {
            Stages = original.Stages.Select(slot => slot.Stage == CameraAgentPresentationStage.Combined ? slot with
            {
                DisplayBasis = CameraAgentPresentationDisplayBasis.OwnArtifact,
                DisplayArtifactId = slot.ArtifactId,
                DisplayReferenceId = null,
                PreviewUrl = new Uri($"/api/v1/operations/artifacts/{slot.ArtifactId:D}/preview", UriKind.Relative)
            } : slot).ToArray()
        };
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        Assert.IsEmpty(cut.FindAll(".sky-layer-canvas"));
        Assert.IsTrue(cut.Find(".sky-layer-save button").HasAttribute("disabled"));
        var before = module.Invocations.Count(static call => call.Identifier == "bindLayerToggles");
        var rebound = module.Setup<string>("bindLayerToggles", _ => true);
        current = original;
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.AreEqual(before + 1, module.Invocations.Count(static call => call.Identifier == "bindLayerToggles")));
        Assert.IsEmpty(cut.FindAll(".sky-layer-canvas--verified"));
        Assert.IsTrue(cut.Find(".sky-layer-save button").HasAttribute("disabled"));
        Assert.IsFalse(cut.Find(".sky-layer-controls input[data-layer-target]").HasAttribute("checked"));
        rebound.SetResult("valid");
        cut.WaitForElement(".sky-layer-canvas--verified");
        Assert.AreEqual("0 selected", cut.Find(".scene-panel header > span").TextContent);
        IReadOnlyList<string>? saved = null;
        service.MaterializationHandler = (id, selected, _) =>
        {
            saved = selected;
            return ValueTask.FromResult(OperatorUiResult<CameraAgentPresentationMaterializationReceipt>.Success(
                new(id, Guid.NewGuid(), new string('A', 64), new string('B', 64), 1, false)));
        };
        await cut.Find(".sky-layer-save button").ClickAsync().ConfigureAwait(false);
        Assert.IsNotNull(saved);
        Assert.IsEmpty(saved);
        StringAssert.Contains(cut.Find(".sky-layer-canvas").GetAttribute("style")!, "min(640px, 100%", StringComparison.Ordinal);
    }

    private static void AssertBaseOnly(IRenderedComponent<CurrentSkyPage> cut, string state)
    {
        Assert.AreEqual(CombinedDisplayUrl,
            cut.Find(".capture-image img").GetAttribute("src"));
        Assert.IsEmpty(cut.FindAll(".sky-layer-overlay svg"));
        StringAssert.Contains(cut.Find(".stage-status").TextContent, $"processed layers are {state}", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".current-sky-summary").TextContent, "Unannotated Combined base", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task LayerSelectionSurvivesPollingAndResetsForNewCapture()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var first = WithStructuredBase();
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000021");
        var current = first;
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(current));
        service.PresentationHandler = (id, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(id, new string('D', 64))));
        context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js")
            .Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find(".sky-layer-save button").HasAttribute("disabled")));

        await cut.Find(".sky-layer-controls input").ChangeAsync(false).ConfigureAwait(false);
        Assert.IsFalse(cut.Find(".sky-layer-controls input").HasAttribute("checked"));
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        Assert.IsFalse(cut.Find(".sky-layer-controls input").HasAttribute("checked"));

        current = first with { DisplayCapture = first.DisplayCapture! with { CaptureId = secondId } };
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual($"/gallery/{secondId:D}", cut.Find(".sky-layer-actions a").GetAttribute("href"));
            Assert.IsTrue(cut.Find(".sky-layer-controls input").HasAttribute("checked"));
        });
    }

    [TestMethod]
    public async Task CaptureChangeDiscardsStaleLayerReadAndPendingSave()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var first = WithStructuredBase();
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000021");
        var second = first with { DisplayCapture = first.DisplayCapture! with { CaptureId = secondId } };
        var current = first;
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(current));
        var selection = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        selection.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        var firstRead = new TaskCompletionSource<OperatorUiResult<CameraAgentLayeredPresentation>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.PresentationHandler = (id, _) => id == first.DisplayCapture!.CaptureId
            ? new ValueTask<OperatorUiResult<CameraAgentLayeredPresentation>>(firstRead.Task)
            : ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(id, new string('E', 64))));
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForElement(".capture-image img");
        current = second;
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForElement(".sky-layer-workspace");
        firstRead.SetResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(first.DisplayCapture!.CaptureId, new string('D', 64))));
        Assert.AreEqual(new string('E', 64), cut.Find(".sky-layer-controls input").GetAttribute("data-layer-identity"));
        var saveCalls = 0;
        var pending = new TaskCompletionSource<OperatorUiResult<CameraAgentPresentationMaterializationReceipt>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.MaterializationHandler = (_, _, _) =>
        {
            Interlocked.Increment(ref saveCalls);
            return new ValueTask<OperatorUiResult<CameraAgentPresentationMaterializationReceipt>>(pending.Task);
        };
        var save = cut.Find(".sky-layer-save button").ClickAsync();
        cut.WaitForAssertion(() => Assert.AreEqual(1, saveCalls));
        current = first;
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        pending.SetResult(OperatorUiResult<CameraAgentPresentationMaterializationReceipt>.Success(new(
            secondId, Guid.NewGuid(), new string('F', 64), new string('A', 64), 1024, false)));
        await save.ConfigureAwait(false);
        Assert.AreEqual(1, saveCalls);
        Assert.IsEmpty(cut.FindAll(".sky-layer-result"));
    }

    [TestMethod]
    public async Task StaleJsBindCannotEnableNewCaptureControls()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var first = WithStructuredBase();
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000021");
        var current = first;
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(current));
        service.PresentationHandler = (id, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(id, new string('D', 64))));
        var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        var bind = module.Setup<string>("bindLayerToggles", _ => true);
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => Assert.HasCount(1, module.Invocations.Where(static call => call.Identifier == "bindLayerToggles")));

        current = first with { DisplayCapture = first.DisplayCapture! with { CaptureId = secondId } };
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Find(".sky-layer-save button").HasAttribute("disabled")));
        bind.SetResult("valid");
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find(".sky-layer-save button").HasAttribute("disabled")));
        Assert.AreEqual($"/gallery/{secondId:D}", cut.Find(".sky-layer-actions a").GetAttribute("href"));
    }

    [TestMethod]
    public async Task SaveExceptionsAndCancellationShowSanitizedRetryableError()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(WithStructuredBase()));
        service.PresentationHandler = (id, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(id, new string('D', 64))));
        context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js")
            .Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find(".sky-layer-save button").HasAttribute("disabled")));

        service.MaterializationHandler = (_, _, _) => throw new InvalidOperationException("sensitive service details");
        await cut.Find(".sky-layer-save button").ClickAsync().ConfigureAwait(false);
        Assert.AreEqual("The presentation stack could not be saved. Please try again.", cut.Find(".sky-layer-result--error").TextContent);
        Assert.IsFalse(cut.Find(".sky-layer-save button").HasAttribute("disabled"));

        service.MaterializationHandler = (_, _, _) => throw new OperationCanceledException();
        await cut.Find(".sky-layer-save button").ClickAsync().ConfigureAwait(false);
        Assert.AreEqual("The presentation stack could not be saved. Please try again.", cut.Find(".sky-layer-result--error").TextContent);
        Assert.IsFalse(cut.Find(".sky-layer-save button").HasAttribute("disabled"));
    }

    private static CameraAgentLayeredPresentation Layered(Guid captureId, string identity, Guid? baseArtifactId = null) => new(
        captureId, baseArtifactId ?? Guid.Parse("00000000-0000-0000-0000-000000000104"), new string('A', 64),
        new string('B', 64), new string('C', 64), 640, 480,
        [new(identity, "scene-annotation", "hvo-layer-0", 20, true, 1_000_000, "renderer-v1", "style-v1")],
        System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 640 480\"><g id=\"hvo-layer-0\"></g></svg>"));

    [TestMethod]
    public void EmptyProjectionExplainsMissingImageWhileReportingSystemState()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(
            new CameraAgentCurrentImagePresentation(
                OperatorUiTestData.Now,
                CameraAgentPresentationImageFreshness.Empty,
                new CameraAgentPresentationSystemStatus(
                    CameraAgentPresentationSystemState.Unavailable,
                    "Capture status unavailable.",
                    OperatorUiTestData.Now),
                null,
                null,
                false,
                null,
                [],
                false,
                true)));

        var cut = context.Render<CurrentSkyPage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "No sky image is available yet", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "No image", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Camera unavailable", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "bounded history", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll(".capture-image__actions"));
        });
    }

    [TestMethod]
    public void LargeViewerUsesDialogAndRestoresFocusOnClose()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        Configure(context);
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForElement("#current-sky-view-large");
        Assert.IsEmpty(cut.FindComponent<HVO.SkyMonitor.CameraAgent.Components.Presentation.LargeImageViewer>()
            .FindAll("img"));

        cut.Find("#current-sky-view-large").Click();
        cut.WaitForAssertion(() => Assert.IsTrue(context.JSInterop.Invocations.Any(static invocation =>
            invocation.Identifier.EndsWith("show", StringComparison.Ordinal))));
        var viewer = cut.FindComponent<HVO.SkyMonitor.CameraAgent.Components.Presentation.LargeImageViewer>();
        Assert.HasCount(1, viewer.FindAll("img"));
        viewer.Find(".large-viewer__close").Click();

        cut.WaitForAssertion(() => Assert.IsTrue(context.JSInterop.Invocations.Any(static invocation =>
            invocation.Identifier.EndsWith("close", StringComparison.Ordinal))));
    }

    [TestMethod]
    public void SameSourcePreviewFailureRetriesAfterProjectionRefreshAndPreservesDetails()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var first = OperatorUiTestData.CurrentImage();
        var read = 0;
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(
            Interlocked.Increment(ref read) == 1 ? first : first with { ObservedUtc = first.ObservedUtc.AddSeconds(1) }));
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForElement(".capture-image img");

        cut.Find(".capture-image img").TriggerEvent("onerror", EventArgs.Empty);

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "Image preview unavailable", StringComparison.Ordinal);
            Assert.HasCount(1, cut.FindAll("a[href^='/gallery/']"));
            Assert.IsEmpty(cut.FindAll("#current-sky-view-large"));
        });
        cut.Find("button.refresh-link").Click();
        cut.WaitForElement(".capture-image img");
    }

    [TestMethod]
    public void LatestCaptureFactsAndDetailsRemainWhenNoImageCanBeDisplayed()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var projection = WithoutDisplayImage(OperatorUiTestData.CurrentImage());
        service.CurrentImageHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(projection));

        var cut = context.Render<CurrentSkyPage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "No sky image is available yet", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Simulated", StringComparison.Ordinal);
            Assert.AreEqual("/gallery/00000000-0000-0000-0000-000000000001", cut.Find("a[href^='/gallery/']").GetAttribute("href"));
        });
    }

    [TestMethod]
    public void RefreshWithoutDisplayImageClosesOpenViewer()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = Configure(context);
        var first = OperatorUiTestData.CurrentImage();
        var read = 0;
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(
            Interlocked.Increment(ref read) == 1 ? first : WithoutDisplayImage(first)));
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForElement("#current-sky-view-large");
        cut.Find("#current-sky-view-large").Click();
        cut.WaitForAssertion(() => Assert.IsTrue(context.JSInterop.Invocations.Any(static invocation =>
            invocation.Identifier.EndsWith("show", StringComparison.Ordinal))));

        cut.Find("button.refresh-link").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.IsEmpty(cut.FindAll("#current-sky-view-large"));
            Assert.IsTrue(context.JSInterop.Invocations.Any(static invocation =>
                invocation.Identifier.EndsWith("close", StringComparison.Ordinal)));
        });
    }

    [TestMethod]
    public async Task DisposalCancelsPendingProjectionRead()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CurrentImageHandler = async token =>
        {
            using var registration = token.Register(() => cancellationObserved.TrySetResult());
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return OperatorUiResult<CameraAgentCurrentImagePresentation>.Failure(OperatorUiResultKind.Unavailable, "unreachable");
        };
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForElement(".capture-image[aria-busy='true']");

        await cut.Instance.DisposeAsync().ConfigureAwait(false);

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    [TestMethod]
    public void RefreshReportsBusyStateAndNavigatesOnAuthorizationRevocation()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var reads = 0;
        service.CurrentImageHandler = _ => ValueTask.FromResult(Interlocked.Increment(ref reads) == 1
            ? OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(OperatorUiTestData.CurrentImage())
            : OperatorUiResult<CameraAgentCurrentImagePresentation>.Failure(OperatorUiResultKind.Unauthorized, "revoked"));
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForElement(".current-sky-summary[aria-live='polite'][aria-atomic='true'][aria-busy='false']");

        cut.Find("button.refresh-link").Click();

        cut.WaitForAssertion(() => Assert.AreEqual(
            "/Account/AccessDenied",
            new Uri(context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri).AbsolutePath));
    }

    private static TestOperatorUiService Configure(BunitContext context)
    {
        var service = new TestOperatorUiService
        {
            CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(
                OperatorUiTestData.CurrentImage()))
        };
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(new ProcessingExecutionPagesTests.GraphUiService());
        context.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(OperatorUiTestData.Now));
        return service;
    }

    private static CameraAgentCurrentImagePresentation WithoutDisplayImage(CameraAgentCurrentImagePresentation source)
        => source with
        {
            DisplayCapture = null,
            SelectedStage = null,
            Stages = source.Stages.Select(static slot => slot with
            {
                Availability = CameraAgentPresentationSlotAvailability.Missing,
                Reason = $"No artifact was produced for {slot.Label}.",
                ArtifactId = null,
                ArtifactRole = null,
                Variant = null,
                MediaType = null,
                PreviewUrl = null
            }).ToArray()
        };
}
