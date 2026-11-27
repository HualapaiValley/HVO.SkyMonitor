using System.Text;
using System.Text.Json;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CurrentSkyPageTests
{
    [TestMethod]
    public async Task LegacyCurrentSkyReconcilesMeasuredDeepSkyAndExpectedNotDetectedDiagnosticSlots()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(WithStructuredBase()));
        service.PresentationHandler = (id, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(id, new string('D', 64))));
        context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js")
            .Setup<string>("bindLayerToggles", _ => true).SetResult("valid");

        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find(".restore-layers").HasAttribute("disabled")));

        var panelText = cut.Find(".scene-panel").TextContent;
        Assert.IsFalse(panelText.Contains("arrives with #526", StringComparison.Ordinal),
            "Stale #526 placeholder text must be replaced.");
        StringAssert.Contains(panelText, "measured product not produced (requires registered stack)", StringComparison.Ordinal);
        StringAssert.Contains(panelText, "Expected, not detected", StringComparison.Ordinal);
        StringAssert.Contains(panelText, "diagnostic catalog positions / not retained for this capture", StringComparison.Ordinal);

        // Now supply both pre-solve measured catalog associations and Expected, not detected retained layers.
        service.PresentationHandler = (id, _) =>
        {
            var baseline = Layered(id, new string('D', 64));
            return ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(baseline with
            {
                Layers =
                [
                    baseline.Layers[0],
                    new(new string('A', 64), MeasuredAssociationPresentation.MeasuredLayerKind, "hvo-layer-1", 21, true, 1_000_000, "renderer-v1", "style-v1"),
                    new(new string('B', 64), MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind, "hvo-layer-2", 23, false, 1_000_000, "renderer-v1", "style-v1")
                ],
                Svg = Encoding.UTF8.GetBytes("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 640 480"><g id="hvo-layer-0"></g><g id="hvo-layer-1"></g><g id="hvo-layer-2"></g></svg>""")
            }));
        };
        var withExpected = context.Render<CurrentSkyPage>();
        withExpected.WaitForAssertion(() => Assert.IsFalse(withExpected.Find(".restore-layers").HasAttribute("disabled")));

        var measuredToggle = withExpected.Find("[aria-label='Measured layers'] input[data-layer-target='hvo-layer-1']");
        Assert.IsFalse(measuredToggle.HasAttribute("disabled"));
        Assert.IsTrue(measuredToggle.HasAttribute("checked"));
        StringAssert.Contains(measuredToggle.ParentElement!.TextContent, "Measured catalog associations", StringComparison.Ordinal);

        var expectedToggle = withExpected.Find("[aria-label='Diagnostics layers'] input[data-layer-target='hvo-layer-2']");
        Assert.IsFalse(expectedToggle.HasAttribute("disabled"));
        Assert.IsFalse(expectedToggle.HasAttribute("checked"));
        StringAssert.Contains(expectedToggle.ParentElement!.TextContent, "Expected, not detected", StringComparison.Ordinal);
        Assert.IsFalse(withExpected.Find("[aria-label='Diagnostics layers']").TextContent
            .Contains("not retained for this capture", StringComparison.Ordinal));

        await expectedToggle.ChangeAsync(true).ConfigureAwait(false);
        Assert.IsTrue(withExpected.Find("input[data-layer-target='hvo-layer-2']").HasAttribute("checked"));
    }

    [TestMethod]
    public void CenteredCurrentSkyGroupsPreSolveMeasuredAssociationsAndExpectedNotDetectedIndependently()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var reference = CenteredExposure(103, clock.GetUtcNow().AddSeconds(-10));
        var compatibility = new PresentationCompatibilityDescriptor(640, 480, new string('B', 64), new string('C', 64));
        var view = CenteredStack(
            reference,
            [CenteredExposure(102, reference.ExposureStartedUtc.AddSeconds(-1)), reference,
             CenteredExposure(104, reference.ExposureStartedUtc.AddSeconds(1))],
            1,
            [
                ReadyLayer(MeasuredAssociationPresentation.MeasuredLayerKind, reference.Capture.CaptureId, 1, compatibility, enabledByDefault: true),
                ReadyLayer(MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind, reference.Capture.CaptureId, 2, compatibility, enabledByDefault: false),
                new(new("measured-deep-sky", CenteredViewLayerScope.Stack, null), 3, EvidencePin("measured-deep-sky"),
                    OperatorUiTestData.Now, CenteredViewReadiness.Ready, null, Drawable("measured-deep-sky", 36, false, compatibility))
            ],
            compatibility);
        service.CenteredCurrentSkyHandler = (_, _) =>
            ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(
                CenteredSkyView(view, reference, CenteredViewLiveState.Ready, clock.GetUtcNow())));

        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() =>
        {
            var measured = cut.Find($"[aria-label='Measured layers'] [data-layer-kind='{MeasuredAssociationPresentation.MeasuredLayerKind}']");
            Assert.AreEqual("Ready", measured.QuerySelector(".layer-readiness-tag")?.TextContent);
            StringAssert.Contains(measured.TextContent, "Measured catalog associations", StringComparison.Ordinal);
            Assert.IsFalse(measured.QuerySelector("input")!.HasAttribute("disabled"));
            Assert.IsTrue(measured.QuerySelector("input")!.HasAttribute("checked"));

            var deepSky = cut.Find("[aria-label='Measured layers'] [data-layer-kind='measured-deep-sky']");
            Assert.AreEqual("Ready", deepSky.QuerySelector(".layer-readiness-tag")?.TextContent);

            var expected = cut.Find($"[aria-label='Diagnostics layers'] [data-layer-kind='{MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind}']");
            Assert.AreEqual("Ready", expected.QuerySelector(".layer-readiness-tag")?.TextContent);
            StringAssert.Contains(expected.TextContent, "Expected, not detected", StringComparison.Ordinal);
            StringAssert.Contains(expected.TextContent, "never a detection claim", StringComparison.Ordinal);
            Assert.IsFalse(expected.QuerySelector("input")!.HasAttribute("disabled"));
            Assert.IsFalse(expected.QuerySelector("input")!.HasAttribute("checked"));
        });
    }

    [TestMethod]
    public void CenteredCurrentSkyRendersTruthfulEmptyAndParentInvalidReasons()
    {
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var reference = CenteredExposure(105, clock.GetUtcNow().AddSeconds(-10));
        var compatibility = new PresentationCompatibilityDescriptor(640, 480, new string('B', 64), new string('C', 64));
        var view = CenteredStack(
            reference,
            [CenteredExposure(104, reference.ExposureStartedUtc.AddSeconds(-1)), reference,
             CenteredExposure(106, reference.ExposureStartedUtc.AddSeconds(1))],
            1,
            [
                new(new(MeasuredAssociationPresentation.MeasuredLayerKind, CenteredViewLayerScope.Reference, reference.Capture.CaptureId),
                    1, EvidencePin("measured-invalid"), OperatorUiTestData.Now, CenteredViewReadiness.Incompatible,
                    "centered.association-parent-invalid", null),
                new(new(MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind, CenteredViewLayerScope.Reference, reference.Capture.CaptureId),
                    2, EvidencePin("expected-filtered"), OperatorUiTestData.Now, CenteredViewReadiness.AssessedEmpty,
                    MeasuredAssociationPresentation.EmptyExpectedFilteredReason, null)
            ],
            compatibility);
        service.CenteredCurrentSkyHandler = (_, _) =>
            ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(
                CenteredSkyView(view, reference, CenteredViewLiveState.Ready, clock.GetUtcNow())));

        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() =>
        {
            var measured = cut.Find($"[aria-label='Measured layers'] [data-layer-kind='{MeasuredAssociationPresentation.MeasuredLayerKind}']");
            StringAssert.Contains(measured.TextContent, "measured association parent verification failed", StringComparison.Ordinal);

            var expected = cut.Find($"[aria-label='Diagnostics layers'] [data-layer-kind='{MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind}']");
            StringAssert.Contains(expected.TextContent, "unmatched predictions excluded by display filter or aperture", StringComparison.Ordinal);
            Assert.IsFalse(expected.TextContent.Contains("all expected catalog predictions matched", StringComparison.Ordinal));
        });
    }
}
