using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Components.Presentation;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class GalleryPageTests
{
    [TestMethod]
    public void EmptyAndError_RenderExplicitStates()
    {
        using var emptyContext = new BunitContext();
        Configure(emptyContext);
        var empty = emptyContext.Render<GalleryPage>();
        empty.WaitForAssertion(() => StringAssert.Contains(empty.Markup, "No captures match these filters", StringComparison.Ordinal));

        using var errorContext = new BunitContext();
        var errorService = Configure(errorContext);
        errorService.GalleryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryPage>.Failure(OperatorUiResultKind.Unavailable, "The gallery is temporarily unavailable."));
        var error = errorContext.Render<GalleryPage>();
        error.WaitForAssertion(() => Assert.AreEqual("alert", error.Find(".gallery-state--error").GetAttribute("role")));
    }

    [TestMethod]
    public void BoundedPage_RendersImageLedCardsAndCollapsedAdvancedFilters()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var simulated = OperatorUiTestData.Capture();
        var fixture = OperatorUiTestData.Capture(Guid.Parse("00000000-0000-0000-0000-000000000002"), GalleryEvidenceOrigin.DeveloperFixture, annotated: false) with { CaptureSequence = 43 };
        service.GalleryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryPage>.Success(new CameraAgentGalleryPage([simulated, fixture], "older-cursor")));

        var cut = context.Render<GalleryPage>();

        cut.WaitForAssertion(() =>
        {
            var images = cut.FindAll(".capture-image img");
            Assert.HasCount(2, images);
            StringAssert.Contains(images[0].GetAttribute("src")!, "/api/v1/operations/artifacts/", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Processed single frame", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Capture #42", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Additional capture history remains unloaded", StringComparison.Ordinal);
            Assert.HasCount(5, cut.FindAll("a[href^='/gallery/']"));
            Assert.HasCount(2, cut.FindAll(".capture-card"));
            Assert.IsFalse(cut.Find(".advanced-filters").HasAttribute("open"));
        });
    }

    [TestMethod]
    public void CardsUseTruthfulFallbackAndRetainFactsWhenNoImageIsDisplayable()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var combinedId = Guid.Parse("00000000-0000-0000-0000-000000000201");
        var combined = OperatorUiTestData.Capture() with
        {
            Artifacts = [new CameraAgentGalleryArtifact(
                combinedId,
                HVO.SkyMonitor.AgentCore.FrameArtifactRole.Combined,
                "combined",
                "stack",
                OperatorUiTestData.Now,
                "application/x-hvo-packed-image",
                new string('C', 64),
                1024,
                null,
                [],
                "combined-node",
                PixelFormat: HVO.SkyMonitor.AgentCore.CameraPixelFormat.Mono16,
                PreviewReconstructionSupported: true)]
        };
        var unavailable = OperatorUiTestData.Capture(
            Guid.Parse("00000000-0000-0000-0000-000000000202"),
            GalleryEvidenceOrigin.Unknown) with
        {
            CaptureSequence = 41,
            Artifacts = [],
            ProcessingNodes = []
        };
        service.GalleryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryPage>.Success(new([combined, unavailable], null)));

        var cut = context.Render<GalleryPage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Find(".capture-card-heading").TextContent, "Unregistered live mean", StringComparison.Ordinal);
            StringAssert.Contains(cut.FindAll(".capture-card")[1].TextContent, "Capture facts retained", StringComparison.Ordinal);
            StringAssert.Contains(cut.FindAll(".capture-card")[1].TextContent, "No display product", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void PreviewFailureShowsPlaceholderAndRetainsFacts()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = Configure(context);
        service.GalleryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryPage>.Success(new([OperatorUiTestData.Capture()], null)));
        var cut = context.Render<GalleryPage>();
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll(".capture-image img")));

        cut.Find(".capture-image img").TriggerEvent("onerror", EventArgs.Empty);
        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Find(".capture-image-placeholder").TextContent, "Image preview unavailable", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find(".capture-image-placeholder").TextContent, "Capture facts retained", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find(".capture-image").GetAttribute("aria-label")!, "Image preview unavailable", StringComparison.Ordinal);
        });
        // The detail link remains available even when the thumbnail bytes fail.
        Assert.IsNotNull(cut.Find(".capture-card-actions a").GetAttribute("href"));
    }

    [TestMethod]
    public async Task ParameterChangeShowsLoadingThenErrorStateAsync()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = Configure(context);
        service.GalleryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryPage>.Success(new([OperatorUiTestData.Capture()], null)));
        var cut = context.Render<GalleryPage>();
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll(".capture-card")));

        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<OperatorUiResult<CameraAgentGalleryPage>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.GalleryHandler = (_, _) =>
        {
            requestStarted.TrySetResult();
            return new ValueTask<OperatorUiResult<CameraAgentGalleryPage>>(pending.Task);
        };
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/gallery?cursor=next");
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Loading gallery page", StringComparison.Ordinal));
        pending.SetResult(OperatorUiResult<CameraAgentGalleryPage>.Failure(
            OperatorUiResultKind.Unavailable,
            "The gallery is temporarily unavailable."));
        cut.WaitForAssertion(() => Assert.AreEqual("alert", cut.Find(".gallery-state--error").GetAttribute("role")));
    }

    [TestMethod]
    public void Filters_ArePassedToBoundedQueryAndNavigationClearsCursor()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        CameraAgentGalleryQuery? observed = null;
        service.GalleryHandler = (query, _) =>
        {
            observed = query;
            return ValueTask.FromResult(OperatorUiResult<CameraAgentGalleryPage>.Success(new CameraAgentGalleryPage([], null)));
        };

        var navigation = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo("/gallery?from=2026-07-23T10%3A00%3A00&origin=Simulated&role=Preview&recipe=preview&status=completed&rawState=durable&minSequence=40&maxSequence=50&pageSize=50&cursor=cursor-token");
        var cut = context.Render<GalleryPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.IsNotNull(observed);
            Assert.AreEqual(50, observed.PageSize);
            Assert.AreEqual("cursor-token", observed.Cursor);
            Assert.AreEqual(GalleryEvidenceOrigin.Simulated, observed.EvidenceOrigin);
            Assert.AreEqual(HVO.SkyMonitor.AgentCore.FrameArtifactRole.Preview, observed.ProcessingRole);
            Assert.AreEqual("preview", observed.Recipe);
            Assert.AreEqual("completed", observed.ProcessingStatus);
            Assert.AreEqual("durable", observed.RawState);
            Assert.AreEqual(40, observed.MinimumSequence);
            Assert.AreEqual(50, observed.MaximumSequence);
            Assert.IsTrue(cut.Find(".advanced-filters").HasAttribute("open"));
        });

        cut.Find("button[type='submit']").Click();
        Assert.IsFalse(navigation.Uri.Contains("cursor=", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DetailLinks_PreserveValidatedGalleryFiltersAndCursor()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var capture = OperatorUiTestData.Capture();
        service.GalleryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryPage>.Success(new CameraAgentGalleryPage([capture], null)));
        var navigation = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo("/gallery?origin=Simulated&cursor=safe-cursor&minSequence=10");

        var cut = context.Render<GalleryPage>();

        cut.WaitForAssertion(() =>
        {
            var href = cut.Find(".capture-image").GetAttribute("href");
            Assert.IsNotNull(href);
            StringAssert.Contains(href, "returnUrl=", StringComparison.Ordinal);
            StringAssert.Contains(Uri.UnescapeDataString(href), "/gallery?origin=Simulated&cursor=safe-cursor&minSequence=10", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll("main"));
        });
    }

    [TestMethod]
    public async Task ParameterChange_CancelsPriorGenerationAndIgnoresLateResultAsync()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var first = new TaskCompletionSource<OperatorUiResult<CameraAgentGalleryPage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.GalleryHandler = (query, token) =>
        {
            if (query.Cursor is null)
            {
                token.Register(() => firstCancelled.TrySetResult());
                return new ValueTask<OperatorUiResult<CameraAgentGalleryPage>>(first.Task);
            }
            var next = OperatorUiTestData.Capture() with { CaptureSequence = 99 };
            return ValueTask.FromResult(OperatorUiResult<CameraAgentGalleryPage>.Success(new CameraAgentGalleryPage([next], null)));
        };

        var cut = context.Render<GalleryPage>();
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/gallery?cursor=next");
        await firstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Capture #99", StringComparison.Ordinal));

        first.SetResult(OperatorUiResult<CameraAgentGalleryPage>.Success(new CameraAgentGalleryPage([
            OperatorUiTestData.Capture() with { CaptureSequence = 1 }
        ], null)));
        await Task.Delay(20).ConfigureAwait(false);
        Assert.IsFalse(cut.Markup.Contains("Capture #1<", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FailedPipelineAndSingleFrameProductAreLabelledHonestly()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        // A calibrated-only capture has no multi-source lineage and must not be labelled a causal mean.
        var calibrated = OperatorUiTestData.Capture() with
        {
            Artifacts =
            [
                new CameraAgentGalleryArtifact(
                    Guid.Parse("00000000-0000-0000-0000-000000000301"),
                    HVO.SkyMonitor.AgentCore.FrameArtifactRole.Calibrated,
                    "calibrated", null, OperatorUiTestData.Now, "image/jpeg", new string('K', 64), 1024, null, [], "cal-node"),
                new CameraAgentGalleryArtifact(
                    Guid.Parse("00000000-0000-0000-0000-000000000302"),
                    HVO.SkyMonitor.AgentCore.FrameArtifactRole.Preview,
                    "preview", null, OperatorUiTestData.Now, "application/x-hvo-packed-image", new string('L', 64), 1024, null, [], "preview-node",
                    PixelFormat: HVO.SkyMonitor.AgentCore.CameraPixelFormat.Mono16, PreviewReconstructionSupported: true)
            ],
            ProcessingNodes =
            [
                new CameraAgentGalleryProcessingNode("cal", true, "TerminalFailure", null, HVO.SkyMonitor.AgentCore.FrameArtifactRole.Calibrated, null, [])
            ]
        };
        service.GalleryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryPage>.Success(new([calibrated], null)));

        var cut = context.Render<GalleryPage>();

        cut.WaitForAssertion(() =>
        {
            var card = cut.Find(".capture-card");
            StringAssert.Contains(card.TextContent, "Processed single frame", StringComparison.Ordinal);
            Assert.IsFalse(card.TextContent.Contains("causal mean", StringComparison.OrdinalIgnoreCase));
            StringAssert.Contains(card.TextContent, "Reference #42 only", StringComparison.Ordinal);
            StringAssert.Contains(card.TextContent, "Failed", StringComparison.Ordinal);
            Assert.HasCount(1, cut.FindAll(".status-icon.failure"));
        });
    }

    [TestMethod]
    public void CombinedArtifactDoesNotMakeASingleFrameDisplayCausal()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        // A multi-source Combined artifact exists, but the displayed Preview is a single frame with no lineage to
        // it. The card must not inherit a causal label from the mere presence of the Combined artifact.
        var capture = OperatorUiTestData.Capture() with
        {
            Artifacts =
            [
                new CameraAgentGalleryArtifact(
                    Guid.Parse("00000000-0000-0000-0000-000000000401"),
                    HVO.SkyMonitor.AgentCore.FrameArtifactRole.Combined, "combined", "stack", OperatorUiTestData.Now,
                    "application/x-hvo-packed-image", new string('M', 64), 1024, null,
                    [Guid.Parse("00000000-0000-0000-0000-000000000402"), Guid.Parse("00000000-0000-0000-0000-000000000403")],
                    "comb-node"),
                new CameraAgentGalleryArtifact(
                    Guid.Parse("00000000-0000-0000-0000-000000000404"),
                    HVO.SkyMonitor.AgentCore.FrameArtifactRole.Preview, "preview", null, OperatorUiTestData.Now,
                    "application/x-hvo-packed-image", new string('N', 64), 1024, null, [], "preview-node",
                    PixelFormat: HVO.SkyMonitor.AgentCore.CameraPixelFormat.Mono16, PreviewReconstructionSupported: true)
            ]
        };
        service.GalleryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryPage>.Success(new([capture], null)));

        var cut = context.Render<GalleryPage>();

        cut.WaitForAssertion(() =>
        {
            var card = cut.Find(".capture-card");
            StringAssert.Contains(card.TextContent, "Processed single frame", StringComparison.Ordinal);
            Assert.IsFalse(card.TextContent.Contains("causal mean", StringComparison.OrdinalIgnoreCase));
            // The single-frame display keeps the reference-only source and does not sum integration across frames.
            StringAssert.Contains(card.TextContent, "Reference #42 only", StringComparison.Ordinal);
            StringAssert.Contains(card.TextContent, "Integration1 s", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void AnnotatedLayerDependenciesDoNotCountAsSourceFrames()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var combinedId = Guid.Parse("00000000-0000-0000-0000-000000000601");
        var previewId = Guid.Parse("00000000-0000-0000-0000-000000000602");
        var annotatedId = Guid.Parse("00000000-0000-0000-0000-000000000603");
        var thumbnailId = Guid.Parse("00000000-0000-0000-0000-000000000604");
        var frameIds = Enumerable.Range(610, 5)
            .Select(value => Guid.Parse($"00000000-0000-0000-0000-{value:000000000000}"))
            .ToArray();
        var layerIds = Enumerable.Range(620, 6)
            .Select(value => Guid.Parse($"00000000-0000-0000-0000-{value:000000000000}"))
            .ToArray();
        var capture = OperatorUiTestData.Capture() with
        {
            Artifacts =
            [
                new CameraAgentGalleryArtifact(combinedId, HVO.SkyMonitor.AgentCore.FrameArtifactRole.Combined,
                    "combined", "rolling-mean", OperatorUiTestData.Now, "application/x-hvo-packed-image",
                    new string('A', 64), 1024, null, frameIds, "combine",
                    PixelFormat: HVO.SkyMonitor.AgentCore.CameraPixelFormat.Mono16, PreviewReconstructionSupported: true),
                new CameraAgentGalleryArtifact(previewId, HVO.SkyMonitor.AgentCore.FrameArtifactRole.Preview,
                    "preview", "combined-preview", OperatorUiTestData.Now, "image/jpeg",
                    new string('B', 64), 1024, null, [combinedId], "preview"),
                new CameraAgentGalleryArtifact(annotatedId, HVO.SkyMonitor.AgentCore.FrameArtifactRole.AnnotatedPreview,
                    "annotated", "installer-annotated-preview", OperatorUiTestData.Now, "application/x-hvo-packed-image",
                    new string('C', 64), 1024, null, [previewId, .. layerIds], "annotate",
                    PixelFormat: HVO.SkyMonitor.AgentCore.CameraPixelFormat.Mono16, PreviewReconstructionSupported: true),
                new CameraAgentGalleryArtifact(thumbnailId, HVO.SkyMonitor.AgentCore.FrameArtifactRole.AnnotatedPreview,
                    "thumbnail", "annotated-thumbnail-1024-jpeg", OperatorUiTestData.Now, "image/jpeg",
                    new string('E', 64), 1024, null, [annotatedId], "thumbnail", EncodedWidth: 640, EncodedHeight: 480),
                .. layerIds.Select(id => new CameraAgentGalleryArtifact(id, HVO.SkyMonitor.AgentCore.FrameArtifactRole.Metadata,
                    "layer", "layer", OperatorUiTestData.Now, "application/json", new string('D', 64), 128, null, [], "layer"))
            ]
        };
        service.GalleryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryPage>.Success(new([capture], null)));

        var cut = context.Render<GalleryPage>();
        cut.WaitForAssertion(() =>
        {
            var card = cut.Find(".capture-card");
            StringAssert.Contains(card.QuerySelector(".capture-card-heading")!.TextContent, "Processed presentation", StringComparison.Ordinal);
            StringAssert.Contains(card.TextContent, "5 frames / endpoint #42", StringComparison.Ordinal);
            StringAssert.Contains(card.TextContent, "IntegrationNot recorded", StringComparison.Ordinal);
            Assert.IsFalse(card.TextContent.Contains("7 source frames", StringComparison.Ordinal));
        });
        var thumbnailPresentation = new CameraAgentCapturePresentation(CameraAgentPresentationStage.Annotated,
        [
            new CameraAgentPresentationSlot(CameraAgentPresentationStage.Annotated, "Processed",
                CameraAgentPresentationSlotAvailability.Available, "Available", thumbnailId,
                HVO.SkyMonitor.AgentCore.FrameArtifactRole.AnnotatedPreview, "annotated-thumbnail-1024-jpeg", "image/jpeg",
                new Uri($"/api/v1/operations/artifacts/{thumbnailId:D}/preview", UriKind.Relative))
        ]);
        Assert.AreEqual(5, ArchiveCardFacts.ProvenSourceCount(capture, thumbnailPresentation));
    }

    [TestMethod]
    public void ProvenCausalMeanIsLabelledWithoutAssumingEqualSourceExposures()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var combined = OperatorUiTestData.Capture() with
        {
            Artifacts =
            [
                new CameraAgentGalleryArtifact(
                    Guid.Parse("00000000-0000-0000-0000-000000000501"),
                    HVO.SkyMonitor.AgentCore.FrameArtifactRole.Combined, "combined", "stack", OperatorUiTestData.Now,
                    "application/x-hvo-packed-image", new string('O', 64), 1024, null,
                    [
                        Guid.Parse("00000000-0000-0000-0000-000000000502"),
                        Guid.Parse("00000000-0000-0000-0000-000000000503"),
                        Guid.Parse("00000000-0000-0000-0000-000000000504")
                    ],
                    "comb-node",
                    PixelFormat: HVO.SkyMonitor.AgentCore.CameraPixelFormat.Mono16, PreviewReconstructionSupported: true)
            ],
            ProcessingNodes =
            [
                new CameraAgentGalleryProcessingNode("comb", true, "Completed", null, HVO.SkyMonitor.AgentCore.FrameArtifactRole.Combined, null, [])
            ]
        };
        service.GalleryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryPage>.Success(new([combined], null)));

        var cut = context.Render<GalleryPage>();

        cut.WaitForAssertion(() =>
        {
            var card = cut.Find(".capture-card");
            StringAssert.Contains(card.TextContent, "Unregistered live mean", StringComparison.Ordinal);
            StringAssert.Contains(card.TextContent, "3 frames / endpoint #42", StringComparison.Ordinal);
            StringAssert.Contains(card.TextContent, "no geometric registration", StringComparison.Ordinal);
            StringAssert.Contains(card.TextContent, "Succeeded", StringComparison.Ordinal);
            // Lineage does not establish the exposure of every source.
            StringAssert.Contains(card.TextContent, "IntegrationNot recorded", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void SearchMatchesTheProductLabelShownOnTheCard()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GalleryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryPage>.Success(new([OperatorUiTestData.Capture()], null)));
        var navigation = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo("/gallery?q=Processed%20single%20frame");

        var cut = context.Render<GalleryPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.HasCount(1, cut.FindAll(".capture-card"));
            StringAssert.Contains(cut.Find(".capture-card-heading").TextContent, "Processed single frame", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void ImageContainsNoHtmlOverlayAndActionsAreConsistentLinks()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GalleryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryPage>.Success(new([OperatorUiTestData.Capture()], null)));
        var cut = context.Render<GalleryPage>();
        cut.WaitForElement(".capture-image img");

        var card = cut.Find(".capture-card");
        Assert.IsEmpty(card.QuerySelectorAll(".capture-image span, .capture-image button, .capture-image-overlay, .capture-badges"));
        StringAssert.Contains(card.QuerySelector(".capture-card-heading")!.TextContent, "Capture #42", StringComparison.Ordinal);
        StringAssert.Contains(card.QuerySelector(".capture-card-heading")!.TextContent, "Processed single frame", StringComparison.Ordinal);
        Assert.HasCount(1, card.QuerySelectorAll(".capture-card-actions a"));
        Assert.AreEqual("Open image", card.QuerySelector(".capture-card-actions a")!.TextContent.Trim());
        var unavailable = card.QuerySelector(".capture-card-actions [aria-disabled='true']");
        Assert.IsNotNull(unavailable);
        StringAssert.Contains(unavailable.GetAttribute("aria-label"), "not resolved", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CardsLinkOnlyTheirExactLiveRunAndRetainedCandidates()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var capture = OperatorUiTestData.Capture();
        var executionId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        service.GalleryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryPage>.Success(new([capture], null)));
        var cards = new Mock<ICameraAgentArchiveCardUiService>(MockBehavior.Strict);
        cards.Setup(card => card.GetLinksAsync(capture.CaptureId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CameraAgentArchiveCardLinks(executionId, string.Empty, [candidateId], true));
        context.Services.AddSingleton(cards.Object);

        var cut = context.Render<GalleryPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual($"/operations/pipeline/executions/{executionId:D}",
                cut.Find(".capture-card-actions .run-link").GetAttribute("href"));
            Assert.AreEqual($"/transients/{candidateId:D}",
                cut.Find(".capture-card-event a").GetAttribute("href"));
            StringAssert.Contains(cut.Find(".gallery-heading-facts").TextContent, "1 linked local candidates", StringComparison.Ordinal);
            Assert.IsFalse(cut.Markup.Contains("Owner confirmed", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void MissingLinkEvidenceLeavesCaptureVisibleWithoutInventingZeroCandidates()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GalleryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryPage>.Success(new([OperatorUiTestData.Capture()], null)));

        var cut = context.Render<GalleryPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.HasCount(1, cut.FindAll(".capture-card"));
            Assert.HasCount(0, cut.FindAll(".capture-card-event a"));
            Assert.AreEqual("true", cut.Find(".capture-card-actions .run-link").GetAttribute("aria-disabled"));
            StringAssert.Contains(cut.Find(".gallery-heading-facts").TextContent, "Unavailable", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void IntegrationUsesTheExactRetainedOutputTotalInsteadOfEndpointExposure()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var capture = OperatorUiTestData.Capture();
        var artifact = ArchiveCardFacts.DisplayArtifact(capture, service.Project(capture))!;
        var product = CardProduct(capture, artifact, TimeSpan.FromSeconds(7.5));
        service.GalleryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryPage>.Success(new([capture], null)));
        service.ProductDetailHandler = (id, _) => ValueTask.FromResult(
            id == artifact.ArtifactId
                ? OperatorUiResult<CameraAgentProductDetail>.Success(new(product, capture.ExposureStartedUtc,
                    ObservingDayCalendar.Utc.Resolve(capture.ExposureStartedUtc), capture.RigId, [], false, null, []))
                : OperatorUiResult<CameraAgentProductDetail>.Failure(OperatorUiResultKind.NotFound, "The product was not found."));

        var cut = context.Render<GalleryPage>();

        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".capture-card-facts").TextContent,
            "Integration7.5 s", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IntegrationRejectsAProductThatBelongsToAnotherCapture()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var capture = OperatorUiTestData.Capture();
        var artifact = ArchiveCardFacts.DisplayArtifact(capture, service.Project(capture))!;
        var product = CardProduct(capture, artifact, TimeSpan.FromSeconds(7.5)) with { CaptureId = Guid.NewGuid() };
        service.GalleryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryPage>.Success(new([capture], null)));
        service.ProductDetailHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentProductDetail>.Success(new(product, capture.ExposureStartedUtc,
                ObservingDayCalendar.Utc.Resolve(capture.ExposureStartedUtc), capture.RigId, [], false, null, [])));

        var cut = context.Render<GalleryPage>();

        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find(".capture-card-facts").TextContent.Contains("7.5", StringComparison.Ordinal)));
    }

    private static CameraAgentProduct CardProduct(CameraAgentGalleryCapture capture, CameraAgentGalleryArtifact artifact, TimeSpan integration)
        => new(artifact.ArtifactId, new string('A', 64), capture.CaptureId, capture.CaptureSequence, capture.AgentId,
            "preview", artifact.Role, artifact.Variant, OperatorUiTestData.Now, OperatorUiTestData.Now, artifact.MediaType,
            artifact.ChecksumSha256, artifact.ByteLength, new("preview", "1", "1", new string('B', 64), new string('C', 64)),
            [], null, null, null, "Available", null, integration, 3, 640, 480, "Live", false);

    [TestMethod]
    [DataRow("run")]
    [DataRow("candidate")]
    [DataRow("product")]
    public void AuthorizationRevokedDuringCardReadsWithholdsTheLoadedPage(string deniedRead)
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var capture = OperatorUiTestData.Capture();
        service.GalleryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryPage>.Success(new([capture], null)));
        service.ProductDetailHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentProductDetail>.Failure(
                deniedRead == "product" ? OperatorUiResultKind.Unauthorized : OperatorUiResultKind.Unavailable, "Unavailable"));
        var runs = new Mock<ICameraAgentProcessingGraphUiService>();
        runs.Setup(run => run.GetLiveExecutionIdAsync(capture.CaptureId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deniedRead == "run"
                ? OperatorUiResult<CameraAgentLiveRunLink>.Failure(OperatorUiResultKind.Unauthorized, "Denied")
                : OperatorUiResult<CameraAgentLiveRunLink>.Success(new(Guid.NewGuid())));
        var transients = new Mock<ICameraAgentTransientUiService>();
        transients.Setup(transient => transient.GetCaptureStagesAsync(capture.CaptureId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deniedRead == "candidate"
                ? OperatorUiResult<TransientCaptureStageView>.Failure(OperatorUiResultKind.Unauthorized, "Denied")
                : OperatorUiResult<TransientCaptureStageView>.Success(new(capture.CaptureId, [])));
        context.Services.AddSingleton<ICameraAgentArchiveCardUiService>(new CameraAgentArchiveCardUiService(runs.Object, transients.Object));

        var cut = context.Render<GalleryPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri
                .EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
            Assert.HasCount(0, cut.FindAll(".capture-card"));
        });
    }

    [TestMethod]
    public void SiteLocalFilters_PreserveUtcQueryAndConvertBothBoundaries()
    {
        using var context = new BunitContext();
        Configure(context);
        context.Services.AddSingleton<IObservingDayCalendarProvider>(new FixedObservingDayCalendarProvider(
            ObservingDayCalendar.Create("Asia/Kolkata")));
        var navigation = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo("/gallery?from=2026-07-23T20%3A00%3A00Z");
        var cut = context.Render<GalleryPage>();
        cut.WaitForAssertion(() => Assert.AreEqual("2026-07-24T01:30", cut.Find("#galleryFrom").GetAttribute("value")));
        cut.Find("#galleryFrom").Change("2026-07-24T02:30:00");
        cut.Find("#galleryTo").Change("2026-07-24T04:30:00");
        cut.Find("form.gallery-toolbar").Submit();
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query);
        Assert.AreEqual("2026-07-23T21:00:00.0000000+00:00", query["from"].ToString());
        Assert.AreEqual("2026-07-23T23:00:00.0000000+00:00", query["to"].ToString());
    }

    [TestMethod]
    public void UnchangedFoldFilter_PreservesUtcOccurrenceAndFractionalSeconds()
    {
        using var context = new BunitContext();
        Configure(context);
        context.Services.AddSingleton<IObservingDayCalendarProvider>(new FixedObservingDayCalendarProvider(
            ObservingDayCalendar.Create("America/New_York")));
        var recorded = "2026-11-01T06:30:00.1234567+00:00";
        var navigation = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo(navigation.GetUriWithQueryParameter("from", recorded));
        var cut = context.Render<GalleryPage>();
        Assert.AreEqual("2026-11-01T01:30:00.123", cut.Find("#galleryFrom").GetAttribute("value"));
        cut.Find("form.gallery-toolbar").Submit();
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query);
        Assert.AreEqual(recorded, query["from"].ToString());
    }

    [TestMethod]
    [DataRow("2026-03-08T02:30:00", "does not exist")]
    [DataRow("2026-11-01T01:30:00", "occurs twice")]
    public void SiteLocalFilter_RejectsDstGapAndFold(string entered, string reason)
    {
        using var context = new BunitContext();
        Configure(context);
        context.Services.AddSingleton<IObservingDayCalendarProvider>(new FixedObservingDayCalendarProvider(
            ObservingDayCalendar.Create("America/New_York")));
        var navigation = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var before = navigation.Uri;
        var cut = context.Render<GalleryPage>();
        cut.Find("#galleryFrom").Change(entered);
        cut.Find("form.gallery-toolbar").Submit();
        Assert.AreEqual(before, navigation.Uri);
        StringAssert.Contains(cut.Markup, reason, StringComparison.Ordinal);
    }

    private static TestOperatorUiService Configure(BunitContext context)
    {
        RetainedPreviewImageTestSupport.Configure(context);
        var service = new TestOperatorUiService();
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);
        context.Services.AddSingleton<ICameraAgentCapturePresentationProjector>(service);
        var cards = new Moq.Mock<ICameraAgentArchiveCardUiService>();
        cards.Setup(card => card.GetLinksAsync(Moq.It.IsAny<Guid>(), Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CameraAgentArchiveCardLinks(null, "The pipeline run identity is not resolved for this card.", [], false));
        context.Services.AddSingleton(cards.Object);
        context.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(OperatorUiTestData.Now));
        return service;
    }
}
