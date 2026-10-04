using Bunit;
using Bunit.JSInterop;
using HVO.SkyMonitor.CameraAgent.Components.Presentation;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class LargeImageViewerTests
{
    [TestMethod]
    public void DelayedShowIsClosedWhenParentClosesViewer()
    {
        using var context = new BunitContext();
        var module = context.JSInterop.SetupModule("./Components/Presentation/LargeImageViewer.razor.js");
        module.SetupVoid("connect", _ => true).SetVoidResult();
        var show = module.SetupVoid("show", _ => true);
        var close = module.SetupVoid("close", _ => true);
        var source = new Uri("/api/v1/operations/artifacts/00000000-0000-0000-0000-000000000001/preview", UriKind.Relative);

        var cut = context.Render<LargeImageViewer>(parameters => parameters
            .Add(viewer => viewer.Open, true)
            .Add(viewer => viewer.Source, source));
        cut.WaitForAssertion(() => Assert.HasCount(1, show.Invocations));

        cut.Render(parameters => parameters
            .Add(viewer => viewer.Open, false)
            .Add(viewer => viewer.Source, source));
        show.SetVoidResult();

        cut.WaitForAssertion(() => Assert.HasCount(1, close.Invocations));
    }

    [TestMethod]
    public void MediaAndDetailsAreShownBesideTheCanvasOnlyWhileOpen()
    {
        using var context = new BunitContext();
        LargeImageViewerTestSupport.Configure(context);

        var cut = context.Render<LargeImageViewer>(parameters => parameters
            .Add(viewer => viewer.Eyebrow, "Nightly product")
            .Add(viewer => viewer.Title, "Keogram")
            .Add(viewer => viewer.CloseLabel, "Close the product viewer")
            .Add(viewer => viewer.Media, "<p class=\"media\">Player</p>")
            .Add(viewer => viewer.Details, "<p class=\"facts\">Facts</p>"));

        Assert.IsEmpty(cut.FindAll(".media, .facts, .large-viewer__details"));
        Assert.AreEqual("Nightly product", cut.Find("header p").TextContent);
        Assert.AreEqual("Close the product viewer", cut.Find(".large-viewer__close").GetAttribute("aria-label"));
        StringAssert.Contains(cut.Find(".large-viewer__body").ClassName, "large-viewer__body--details", StringComparison.Ordinal);

        cut.Render(parameters => parameters.Add(viewer => viewer.Open, true));

        Assert.IsNotNull(cut.Find(".large-viewer__canvas .media"));
        Assert.AreEqual("Keogram details", cut.Find("aside.large-viewer__details").GetAttribute("aria-label"));
        Assert.IsNotNull(cut.Find("aside.large-viewer__details .facts"));
        // Media has no size modes to offer.
        Assert.IsEmpty(cut.FindAll(".large-viewer__modes"));
        Assert.AreEqual("Escape closes this viewer.", cut.Find("footer span").TextContent);
    }

    [TestMethod]
    public void SourceTakesPrecedenceOverMediaAndReportsItsFailure()
    {
        using var context = new BunitContext();
        LargeImageViewerTestSupport.Configure(context);
        var failures = 0;

        var cut = context.Render<LargeImageViewer>(parameters => parameters
            .Add(viewer => viewer.Open, true)
            .Add(viewer => viewer.Source, new Uri("/still-products/preview", UriKind.Relative))
            .Add(viewer => viewer.Media, "<p class=\"media\">Preview unavailable</p>")
            .Add(viewer => viewer.OnImageError, () => failures++));

        Assert.IsEmpty(cut.FindAll(".media"));
        Assert.HasCount(2, cut.FindAll(".large-viewer__modes button"));
        Assert.AreEqual("Protected local image", cut.Find("header p").TextContent);
        Assert.IsEmpty(cut.FindAll(".large-viewer__details"));
        cut.Find(".large-viewer__canvas img").TriggerEvent("onerror", EventArgs.Empty);
        Assert.AreEqual(1, failures);
    }

    [TestMethod]
    public async Task CloseQueuedAfterDisposalDoesNotNotifyParentAsync()
    {
        using var context = new BunitContext();
        var notified = false;
        var cut = context.Render<LargeImageViewer>(parameters => parameters
            .Add(viewer => viewer.OpenChanged, _ => notified = true));

        await cut.Instance.DisposeAsync().ConfigureAwait(false);
        await cut.Instance.CloseAsync().ConfigureAwait(false);

        Assert.IsFalse(notified);
    }
}

internal static class LargeImageViewerTestSupport
{
    internal const string Module = "./Components/Presentation/LargeImageViewer.razor.js";

    internal static void Configure(BunitContext context)
    {
        var module = context.JSInterop.SetupModule(Module);
        foreach (var identifier in new[] { "connect", "show", "close", "disconnect" })
        {
            module.SetupVoid(identifier, _ => true).SetVoidResult();
        }
    }
}
