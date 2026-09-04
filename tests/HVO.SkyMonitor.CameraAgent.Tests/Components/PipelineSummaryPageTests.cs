using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class PipelineSummaryPageTests
{
    [TestMethod]
    public void Render_ShowsActiveRevisionGraphAndNoPendingNotice()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(new SchedulePageTests.ScheduleUiService(SchedulePageTests.State()));

        var cut = context.Render<PipelineSummaryPage>();

        cut.WaitForElement("#pipeline-active");

        Assert.AreEqual("Pipeline summary", cut.Find("h1").TextContent.Trim());
        Assert.AreEqual("Revision 2", cut.Find("#pipeline-active").TextContent.Trim());
        Assert.IsTrue(cut.Markup.Contains("Desired graph", StringComparison.Ordinal));
        Assert.IsTrue(cut.Markup.Contains("Effective graph", StringComparison.Ordinal));
        Assert.IsTrue(cut.Markup.Contains("Preview / required / enabled", StringComparison.Ordinal));
        Assert.IsTrue(cut.Markup.Contains("No pending revision", StringComparison.Ordinal));
        Assert.IsEmpty(cut.FindAll("#pipeline-pending"));
        Assert.IsFalse(cut.FindAll("button").Any(static button => button.TextContent.Contains("Disable", StringComparison.Ordinal)));
        Assert.IsNotNull(cut.Find("a[href='/operations/schedule']"));
    }

    [TestMethod]
    public void Render_WhenUnauthorized_NavigatesToAccessDenied()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(new SchedulePageTests.ScheduleUiService(null));
        var navigation = context.Services.GetRequiredService<NavigationManager>();

        _ = context.Render<PipelineSummaryPage>();

        Assert.IsTrue(navigation.Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
    }
}
