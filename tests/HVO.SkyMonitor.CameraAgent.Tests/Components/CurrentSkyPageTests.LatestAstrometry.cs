using System.Text.Json;
using Bunit;
using HVO.SkyMonitor.Astronomy;
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
        service.LatestAstrometryHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(new(true, summary)));
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".latest-measured-solution").TextContent,
            previous ? "For a different capture" : "For the selected capture", StringComparison.Ordinal));
        Assert.HasCount(1, cut.FindAll(".astrometry-card .latest-measured-solution"));
        Assert.IsEmpty(cut.FindAll(".sky-inspector > .latest-measured-solution"));
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
            "For a different capture", StringComparison.Ordinal));
        Assert.IsTrue(cut.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
        StringAssert.Contains(cut.Find(".latest-measured-solution").TextContent, summary.SolutionIdentitySha256, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LatestSummarySurvivesABusyReadOrDeadlineAndRetries(bool deadline)
    {
        var evidence = (await AstrometryEvidenceAsync().ConfigureAwait(false)) with { ArtifactId = Guid.NewGuid() };
        var summary = CameraAgentLatestAstrometry.FromVerified(evidence)!;
        using var context = new BunitContext();
        var service = Configure(context);
        var current = AstrometryImage(evidence);
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(current));
        service.AstrometryHandler = (id, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentRetainedAstrometry>.Success(
            id == evidence.CaptureId ? evidence : new(id, "NotRetained", "assessment-not-retained")));
        service.LatestAstrometryHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(new(true, summary)));
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".latest-measured-solution").TextContent,
            summary.SolutionIdentitySha256, StringComparison.Ordinal));

        var next = current.DisplayCapture! with { CaptureId = Guid.NewGuid() };
        current = current with { DisplayCapture = next, LatestCapture = next };
        service.LatestAstrometryHandler = (_, _) => deadline
            ? ValueTask.FromException<OperatorUiResult<CameraAgentLatestAstrometryRead>>(new OperationCanceledException())
            : ValueTask.FromResult(OperatorUiResult<CameraAgentLatestAstrometryRead>.Failure(OperatorUiResultKind.Unavailable, "Astrometry read capacity is unavailable."));
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            var text = cut.Find(".latest-measured-solution").TextContent;
            StringAssert.Contains(text, "Showing the previously verified result", StringComparison.Ordinal);
            StringAssert.Contains(text, summary.MidpointUtc.ToString("O"), StringComparison.Ordinal);
            StringAssert.Contains(text, summary.SolutionIdentitySha256, StringComparison.Ordinal);
            Assert.IsTrue(cut.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
        });

        service.LatestAstrometryHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(new(true, summary)));
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            var text = cut.Find(".latest-measured-solution").TextContent;
            Assert.IsFalse(text.Contains("temporarily unavailable", StringComparison.Ordinal));
            StringAssert.Contains(text, summary.MidpointUtc.ToString("O"), StringComparison.Ordinal);
            StringAssert.Contains(text, summary.SolutionIdentitySha256, StringComparison.Ordinal);
        });

        service.LatestAstrometryHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(new(true)));
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".latest-measured-solution").TextContent,
            "No accepted measured result", StringComparison.Ordinal));
        Assert.IsEmpty(cut.FindAll(".latest-measured-solution time"));
    }

    [TestMethod]
    public void BusyWithoutAPriorSummaryShowsTemporaryUnavailability()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.LatestAstrometryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentLatestAstrometryRead>.Failure(OperatorUiResultKind.Unavailable, "Astrometry read capacity is unavailable."));
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".latest-measured-solution").TextContent,
            "temporarily unavailable", StringComparison.Ordinal));
        Assert.IsEmpty(cut.FindAll(".latest-measured-solution time"));
        Assert.IsTrue(cut.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
    }

    [TestMethod]
    public async Task LatestMeasuredSummaryIsAvailableWithoutASelectedImage()
    {
        var evidence = (await AstrometryEvidenceAsync().ConfigureAwait(false)) with { ArtifactId = Guid.NewGuid() };
        var summary = CameraAgentLatestAstrometry.FromVerified(evidence)!;
        using var context = new BunitContext();
        var service = Configure(context);
        var current = WithoutDisplayImage(AstrometryImage(evidence));
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(current));
        service.LatestAstrometryHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(new(true, summary)));

        var cut = context.Render<CurrentSkyPage>();

        cut.WaitForAssertion(() =>
        {
            var text = cut.Find(".latest-measured-solution").TextContent;
            StringAssert.Contains(text, summary.SolutionIdentitySha256, StringComparison.Ordinal);
            StringAssert.Contains(text, summary.MidpointUtc.ToString("O"), StringComparison.Ordinal);
            StringAssert.Contains(text, "No image is selected", StringComparison.Ordinal);
            Assert.IsTrue(cut.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
        });
    }

    [TestMethod]
    public async Task LatestSummaryAdvancesWhileOlderImageIsHeld()
    {
        var evidence = (await AstrometryEvidenceAsync().ConfigureAwait(false)) with { ArtifactId = Guid.NewGuid() };
        var summary = CameraAgentLatestAstrometry.FromVerified(evidence)!;
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var older = WithStructuredBase();
        var displayed = older.DisplayCapture! with
        {
            CaptureId = Guid.NewGuid(),
            ExposureStartedUtc = summary.MidpointUtc.AddSeconds(-20)
        };
        older = older with { DisplayCapture = displayed, LatestCapture = displayed };
        var first = View(older) with { MinimumRefreshInterval = TimeSpan.FromMinutes(1) };
        var current = first;
        service.CurrentSkyHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(current));
        var cut = context.Render<CurrentSkyPage>();
        WaitForFirst(cut, clock, first);
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".latest-measured-solution").TextContent,
            "No accepted measured result", StringComparison.Ordinal));

        var next = Next(first, summary.CaptureId, older.DisplayCapture!.CaptureSequence + 1);
        current = next with
        {
            ProcessingPending = true,
            Presentation = next.Presentation with
            {
                DisplayCapture = next.Presentation.DisplayCapture! with { ExposureStartedUtc = summary.MidpointUtc }
            }
        };
        service.LatestAstrometryHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(new(true, summary)));
        clock.Advance(TimeSpan.FromSeconds(15));

        cut.WaitForAssertion(() =>
        {
            var text = cut.Find(".latest-measured-solution").TextContent;
            StringAssert.Contains(text, "For a different capture", StringComparison.Ordinal);
            StringAssert.Contains(text, summary.SolutionIdentitySha256, StringComparison.Ordinal);
            Assert.AreEqual(CaptureLink(first), cut.Find(".layers-link").GetAttribute("href"));
            Assert.IsTrue(cut.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
        });
    }

    [TestMethod]
    [DataRow(142d, true)]
    [DataRow(400d, false)]
    public async Task CroppedMeasuredSummaryKeepsAcceptedFitWhenBoresightIsOutsideReadout(double principalPointX, bool scaleAvailable)
    {
        var evidence = (await AstrometryEvidenceAsync().ConfigureAwait(false)) with { ArtifactId = Guid.NewGuid() };
        var original = evidence.Solution!;
        var calibrationPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "retained-astrometry", "calibration.json");
        var calibration = JsonSerializer.Deserialize<AstrometricCalibration>(
            await File.ReadAllBytesAsync(calibrationPath).ConfigureAwait(false))!;
        var projection = calibration.Projection with
        {
            PrincipalPointX = principalPointX,
            PrincipalPointY = 60,
            FocalLengthXPixels = 379.323 / 4,
            FocalLengthYPixels = 379.323 / 4,
            WidthPixels = 120,
            HeightPixels = 120,
            ImageCircleRadiusPixels = 595.84 / 4
        };
        var cropped = new AstrometricCalibration(projection, calibration.CalibrationVersion, calibration.ReadoutIdentitySha256);
        var assessment = original.Assessment with { CalibrationIdentitySha256 = cropped.IdentitySha256, IdentitySha256 = string.Empty };
        assessment = assessment with { IdentitySha256 = AstrometricIdentity.Hash(assessment) };
        var mapping = new AstrometricMapping(cropped, assessment);
        var fit = assessment.Parameters!;
        Assert.IsNull(mapping.SkyToPixel(new(fit.BoresightRightAscensionHours, fit.BoresightDeclinationDegrees)));

        var croppedSolution = original with { Assessment = assessment };
        var accepted = CameraAgentLatestAstrometry.FromVerified(evidence with
        {
            Solution = croppedSolution,
            SolutionIdentitySha256 = croppedSolution.SolutionIdentitySha256,
            Mapping = mapping,
            WidthPixels = 120,
            HeightPixels = 120
        });

        Assert.IsNotNull(accepted);
        Assert.AreEqual(croppedSolution.SolutionIdentitySha256, accepted.SolutionIdentitySha256);
        Assert.AreEqual(assessment.Frame.MidpointUtc, accepted.MidpointUtc);
        Assert.AreEqual(fit.BoresightRightAscensionHours, accepted.RightAscensionHours);
        Assert.AreEqual(fit.BoresightDeclinationDegrees, accepted.DeclinationDegrees);
        Assert.AreEqual(fit.RollDegrees, accepted.RollDegrees);
        if (scaleAvailable)
        {
            Assert.AreEqual(new PixelPoint(59.5, 59.5), accepted.ReferencePixel);
            Assert.AreEqual("source image center", accepted.ScaleReference);
            Assert.IsTrue(accepted.LocalScale.XArcsecondsPerPixel > 0);
            Assert.IsTrue(accepted.LocalScale.YArcsecondsPerPixel > 0);
        }
        else
        {
            Assert.IsNull(accepted.ReferencePixel);
            Assert.IsNull(accepted.LocalScale.XArcsecondsPerPixel);
            Assert.IsNull(accepted.LocalScale.YArcsecondsPerPixel);
        }
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
