using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CurrentSkyPageTests
{
    [TestMethod]
    [DataRow(true, true)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public async Task InvalidatedPrefetchWaitsForFreshLayersOnlyWhenAdvertised(bool advertisedLayers, bool invalidateIncoming)
    {
        var fit = (await AstrometryEvidenceAsync().ConfigureAwait(false)) with { ArtifactId = Guid.NewGuid() };
        var firstImage = WithStructuredBase();
        var firstCapture = firstImage.DisplayCapture! with
        {
            CaptureId = fit.CaptureId,
            ExposureStartedUtc = fit.Solution!.Assessment.Frame.ExposureStartUtc
        };
        var first = View(firstImage with { DisplayCapture = firstCapture, LatestCapture = firstCapture });
        var firstBase = firstImage.Stages.Single(slot => slot.Stage == CameraAgentPresentationStage.Combined).DisplayArtifactId;
        fit = fit with { CompatibleBaseArtifactId = firstBase };
        var summary = CameraAgentLatestAstrometry.FromVerified(fit)!;
        var headline = invalidateIncoming
            ? summary with { CaptureId = SecondCaptureId, ArtifactId = Guid.NewGuid() }
            : summary;
        var priorLayers = Layered(fit.CaptureId, new string('D', 64));
        var measuredLayers = priorLayers with
        {
            Astrometry = fit,
            Layers = [.. priorLayers.Layers,
                new(new string('E', 64), AstrometricDiagnosticPresentation.AssociationsLayer, "measured-associations", 600, false, 1_000_000, "v1", "v1"),
                new(new string('F', 64), AstrometricDiagnosticPresentation.ResidualsLayer, "measured-residuals", 610, false, 1_000_000, "v1", "v1")]
        };
        var nextBase = Guid.Parse("00000000-0000-0000-0000-000000000204");
        var nextImage = Next(first, SecondCaptureId, firstCapture.CaptureSequence + 1);
        var incoming = nextImage with
        {
            Presentation = nextImage.Presentation with
            {
                StructuredLayersAvailable = advertisedLayers,
                Stages = nextImage.Presentation.Stages.Select(slot => slot.Stage == CameraAgentPresentationStage.Combined
                    ? slot with
                    {
                        DisplayArtifactId = nextBase,
                        DisplayReferenceId = nextBase,
                        PreviewUrl = new($"/api/v1/operations/artifacts/{nextBase:D}/preview", UriKind.Relative)
                    }
                    : slot).ToArray()
            }
        };

        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var current = first;
        var invalidation = new TaskCompletionSource<OperatorUiResult<CameraAgentLatestAstrometryRead>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleRead = new TaskCompletionSource<OperatorUiResult<CameraAgentLayeredPresentation>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var freshRead = new TaskCompletionSource<OperatorUiResult<CameraAgentLayeredPresentation>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var freshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestReads = 0;
        var incomingFreshReads = 0;
        var unavailable = OperatorUiResult<CameraAgentLayeredPresentation>.Failure(OperatorUiResultKind.Unavailable, "Incoming layer read failed.");
        service.CurrentSkyHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(current));
        service.LatestAstrometryHandler = (_, _) => ++latestReads switch
        {
            1 => ValueTask.FromResult(OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(new(true, headline))),
            2 => new(invalidation.Task),
            _ => ValueTask.FromResult(OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(new(true)))
        };
        service.PresentationHandler = (id, _) =>
        {
            if (id == firstCapture.CaptureId)
                return ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(measuredLayers));
            Assert.AreEqual(SecondCaptureId, id);
            staleStarted.TrySetResult();
            return new(staleRead.Task);
        };
        service.CurrentPresentationHandler = (id, _) =>
        {
            if (id == firstCapture.CaptureId)
                return ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(priorLayers));
            Assert.AreEqual(SecondCaptureId, id);
            if (Interlocked.Increment(ref incomingFreshReads) == 1)
            {
                freshStarted.TrySetResult();
                return new(freshRead.Task);
            }
            return ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(
                Layered(id, new string('B', 64), nextBase)));
        };

        var page = context.Render<CurrentSkyPage>();
        WaitForFirst(page, clock, first);
        page.WaitForAssertion(() =>
        {
            Assert.IsFalse(page.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
            Assert.IsNotEmpty(page.FindAll("input[data-layer-target='measured-residuals']"));
            Assert.IsNotEmpty(page.FindAll("input[data-layer-target='measured-associations']"));
            StringAssert.Contains(page.Find(".latest-measured-solution").TextContent, summary.SolutionIdentitySha256, StringComparison.Ordinal);
        });
        var firstImageUrl = page.Find(".sky-layer-canvas img").GetAttribute("src");
        await page.Find(".pixel-sky-calculator input").ChangeAsync("280").ConfigureAwait(false);
        await page.FindAll(".pixel-sky-calculator input")[1].ChangeAsync("240").ConfigureAwait(false);
        await page.Find("form.pixel-sky-calculator").SubmitAsync().ConfigureAwait(false);
        Assert.HasCount(1, page.FindAll(".astrometry-calculator-result"));

        await page.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        Assert.AreEqual(2, latestReads);
        current = incoming;
        var refresh = page.Find("button.refresh-link").ClickAsync();
        try
        {
            await staleStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            invalidation.SetResult(OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(new(true)
            {
                PreviousSummaryEvicted = true,
                SelectedAssessmentInvalidated = invalidateIncoming
            }));
            page.WaitForAssertion(() =>
            {
                Assert.AreEqual(CaptureLink(first), page.Find(".layers-link").GetAttribute("href"));
                Assert.AreEqual(firstImageUrl, page.Find(".sky-layer-canvas img").GetAttribute("src"));
                Assert.IsTrue(page.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
                Assert.IsEmpty(page.FindAll(".astrometry-calculator-result, input[data-layer-target='measured-residuals'], input[data-layer-target='measured-associations']"));
            });

            staleRead.SetResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(SecondCaptureId, new string('C', 64), nextBase)));
            if (!invalidateIncoming)
            {
                var firstCompletion = await Task.WhenAny(refresh, freshStarted.Task).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.AreSame(refresh, firstCompletion, "Invalidation of the outgoing capture must not restart an eligible incoming projection.");
                await refresh.ConfigureAwait(false);
                Assert.AreEqual(CaptureLink(incoming), page.Find(".layers-link").GetAttribute("href"));
                Assert.AreEqual(0, incomingFreshReads);
                StringAssert.Contains(page.Find(".sky-layer-canvas img").GetAttribute("src"), nextBase.ToString("D"), StringComparison.Ordinal);
                return;
            }
            await freshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            // Drain the renderer work that requested the replacement before checking what it made visible.
            await page.InvokeAsync(() => { }).ConfigureAwait(false);
            var expectedLink = CaptureLink(advertisedLayers ? first : incoming);
            var heldLink = page.Find(".layers-link").GetAttribute("href");
            Console.WriteLine($"advertised={advertisedLayers} fresh-read=held expected={expectedLink} displayed={heldLink}");
            Assert.AreEqual(expectedLink, heldLink);
            if (advertisedLayers)
                Assert.AreEqual(firstImageUrl, page.Find(".sky-layer-canvas img").GetAttribute("src"));

            freshRead.SetResult(unavailable);
            await refresh.ConfigureAwait(false);
            Assert.AreEqual(expectedLink, page.Find(".layers-link").GetAttribute("href"));
            Assert.IsTrue(page.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
            if (!advertisedLayers)
            {
                page.WaitForAssertion(() => StringAssert.Contains(page.Find(".sky-layer-unavailable").TextContent,
                    "Incoming layer read failed.", StringComparison.Ordinal));
                return;
            }

            Assert.AreEqual(firstImageUrl, page.Find(".sky-layer-canvas img").GetAttribute("src"));
            Assert.IsEmpty(page.FindAll(".sky-layer-unavailable"));
            await page.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
            page.WaitForAssertion(() =>
            {
                Assert.AreEqual(CaptureLink(incoming), page.Find(".layers-link").GetAttribute("href"));
                Assert.HasCount(1, page.FindAll(".sky-layer-workspace"));
                StringAssert.Contains(page.Find(".sky-layer-canvas img").GetAttribute("src"), nextBase.ToString("D"), StringComparison.Ordinal);
                Assert.IsTrue(page.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
            });
            Assert.AreEqual(2, incomingFreshReads);
        }
        finally
        {
            invalidation.TrySetResult(OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(new(true)));
            staleRead.TrySetResult(unavailable);
            freshRead.TrySetResult(unavailable);
            await refresh.ConfigureAwait(false);
        }
    }

    [TestMethod]
    [DataRow("different-base")]
    [DataRow("ambiguous-derivative")]
    [DataRow("missing-base")]
    [DataRow("preview-dimensions")]
    [DataRow("combined-selected")]
    [DataRow("no-layers")]
    public async Task RefreshSwitchUsesTheBaseAndGeometryOfTheSelectedDisplay(string scenario)
    {
        var image = WithStructuredBase();
        var fit = await AstrometryEvidenceAsync().ConfigureAwait(false);
        var shown = image.DisplayCapture! with { CaptureId = fit.CaptureId, ExposureStartedUtc = fit.Solution!.Assessment.Frame.ExposureStartUtc };
        var first = View(image with { DisplayCapture = shown, LatestCapture = shown });
        var firstLayers = Layered(fit.CaptureId, new string('D', 64));
        firstLayers = firstLayers with { Astrometry = fit with { CompatibleBaseArtifactId = firstLayers.BaseArtifactId } };
        var second = Next(first, SecondCaptureId, shown.CaptureSequence + 1);
        var secondBase = Guid.Parse("00000000-0000-0000-0000-000000000204");
        var linearBase = Guid.Parse("00000000-0000-0000-0000-000000000203");
        var eligible = second.Presentation.Stages.Single(slot => slot.Stage == CameraAgentPresentationStage.Combined) with
        {
            ArtifactId = linearBase,
            DisplayArtifactId = secondBase,
            DisplayReferenceId = secondBase,
            PreviewUrl = new($"/api/v1/operations/artifacts/{secondBase:D}/preview", UriKind.Relative),
            PreviewWidth = 640,
            PreviewHeight = 480
        };
        var offered = scenario switch
        {
            "ambiguous-derivative" or "combined-selected" => eligible with
            {
                DisplayBasis = CameraAgentPresentationDisplayBasis.OwnArtifact,
                DisplayArtifactId = linearBase,
                DisplayReferenceId = null,
                PreviewUrl = new($"/api/v1/operations/artifacts/{linearBase:D}/preview", UriKind.Relative)
            },
            "missing-base" => eligible with { Availability = CameraAgentPresentationSlotAvailability.Unavailable, PreviewUrl = null },
            "preview-dimensions" => eligible with { PreviewWidth = 320, PreviewHeight = 240 },
            _ => eligible
        };
        using var context = new BunitContext();
        var (service, clock) = ConfigurePolling(context);
        var current = first;
        var reportedBase = scenario == "different-base" ? Guid.NewGuid() : secondBase;
        service.CurrentSkyHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(current));
        service.PresentationHandler = (id, _) => ValueTask.FromResult(id == shown.CaptureId
            ? OperatorUiResult<CameraAgentLayeredPresentation>.Success(firstLayers)
            : scenario == "no-layers"
                ? OperatorUiResult<CameraAgentLayeredPresentation>.Failure(OperatorUiResultKind.NotFound, "No retained layers.")
                : OperatorUiResult<CameraAgentLayeredPresentation>.Success(Layered(id, new string('E', 64), reportedBase)));

        var page = context.Render<CurrentSkyPage>();
        WaitForFirst(page, clock, first);
        page.WaitForElement(".sky-layer-canvas--verified");
        page.WaitForAssertion(() => Assert.IsFalse(page.Find(".pixel-sky-calculator button").HasAttribute("disabled")));
        var firstUrl = page.Find(".sky-layer-canvas img").GetAttribute("src");
        if (scenario == "combined-selected")
            await page.Find("button[title='Show Combined image']").ClickAsync().ConfigureAwait(false);
        current = WithBase(second, offered, scenario != "no-layers");
        await page.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        if (scenario is "combined-selected" or "no-layers")
        {
            Assert.AreEqual(CaptureLink(second), page.Find(".layers-link").GetAttribute("href"));
            Assert.IsEmpty(page.FindAll(".sky-layer-workspace"));
            if (scenario == "combined-selected")
                Assert.AreEqual(offered.PreviewUrl!.OriginalString, page.Find(".capture-image img").GetAttribute("src"));
            return;
        }

        Assert.AreEqual(CaptureLink(first), page.Find(".layers-link").GetAttribute("href"));
        Assert.AreEqual(firstUrl, page.Find(".sky-layer-canvas img").GetAttribute("src"));
        Assert.IsFalse(page.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
        // Other cases repair the same proposed capture. The ambiguous capture can also be passed over by rotation.
        var recovered = scenario == "ambiguous-derivative"
            ? Next(first, Guid.NewGuid(), shown.CaptureSequence + 2)
            : second;
        current = WithBase(recovered, eligible, true);
        reportedBase = secondBase;
        await page.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
        page.WaitForAssertion(() =>
        {
            Assert.AreEqual(CaptureLink(recovered), page.Find(".layers-link").GetAttribute("href"));
            StringAssert.Contains(page.Find(".sky-layer-canvas img").GetAttribute("src"), secondBase.ToString("D"), StringComparison.Ordinal);
            Assert.IsTrue(page.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
        });

        static CameraAgentCurrentSkyView WithBase(CameraAgentCurrentSkyView candidate,
            CameraAgentPresentationSlot slot, bool advertised) => candidate with
        {
            Presentation = candidate.Presentation with
            {
                StructuredLayersAvailable = advertised,
                Stages = candidate.Presentation.Stages.Select(stage => stage.Stage == slot.Stage ? slot : stage).ToArray()
            }
        };
    }
}
