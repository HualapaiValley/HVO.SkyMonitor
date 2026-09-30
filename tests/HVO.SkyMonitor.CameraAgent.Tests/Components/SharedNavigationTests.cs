using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

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

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ResponsiveDialog_DisposalReleasesLateImportOrInitialization(bool delayInitialization)
    {
        using var context = new BunitContext();
        var js = new DelayedNavigationJs(delayInitialization);
        context.Services.AddSingleton<IJSRuntime>(js);
        var cut = context.Render<ResponsiveNavigation>(parameters => parameters
            .Add(static component => component.Id, "delayed-menu")
            .Add(static component => component.Label, "Delayed navigation"));
        await js.Started.Task.ConfigureAwait(false);

        await cut.Instance.DisposeAsync().ConfigureAwait(false);
        Assert.AreEqual(0, js.ModuleDisposals, "The in-flight initialization owns its resources until it completes.");
        if (delayInitialization) Assert.IsNotNull(js.Receiver!.Value, "The pending JS call must not receive a disposed callback reference.");
        js.Continue.SetResult();
        await js.Released.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(delayInitialization ? 1 : 0, js.Initializations);
            Assert.AreEqual(delayInitialization ? 1 : 0, js.PanelDisposals);
            Assert.AreEqual(1, js.ModuleDisposals);
            if (delayInitialization) Assert.Throws<ObjectDisposedException>(() => _ = js.Receiver!.Value);
        });
        await cut.Instance.SetExpanded(true).ConfigureAwait(false);
        Assert.AreEqual("false", cut.Find(".navigation-toggle").GetAttribute("aria-expanded"));
    }

    private sealed class DelayedNavigationJs(bool delayInitialization) : IJSRuntime, IJSObjectReference
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DotNetObjectReference<ResponsiveNavigation>? Receiver { get; private set; }
        public int Initializations { get; private set; }
        public int PanelDisposals { get; private set; }
        public int ModuleDisposals { get; private set; }

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "initialize")
            {
                Initializations++;
                Receiver = (DotNetObjectReference<ResponsiveNavigation>)args![3]!;
            }
            if (identifier == (delayInitialization ? "initialize" : "import"))
            {
                Started.SetResult();
                await Continue.Task.ConfigureAwait(false);
            }
            if (identifier == "dispose") PanelDisposals++;
            return identifier == "import" ? (TValue)(object)this : default!;
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync()
        {
            ModuleDisposals++;
            Released.SetResult();
            return ValueTask.CompletedTask;
        }
    }
}
