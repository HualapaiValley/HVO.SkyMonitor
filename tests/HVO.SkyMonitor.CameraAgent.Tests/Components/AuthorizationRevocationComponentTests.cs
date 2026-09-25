using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "bUnit InvokeAsync must retain its renderer synchronization context.")]
public sealed class AuthorizationRevocationComponentTests
{
    [TestMethod]
    public async Task LiveRefreshClearsOperationsGallerySystemAndQuarantineData()
    {
        await AssertOperationsRevocationAsync().ConfigureAwait(false);
        await AssertGalleryRevocationAsync().ConfigureAwait(false);
        await AssertSystemRevocationAsync().ConfigureAwait(false);
        await AssertQuarantineRevocationAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LiveRefreshClearsGalleryDetailData()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var capture = OperatorUiTestData.Capture();
        service.DetailHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryCapture>.Success(capture));
        var cut = context.Render<GalleryDetail>(parameters => parameters.Add(page => page.CaptureId, capture.CaptureId));
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, capture.CaptureId.ToString(), StringComparison.Ordinal));
        service.DetailHandler = (_, _) => ValueTask.FromResult(Denied<CameraAgentGalleryCapture>());

        await cut.InvokeAsync(cut.Instance.RefreshAuthorizationAsync);
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            AssertAccessDenied(context);
            Assert.IsFalse(cut.Markup.Contains(capture.CaptureId.ToString(), StringComparison.Ordinal));
        });
    }

    private static async Task AssertOperationsRevocationAsync()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = Configure(context);
        var cut = context.Render<OperationsPage>();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Raw ingress", StringComparison.Ordinal));
        service.OperationsHandler = _ => ValueTask.FromResult(Denied<CameraAgentOperationsView>());

        await cut.Find(".refresh-link").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() =>
        {
            AssertAccessDenied(context);
            Assert.IsFalse(cut.Markup.Contains("Raw ingress", StringComparison.Ordinal));
        });
    }

    private static async Task AssertGalleryRevocationAsync()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GalleryHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryPage>.Success(new([OperatorUiTestData.Capture()], null)));
        var cut = context.Render<GalleryPage>();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Capture #42", StringComparison.Ordinal));
        service.GalleryHandler = (_, _) => ValueTask.FromResult(Denied<CameraAgentGalleryPage>());

        await cut.InvokeAsync(cut.Instance.RefreshAuthorizationAsync);
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            AssertAccessDenied(context);
            Assert.IsFalse(cut.Markup.Contains("Capture #42", StringComparison.Ordinal));
        });
    }

    private static async Task AssertSystemRevocationAsync()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var cut = context.Render<SystemStatusPage>();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Virtual sensor", StringComparison.Ordinal));
        service.SystemHandler = _ => ValueTask.FromResult(Denied<CameraAgentSystemStatus>());

        await cut.InvokeAsync(cut.Instance.RefreshAuthorizationAsync);
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            AssertAccessDenied(context);
            Assert.IsFalse(cut.Markup.Contains("Virtual sensor", StringComparison.Ordinal));
        });
    }

    private static async Task AssertQuarantineRevocationAsync()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = Configure(context);
        var cut = context.Render<QuarantinePage>();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Artifact quarantine", StringComparison.Ordinal));
        service.QuarantineHandler = (_, _, _, _, _) => ValueTask.FromResult(Denied<OperatorOutboxPage>());

        await cut.InvokeAsync(cut.Instance.RefreshAuthorizationAsync);
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            AssertAccessDenied(context);
            Assert.IsFalse(cut.Markup.Contains("Artifact quarantine /", StringComparison.Ordinal));
        });
    }

    private static TestOperatorUiService Configure(BunitContext context)
    {
        var service = new TestOperatorUiService();
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(new ProcessingExecutionPagesTests.GraphUiService());
        context.Services.AddSingleton<ICameraAgentCapturePresentationProjector>(service);
        context.Services.AddSingleton(TimeProvider.System);
        return service;
    }

    private static OperatorUiResult<T> Denied<T>() =>
        OperatorUiResult<T>.Failure(OperatorUiResultKind.Unauthorized, "Authorization revoked.");

    private static void AssertAccessDenied(BunitContext context)
    {
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        Assert.IsTrue(navigation.Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
    }
}
