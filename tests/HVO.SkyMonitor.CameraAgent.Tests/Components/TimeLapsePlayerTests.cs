using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Presentation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class TimeLapsePlayerTests
{
    private const string Source = "/api/v1/operations/time-lapse-sample";

    [TestMethod]
    public void Video_AttachesItsSourceOnlyOnceTheBrowserSaysItCanPlayTheType()
    {
        using var context = new BunitContext();
        var probe = context.JSInterop.SetupModule(TimeLapsePlayerTestSupport.Module).Setup<bool>("canPlay", "video/mp4");

        var player = Render(context, "video/mp4", 1280, 720);

        Assert.AreEqual("checking", player.Find("figure").GetAttribute("data-player-state"));
        Assert.IsEmpty(player.FindAll("video"));
        StringAssert.Contains(player.Find("figure").GetAttribute("style"), "--time-lapse-aspect: 1280 / 720", StringComparison.Ordinal);
        probe.SetResult(true);
        player.WaitForAssertion(() =>
        {
            var video = player.Find("video");
            Assert.IsNotNull(video.GetAttribute("controls"));
            Assert.AreEqual("metadata", video.GetAttribute("preload"));
            Assert.IsNull(video.GetAttribute("autoplay"));
            Assert.AreEqual(Source, player.Find("video source").GetAttribute("src"));
            Assert.AreEqual("video/mp4", player.Find("video source").GetAttribute("type"));
        });
        Assert.AreEqual(Source, player.Find("figcaption a[download]").GetAttribute("href"));
        StringAssert.Contains(player.Find("figcaption").TextContent, "Sample caption", StringComparison.Ordinal);
    }

    [TestMethod]
    public void UnplayableType_OffersTheDownloadInsteadOfABrokenPlayer()
    {
        using var context = new BunitContext();
        TimeLapsePlayerTestSupport.Configure(context, playable: false);

        var player = Render(context, "video/x-ms-wmv", 1280, 1280);

        player.WaitForAssertion(() => Assert.AreEqual("unsupported", player.Find("figure").GetAttribute("data-player-state")));
        Assert.IsEmpty(player.FindAll("video"));
        StringAssert.Contains(player.Find(".time-lapse-player__fallback").TextContent, "cannot play video/x-ms-wmv", StringComparison.Ordinal);
        Assert.AreEqual(Source, player.Find("figcaption a[download]").GetAttribute("href"));
    }

    [TestMethod]
    public void DecodingFailure_ReplacesThePlayerWithTheDownload()
    {
        using var context = new BunitContext();
        TimeLapsePlayerTestSupport.Configure(context);
        var player = Render(context, "video/mp4", 960, 720);
        player.WaitForAssertion(() => Assert.HasCount(1, player.FindAll("video source")));

        player.Find("video source").TriggerEvent("onerror", EventArgs.Empty);

        player.WaitForAssertion(() => Assert.AreEqual("failed", player.Find("figure").GetAttribute("data-player-state")));
        StringAssert.Contains(player.Find(".time-lapse-player__fallback").TextContent, "could not play this file", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("image/gif")]
    [DataRow("image/webp")]
    public void AnimatedImage_MovesOnlyAfterAnExplicitRequest(string mediaType)
    {
        using var context = new BunitContext();
        var player = Render(context, mediaType, 640, 480);

        Assert.IsEmpty(player.FindAll("img"));
        player.Find("button.time-lapse-player__reveal").Click();

        Assert.AreEqual(Source, player.Find("img").GetAttribute("src"));
        Assert.AreEqual("Sample label", player.Find("img").GetAttribute("alt"));
        Assert.IsEmpty(context.JSInterop.Invocations);
    }

    [TestMethod]
    public void ProbeFailure_LeavesTheDecisionToTheBrowser()
    {
        using var context = new BunitContext();
        var runtime = new Mock<IJSRuntime>();
        runtime.Setup(js => js.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()))
            .Throws(new JSException("Module unavailable"));
        context.Services.AddSingleton(runtime.Object);

        var player = Render(context, "video/webm", 1280, 1280);

        player.WaitForAssertion(() => Assert.AreEqual("video/webm", player.Find("video source").GetAttribute("type")));
    }

    private static IRenderedComponent<TimeLapsePlayer> Render(BunitContext context, string mediaType, int width, int height)
        => context.Render<TimeLapsePlayer>(parameters => parameters
            .Add(component => component.Source, Source)
            .Add(component => component.MediaType, mediaType)
            .Add(component => component.Width, width)
            .Add(component => component.Height, height)
            .Add(component => component.Label, "Sample label")
            .Add(component => component.Caption, "Sample caption"));
}

internal static class TimeLapsePlayerTestSupport
{
    internal const string Module = "./Components/Presentation/TimeLapsePlayer.razor.js";

    internal static void Configure(BunitContext context, bool playable = true)
        => context.JSInterop.SetupModule(Module).Setup<bool>("canPlay", _ => true).SetResult(playable);
}
