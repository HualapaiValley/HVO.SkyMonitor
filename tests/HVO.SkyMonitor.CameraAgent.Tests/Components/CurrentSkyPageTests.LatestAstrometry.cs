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
    [DataRow("matching")]
    [DataRow("distinct")]
    [DataRow("timeout")]
    [DataRow("inflight-positive")]
    public async Task ExactSelectedRejectionWithdrawsOnlyItsHeadlineAndFencesOlderHistory(string scenario)
    {
        var evidence = await AstrometryEvidenceAsync().ConfigureAwait(false);
        var exact = CameraAgentLatestAstrometry.FromVerified(evidence)!;
        var headline = scenario == "distinct" ? exact with { ArtifactId = Guid.NewGuid() } : exact;
        var selectedRead = new DeferredAstrometryRead<CameraAgentRetainedAstrometry>();
        var heldHistory = new DeferredAstrometryRead<CameraAgentLatestAstrometryRead>();
        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(AstrometryImage(evidence)));
        service.AstrometryHandler = (_, _) => selectedRead.Read();
        service.LatestAstrometryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(new(true, headline)));
        service.AstrometryPublicationHandler = (identity, _) =>
        {
            Assert.AreEqual(new CameraAgentAstrometryAssessmentIdentity(exact.CaptureId, exact.ArtifactId, exact.SolutionIdentitySha256), identity);
            return scenario == "timeout"
                ? ValueTask.FromException<OperatorUiResult<CameraAgentAstrometryPublicationState>>(new OperationCanceledException())
                : ValueTask.FromResult(OperatorUiResult<CameraAgentAstrometryPublicationState>.Success(
                    CameraAgentAstrometryPublicationState.Invalidated));
        };

        var cut = context.Render<CurrentSkyPage>();
        await selectedRead.Awaiting.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        cut.WaitForAssertion(() => AssertHeadlineIsVisible(headline));
        if (scenario == "inflight-positive")
        {
            service.LatestAstrometryHandler = (_, _) => heldHistory.Read();
            await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
            await heldHistory.Awaiting.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        await Task.Run(() => selectedRead.Complete(OperatorUiResult<CameraAgentRetainedAstrometry>.Success(evidence))).ConfigureAwait(false);
        await cut.InvokeAsync(static () => { }).ConfigureAwait(false);
        Assert.IsTrue(cut.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
        Assert.IsEmpty(cut.FindAll(".astrometry-provenance"));
        if (scenario is "distinct" or "timeout")
        {
            AssertHeadlineIsVisible(headline);
            return;
        }

        AssertHeadlineWasWithdrawn();
        if (scenario == "inflight-positive")
        {
            await Task.Run(() => heldHistory.Complete(OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(new(true, exact)))).ConfigureAwait(false);
            await cut.InvokeAsync(static () => { }).ConfigureAwait(false);
            AssertHeadlineWasWithdrawn();
        }

        service.AstrometryPublicationHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentAstrometryPublicationState>.Success(CameraAgentAstrometryPublicationState.Available));
        service.AstrometryHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentRetainedAstrometry>.Success(evidence));
        service.LatestAstrometryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(new(true, exact)));
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            AssertHeadlineIsVisible(exact);
            Assert.IsFalse(cut.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
        });

        void AssertHeadlineIsVisible(CameraAgentLatestAstrometry expected)
        {
            var section = cut.Find(".latest-measured-solution");
            StringAssert.Contains(section.TextContent, expected.ArtifactId.ToString(), StringComparison.Ordinal);
            StringAssert.Contains(section.TextContent, expected.MidpointUtc.ToString("O"), StringComparison.Ordinal);
            Assert.IsFalse(section.TextContent.Contains("temporarily unavailable", StringComparison.Ordinal));
        }

        void AssertHeadlineWasWithdrawn()
        {
            StringAssert.Contains(cut.Find(".latest-measured-solution").TextContent, "temporarily unavailable", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll(".latest-measured-solution time"));
        }
    }

    [TestMethod]
    public async Task QueuedLatestReadRestoresItsVerifiedSelection()
    {
        var evidence = await AstrometryEvidenceAsync().ConfigureAwait(false);
        var exact = CameraAgentLatestAstrometry.FromVerified(evidence)!;
        var older = exact with { CaptureId = Guid.NewGuid(), ArtifactId = Guid.NewGuid(), MidpointUtc = exact.MidpointUtc.AddSeconds(-20) };
        var selectedRead = new DeferredAstrometryRead<CameraAgentRetainedAstrometry>();
        var heldHistory = new DeferredAstrometryRead<CameraAgentLatestAstrometryRead>();
        var heldRecheck = new DeferredAstrometryRead<CameraAgentAstrometryPublicationState>();
        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(AstrometryImage(evidence)));
        service.AstrometryHandler = (_, _) => selectedRead.Read();
        service.LatestAstrometryHandler = (_, _) => heldHistory.Read();
        var checks = 0;
        service.AstrometryPublicationHandler = (_, _) => ++checks == 2 ? heldRecheck.Read() : ValueTask.FromResult(
            OperatorUiResult<CameraAgentAstrometryPublicationState>.Success(CameraAgentAstrometryPublicationState.Available));

        var cut = context.Render<CurrentSkyPage>();
        var timeout = TimeSpan.FromSeconds(3);
        await selectedRead.Awaiting.Task.WaitAsync(timeout).ConfigureAwait(false);
        await heldHistory.Awaiting.Task.WaitAsync(timeout).ConfigureAwait(false);
        await Task.Run(() => selectedRead.Complete(OperatorUiResult<CameraAgentRetainedAstrometry>.Success(evidence))).ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find(".pixel-sky-calculator button").HasAttribute("disabled")));
        await Task.Run(() => heldHistory.Complete(OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(new(true, older)))).ConfigureAwait(false);
        await heldRecheck.Awaiting.Task.WaitAsync(timeout).ConfigureAwait(false);

        var replayed = 0;
        service.LatestAstrometryHandler = (_, _) =>
        {
            Interlocked.Increment(ref replayed);
            return ValueTask.FromResult(OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(new(true, exact)));
        };
        await cut.InvokeAsync(() => cut.Find("button.refresh-link").ClickAsync()).ConfigureAwait(false);
        Assert.AreEqual(0, Volatile.Read(ref replayed));
        await Task.Run(() => heldRecheck.Complete(OperatorUiResult<CameraAgentAstrometryPublicationState>.Success(
            CameraAgentAstrometryPublicationState.Invalidated))).ConfigureAwait(false);
        cut.WaitForAssertion(AssertExactHeadlineAndCalculator);
        Assert.AreEqual(1, Volatile.Read(ref replayed));

        void AssertExactHeadlineAndCalculator()
        {
            StringAssert.Contains(cut.Find(".latest-measured-solution").TextContent, exact.ArtifactId.ToString(), StringComparison.Ordinal);
            Assert.IsFalse(cut.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
        }
    }

    [TestMethod]
    public async Task KnownHistoryInvalidationSurvivesASupersedingTransientRead()
    {
        var evidence = await AstrometryEvidenceAsync().ConfigureAwait(false);
        var original = CameraAgentLatestAstrometry.FromVerified(evidence)!;
        var replacement = evidence with { ArtifactId = Guid.NewGuid() };
        var older = original with { CaptureId = Guid.NewGuid(), ArtifactId = Guid.NewGuid(), MidpointUtc = original.MidpointUtc.AddSeconds(-20) };
        var heldLayer = new DeferredAstrometryRead<CameraAgentLayeredPresentation>();
        var heldHistory = new DeferredAstrometryRead<CameraAgentLatestAstrometryRead>();
        var heldRecheck = new DeferredAstrometryRead<CameraAgentAstrometryPublicationState>();
        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(AstrometryImage(evidence)));
        service.AstrometryHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentRetainedAstrometry>.Success(evidence));
        service.LatestAstrometryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(new(true, original)));

        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Find(".latest-measured-solution").TextContent, original.ArtifactId.ToString(), StringComparison.Ordinal);
            Assert.IsFalse(cut.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
        });
        service.PresentationHandler = (_, _) => heldLayer.Read();
        service.LatestAstrometrySelectionHandler = (_, previous, selected) =>
        {
            Assert.AreEqual(original, previous);
            Assert.AreEqual(original.ArtifactId, selected?.ArtifactId);
            return heldHistory.Read();
        };
        var timeout = TimeSpan.FromSeconds(3);
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        await heldLayer.Awaiting.Task.WaitAsync(timeout).ConfigureAwait(false);
        await heldHistory.Awaiting.Task.WaitAsync(timeout).ConfigureAwait(false);

        var replacementReads = 0;
        service.AstrometryPublicationHandler = (identity, _) =>
        {
            Assert.AreEqual(replacement.ArtifactId, identity.ArtifactId);
            return ++replacementReads == 1 ? ValueTask.FromResult(OperatorUiResult<CameraAgentAstrometryPublicationState>.Success(
                CameraAgentAstrometryPublicationState.Available)) : heldRecheck.Read();
        };
        var layer = Layered(evidence.CaptureId, new string('D', 64)) with { Astrometry = replacement };
        await Task.Run(() => heldLayer.Complete(OperatorUiResult<CameraAgentLayeredPresentation>.Success(layer))).ConfigureAwait(false);
        await cut.InvokeAsync(static () => { }).ConfigureAwait(false);
        Assert.AreEqual(1, replacementReads);

        var rejectedHistory = new CameraAgentLatestAstrometryRead(true, older)
        {
            PreviousSummaryQuarantined = true,
            SelectedAssessmentInvalidated = true
        };
        await Task.Run(() => heldHistory.Complete(OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(rejectedHistory))).ConfigureAwait(false);
        await heldRecheck.Awaiting.Task.WaitAsync(timeout).ConfigureAwait(false);
        service.LatestAstrometrySelectionHandler = (_, _, selected) =>
        {
            Assert.AreEqual(replacement.ArtifactId, selected?.ArtifactId);
            return ValueTask.FromResult(OperatorUiResult<CameraAgentLatestAstrometryRead>.Failure(
                OperatorUiResultKind.Unavailable, "Temporary history read failure."));
        };
        await cut.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        await Task.Run(() => heldRecheck.Complete(OperatorUiResult<CameraAgentAstrometryPublicationState>.Success(
            CameraAgentAstrometryPublicationState.Available))).ConfigureAwait(false);
        await cut.InvokeAsync(static () => { }).ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            var headline = cut.Find(".latest-measured-solution");
            StringAssert.Contains(headline.TextContent, "Showing the previously verified result", StringComparison.Ordinal);
            StringAssert.Contains(headline.TextContent, older.ArtifactId.ToString(), StringComparison.Ordinal);
            Assert.IsFalse(headline.TextContent.Contains(original.ArtifactId.ToString(), StringComparison.Ordinal));
            Assert.IsFalse(cut.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
        });
    }

    [TestMethod]
    public async Task BusyHistoryCoalescesRepeatedManualRefreshesWithoutRetryingItsOwnFailure()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var pending = new DeferredAstrometryRead<CameraAgentLatestAstrometryRead>();
        var failed = new OperatorUiResult<CameraAgentLatestAstrometryRead>(OperatorUiResultKind.Unavailable, new(false));
        var reads = 0;
        service.LatestAstrometryHandler = (_, _) => Interlocked.Increment(ref reads) == 1
            ? pending.Read() : ValueTask.FromResult(failed);

        var cut = context.Render<CurrentSkyPage>();
        await pending.Awaiting.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        for (var click = 0; click < 3; click++)
            await cut.InvokeAsync(() => cut.Find("button.refresh-link").ClickAsync()).ConfigureAwait(false);
        Assert.AreEqual(1, Volatile.Read(ref reads));

        await cut.InvokeAsync(() => pending.Complete(failed)).ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.AreEqual(2, Volatile.Read(ref reads)));
        await cut.InvokeAsync(static () => { }).ConfigureAwait(false);
        Assert.AreEqual(2, Volatile.Read(ref reads));
        StringAssert.Contains(cut.Find(".latest-measured-solution").TextContent, "temporarily unavailable", StringComparison.Ordinal);
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

    [TestMethod]
    [DataRow("matching", true, 1)]
    [DataRow("incomplete", true, 1)]
    [DataRow("different-healthy", true, 2)]
    [DataRow("different-quarantined", false, 2)]
    public async Task NonInvalidatingHeadlineLandingBeforeSelectedApplyPreservesOrRevalidatesFit(
        string scenario, bool expectEnabled, int expectedPublicationChecks)
    {
        var evidence = await AstrometryEvidenceAsync().ConfigureAwait(false);
        var exact = CameraAgentLatestAstrometry.FromVerified(evidence)!;
        var other = exact with { CaptureId = Guid.NewGuid(), ArtifactId = Guid.NewGuid(), MidpointUtc = exact.MidpointUtc.AddSeconds(-20) };
        var heldCheck = new DeferredAstrometryRead<CameraAgentAstrometryPublicationState>();
        var heldHeadline = new DeferredAstrometryRead<CameraAgentLatestAstrometryRead>();
        var publicationChecks = 0;

        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(AstrometryImage(evidence)));
        service.AstrometryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentRetainedAstrometry>.Success(evidence));
        service.LatestAstrometryHandler = (_, _) => heldHeadline.Read();
        service.AstrometryPublicationHandler = (_, _) => Interlocked.Increment(ref publicationChecks) == 1
            ? heldCheck.Read()
            : ValueTask.FromResult(OperatorUiResult<CameraAgentAstrometryPublicationState>.Success(
                scenario == "different-quarantined"
                    ? CameraAgentAstrometryPublicationState.Invalidated
                    : CameraAgentAstrometryPublicationState.Available));

        var cut = context.Render<CurrentSkyPage>();
        var timeout = TimeSpan.FromSeconds(5);
        await heldCheck.Awaiting.Task.WaitAsync(timeout).ConfigureAwait(false);
        await heldHeadline.Awaiting.Task.WaitAsync(timeout).ConfigureAwait(false);

        var headlineResult = scenario switch
        {
            "matching" => OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(new(true, exact)),
            "incomplete" => new OperatorUiResult<CameraAgentLatestAstrometryRead>(OperatorUiResultKind.Unavailable, new(false)),
            _ => OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(new(true, other))
        };

        // Hold the bUnit renderer dispatcher so ValidateAstrometryPublicationAsync returns Available and queues
        // LoadAstrometryAsync's apply callback, then ApplyLatestAstrometryReadAsync executes ahead of it.
        await cut.InvokeAsync(() =>
        {
            var queuedApply = Task.Run(() => heldCheck.Complete(
                OperatorUiResult<CameraAgentAstrometryPublicationState>.Success(CameraAgentAstrometryPublicationState.Available)));
            Assert.IsTrue(queuedApply.Wait(timeout));
            heldHeadline.Complete(headlineResult);
        }).ConfigureAwait(false);

        await cut.WaitForAssertionAsync(() => cut.InvokeAsync(() =>
        {
            Assert.AreEqual(expectedPublicationChecks, Volatile.Read(ref publicationChecks));
            Assert.AreEqual(!expectEnabled, cut.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
            Assert.AreEqual(expectEnabled ? "Accepted" : "Unavailable", cut.Find(".astrometry-card .state-chip").TextContent);
            if (scenario == "matching")
            {
                StringAssert.Contains(cut.Find(".latest-measured-solution").TextContent, exact.ArtifactId.ToString(), StringComparison.Ordinal);
            }
            else if (scenario == "incomplete")
            {
                StringAssert.Contains(cut.Find(".latest-measured-solution").TextContent, "temporarily unavailable", StringComparison.Ordinal);
            }
            else
            {
                StringAssert.Contains(cut.Find(".latest-measured-solution").TextContent, other.ArtifactId.ToString(), StringComparison.Ordinal);
            }
        }), timeout).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("matching", 1)]
    [DataRow("different-healthy", 2)]
    public async Task NonInvalidatingHeadlineLandingBeforeLayerApplyPreservesVerifiedLayers(
        string scenario, int expectedPublicationChecks)
    {
        var evidence = await AstrometryEvidenceAsync().ConfigureAwait(false);
        var exact = CameraAgentLatestAstrometry.FromVerified(evidence)!;
        var other = exact with { CaptureId = Guid.NewGuid(), ArtifactId = Guid.NewGuid(), MidpointUtc = exact.MidpointUtc.AddSeconds(-20) };
        var structured = WithStructuredBase();
        var capture = AstrometryImage(evidence).DisplayCapture!;
        var combined = structured.Stages.Single(slot => slot.Stage == CameraAgentPresentationStage.Combined);
        var image = structured with { DisplayCapture = capture, LatestCapture = capture };
        var enriched = evidence with { CompatibleBaseArtifactId = combined.DisplayArtifactId };
        var layer = Layered(evidence.CaptureId, new string('D', 64), combined.DisplayArtifactId) with { Astrometry = enriched };
        var heldCheck = new DeferredAstrometryRead<CameraAgentAstrometryPublicationState>();
        var heldHeadline = new DeferredAstrometryRead<CameraAgentLatestAstrometryRead>();
        var publicationChecks = 0;

        using var context = new BunitContext();
        context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js")
            .Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(image));
        service.PresentationHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentLayeredPresentation>.Success(layer));
        service.LatestAstrometryHandler = (_, _) => heldHeadline.Read();
        service.AstrometryPublicationHandler = (_, _) => Interlocked.Increment(ref publicationChecks) == 1
            ? heldCheck.Read()
            : ValueTask.FromResult(OperatorUiResult<CameraAgentAstrometryPublicationState>.Success(
                CameraAgentAstrometryPublicationState.Available));

        var cut = context.Render<CurrentSkyPage>();
        var timeout = TimeSpan.FromSeconds(5);
        await heldCheck.Awaiting.Task.WaitAsync(timeout).ConfigureAwait(false);
        await heldHeadline.Awaiting.Task.WaitAsync(timeout).ConfigureAwait(false);

        var headlineSummary = scenario == "matching" ? exact : other;
        await cut.InvokeAsync(() =>
        {
            var queuedApply = Task.Run(() => heldCheck.Complete(
                OperatorUiResult<CameraAgentAstrometryPublicationState>.Success(CameraAgentAstrometryPublicationState.Available)));
            Assert.IsTrue(queuedApply.Wait(timeout));
            heldHeadline.Complete(OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(new(true, headlineSummary)));
        }).ConfigureAwait(false);

        await cut.WaitForAssertionAsync(() => cut.InvokeAsync(() =>
        {
            Assert.AreEqual(expectedPublicationChecks, Volatile.Read(ref publicationChecks));
            Assert.IsFalse(cut.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
            Assert.AreEqual("Accepted", cut.Find(".astrometry-card .state-chip").TextContent);
            Assert.IsNotNull(cut.Find(".sky-layer-canvas--verified"));
            StringAssert.Contains(cut.Find(".latest-measured-solution").TextContent, headlineSummary.ArtifactId.ToString(), StringComparison.Ordinal);
        }), timeout).ConfigureAwait(false);
    }
}
