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
