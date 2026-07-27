using Bunit;
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
    public void Detail_RendersExactLineageRecipesNodesAndDirectEndpointsWithoutDisclosure()
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
    public void DetailWithoutPreview_RendersUnsupportedText()
    {
        using var context = new BunitContext();
        var service = new TestOperatorUiService();
        var capture = OperatorUiTestData.Capture() with
        {
            Artifacts = OperatorUiTestData.Capture().Artifacts.Where(static artifact => artifact.Role == HVO.SkyMonitor.AgentCore.FrameArtifactRole.Raw).ToArray()
        };
        service.DetailHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<HVO.SkyMonitor.CameraAgent.Common.Gallery.CameraAgentGalleryCapture>.Success(capture));
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);

        var cut = context.Render<GalleryDetail>(parameters => parameters.Add(page => page.CaptureId, capture.CaptureId));

        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Preview unsupported", StringComparison.Ordinal));
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
}
