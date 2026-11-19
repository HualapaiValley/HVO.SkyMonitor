using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CurrentSkyPageTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LatestMeasuredSummaryLabelsItsCaptureAndNeverEnablesForeignGeometry(bool previous)
    {
        var evidence = (await AstrometryEvidenceAsync().ConfigureAwait(false)) with { ArtifactId = Guid.NewGuid() };
        var summary = CameraAgentLatestAstrometry.FromVerified(evidence)!;
        using var context = new BunitContext();
        var service = Configure(context);
        var other = OperatorUiTestData.CurrentImage();
        var next = other.DisplayCapture! with { CaptureId = Guid.NewGuid() };
        other = other with { DisplayCapture = next, LatestCapture = next };
        var current = previous ? other : AstrometryImage(evidence);
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(current));
        service.AstrometryHandler = (id, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentRetainedAstrometry>.Success(
            previous ? new(id, "NotRetained", "assessment-not-retained") : evidence));
        service.LatestAstrometryHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentLatestAstrometry?>.Success(summary));
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".latest-measured-solution").TextContent,
            previous ? "not a solve for the selected image" : "For the selected capture", StringComparison.Ordinal));
        var text = cut.Find(".latest-measured-solution").TextContent;
        StringAssert.Contains(text, "Measured center RA / Dec", StringComparison.Ordinal);
        StringAssert.Contains(text, "arcsec/pixel", StringComparison.Ordinal);
        StringAssert.Contains(text, "Zero-based source output X right, Y down", StringComparison.Ordinal);
        StringAssert.Contains(text, summary.MidpointUtc.ToString("O"), StringComparison.Ordinal);
        Assert.AreEqual(previous, cut.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
        current = other;
        service.AstrometryHandler = (id, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentRetainedAstrometry>.Success(new(id, "NotRetained", "assessment-not-retained")));
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".latest-measured-solution").TextContent,
            "not a solve for the selected image", StringComparison.Ordinal));
        Assert.IsTrue(cut.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
        StringAssert.Contains(cut.Find(".latest-measured-solution").TextContent, summary.SolutionIdentitySha256, StringComparison.Ordinal);
    }

    [TestMethod]
    public void NoAcceptedSummaryShowsHonestAbsence()
    {
        using var context = new BunitContext();
        Configure(context);
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".latest-measured-solution").TextContent,
            "No accepted measured result", StringComparison.Ordinal));
        Assert.IsEmpty(cut.FindAll(".latest-measured-solution time"));
    }
}
