using Bunit;
using Bunit.JSInterop;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class GalleryDetailTests
{
    private static void ConfigureGraphService(BunitContext context)
    {
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(new ProcessingExecutionPagesTests.GraphUiService());
        context.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(OperatorUiTestData.Now));
    }

    private static CameraAgentCapturePresentation LayeredStages(CameraAgentGalleryCapture capture)
    {
        var stages = new TestOperatorUiService().Project(capture);
        return stages with
        {
            Stages = stages.Stages.Select(slot => slot.Stage == CameraAgentPresentationStage.Combined
                ? slot with
                {
                    Availability = CameraAgentPresentationSlotAvailability.Available,
                    ArtifactId = Guid.Parse("00000000-0000-0000-0000-000000000103"),
                    DisplayArtifactId = Guid.Parse("00000000-0000-0000-0000-000000000102"),
                    DisplayBasis = CameraAgentPresentationDisplayBasis.RetainedDerivative,
                    PreviewUrl = new Uri("/api/v1/operations/artifacts/00000000-0000-0000-0000-000000000102/preview", UriKind.Relative)
                } : slot).ToArray()
        };
    }
    [TestMethod]
    public void DetailReusesDashboardWithExactStageFactsAndFullscreen()
    {
        using var context = new BunitContext();
        ConfigureGraphService(context);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var capture = OperatorUiTestData.Capture();
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(new TestOperatorUiService
        {
            DetailHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentGalleryCapture>.Success(capture))
        });

        var cut = context.Render<GalleryDetail>(parameters => parameters.Add(page => page.CaptureId, capture.CaptureId));
        cut.WaitForElement(".sky-image-stage img");

        Assert.AreEqual(
            "/api/v1/operations/artifacts/00000000-0000-0000-0000-000000000102/preview",
            cut.Find(".sky-image-stage img").GetAttribute("src"));
        StringAssert.Contains(cut.Find(".current-sky-summary").TextContent, "Processed", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".current-sky-summary").TextContent, "1 s", StringComparison.Ordinal);
        Assert.IsFalse(cut.Find(".technical-evidence").HasAttribute("open"));
        Assert.AreEqual("Capture #42", cut.Find("h1").TextContent);
        Assert.HasCount(1, cut.FindComponents<CurrentSkyPage>());
        Assert.IsEmpty(cut.FindAll(".layered-workspace"));

        cut.Find("button[title='Show Raw image']").Click();

        Assert.AreEqual(
            "/api/v1/operations/artifacts/00000000-0000-0000-0000-000000000101/preview",
            cut.Find(".sky-image-stage img").GetAttribute("src"));
        StringAssert.Contains(cut.Find(".current-sky-summary").TextContent, "Raw", StringComparison.Ordinal);
        cut.Find("#current-sky-view-large").Click();
        cut.WaitForAssertion(() => Assert.IsTrue(context.JSInterop.Invocations.Any(static invocation =>
            invocation.Identifier == "requestFullScreen")));
        Assert.HasCount(1, cut.FindAll(".sky-image-stage img"));

        cut.Find(".sky-image-stage img").TriggerEvent("onerror", EventArgs.Empty);
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Image preview unavailable", StringComparison.Ordinal));
        StringAssert.Contains(cut.Markup, "Download content", StringComparison.Ordinal);
    }

    [TestMethod]
    public void DetailNavigationStaysWithinOriginatingBoundedArchivePage()
    {
        using var context = new BunitContext();
        ConfigureGraphService(context);
        var current = OperatorUiTestData.Capture();
        var newer = OperatorUiTestData.Capture(Guid.Parse("00000000-0000-0000-0000-000000000011")) with { CaptureSequence = 43 };
        var older = OperatorUiTestData.Capture(Guid.Parse("00000000-0000-0000-0000-000000000012")) with { CaptureSequence = 41 };
        CameraAgentGalleryQuery? observedQuery = null;
        var service = new TestOperatorUiService
        {
            DetailHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentGalleryCapture>.Success(current)),
            NeighboursHandler = (captureId, filters, _) =>
            {
                observedQuery = filters;
                return ValueTask.FromResult(OperatorUiResult<CameraAgentGalleryNeighbours>.Success(
                    new(captureId, newer.CaptureId, older.CaptureId)));
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
        Assert.IsEmpty(cut.FindAll(".breadcrumb"));
        Assert.AreEqual("All captures", cut.Find(".capture-navigation__archive").TextContent);
        Assert.AreEqual("current-sky-heading", cut.Find(".capture-navigation").PreviousElementSibling?.QuerySelector("h1")?.Id);
        Assert.AreEqual(GalleryEvidenceOrigin.Simulated, observedQuery?.EvidenceOrigin);
        Assert.IsNull(observedQuery?.PageSize);
        Assert.IsNull(observedQuery?.Cursor);
        var links = cut.FindAll(".capture-navigation__control[href]");
        StringAssert.Contains(links[0].GetAttribute("href"), older.CaptureId.ToString(), StringComparison.Ordinal);
        StringAssert.Contains(links[1].GetAttribute("href"), newer.CaptureId.ToString(), StringComparison.Ordinal);
        Assert.IsTrue(links.All(link => link.GetAttribute("href")?.Contains("returnUrl=", StringComparison.Ordinal) == true));

        service.NeighboursHandler = (captureId, _, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryNeighbours>.Success(new(captureId, null, older.CaptureId)));
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
        ConfigureGraphService(context);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var capture = OperatorUiTestData.Capture();
        var service = new TestOperatorUiService
        {
            DetailPresentationHandler = LayeredStages,
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

        context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js")
            .Setup<string>("bindLayerToggles", _ => true).SetResult("valid");

        var cut = context.Render<GalleryDetail>(parameters => parameters.Add(page => page.CaptureId, capture.CaptureId));

        cut.WaitForAssertion(() =>
        {
            Assert.HasCount(1, cut.FindAll(".sky-layer-canvas > img"));
            Assert.HasCount(1, cut.FindAll(".sky-layer-overlay svg"));
            Assert.HasCount(2, cut.FindAll(".sky-layer-controls input[data-layer-target]"));
            Assert.IsEmpty(cut.FindAll(".layered-workspace"));
            StringAssert.Contains(cut.Markup, "/api/v1/operations/artifacts/00000000-0000-0000-0000-000000000102/preview", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll("script"));
        });
    }

    [TestMethod]
    public async Task SaveSelectedStackUsesLocalSelectionAndReportsSuccessOrFailure()
    {
        using var context = new BunitContext();
        ConfigureGraphService(context);
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
            DetailPresentationHandler = LayeredStages,
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
        var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        module.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        var cut = context.Render<GalleryDetail>(parameters => parameters.Add(page => page.CaptureId, capture.CaptureId));
        cut.WaitForElement(".sky-layer-canvas--verified");

        await cut.Find(".sky-layer-save button").ClickAsync().ConfigureAwait(false);

        CollectionAssert.AreEqual(new[] { selectedIdentity }, submitted?.ToArray());
        cut.WaitForAssertion(() => StringAssert.Contains(
            cut.Find(".sky-layer-result[role='status']").TextContent, "new immutable artifact", StringComparison.Ordinal));

        fail = true;
        await cut.Find(".sky-layer-save button").ClickAsync().ConfigureAwait(false);

        cut.WaitForAssertion(() => StringAssert.Contains(
            cut.Find("[role='alert']").TextContent, "conflicts with the selected stack", StringComparison.Ordinal));
        Assert.IsFalse(cut.Find(".sky-layer-save button").HasAttribute("disabled"));
    }

    [TestMethod]
    public async Task NavigatingDuringSaveNeverShowsReceiptOnAnotherCaptureAsync()
    {
        using var context = new BunitContext();
        ConfigureGraphService(context);
        var first = OperatorUiTestData.Capture();
        var second = OperatorUiTestData.Capture(Guid.Parse("00000000-0000-0000-0000-000000000021"));
        var selectedIdentity = new string('D', 64);
        var materializationCalls = 0;
        var pending = new TaskCompletionSource<OperatorUiResult<CameraAgentPresentationMaterializationReceipt>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new TestOperatorUiService
        {
            DetailPresentationHandler = LayeredStages,
            DetailHandler = (captureId, _) => ValueTask.FromResult(
                OperatorUiResult<CameraAgentGalleryCapture>.Success(captureId == first.CaptureId ? first : second)),
            PresentationHandler = (captureId, _) => ValueTask.FromResult(
                OperatorUiResult<CameraAgentLayeredPresentation>.Success(new(
                    captureId,
                    Guid.Parse("00000000-0000-0000-0000-000000000102"),
                    new string('A', 64),
                    new string('B', 64),
                    new string('C', 64),
                    640,
                    480,
                    [new(selectedIdentity, "scene-annotation", "hvo-layer-0", 20, true, 1_000_000, "renderer-v1", "style-v1")],
                    System.Text.Encoding.UTF8.GetBytes(
                        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 640 480\"><g id=\"hvo-layer-0\"></g></svg>")))),
            MaterializationHandler = (captureId, _, _) =>
            {
                Assert.AreEqual(first.CaptureId, captureId);
                Interlocked.Increment(ref materializationCalls);
                return new(pending.Task);
            }
        };
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);
        var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        module.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        var cut = context.Render<GalleryDetail>(parameters => parameters.Add(page => page.CaptureId, first.CaptureId));
        cut.WaitForElement(".sky-layer-canvas--verified");

        var save = cut.Find(".sky-layer-save button").ClickAsync();
        cut.WaitForAssertion(() => Assert.AreEqual(1, materializationCalls));
        cut.Render(parameters => parameters.Add(page => page.CaptureId, second.CaptureId));
        pending.SetResult(OperatorUiResult<CameraAgentPresentationMaterializationReceipt>.Success(new(
            first.CaptureId, Guid.NewGuid(), new string('F', 64), new string('A', 64), 1024, false)));
        await save.ConfigureAwait(false);

        Assert.AreEqual(1, materializationCalls);
        Assert.IsEmpty(cut.FindAll(".sky-layer-result"));
        Assert.IsEmpty(cut.FindAll("[role='alert']"));
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find(".sky-layer-save button").HasAttribute("disabled")));
    }

    [TestMethod]
    public void Detail_RetainsExactLineageRecipesNodesAndDirectEndpointsInTechnicalDisclosure()
    {
        using var context = new BunitContext();
        ConfigureGraphService(context);
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
        ConfigureGraphService(context);
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
             local.Find(".capture-navigation__archive").GetAttribute("href")));

        navigation.NavigateTo(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
            "/gallery/capture",
            "returnUrl",
            "https://attacker.example/gallery"));
        var rejected = context.Render<GalleryDetail>(parameters => parameters.Add(page => page.CaptureId, capture.CaptureId));
        rejected.WaitForAssertion(() => Assert.AreEqual("/gallery", rejected.Find(".capture-navigation__archive").GetAttribute("href")));
    }

    [TestMethod]
    public void MissingExtendedDetail_RendersExplicitUnavailableStates()
    {
        using var context = new BunitContext();
        ConfigureGraphService(context);
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
        ConfigureGraphService(context);
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
        ConfigureGraphService(context);
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
    public async Task ArchivedWorkspaceNeverPollsLatestAndKeepsSelectionOnRetry()
    {
        using var context = new BunitContext();
        ConfigureGraphService(context);
        context.Services.AddSingleton<TimeProvider>(new NoPollingTimeProvider());
        var service = new TestOperatorUiService();
        var capture = OperatorUiTestData.Capture();
        var currentReads = 0;
        var exactReads = 0;
        service.CurrentSkyHandler = _ =>
        {
            currentReads++;
            throw new AssertFailedException("An archived view must not read Current Sky.");
        };
        service.DetailHandler = (id, _) =>
        {
            Assert.AreEqual(capture.CaptureId, id);
            exactReads++;
            return ValueTask.FromResult(OperatorUiResult<CameraAgentGalleryCapture>.Success(capture));
        };
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(QueryHelpers.AddQueryString($"/gallery/{capture.CaptureId:D}", "returnUrl", "/gallery?cursor=retained"));
        var cut = context.Render<GalleryDetail>(parameters => parameters.Add(page => page.CaptureId, capture.CaptureId));
        await cut.Find("button[title='Show Raw image']").ClickAsync().ConfigureAwait(false);
        var source = cut.Find(".sky-image-stage img").GetAttribute("src");
        await cut.Find(".refresh-link").ClickAsync().ConfigureAwait(false);

        Assert.AreEqual(0, currentReads);
        Assert.AreEqual(1, exactReads);
        Assert.AreEqual("Capture #42", cut.Find("h1").TextContent);
        Assert.AreEqual(source, cut.Find(".sky-image-stage img").GetAttribute("src"));
        Assert.AreEqual("true", cut.Find("button[title='Show Raw image']").GetAttribute("aria-pressed"));
        Assert.IsEmpty(cut.FindAll(".live-indicator"));
        Assert.AreEqual(new Uri(navigation.Uri).PathAndQuery + "#technical-evidence",
            cut.Find(".figure-actions a").GetAttribute("href"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MissingOrMismatchedCaptureNeverRendersAnotherCapture(bool mismatched)
    {
        using var context = new BunitContext();
        ConfigureGraphService(context);
        var requested = Guid.NewGuid();
        var service = new TestOperatorUiService
        {
            DetailHandler = (_, _) => ValueTask.FromResult(mismatched
                ? OperatorUiResult<CameraAgentGalleryCapture>.Success(OperatorUiTestData.Capture())
                : OperatorUiResult<CameraAgentGalleryCapture>.Failure(OperatorUiResultKind.NotFound, "Capture not found."))
        };
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);
        var cut = context.Render<GalleryDetail>(parameters => parameters.Add(page => page.CaptureId, requested));
        Assert.HasCount(1, cut.FindAll(".detail-state--error"));
        Assert.IsEmpty(cut.FindComponents<CurrentSkyPage>());
        Assert.IsEmpty(cut.FindAll("img"));
    }

    [TestMethod]
    public async Task LateNeighbourResponseCannotReplaceNewCaptureNavigation()
    {
        using var context = new BunitContext();
        ConfigureGraphService(context);
        var first = OperatorUiTestData.Capture();
        var second = first with { CaptureId = Guid.NewGuid(), CaptureSequence = 99 };
        var pending = new TaskCompletionSource<OperatorUiResult<CameraAgentGalleryNeighbours>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new TestOperatorUiService
        {
            DetailHandler = (id, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentGalleryCapture>.Success(id == first.CaptureId ? first : second)),
            NeighboursHandler = (id, _, _) => id == first.CaptureId ? new(pending.Task)
                : ValueTask.FromResult(OperatorUiResult<CameraAgentGalleryNeighbours>.Success(new(id, null, first.CaptureId)))
        };
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);
        var cut = context.Render<GalleryDetail>(parameters => parameters.Add(page => page.CaptureId, first.CaptureId));
        cut.Render(parameters => parameters.Add(page => page.CaptureId, second.CaptureId));
        var unrelated = Guid.NewGuid();
        pending.SetResult(OperatorUiResult<CameraAgentGalleryNeighbours>.Success(new(first.CaptureId, unrelated, unrelated)));
        await cut.InvokeAsync(() => Task.CompletedTask).ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.AreEqual("Capture #99", cut.Find("h1").TextContent));
        var link = cut.Find(".capture-navigation__control[href]");
        StringAssert.Contains(link.GetAttribute("href"), first.CaptureId.ToString("D"), StringComparison.Ordinal);
        Assert.IsFalse(cut.Markup.Contains(unrelated.ToString("D"), StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("product=Combined&outcome=TerminalFailure", true)]
    [DataRow("q=single&product=Combined", false)]
    public void NeighbourQueryKeepsArchiveFiltersOrDisablesPageLocalSearch(string query, bool expectedRead)
    {
        using var context = new BunitContext();
        ConfigureGraphService(context);
        CameraAgentGalleryQuery? observed = null;
        var service = new TestOperatorUiService
        {
            NeighboursHandler = (id, filters, _) =>
            {
                observed = filters;
                return ValueTask.FromResult(OperatorUiResult<CameraAgentGalleryNeighbours>.Success(new(id, null, null)));
            }
        };
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(QueryHelpers.AddQueryString(
            $"/gallery/{OperatorUiTestData.Capture().CaptureId:D}", "returnUrl", $"/gallery?{query}"));
        var cut = context.Render<GalleryDetail>(parameters => parameters.Add(page => page.CaptureId, OperatorUiTestData.Capture().CaptureId));
        Assert.AreEqual(expectedRead, observed is not null);
        if (expectedRead)
        {
            Assert.AreEqual(HVO.SkyMonitor.AgentCore.FrameArtifactRole.Combined, observed!.ProcessingRole);
            Assert.AreEqual("TerminalFailure", observed.ProcessingStatus);
        }
        Assert.AreEqual($"/gallery?{query}", cut.Find(".capture-navigation__archive").GetAttribute("href"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DisclosureInteropFailurePreservesCaptureAndManualEvidence(bool importFails)
    {
        using var context = new BunitContext();
        ConfigureGraphService(context);
        var capture = OperatorUiTestData.Capture();
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(new TestOperatorUiService());
        if (importFails)
            context.Services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(new FailingImportRuntime());
        else
            context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js")
                .SetupVoid("openTechnicalEvidence", _ => true).SetException(new Microsoft.JSInterop.JSException("private browser diagnostic"));
        var cut = context.Render<GalleryDetail>(parameters => parameters.Add(page => page.CaptureId, capture.CaptureId));
        await cut.Find(".figure-actions a").ClickAsync().ConfigureAwait(false);
        StringAssert.Contains(cut.Find(".technical-evidence-error").TextContent, "below the image", StringComparison.Ordinal);
        Assert.AreEqual("Capture #42", cut.Find("h1").TextContent);
        Assert.HasCount(1, cut.FindAll("#technical-evidence > summary"));
        Assert.IsFalse(cut.Markup.Contains("private browser diagnostic", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DisposalCancelsPendingCaptureRead()
    {
        using var context = new BunitContext();
        ConfigureGraphService(context);
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

    private sealed class NoPollingTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => OperatorUiTestData.Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => throw new AssertFailedException("Historical capture must not start a polling timer.");
    }

    private sealed class FailingImportRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => ValueTask.FromException<TValue>(new Microsoft.JSInterop.JSException("private browser diagnostic"));
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => InvokeAsync<TValue>(identifier, args);
    }
}
