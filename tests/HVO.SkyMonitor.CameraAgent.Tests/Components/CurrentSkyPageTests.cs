using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class CurrentSkyPageTests
{
    [TestMethod]
    public void CurrentImageLeadsWithIndependentFreshnessAndSystemState()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(
            OperatorUiTestData.CurrentImage(
                CameraAgentPresentationImageFreshness.Delayed,
                CameraAgentPresentationSystemState.Standby)));

        var cut = context.Render<CurrentSkyPage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "Image delayed", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Standby", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Processed sky capture", StringComparison.Ordinal);
            Assert.AreEqual(
                "/api/v1/operations/artifacts/00000000-0000-0000-0000-000000000102/preview",
                cut.Find(".capture-image img").GetAttribute("src"));
        });
    }

    [TestMethod]
    public void AvailableStageCanBeSelectedAndUnavailableStageCannot()
    {
        using var context = new BunitContext();
        Configure(context);
        var cut = context.Render<CurrentSkyPage>();

        cut.WaitForElement(".capture-image");
        Assert.IsTrue(cut.Find("button[title='Calibrated: This stage was not produced.']").HasAttribute("disabled"));
        StringAssert.Contains(cut.Markup, "This stage was not produced.", StringComparison.Ordinal);

        cut.Find("button[title='Show Raw image']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("true", cut.Find("button[title='Show Raw image']").GetAttribute("aria-pressed"));
            StringAssert.Contains(
                cut.Find(".capture-image img").GetAttribute("src"),
                "00000000-0000-0000-0000-000000000101",
                StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Showing Raw", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void RefreshFailurePreservesLastSuccessfulImage()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var read = 0;
        service.CurrentImageHandler = _ => ValueTask.FromResult(Interlocked.Increment(ref read) == 1
            ? OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(OperatorUiTestData.CurrentImage())
            : OperatorUiResult<CameraAgentCurrentImagePresentation>.Failure(OperatorUiResultKind.Unavailable, "projection read failed"));
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForElement(".capture-image");

        cut.Find("button.refresh-link").Click();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "projection read failed", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "last successfully read image", StringComparison.Ordinal);
            Assert.IsNotNull(cut.Find(".capture-image img").GetAttribute("src"));
        });
    }

    [TestMethod]
    public void EmptyProjectionExplainsMissingImageWhileReportingSystemState()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(
            new CameraAgentCurrentImagePresentation(
                OperatorUiTestData.Now,
                CameraAgentPresentationImageFreshness.Empty,
                new CameraAgentPresentationSystemStatus(
                    CameraAgentPresentationSystemState.Unavailable,
                    "Capture status unavailable.",
                    OperatorUiTestData.Now),
                null,
                null,
                false,
                null,
                [],
                false,
                true)));

        var cut = context.Render<CurrentSkyPage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "No sky image is available yet", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "No image", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Camera unavailable", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "bounded history", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll(".capture-image__actions"));
        });
    }

    [TestMethod]
    public void LargeViewerUsesDialogAndRestoresFocusOnClose()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        Configure(context);
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForElement("#current-sky-view-large");
        Assert.IsEmpty(cut.FindComponent<HVO.SkyMonitor.CameraAgent.Components.Presentation.LargeImageViewer>()
            .FindAll("img"));

        cut.Find("#current-sky-view-large").Click();
        cut.WaitForAssertion(() => Assert.IsTrue(context.JSInterop.Invocations.Any(static invocation =>
            invocation.Identifier.EndsWith("show", StringComparison.Ordinal))));
        var viewer = cut.FindComponent<HVO.SkyMonitor.CameraAgent.Components.Presentation.LargeImageViewer>();
        Assert.HasCount(1, viewer.FindAll("img"));
        viewer.Find(".large-viewer__close").Click();

        cut.WaitForAssertion(() => Assert.IsTrue(context.JSInterop.Invocations.Any(static invocation =>
            invocation.Identifier.EndsWith("close", StringComparison.Ordinal))));
    }

    [TestMethod]
    public void SameSourcePreviewFailureRetriesAfterProjectionRefreshAndPreservesDetails()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var first = OperatorUiTestData.CurrentImage();
        var read = 0;
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(
            Interlocked.Increment(ref read) == 1 ? first : first with { ObservedUtc = first.ObservedUtc.AddSeconds(1) }));
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForElement(".capture-image img");

        cut.Find(".capture-image img").TriggerEvent("onerror", EventArgs.Empty);

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "Image preview unavailable", StringComparison.Ordinal);
            Assert.HasCount(1, cut.FindAll("a[href^='/gallery/']"));
            Assert.IsEmpty(cut.FindAll("#current-sky-view-large"));
        });
        cut.Find("button.refresh-link").Click();
        cut.WaitForElement(".capture-image img");
    }

    [TestMethod]
    public void LatestCaptureFactsAndDetailsRemainWhenNoImageCanBeDisplayed()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var projection = WithoutDisplayImage(OperatorUiTestData.CurrentImage());
        service.CurrentImageHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(projection));

        var cut = context.Render<CurrentSkyPage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "No sky image is available yet", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Simulated", StringComparison.Ordinal);
            Assert.AreEqual("/gallery/00000000-0000-0000-0000-000000000001", cut.Find("a[href^='/gallery/']").GetAttribute("href"));
        });
    }

    [TestMethod]
    public void RefreshWithoutDisplayImageClosesOpenViewer()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = Configure(context);
        var first = OperatorUiTestData.CurrentImage();
        var read = 0;
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(
            Interlocked.Increment(ref read) == 1 ? first : WithoutDisplayImage(first)));
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForElement("#current-sky-view-large");
        cut.Find("#current-sky-view-large").Click();
        cut.WaitForAssertion(() => Assert.IsTrue(context.JSInterop.Invocations.Any(static invocation =>
            invocation.Identifier.EndsWith("show", StringComparison.Ordinal))));

        cut.Find("button.refresh-link").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.IsEmpty(cut.FindAll("#current-sky-view-large"));
            Assert.IsTrue(context.JSInterop.Invocations.Any(static invocation =>
                invocation.Identifier.EndsWith("close", StringComparison.Ordinal)));
        });
    }

    [TestMethod]
    public async Task DisposalCancelsPendingProjectionRead()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CurrentImageHandler = async token =>
        {
            using var registration = token.Register(() => cancellationObserved.TrySetResult());
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return OperatorUiResult<CameraAgentCurrentImagePresentation>.Failure(OperatorUiResultKind.Unavailable, "unreachable");
        };
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForElement(".capture-image[aria-busy='true']");

        await cut.Instance.DisposeAsync().ConfigureAwait(false);

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    [TestMethod]
    public void RefreshReportsBusyStateAndNavigatesOnAuthorizationRevocation()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var reads = 0;
        service.CurrentImageHandler = _ => ValueTask.FromResult(Interlocked.Increment(ref reads) == 1
            ? OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(OperatorUiTestData.CurrentImage())
            : OperatorUiResult<CameraAgentCurrentImagePresentation>.Failure(OperatorUiResultKind.Unauthorized, "revoked"));
        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForElement(".current-sky-summary[aria-live='polite'][aria-atomic='true'][aria-busy='false']");

        cut.Find("button.refresh-link").Click();

        cut.WaitForAssertion(() => Assert.AreEqual(
            "/Account/AccessDenied",
            new Uri(context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri).AbsolutePath));
    }

    private static TestOperatorUiService Configure(BunitContext context)
    {
        var service = new TestOperatorUiService
        {
            CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(
                OperatorUiTestData.CurrentImage()))
        };
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);
        context.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(OperatorUiTestData.Now));
        return service;
    }

    private static CameraAgentCurrentImagePresentation WithoutDisplayImage(CameraAgentCurrentImagePresentation source)
        => source with
        {
            DisplayCapture = null,
            SelectedStage = null,
            Stages = source.Stages.Select(static slot => slot with
            {
                Availability = CameraAgentPresentationSlotAvailability.Missing,
                Reason = $"No artifact was produced for {slot.Label}.",
                ArtifactId = null,
                ArtifactRole = null,
                Variant = null,
                MediaType = null,
                PreviewUrl = null
            }).ToArray()
        };
}
