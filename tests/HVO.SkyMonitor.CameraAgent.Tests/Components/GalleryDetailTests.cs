using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
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
            StringAssert.Contains(cut.Markup, "/preview", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "/content", StringComparison.Ordinal);
            Assert.HasCount(1, cut.FindAll("a[href$='/preview']"));
            Assert.HasCount(2, cut.FindAll("a[href$='/content']"));
            Assert.IsFalse(cut.Markup.Contains("/tmp/", StringComparison.Ordinal));
            Assert.IsFalse(cut.Markup.Contains("optionsJson", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(cut.Markup.Contains("secret", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(cut.Markup.Contains("sensitive processing failure", StringComparison.OrdinalIgnoreCase));
            Assert.IsEmpty(cut.FindAll("main"));
        });
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
    }
}
