using Bunit;
using Bunit.JSInterop;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class GalleryDetailTests
{
    [TestMethod]
    public void DetailLeadsWithExactStageSummaryAndLargeViewer()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var capture = OperatorUiTestData.Capture();
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(new TestOperatorUiService
        {
            DetailHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentGalleryCapture>.Success(capture))
        });

        var cut = context.Render<GalleryDetail>(parameters => parameters.Add(page => page.CaptureId, capture.CaptureId));
        cut.WaitForElement(".detail-capture-image img");

        Assert.AreEqual(
            "/api/v1/operations/artifacts/00000000-0000-0000-0000-000000000102/preview",
            cut.Find(".detail-capture-image img").GetAttribute("src"));
        StringAssert.Contains(cut.Find(".capture-summary").TextContent, "Processed", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".capture-summary").TextContent, "1 sec", StringComparison.Ordinal);
        Assert.IsFalse(cut.Find(".technical-evidence").HasAttribute("open"));

        cut.FindAll(".stage-selector__button").Single(button => button.TextContent.Contains("Raw", StringComparison.Ordinal)).Click();

        Assert.AreEqual(
            "/api/v1/operations/artifacts/00000000-0000-0000-0000-000000000101/preview",
            cut.Find(".detail-capture-image img").GetAttribute("src"));
        StringAssert.Contains(cut.Find(".capture-summary").TextContent, "Raw", StringComparison.Ordinal);
        cut.Find("#capture-detail-view-large").Click();
        cut.WaitForAssertion(() => Assert.IsTrue(context.JSInterop.Invocations.Any(static invocation =>
            invocation.Identifier.EndsWith("show", StringComparison.Ordinal))));
        var viewer = cut.FindComponent<HVO.SkyMonitor.CameraAgent.Components.Presentation.LargeImageViewer>();
        Assert.AreEqual(cut.Find(".detail-capture-image img").GetAttribute("src"), viewer.Find("img").GetAttribute("src"));
        viewer.FindAll(".large-viewer__modes button")[1].Click();
        Assert.HasCount(1, viewer.FindAll(".large-viewer__canvas--native"));

        cut.Find(".detail-capture-image img").TriggerEvent("onerror", EventArgs.Empty);
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "retained preview could not be loaded", StringComparison.Ordinal));
        StringAssert.Contains(cut.Markup, "Download content", StringComparison.Ordinal);
    }

    [TestMethod]
    public void DetailNavigationStaysWithinOriginatingBoundedArchivePage()
    {
        using var context = new BunitContext();
        var current = OperatorUiTestData.Capture();
        var newer = OperatorUiTestData.Capture(Guid.Parse("00000000-0000-0000-0000-000000000011")) with { CaptureSequence = 43 };
        var older = OperatorUiTestData.Capture(Guid.Parse("00000000-0000-0000-0000-000000000012")) with { CaptureSequence = 41 };
        CameraAgentGalleryQuery? observedQuery = null;
        var service = new TestOperatorUiService
        {
            DetailHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentGalleryCapture>.Success(current)),
            GalleryHandler = (query, _) =>
            {
                observedQuery = query;
                return ValueTask.FromResult(OperatorUiResult<CameraAgentGalleryPage>.Success(new([newer, current, older], null)));
            }
        };
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);

        var returnUrl = "/gallery?origin=Simulated&pageSize=3&cursor=bounded-cursor";
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
            $"/gallery/{current.CaptureId:D}",
            "returnUrl",
            returnUrl));
        var cut = context.Render<GalleryDetail>(parameters => parameters.Add(page => page.CaptureId, current.CaptureId));

        cut.WaitForAssertion(() => Assert.HasCount(2, cut.FindAll(".capture-navigation__control[href]")));
        Assert.AreEqual(GalleryEvidenceOrigin.Simulated, observedQuery?.EvidenceOrigin);
        Assert.AreEqual(3, observedQuery?.PageSize);
        Assert.AreEqual("bounded-cursor", observedQuery?.Cursor);
        var links = cut.FindAll(".capture-navigation__control[href]");
        StringAssert.Contains(links[0].GetAttribute("href"), older.CaptureId.ToString(), StringComparison.Ordinal);
        StringAssert.Contains(links[1].GetAttribute("href"), newer.CaptureId.ToString(), StringComparison.Ordinal);
        Assert.IsTrue(links.All(link => link.GetAttribute("href")?.Contains("returnUrl=", StringComparison.Ordinal) == true));

        service.GalleryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryPage>.Success(new([current, older], "older-page")));
        var boundary = context.Render<GalleryDetail>(parameters => parameters.Add(page => page.CaptureId, current.CaptureId));
        boundary.WaitForAssertion(() => Assert.HasCount(1, boundary.FindAll(".capture-navigation__control[href]")));
        Assert.HasCount(1, boundary.FindAll(".capture-navigation__control--disabled"));
    }

    [TestMethod]
    public void CaptureDetailPresentsOneProcessedStageWithItsExactPreviewBackingRole()
    {
        var previewId = Guid.Parse("00000000-0000-0000-0000-000000000221");
        var durableProjection = new CameraAgentCapturePresentation(
            CameraAgentPresentationStage.Preview,
            [
                new(CameraAgentPresentationStage.Raw, "Raw", CameraAgentPresentationSlotAvailability.Missing, "NotProduced"),
                new(CameraAgentPresentationStage.Calibrated, "Calibrated", CameraAgentPresentationSlotAvailability.Missing, "NotProduced"),
                new(CameraAgentPresentationStage.Combined, "Combined", CameraAgentPresentationSlotAvailability.Missing, "NotProduced"),
                new(CameraAgentPresentationStage.Preview, "Preview", CameraAgentPresentationSlotAvailability.Available,
                    "Available", previewId, HVO.SkyMonitor.AgentCore.FrameArtifactRole.Preview, "display", "image/jpeg",
                    new Uri($"/api/v1/operations/artifacts/{previewId:D}/preview", UriKind.Relative)),
                new(CameraAgentPresentationStage.Annotated, "Processed", CameraAgentPresentationSlotAvailability.Missing, "NotProduced")
            ]);

        var detailProjection = CameraAgentOperatorUiService.ProjectCaptureDetailPresentation(durableProjection);

        Assert.HasCount(4, detailProjection.Stages);
        Assert.IsFalse(detailProjection.Stages.Any(static slot => slot.Stage == CameraAgentPresentationStage.Preview));
        var processed = detailProjection.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Annotated);
        Assert.AreEqual("Processed", processed.Label);
        Assert.AreEqual(HVO.SkyMonitor.AgentCore.FrameArtifactRole.Preview, processed.ArtifactRole);
        Assert.AreEqual(previewId, processed.ArtifactId);
        Assert.AreEqual(CameraAgentPresentationStage.Annotated, detailProjection.SelectedStage);
    }

    [TestMethod]
    public void CaptureDetailPreservesUnavailableAnnotatedEvidenceWhenProcessedFallbackIsMissing()
    {
        var durableProjection = new CameraAgentCapturePresentation(
            null,
            [
                new(CameraAgentPresentationStage.Raw, "Raw", CameraAgentPresentationSlotAvailability.Missing, "NotProduced"),
                new(CameraAgentPresentationStage.Calibrated, "Calibrated", CameraAgentPresentationSlotAvailability.Missing, "NotProduced"),
                new(CameraAgentPresentationStage.Combined, "Combined", CameraAgentPresentationSlotAvailability.Missing, "NotProduced"),
                new(CameraAgentPresentationStage.Preview, "Preview", CameraAgentPresentationSlotAvailability.Missing, "NotProduced"),
                new(CameraAgentPresentationStage.Annotated, "Processed", CameraAgentPresentationSlotAvailability.Unavailable, "ArtifactInvalid")
            ]);

        var detailProjection = CameraAgentOperatorUiService.ProjectCaptureDetailPresentation(durableProjection);

        var processed = detailProjection.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Annotated);
        Assert.AreEqual(CameraAgentPresentationSlotAvailability.Unavailable, processed.Availability);
        Assert.AreEqual("ArtifactInvalid", processed.Reason);
    }

    [TestMethod]
    [DataRow("ProcessingFailed")]
    [DataRow("ProcessingSkipped")]
    public void CaptureDetailPrioritizesExplicitProcessedFailureOverUnavailableAnnotatedEvidence(string reason)
    {
        var durableProjection = new CameraAgentCapturePresentation(
            null,
            [
                new(CameraAgentPresentationStage.Raw, "Raw", CameraAgentPresentationSlotAvailability.Missing, "NotProduced"),
                new(CameraAgentPresentationStage.Calibrated, "Calibrated", CameraAgentPresentationSlotAvailability.Missing, "NotProduced"),
                new(CameraAgentPresentationStage.Combined, "Combined", CameraAgentPresentationSlotAvailability.Missing, "NotProduced"),
                new(CameraAgentPresentationStage.Preview, "Preview", CameraAgentPresentationSlotAvailability.Unavailable, reason),
                new(CameraAgentPresentationStage.Annotated, "Processed", CameraAgentPresentationSlotAvailability.Unavailable, "ArtifactInvalid")
            ]);

        var detailProjection = CameraAgentOperatorUiService.ProjectCaptureDetailPresentation(durableProjection);

        var processed = detailProjection.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Annotated);
        Assert.AreEqual(CameraAgentPresentationSlotAvailability.Unavailable, processed.Availability);
        Assert.AreEqual(reason, processed.Reason);
    }

    [TestMethod]
    public void LayeredDetailRendersOneBaseOneGroupedSvgAndLocalToggleControls()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var capture = OperatorUiTestData.Capture();
        var service = new TestOperatorUiService
        {
            DetailHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentGalleryCapture>.Success(capture)),
            PresentationHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(new(
                capture.CaptureId,
                Guid.Parse("00000000-0000-0000-0000-000000000102"),
                new string('A', 64),
                new string('B', 64),
                new string('C', 64),
                640,
                480,
                [
                    new(new string('D', 64), "scene-annotation", "hvo-layer-0", 20, true, 1_000_000, "renderer-v1", "style-v1"),
                    new(new string('E', 64), "cloud-mask", "hvo-layer-1", 30, false, 500_000, "renderer-v1", "style-v1")
                ],
                System.Text.Encoding.UTF8.GetBytes(
                    "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 640 480\"><g id=\"hvo-layer-0\"></g><g id=\"hvo-layer-1\" display=\"none\"></g></svg>"))))
        };
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);

        var cut = context.Render<GalleryDetail>(parameters => parameters.Add(page => page.CaptureId, capture.CaptureId));

        cut.WaitForAssertion(() =>
        {
            Assert.HasCount(1, cut.FindAll(".layered-canvas > img"));
            Assert.HasCount(1, cut.FindAll(".layered-overlay svg"));
            Assert.HasCount(2, cut.FindAll(".layer-controls input[type='checkbox']"));
            StringAssert.Contains(cut.Markup, "toggles run locally", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "/api/v1/operations/artifacts/00000000-0000-0000-0000-000000000102/preview", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll("script"));
        });
    }

    [TestMethod]
    public async Task SaveSelectedStackUsesLocalSelectionAndReportsSuccessOrFailure()
    {
        using var context = new BunitContext();
        var capture = OperatorUiTestData.Capture();
        var selectedIdentity = new string('D', 64);
        var presentation = new CameraAgentLayeredPresentation(
            capture.CaptureId,
            Guid.Parse("00000000-0000-0000-0000-000000000102"),
            new string('A', 64),
            new string('B', 64),
            new string('C', 64),
            640,
            480,
            [new(selectedIdentity, "scene-annotation", "hvo-layer-0", 20, true, 1_000_000, "renderer-v1", "style-v1")],
            System.Text.Encoding.UTF8.GetBytes(
                "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 640 480\"><g id=\"hvo-layer-0\"></g></svg>"));
        IReadOnlyList<string>? submitted = null;
        var fail = false;
        var service = new TestOperatorUiService
        {
            DetailHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentGalleryCapture>.Success(capture)),
            PresentationHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(presentation)),
            MaterializationHandler = (captureId, selected, _) =>
            {
                submitted = selected;
                return ValueTask.FromResult(fail
                    ? OperatorUiResult<CameraAgentPresentationMaterializationReceipt>.Failure(
                        OperatorUiResultKind.Conflict, "The retained presentation conflicts with the selected stack.")
                    : OperatorUiResult<CameraAgentPresentationMaterializationReceipt>.Success(new(
                        captureId, Guid.Parse("00000000-0000-0000-0000-000000000104"), new string('F', 64),
                        new string('A', 64), 1024, false)));
            }
        };
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);
        var module = context.JSInterop.SetupModule("./Components/Pages/GalleryDetail.razor.js");
        module.SetupVoid("bindLayerToggles", _ => true);
        module.Setup<string[]>("selectedLayerIdentities", _ => true).SetResult([selectedIdentity]);
        var cut = context.Render<GalleryDetail>(parameters => parameters.Add(page => page.CaptureId, capture.CaptureId));
        cut.WaitForElement(".layer-save button");

        await cut.Find(".layer-save button").ClickAsync().ConfigureAwait(false);

        CollectionAssert.AreEqual(new[] { selectedIdentity }, submitted?.ToArray());
        cut.WaitForAssertion(() => StringAssert.Contains(
            cut.Find("[role='status']").TextContent, "new immutable artifact", StringComparison.Ordinal));

        fail = true;
        await cut.Find(".layer-save button").ClickAsync().ConfigureAwait(false);

        cut.WaitForAssertion(() => StringAssert.Contains(
            cut.Find("[role='alert']").TextContent, "conflicts with the selected stack", StringComparison.Ordinal));
        Assert.IsFalse(cut.Find(".layer-save button").HasAttribute("disabled"));
    }

    [TestMethod]
    public void Detail_RetainsExactLineageRecipesNodesAndDirectEndpointsInTechnicalDisclosure()
    {
        using var context = new BunitContext();
        var service = new TestOperatorUiService();
        var capture = OperatorUiTestData.Capture();
        service.DetailHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<HVO.SkyMonitor.CameraAgent.Common.Gallery.CameraAgentGalleryCapture>.Success(capture));
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);

        var cut = context.Render<GalleryDetail>(parameters => parameters.Add(page => page.CaptureId, capture.CaptureId));

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, capture.CaptureId.ToString(), StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Source artifact IDs", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Identity SHA-256", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "preview-node", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Manifest schema", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Raw retention hold", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Delivery availability", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Dependencies", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Cloud assessment", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Mask SHA-256", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Artifact comparison", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Bounded same-capture view", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Node outcome", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "selected=yes", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "/preview", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "/content", StringComparison.Ordinal);
            Assert.HasCount(2, cut.FindAll("a[href$='/preview']"));
            Assert.HasCount(2, cut.FindAll(".comparison-grid img"));
            Assert.HasCount(2, cut.FindAll(".comparison-selectors select"));
            Assert.HasCount(2, cut.FindAll("a[href$='/content']"));
            Assert.IsFalse(cut.Find(".technical-evidence").HasAttribute("open"));
            Assert.IsFalse(cut.Markup.Contains("/tmp/", StringComparison.Ordinal));
            Assert.IsFalse(cut.Markup.Contains("optionsJson", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(cut.Markup.Contains("secret", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(cut.Markup.Contains("sensitive processing failure", StringComparison.OrdinalIgnoreCase));
            Assert.IsEmpty(cut.FindAll("main"));
        });
        var selectors = cut.FindAll(".comparison-selectors select");
        var originalLeft = selectors[0].GetAttribute("value");
        selectors[0].Change(selectors[1].GetAttribute("value"));
        Assert.AreEqual(originalLeft, cut.FindAll(".comparison-selectors select")[0].GetAttribute("value"));

        var unsupportedPreferred = capture.Artifacts[1] with { MediaType = "image/jpeg" };
        var supportedCalibrated = capture.Artifacts[0] with
        {
            ArtifactId = Guid.Parse("00000000-0000-0000-0000-000000000103"),
            Role = HVO.SkyMonitor.AgentCore.FrameArtifactRole.Calibrated,
            MediaType = "application/x-hvo-packed-image"
        };
        service.DetailHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryCapture>.Success(capture with
            {
                Artifacts = [capture.Artifacts[0], unsupportedPreferred, supportedCalibrated]
            }));
        var fallback = context.Render<GalleryDetail>(parameters => parameters.Add(page => page.CaptureId, capture.CaptureId));
        fallback.WaitForAssertion(() => Assert.HasCount(2, fallback.FindAll(".comparison-grid article")));
    }

    [TestMethod]
    public void ReturnUrl_AllowsGalleryQueryAndRejectsExternalOrNonGalleryTargets()
    {
        using var context = new BunitContext();
        var service = new TestOperatorUiService();
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);
        var capture = OperatorUiTestData.Capture();
        var navigation = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

        navigation.NavigateTo(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
            "/gallery/capture",
            "returnUrl",
            "/gallery?origin=Simulated&cursor=abc"));
        var local = context.Render<GalleryDetail>(parameters => parameters.Add(page => page.CaptureId, capture.CaptureId));
        local.WaitForAssertion(() => Assert.AreEqual(
            "/gallery?origin=Simulated&cursor=abc",
            local.Find(".breadcrumb a").GetAttribute("href")));

        navigation.NavigateTo(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
            "/gallery/capture",
            "returnUrl",
            "https://attacker.example/gallery"));
        var rejected = context.Render<GalleryDetail>(parameters => parameters.Add(page => page.CaptureId, capture.CaptureId));
        rejected.WaitForAssertion(() => Assert.AreEqual("/gallery", rejected.Find(".breadcrumb a").GetAttribute("href")));
    }

    [TestMethod]
    public void MissingExtendedDetail_RendersExplicitUnavailableStates()
    {
        using var context = new BunitContext();
        var capture = OperatorUiTestData.Capture() with { Detail = null };
        var service = new TestOperatorUiService
        {
            DetailHandler = (_, _) => ValueTask.FromResult(
                OperatorUiResult<HVO.SkyMonitor.CameraAgent.Common.Gallery.CameraAgentGalleryCapture>.Success(capture))
        };
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);

        var cut = context.Render<GalleryDetail>(parameters => parameters.Add(page => page.CaptureId, capture.CaptureId));

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "Extended capture evidence is unavailable", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Cloud assessment is unavailable", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void DetailWithoutDisplayableArtifact_RendersUnsupportedTextAndTechnicalDownload()
    {
        using var context = new BunitContext();
        var service = new TestOperatorUiService();
        var capture = OperatorUiTestData.Capture() with
        {
            Artifacts = [OperatorUiTestData.Capture().Artifacts[0] with
            {
                MediaType = "application/octet-stream",
                PreviewReconstructionSupported = false
            }]
        };
        service.DetailHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<HVO.SkyMonitor.CameraAgent.Common.Gallery.CameraAgentGalleryCapture>.Success(capture));
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);

        var cut = context.Render<GalleryDetail>(parameters => parameters.Add(page => page.CaptureId, capture.CaptureId));

        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "This capture cannot be displayed", StringComparison.Ordinal));
        StringAssert.Contains(cut.Markup, "Download content", StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, "Two reconstructable artifacts", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CurrentEvidenceDistinguishesInfrastructureAndUnexecutedNodesFromLegacyRecords()
    {
        using var context = new BunitContext();
        var baseline = OperatorUiTestData.Capture();
        var completedUtc = OperatorUiTestData.Now;
        var detail = baseline.Detail! with
        {
            ProcessingNodes =
            [
                new CameraAgentGalleryProcessingNodeDetail(
                    "infrastructure", [], 1, completedUtc, null, new string('A', 64),
                    completedUtc.AddMilliseconds(-2), 2, null, []),
                new CameraAgentGalleryProcessingNodeDetail(
                    "blocked", ["source"], 1, completedUtc, "dependency-unavailable", new string('A', 64),
                    null, null, "Skipped", [])
            ]
        };
        var capture = baseline with
        {
            ProcessingNodes =
            [
                new CameraAgentGalleryProcessingNode("infrastructure", true, "Completed", null, null, null, []),
                new CameraAgentGalleryProcessingNode("blocked", false, "Skipped", "preview", null, null, [])
            ],
            Detail = detail
        };
        var service = new TestOperatorUiService
        {
            DetailHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentGalleryCapture>.Success(capture))
        };
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);

        var cut = context.Render<GalleryDetail>(parameters => parameters.Add(page => page.CaptureId, capture.CaptureId));

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "Not applicable (infrastructure node)", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Not executed", StringComparison.Ordinal);
            Assert.IsFalse(cut.Markup.Contains("Unavailable for legacy processing record", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public async Task DisposalCancelsPendingCaptureRead()
    {
        using var context = new BunitContext();
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(new TestOperatorUiService
        {
            DetailHandler = async (_, token) =>
            {
                using var registration = token.Register(() => cancellationObserved.TrySetResult());
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                return OperatorUiResult<CameraAgentGalleryCapture>.Failure(OperatorUiResultKind.Unavailable, "unreachable");
            }
        });
        var cut = context.Render<GalleryDetail>(parameters => parameters.Add(page => page.CaptureId, Guid.NewGuid()));
        cut.WaitForElement(".detail-state[role='status']");

        await cut.Instance.DisposeAsync().ConfigureAwait(false);

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }
}
