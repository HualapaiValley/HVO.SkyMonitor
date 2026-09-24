using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class SharedNavigationTests
{
    [TestMethod]
    public void Archive_DirectAndChangedRoutesSelectExactlyOneSection()
    {
        using var context = new BunitContext();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/archive/day/2026-09-24");
        var cut = context.Render<ArchiveNavigation>();
        Assert.AreEqual("Calendar", cut.Find("[aria-current='page']").TextContent);
        foreach (var (path, expected) in new[]
        {
            ("/gallery", "Captures"), ("/gallery/capture-id?stage=Raw", "Captures"),
            ("/archive/calendar", "Calendar"), ("/archive/day/2026-09-23", "Calendar"),
            ("/archive/products", "Products"), ("/archive/products/artifact-id", "Products"),
            ("/GALLERY/", "Captures"), ("/gallery", "Captures")
        })
        {
            navigation.NavigateTo(path);
            cut.WaitForAssertion(() =>
            {
                Assert.HasCount(3, cut.FindAll("a"));
                Assert.HasCount(1, cut.FindAll("[aria-current='page']"));
                Assert.AreEqual(expected, cut.Find("[aria-current='page']").TextContent);
            });
        }
    }

    [TestMethod]
    public void Archive_DoesNotAppearOnOtherOrLookalikeRoutes()
    {
        using var context = new BunitContext();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var cut = context.Render<ArchiveNavigation>();
        foreach (var path in new[] { "/", "/transients", "/operations", "/gallery-other", "/archived" })
        {
            navigation.NavigateTo(path);
            cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("nav")));
        }
    }

    [TestMethod]
    public async Task ResponsiveDialog_InitializesNativeBehaviorAndSynchronizesExpandedState()
    {
        using var context = new BunitContext();
        var module = context.JSInterop.SetupModule("./Components/Layout/ResponsiveNavigation.razor.js");
        module.Mode = JSRuntimeMode.Loose;
        var cut = context.Render<ResponsiveNavigation>(parameters => parameters
            .Add(static component => component.Id, "test-menu")
            .Add(static component => component.Label, "Test navigation"));
        Assert.HasCount(1, module.Invocations["initialize"]);
        Assert.AreEqual("Test navigation", cut.Find("dialog").GetAttribute("aria-label"));
        await cut.Find(".navigation-toggle").ClickAsync(new MouseEventArgs()).ConfigureAwait(false);
        Assert.HasCount(1, module.Invocations["open"]);
        await cut.Instance.SetExpanded(true).ConfigureAwait(false);
        Assert.AreEqual("true", cut.Find(".navigation-toggle").GetAttribute("aria-expanded"));
        await cut.Find(".navigation-close").ClickAsync(new MouseEventArgs()).ConfigureAwait(false);
        Assert.HasCount(1, module.Invocations["close"]);
        await cut.Instance.SetExpanded(false).ConfigureAwait(false);
        Assert.AreEqual("false", cut.Find(".navigation-toggle").GetAttribute("aria-expanded"));
        await context.DisposeAsync().ConfigureAwait(false);
        Assert.HasCount(1, module.Invocations["dispose"]);
    }
}
