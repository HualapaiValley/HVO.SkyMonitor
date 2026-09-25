using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class GalleryPageTests
{
    [TestMethod]
    public void EmptyAndError_RenderExplicitStates()
    {
        using var emptyContext = new BunitContext();
        Configure(emptyContext);
        var empty = emptyContext.Render<GalleryPage>();
        empty.WaitForAssertion(() => StringAssert.Contains(empty.Markup, "No captures match these filters", StringComparison.Ordinal));

        using var errorContext = new BunitContext();
        var errorService = Configure(errorContext);
        errorService.GalleryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryPage>.Failure(OperatorUiResultKind.Unavailable, "The gallery is temporarily unavailable."));
        var error = errorContext.Render<GalleryPage>();
        error.WaitForAssertion(() => Assert.AreEqual("alert", error.Find(".gallery-state--error").GetAttribute("role")));
    }

    [TestMethod]
    public void BoundedPage_RendersImageLedCardsAndCollapsedAdvancedFilters()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var simulated = OperatorUiTestData.Capture();
        var fixture = OperatorUiTestData.Capture(Guid.Parse("00000000-0000-0000-0000-000000000002"), GalleryEvidenceOrigin.DeveloperFixture, annotated: false) with { CaptureSequence = 43 };
        service.GalleryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryPage>.Success(new CameraAgentGalleryPage([simulated, fixture], "older-cursor")));

        var cut = context.Render<GalleryPage>();

        cut.WaitForAssertion(() =>
        {
            var images = cut.FindAll(".capture-image img");
            Assert.HasCount(2, images);
            StringAssert.Contains(images[0].GetAttribute("src")!, "/api/v1/operations/artifacts/", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Processed presentation", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Capture #42", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Additional capture history remains unloaded", StringComparison.Ordinal);
            Assert.IsTrue(cut.FindAll("a[href^='/gallery/']").Count >= 2);
            Assert.HasCount(2, cut.FindAll(".capture-card"));
        });
    }

    [TestMethod]
    public void CardsUseTruthfulFallbackAndRetainFactsWhenNoImageIsDisplayable()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var combinedId = Guid.Parse("00000000-0000-0000-0000-000000000201");
        var combined = OperatorUiTestData.Capture() with
        {
            Artifacts = [new CameraAgentGalleryArtifact(
                combinedId,
                HVO.SkyMonitor.AgentCore.FrameArtifactRole.Combined,
                "combined",
                "stack",
                OperatorUiTestData.Now,
                "application/x-hvo-packed-image",
                new string('C', 64),
                1024,
                null,
                [],
                "combined-node",
                PixelFormat: HVO.SkyMonitor.AgentCore.CameraPixelFormat.Mono16,
                PreviewReconstructionSupported: true)]
        };
        var unavailable = OperatorUiTestData.Capture(
            Guid.Parse("00000000-0000-0000-0000-000000000202"),
            GalleryEvidenceOrigin.Unknown) with
        {
            CaptureSequence = 41,
            Artifacts = [],
            ProcessingNodes = []
        };
        service.GalleryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryPage>.Success(new([combined, unavailable], null)));

        var cut = context.Render<GalleryPage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Find(".capture-image-overlay small").TextContent, "Combined", StringComparison.Ordinal);
            StringAssert.Contains(cut.FindAll(".capture-card")[1].TextContent, "Capture facts retained", StringComparison.Ordinal);
            StringAssert.Contains(cut.FindAll(".capture-card")[1].TextContent, "No display product", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void PreviewFailureShowsPlaceholderAndRetainsFacts()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = Configure(context);
        service.GalleryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryPage>.Success(new([OperatorUiTestData.Capture()], null)));
        var cut = context.Render<GalleryPage>();
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll(".capture-image img")));

        cut.Find(".capture-image img").TriggerEvent("onerror", EventArgs.Empty);
        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Find(".capture-image-placeholder").TextContent, "Image preview unavailable", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find(".capture-image-placeholder").TextContent, "Capture facts retained", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find(".capture-image").GetAttribute("aria-label")!, "Image preview unavailable", StringComparison.Ordinal);
        });
        // The detail link remains available even when the thumbnail bytes fail.
        Assert.IsNotNull(cut.Find(".capture-card-actions a").GetAttribute("href"));
    }

    [TestMethod]
    public async Task ParameterChangeShowsLoadingThenErrorStateAsync()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = Configure(context);
        service.GalleryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryPage>.Success(new([OperatorUiTestData.Capture()], null)));
        var cut = context.Render<GalleryPage>();
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll(".capture-card")));

        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<OperatorUiResult<CameraAgentGalleryPage>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.GalleryHandler = (_, _) =>
        {
            requestStarted.TrySetResult();
            return new ValueTask<OperatorUiResult<CameraAgentGalleryPage>>(pending.Task);
        };
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/gallery?cursor=next");
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Loading gallery page", StringComparison.Ordinal));
        pending.SetResult(OperatorUiResult<CameraAgentGalleryPage>.Failure(
            OperatorUiResultKind.Unavailable,
            "The gallery is temporarily unavailable."));
        cut.WaitForAssertion(() => Assert.AreEqual("alert", cut.Find(".gallery-state--error").GetAttribute("role")));
    }

    [TestMethod]
    public void Filters_ArePassedToBoundedQueryAndNavigationClearsCursor()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        CameraAgentGalleryQuery? observed = null;
        service.GalleryHandler = (query, _) =>
        {
            observed = query;
            return ValueTask.FromResult(OperatorUiResult<CameraAgentGalleryPage>.Success(new CameraAgentGalleryPage([], null)));
        };

        var navigation = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo("/gallery?from=2026-07-23T10%3A00%3A00&origin=Simulated&role=Preview&recipe=preview&status=completed&rawState=durable&minSequence=40&maxSequence=50&pageSize=50&cursor=cursor-token");
        var cut = context.Render<GalleryPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.IsNotNull(observed);
            Assert.AreEqual(50, observed.PageSize);
            Assert.AreEqual("cursor-token", observed.Cursor);
            Assert.AreEqual(GalleryEvidenceOrigin.Simulated, observed.EvidenceOrigin);
            Assert.AreEqual(HVO.SkyMonitor.AgentCore.FrameArtifactRole.Preview, observed.ProcessingRole);
            Assert.AreEqual("preview", observed.Recipe);
            Assert.AreEqual("completed", observed.ProcessingStatus);
            Assert.AreEqual("durable", observed.RawState);
            Assert.AreEqual(40, observed.MinimumSequence);
            Assert.AreEqual(50, observed.MaximumSequence);
            Assert.IsTrue(cut.Find(".advanced-filters").HasAttribute("open"));
        });

        cut.Find("button[type='submit']").Click();
        Assert.IsFalse(navigation.Uri.Contains("cursor=", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DetailLinks_PreserveValidatedGalleryFiltersAndCursor()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var capture = OperatorUiTestData.Capture();
        service.GalleryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryPage>.Success(new CameraAgentGalleryPage([capture], null)));
        var navigation = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo("/gallery?origin=Simulated&cursor=safe-cursor&minSequence=10");

        var cut = context.Render<GalleryPage>();

        cut.WaitForAssertion(() =>
        {
            var href = cut.Find(".capture-image").GetAttribute("href");
            Assert.IsNotNull(href);
            StringAssert.Contains(href, "returnUrl=", StringComparison.Ordinal);
            StringAssert.Contains(Uri.UnescapeDataString(href), "/gallery?origin=Simulated&cursor=safe-cursor&minSequence=10", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll("main"));
        });
    }

    [TestMethod]
    public async Task ParameterChange_CancelsPriorGenerationAndIgnoresLateResultAsync()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var first = new TaskCompletionSource<OperatorUiResult<CameraAgentGalleryPage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.GalleryHandler = (query, token) =>
        {
            if (query.Cursor is null)
            {
                token.Register(() => firstCancelled.TrySetResult());
                return new ValueTask<OperatorUiResult<CameraAgentGalleryPage>>(first.Task);
            }
            var next = OperatorUiTestData.Capture() with { CaptureSequence = 99 };
            return ValueTask.FromResult(OperatorUiResult<CameraAgentGalleryPage>.Success(new CameraAgentGalleryPage([next], null)));
        };

        var cut = context.Render<GalleryPage>();
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/gallery?cursor=next");
        await firstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Capture #99", StringComparison.Ordinal));

        first.SetResult(OperatorUiResult<CameraAgentGalleryPage>.Success(new CameraAgentGalleryPage([
            OperatorUiTestData.Capture() with { CaptureSequence = 1 }
        ], null)));
        await Task.Delay(20).ConfigureAwait(false);
        Assert.IsFalse(cut.Markup.Contains("Capture #1<", StringComparison.Ordinal));
    }

    private static TestOperatorUiService Configure(BunitContext context)
    {
        var service = new TestOperatorUiService();
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);
        context.Services.AddSingleton<ICameraAgentCapturePresentationProjector>(service);
        context.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(OperatorUiTestData.Now));
        return service;
    }
}
