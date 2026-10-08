using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Account.IdentityShared;
using Microsoft.AspNetCore.Http;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class AccountShellTests
{
    private static readonly string[] SettingsLinks = ["Account/Manage", "Account/Manage/Email", "Account/Manage/ChangePassword"];

    private static readonly string[] SettingsNames = ["Profile", "Owner email", "Password"];

    [TestMethod]
    public void StatusMessage_Error_RendersTheFailureBanner()
    {
        using var context = new BunitContext();

        var component = context.Render<StatusMessage>(parameters => parameters
            .Add(message => message.Message, "Error: Incorrect password.")
            .AddCascadingValue<HttpContext>(new DefaultHttpContext()));

        var banner = component.Find(".ops-note-banner");
        Assert.AreEqual("alert", banner.GetAttribute("role"));
        Assert.IsTrue(banner.ClassList.Contains("failure"));
        Assert.IsNotNull(banner.QuerySelector(".status-icon.failure[aria-hidden=true]"));
        Assert.AreEqual("Error: Incorrect password.", banner.TextContent.Trim());
    }

    [TestMethod]
    public void StatusMessage_Confirmation_RendersTheSuccessBanner()
    {
        using var context = new BunitContext();

        var component = context.Render<StatusMessage>(parameters => parameters
            .Add(message => message.Message, "Your password has been changed")
            .AddCascadingValue<HttpContext>(new DefaultHttpContext()));

        var banner = component.Find(".ops-note-banner");
        Assert.IsTrue(banner.ClassList.Contains("success"));
        Assert.IsNotNull(banner.QuerySelector(".status-icon.success"));
    }

    [TestMethod]
    public void StatusMessage_WithoutAMessage_RendersNothing()
    {
        using var context = new BunitContext();

        var component = context.Render<StatusMessage>(parameters => parameters
            .AddCascadingValue<HttpContext>(new DefaultHttpContext()));

        Assert.AreEqual(string.Empty, component.Markup.Trim());
    }

    [TestMethod]
    public void ManageNavMenu_LinksTheThreeSettingsPagesInOrder()
    {
        using var context = new BunitContext();

        var links = context.Render<ManageNavMenu>().FindAll(".manage-navigation a");

        CollectionAssert.AreEqual(SettingsLinks, links.Select(static link => link.GetAttribute("href")).ToArray());
        CollectionAssert.AreEqual(SettingsNames, links.Select(static link => link.TextContent.Trim()).ToArray());
        Assert.IsTrue(links.All(static link => link.QuerySelector(".manage-nav-icon[aria-hidden=true] svg") is not null));
    }
}
