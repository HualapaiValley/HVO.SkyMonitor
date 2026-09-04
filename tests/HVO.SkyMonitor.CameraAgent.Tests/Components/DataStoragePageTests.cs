using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class DataStoragePageTests
{
    [TestMethod]
    public void Render_ShowsQueueLaneStorageAndDeliveryFactsWithoutPaths()
    {
        using var context = new BunitContext();
        var service = new TestOperatorUiService();
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Success(OperatorUiTestData.Operations(lanePressure: 2, storagePressure: true)));

        var cut = context.Render<DataStoragePage>();

        cut.WaitForElement(".data-grid");
        Assert.AreEqual("Data & storage", cut.Find("h1").TextContent.Trim());
        foreach (var heading in new[] { "Raw ingress", "Processing", "Artifact outbox", "Capture lanes", "Storage", "Heartbeat delivery", "Environmental outbox", "Held items" })
        {
            Assert.IsTrue(cut.FindAll("h2").Any(element => element.TextContent.Trim() == heading), heading);
        }
        Assert.IsTrue(cut.Markup.Contains("Storage or lane pressure detected", StringComparison.Ordinal));
        Assert.IsNotNull(cut.Find(".lane--critical"));
        Assert.IsNotNull(cut.Find(".storage-item--pressure"));
        Assert.IsFalse(cut.Markup.Contains("/var/", StringComparison.Ordinal));
        Assert.IsNotNull(cut.Find("a[href='/operations/quarantine']"));
    }

    [TestMethod]
    public void FailedRefresh_KeepsLastValidDataAndMarksItStale()
    {
        using var context = new BunitContext();
        var service = new TestOperatorUiService();
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Success(OperatorUiTestData.Operations()));
        var cut = context.Render<DataStoragePage>();
        cut.WaitForElement(".data-grid");
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Failure(OperatorUiResultKind.Unavailable, "The durable stores are locked."));

        cut.Find("button").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("Showing last valid data", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains("The durable stores are locked.", StringComparison.Ordinal));
            Assert.IsNotNull(cut.Find(".data-grid"));
        });
    }

    [TestMethod]
    public void InitialFailure_RendersErrorWithRetry()
    {
        using var context = new BunitContext();
        var service = new TestOperatorUiService();
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Failure(OperatorUiResultKind.Unavailable, "Nothing yet."));

        var cut = context.Render<DataStoragePage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Find("[role='alert']").TextContent, "Nothing yet.", StringComparison.Ordinal);
            Assert.IsNotNull(cut.Find("[role='alert'] button"));
        });
    }

    [TestMethod]
    public void Render_WhenUnauthorized_NavigatesToAccessDenied()
    {
        using var context = new BunitContext();
        var service = new TestOperatorUiService();
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Failure(OperatorUiResultKind.Unauthorized, "denied"));

        _ = context.Render<DataStoragePage>();

        Assert.IsTrue(context.Services.GetRequiredService<NavigationManager>().Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
    }
}
