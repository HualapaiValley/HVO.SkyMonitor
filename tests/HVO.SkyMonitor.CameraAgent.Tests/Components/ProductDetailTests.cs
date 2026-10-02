using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class ProductDetailTests
{
    private static readonly Guid ArtifactId = Guid.Parse("01000000-0000-0000-0000-000000000201");
    private static readonly DateTimeOffset Instant = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private static TestOperatorUiService Configure(BunitContext context, CameraAgentProductDetail detail, bool failed = false)
    {
        RetainedPreviewImageTestSupport.Configure(context, failed);
        ProductDetailTestSupport.Configure(context);
        var service = new TestOperatorUiService
        {
            ProductDetailHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentProductDetail>.Success(detail))
        };
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);
        return service;
    }

    private static CameraAgentProductDetail Detail(string media = "image/png", string availability = "Available", string status = "Completed")
    {
        var product = new CameraAgentProduct(ArtifactId, new string('A', 64), Guid.NewGuid(), 42, "agent",
            "preview", FrameArtifactRole.Preview, "native-preview", Instant, Instant, media,
            new string('B', 64), 1024, new("encoded-preview", "1.0.0", "encoder-v1", new string('C', 64), new string('D', 64)),
            [], null, null, null, availability, availability == "Available" ? null : "Retained bytes missing",
            TimeSpan.FromSeconds(20), 1, 1936, 1216, "Live", false);
        return new(product, Instant, ObservingDayCalendar.Create("America/Phoenix").Resolve(Instant), "rig",
            [new(0, Guid.NewGuid(), FrameArtifactRole.Raw, product.CaptureId, 42)], false,
            new("preview", status, 1, Instant, Instant.AddSeconds(1), 1000, "Produced"), []);
    }

    [TestMethod]
    public void ExactEncodedImageUsesOriginalIdentityAndInlineContent()
    {
        using var context = new BunitContext();
        Configure(context, Detail());
        var cut = context.Render<ProductDetail>(parameters => parameters.Add(page => page.ArtifactId, ArtifactId));
        cut.WaitForAssertion(() => Assert.AreEqual($"/api/v1/operations/artifacts/{ArtifactId:D}/content?inline=true", cut.Find(".generated-media img").GetAttribute("src")));
        StringAssert.Contains(cut.Find(".generated-media img").GetAttribute("alt")!, ArtifactId.ToString("D"), StringComparison.Ordinal);
        Assert.IsNotNull(cut.Find("button[aria-label='Product fullscreen']"));
        cut.FindAll("button").Single(static button => button.TextContent.Contains("100%", StringComparison.Ordinal)).Click();
        Assert.IsTrue(cut.Find(".generated-media").ClassList.Contains("generated-media--native"));
        Assert.IsNotNull(cut.Find("button[disabled][title='No authorized successor-generation action is exposed']"));
    }

    [TestMethod]
    public void PackedPreviewUsesExactArtifactDisplayEndpoint()
    {
        using var context = new BunitContext();
        Configure(context, Detail("application/x-hvo-packed-image"));
        var cut = context.Render<ProductDetail>(parameters => parameters.Add(page => page.ArtifactId, ArtifactId));
        cut.WaitForAssertion(() => Assert.AreEqual($"/api/v1/operations/artifacts/{ArtifactId:D}/preview", cut.Find(".generated-media img").GetAttribute("src")));
        StringAssert.Contains(cut.Find(".media-controls").TextContent, "original bytes remain downloadable", StringComparison.Ordinal);
    }

    [TestMethod]
    public void MissingOrNonDisplayableContentPreservesEvidenceWithoutMediaSubstitution()
    {
        using var context = new BunitContext();
        var detail = Detail("application/json", "Missing");
        Configure(context, detail);
        var cut = context.Render<ProductDetail>(parameters => parameters.Add(page => page.ArtifactId, ArtifactId));
        cut.WaitForElement(".media-unavailable");
        Assert.IsEmpty(cut.FindAll("img,video"));
        StringAssert.Contains(cut.Find(".media-unavailable").TextContent, "Retained bytes missing", StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, detail.Product.ChecksumSha256, StringComparison.Ordinal);
        Assert.IsFalse(cut.FindAll("a").Any(static link => link.TextContent.Contains("Download content", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ImageFailureKeepsArtifactFactsAndRemovesFailedMedia()
    {
        using var context = new BunitContext();
        Configure(context, Detail(), failed: true);
        var cut = context.Render<ProductDetail>(parameters => parameters.Add(page => page.ArtifactId, ArtifactId));
        cut.WaitForElement(".media-unavailable");
        Assert.IsEmpty(cut.FindAll("img"));
        StringAssert.Contains(cut.Markup, ArtifactId.ToString("D"), StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".media-unavailable").TextContent, "Product preview unavailable", StringComparison.Ordinal);
    }

    [TestMethod]
    public void WorkingOutputDoesNotClaimFinalCompletenessOrVersion()
    {
        using var context = new BunitContext();
        Configure(context, Detail(status: "Running"));
        var cut = context.Render<ProductDetail>(parameters => parameters.Add(page => page.ArtifactId, ArtifactId));
        cut.WaitForElement(".product-inspector");
        StringAssert.Contains(cut.Find(".product-inspector").TextContent, "Current step state", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".product-inspector").TextContent, "Running", StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, "Artifact-specific finality not recorded", StringComparison.Ordinal);
        Assert.IsFalse(cut.Find(".media-label").TextContent.Contains("generation in progress", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(cut.Markup.Contains("Version 2", StringComparison.Ordinal));
        Assert.IsFalse(cut.Markup.Contains("Complete night", StringComparison.Ordinal));
        StringAssert.Contains(cut.Find(".product-inspector").TextContent, "Integration is not elapsed observation span", StringComparison.Ordinal);
    }

    [TestMethod]
    public void RelatedSiblingOutputsAreNotMisrepresentedAsPredecessors()
    {
        using var context = new BunitContext();
        var original = Detail();
        var detail = original with { Product = original.Product with { ExecutionClass = "Unassociated" }, Predecessors = [new(Guid.NewGuid(), "older", Instant.AddMinutes(-1), "Available"), new(Guid.NewGuid(), "later", Instant.AddMinutes(1), "Available")] };
        Configure(context, detail);
        var cut = context.Render<ProductDetail>(parameters => parameters.Add(page => page.ArtifactId, ArtifactId));
        cut.WaitForElement(".product-versions");
        StringAssert.Contains(cut.Find(".product-versions").TextContent, "Earlier committed output", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".product-versions").TextContent, "Later committed output", StringComparison.Ordinal);
        Assert.IsFalse(cut.Markup.Contains("Retained predecessors", StringComparison.Ordinal));
        Assert.IsFalse(cut.Markup.Contains("Unassociated / local processing", StringComparison.Ordinal));
        StringAssert.Contains(cut.Find(".product-inspector").TextContent, "ExecutorNot recorded", StringComparison.Ordinal);
    }

    [TestMethod]
    public void MismatchedRequestedArtifactIsRejected()
    {
        using var context = new BunitContext();
        Configure(context, Detail());
        var cut = context.Render<ProductDetail>(parameters => parameters.Add(page => page.ArtifactId, Guid.NewGuid()));
        cut.WaitForElement(".page-state--error");
        Assert.IsEmpty(cut.FindAll("img,video,.product-inspector"));
    }

    [TestMethod]
    public async Task StaleResultCannotReplaceNewArtifactAsync()
    {
        using var context = new BunitContext();
        var first = Detail();
        var secondId = Guid.NewGuid();
        var second = first with { Product = first.Product with { ArtifactId = secondId, Variant = "second-preview" } };
        var pending = new TaskCompletionSource<OperatorUiResult<CameraAgentProductDetail>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Configure(context, first);
        service.ProductDetailHandler = (id, _) => id == ArtifactId ? new(pending.Task)
            : ValueTask.FromResult(OperatorUiResult<CameraAgentProductDetail>.Success(second));
        var cut = context.Render<ProductDetail>(parameters => parameters.Add(page => page.ArtifactId, ArtifactId));
        cut.Render(parameters => parameters.Add(page => page.ArtifactId, secondId));
        cut.WaitForElement(".product-inspector");
        pending.SetResult(OperatorUiResult<CameraAgentProductDetail>.Success(first));
        await Task.Yield();
        cut.WaitForAssertion(() => Assert.AreEqual($"/api/v1/operations/artifacts/{secondId:D}/content?inline=true", cut.Find("img").GetAttribute("src")));
    }

    [TestMethod]
    public void UnauthorizedRefreshClearsProtectedMediaAndRestrictsReturnUrl()
    {
        using var context = new BunitContext();
        var service = Configure(context, Detail());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/archive/products/{ArtifactId:D}?returnUrl=https%3A%2F%2Funtrusted.example%2Fpath");
        var cut = context.Render<ProductDetail>(parameters => parameters.Add(page => page.ArtifactId, ArtifactId));
        cut.WaitForElement("img");
        Assert.IsFalse(cut.FindAll("a").Any(static link => (link.GetAttribute("href") ?? "").Contains("untrusted.example", StringComparison.Ordinal)));
        service.ProductDetailHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentProductDetail>.Failure(OperatorUiResultKind.Unauthorized, "denied"));
        cut.Render(parameters => parameters.Add(page => page.ArtifactId, ArtifactId));
        cut.WaitForAssertion(() =>
        {
            Assert.IsEmpty(cut.FindAll("img,video,.product-inspector"));
            Assert.IsTrue(context.Services.GetRequiredService<NavigationManager>().Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
        });
    }
}

internal static class ProductDetailTestSupport
{
    internal static void Configure(BunitContext context)
    {
        var module = context.JSInterop.SetupModule("./Components/Pages/ProductDetail.razor.js");
        module.Mode = JSRuntimeMode.Loose;
        module.SetupModule("bind").Mode = JSRuntimeMode.Loose;
    }
}
