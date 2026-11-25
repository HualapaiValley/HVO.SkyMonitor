using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CurrentSkyPageTests
{
    [TestMethod]
    [DataRow(true, true, false)]
    [DataRow(false, true, false)]
    [DataRow(true, false, false)]
    [DataRow(true, false, true)]
    public async Task InvalidatedPrefetchWaitsForFreshLayersOnlyWhenAdvertised(bool advertisedLayers, bool invalidateIncoming, bool mismatchedBase)
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
        var offeredBase = mismatchedBase ? Guid.NewGuid() : nextBase;
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
        var invalidation = new DeferredAstrometryRead<CameraAgentLatestAstrometryRead>();
        var staleRead = new DeferredAstrometryRead<CameraAgentLayeredPresentation>();
        var freshRead = new DeferredAstrometryRead<CameraAgentLayeredPresentation>();
        var latestReads = 0;
        var incomingFreshReads = 0;
        var unavailable = OperatorUiResult<CameraAgentLayeredPresentation>.Failure(OperatorUiResultKind.Unavailable, "Incoming layer read failed.");
        service.CurrentSkyHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentSkyView>.Success(current));
        service.LatestAstrometryHandler = (_, _) => ++latestReads switch
        {
            1 => ValueTask.FromResult(OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(new(true, headline))),
            2 => invalidation.Read(),
            _ => ValueTask.FromResult(OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(new(true)))
        };
        service.PresentationHandler = (id, _) =>
        {
            if (id == firstCapture.CaptureId)
                return ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(measuredLayers));
            Assert.AreEqual(SecondCaptureId, id);
            return staleRead.Read();
        };
        service.CurrentPresentationHandler = (id, _) =>
        {
            if (id == firstCapture.CaptureId)
                return ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(priorLayers));
            Assert.AreEqual(SecondCaptureId, id);
            if (Interlocked.Increment(ref incomingFreshReads) == 1 && !mismatchedBase)
                return freshRead.Read();
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
        await invalidation.Awaiting.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        current = incoming;
        var refresh = page.Find("button.refresh-link").ClickAsync();
        try
        {
            await staleRead.Awaiting.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Task.Run(() => invalidation.Complete(OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(new(true)
            {
                PreviousSummaryEvicted = true,
                SelectedAssessmentInvalidated = invalidateIncoming
            }))).ConfigureAwait(false);
            // Drain ApplyLatestAstrometryReadAsync and the replacement prior-layer read it queues on the renderer.
            await page.InvokeAsync(static () => { }).ConfigureAwait(false);
            await page.InvokeAsync(static () => { }).ConfigureAwait(false);
            await page.InvokeAsync(() =>
            {
                Assert.AreEqual(CaptureLink(first), page.Find(".layers-link").GetAttribute("href"));
                Assert.AreEqual(firstImageUrl, page.Find(".sky-layer-canvas img").GetAttribute("src"));
                Assert.IsTrue(page.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
                Assert.IsEmpty(page.FindAll(".astrometry-calculator-result, input[data-layer-target='measured-residuals'], input[data-layer-target='measured-associations']"));
            }).ConfigureAwait(false);

            await Task.Run(() => staleRead.Complete(OperatorUiResult<CameraAgentLayeredPresentation>.Success(
                Layered(SecondCaptureId, new string('C', 64), offeredBase)))).ConfigureAwait(false);
            if (mismatchedBase)
            {
                await refresh.ConfigureAwait(false);
                Assert.AreEqual(CaptureLink(first), page.Find(".layers-link").GetAttribute("href"));
                Assert.AreEqual(firstImageUrl, page.Find(".sky-layer-canvas img").GetAttribute("src"));
                Assert.IsTrue(page.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
                Assert.IsEmpty(page.FindAll(".astrometry-calculator-result, input[data-layer-target='measured-residuals'], input[data-layer-target='measured-associations']"));
                Assert.AreEqual(0, incomingFreshReads);

                await page.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
                Assert.AreEqual(CaptureLink(incoming), page.Find(".layers-link").GetAttribute("href"));
                StringAssert.Contains(page.Find(".sky-layer-canvas img").GetAttribute("src"), nextBase.ToString("D"), StringComparison.Ordinal);
                Assert.AreEqual(1, incomingFreshReads);
                return;
            }
            if (!invalidateIncoming)
            {
                var firstCompletion = await Task.WhenAny(refresh, freshRead.Awaiting.Task).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.AreSame(refresh, firstCompletion, "Invalidation of the outgoing capture must not restart an eligible incoming projection.");
                await refresh.ConfigureAwait(false);
                Assert.AreEqual(CaptureLink(incoming), page.Find(".layers-link").GetAttribute("href"));
                Assert.AreEqual(0, incomingFreshReads);
                StringAssert.Contains(page.Find(".sky-layer-canvas img").GetAttribute("src"), nextBase.ToString("D"), StringComparison.Ordinal);
                return;
            }
            await freshRead.Awaiting.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            // Drain the renderer work that requested the replacement before checking what it made visible.
            await page.InvokeAsync(static () => { }).ConfigureAwait(false);
            var expectedLink = CaptureLink(advertisedLayers ? first : incoming);
            var heldLink = page.Find(".layers-link").GetAttribute("href");
            Console.WriteLine($"advertised={advertisedLayers} fresh-read=held expected={expectedLink} displayed={heldLink}");
            Assert.AreEqual(expectedLink, heldLink);
            if (advertisedLayers)
                Assert.AreEqual(firstImageUrl, page.Find(".sky-layer-canvas img").GetAttribute("src"));

            await Task.Run(() => freshRead.Complete(unavailable)).ConfigureAwait(false);
            await refresh.ConfigureAwait(false);
            // Drain the detached layer-read application and its standalone astrometry follow-up before asserting DOM state.
            await page.InvokeAsync(static () => { }).ConfigureAwait(false);
            await page.InvokeAsync(static () => { }).ConfigureAwait(false);
            await page.InvokeAsync(() =>
            {
                Assert.AreEqual(expectedLink, page.Find(".layers-link").GetAttribute("href"));
                Assert.IsTrue(page.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
                if (!advertisedLayers)
                {
                    StringAssert.Contains(page.Find(".sky-layer-unavailable").TextContent,
                        "Incoming layer read failed.", StringComparison.Ordinal);
                }
            }).ConfigureAwait(false);
            if (!advertisedLayers)
                return;

            Assert.AreEqual(firstImageUrl, page.Find(".sky-layer-canvas img").GetAttribute("src"));
            Assert.IsEmpty(page.FindAll(".sky-layer-unavailable"));
            await page.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
            await page.InvokeAsync(static () => { }).ConfigureAwait(false);
            await page.InvokeAsync(() =>
            {
                Assert.AreEqual(CaptureLink(incoming), page.Find(".layers-link").GetAttribute("href"));
                Assert.HasCount(1, page.FindAll(".sky-layer-workspace"));
                StringAssert.Contains(page.Find(".sky-layer-canvas img").GetAttribute("src"), nextBase.ToString("D"), StringComparison.Ordinal);
                Assert.IsTrue(page.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
            }).ConfigureAwait(false);
            Assert.AreEqual(2, incomingFreshReads);
        }
        finally
        {
            await Task.Run(() =>
            {
                invalidation.TryComplete(OperatorUiResult<CameraAgentLatestAstrometryRead>.Success(new(true)));
                staleRead.TryComplete(unavailable);
                freshRead.TryComplete(unavailable);
            }).ConfigureAwait(false);
            await refresh.ConfigureAwait(false);
        }
    }

    [TestMethod]
    [DataRow("different-base")]
    [DataRow("ambiguous-derivative")]
    [DataRow("sustained-mismatch")]
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
            "ambiguous-derivative" or "sustained-mismatch" or "combined-selected" => eligible with
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
        if (scenario == "sustained-mismatch")
        {
            var newer = Next(first, Guid.NewGuid(), shown.CaptureSequence + 2);
            current = WithBase(newer, offered, true) with { ProcessingPending = true };
            await page.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
            Assert.AreEqual(CaptureLink(first), page.Find(".layers-link").GetAttribute("href"));
            Assert.IsFalse(page.Find(".pixel-sky-calculator button").HasAttribute("disabled"));

            current = current with { ProcessingPending = false, MinimumRefreshInterval = TimeSpan.FromSeconds(45) };
            clock.Advance(TimeSpan.FromSeconds(15));
            var read = service.CurrentSkyHandler;
            var tick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.CurrentSkyHandler = async token =>
            {
                var result = await read(token).ConfigureAwait(false);
                tick.TrySetResult();
                return result;
            };
            clock.Advance(TimeSpan.FromSeconds(15));
            await tick.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            page.WaitForAssertion(() => Assert.IsFalse(page.Find("button.refresh-link").HasAttribute("disabled")));
            Assert.AreEqual(CaptureLink(first), page.Find(".layers-link").GetAttribute("href"));

            clock.Advance(TimeSpan.FromSeconds(15));
            page.WaitForAssertion(() =>
            {
                Assert.AreEqual(CaptureLink(newer), page.Find(".layers-link").GetAttribute("href"));
                Assert.AreEqual(offered.PreviewUrl!.OriginalString, page.Find(".capture-image img").GetAttribute("src"));
                Assert.IsEmpty(page.FindAll(".sky-layer-workspace"));
                StringAssert.Contains(page.Find(".sky-layer-unavailable").TextContent, "manifest base does not match", StringComparison.Ordinal);
                Assert.IsTrue(page.Find(".pixel-sky-calculator button").HasAttribute("disabled"));
            });
            current = WithBase(second, eligible, true);
            await page.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
            Assert.AreEqual(CaptureLink(newer), page.Find(".layers-link").GetAttribute("href"));
            current = WithBase(newer, eligible, true);
            await page.Find("button.refresh-link").ClickAsync().ConfigureAwait(false);
            page.WaitForAssertion(() =>
            {
                Assert.AreEqual(CaptureLink(newer), page.Find(".layers-link").GetAttribute("href"));
                StringAssert.Contains(page.Find(".sky-layer-canvas img").GetAttribute("src"), secondBase.ToString("D"), StringComparison.Ordinal);
                Assert.IsFalse(page.Find(".sky-layer-save button").HasAttribute("disabled"));
            });
            return;
        }

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
