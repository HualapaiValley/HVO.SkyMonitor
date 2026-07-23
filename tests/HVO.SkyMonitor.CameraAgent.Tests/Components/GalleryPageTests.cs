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
    public void BoundedPage_RendersPreferredPreviewOriginsProcessingAndCursor()
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
            StringAssert.Contains(cut.Markup, "Annotated Preview", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Simulated evidence", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Developer fixture", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "1/1 complete", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Additional capture history remains unloaded", StringComparison.Ordinal);
            Assert.HasCount(4, cut.FindAll("a[href^='/gallery/']"));
        });
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
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Sequence 99", StringComparison.Ordinal));

        first.SetResult(OperatorUiResult<CameraAgentGalleryPage>.Success(new CameraAgentGalleryPage([
            OperatorUiTestData.Capture() with { CaptureSequence = 1 }
        ], null)));
        await Task.Delay(20).ConfigureAwait(false);
        Assert.IsFalse(cut.Markup.Contains("Sequence 1<", StringComparison.Ordinal));
    }

    private static TestOperatorUiService Configure(BunitContext context)
    {
        var service = new TestOperatorUiService();
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);
        return service;
    }
}
