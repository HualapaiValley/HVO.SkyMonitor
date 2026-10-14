using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Async disposal keeps the browser fixture scope readable.")]
public sealed class CameraRigBrowserAcceptanceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task OwnerComposesVirtualRigPreviewsStagesAndCancelsAcrossViewportsAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await playwright.EnsureLaunchableOrInconclusiveAsync().ConfigureAwait(false);
        await using var host = await CameraAgentKestrelFixture.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await using var context = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
        }).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        await OpenCameraRigAsync(page).ConfigureAwait(false);
        var selection = page.GetByRole(AriaRole.Region, new() { Name = "Rig selection" });
        await selection.WaitForAsync().ConfigureAwait(false);
        StringAssert.Contains(await selection.InnerTextAsync().ConfigureAwait(false), "is active", StringComparison.Ordinal);
        Assert.AreEqual("None", await SelectionFactAsync(selection, "Pending restart").ConfigureAwait(false));
        Assert.AreEqual("Restart", await SelectionFactAsync(selection, "Apply boundary").ConfigureAwait(false));
        var composer = page.GetByRole(AriaRole.Region, new() { Name = "Rig composer" });
        await composer.WaitForAsync().ConfigureAwait(false);
        await CaptureAsync(page, "before-1440x900").ConfigureAwait(false);

        await page.Locator("#rig-camera-edit").ClickAsync().ConfigureAwait(false);
        var editor = page.GetByRole(AriaRole.Dialog, new() { Name = "Edit camera" });
        await editor.GetByLabel("Module type").WaitForAsync().ConfigureAwait(false);
        Assert.AreEqual("VirtualSky", await editor.GetByLabel("Module type").InputValueAsync().ConfigureAwait(false));
        await CaptureAsync(page, "editor-1440x900").ConfigureAwait(false);
        await editor.GetByLabel("Equipment name").FillAsync("Browser virtual camera").ConfigureAwait(false);
        await editor.GetByRole(AriaRole.Button, new() { Name = "Create duplicate equipment" }).ClickAsync()
            .ConfigureAwait(false);
        await page.GetByText("Equipment revision saved. Compose a rig to use it.").WaitForAsync()
            .ConfigureAwait(false);
        await editor.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden }).ConfigureAwait(false);
        StringAssert.Contains(await page.Locator("#rig-camera-select option:checked").InnerTextAsync()
            .ConfigureAwait(false), "Browser virtual camera", StringComparison.Ordinal);
        Assert.IsTrue(await page.Locator("#rig-camera-select").EvaluateAsync<bool>("element => document.activeElement === element")
            .ConfigureAwait(false), "Saving equipment must return focus to its composer dropdown.");

        await page.Locator("#rig-profile-new").ClickAsync().ConfigureAwait(false);
        var naming = page.GetByRole(AriaRole.Dialog, new() { Name = "New named rig" });
        await naming.GetByLabel("Rig name").FillAsync("Browser virtual rig").ConfigureAwait(false);
        await naming.GetByRole(AriaRole.Button, new() { Name = "Save rig name" }).ClickAsync().ConfigureAwait(false);
        await page.GetByText("Rig name saved.").WaitForAsync().ConfigureAwait(false);
        StringAssert.Contains(await page.Locator("#rig-profile-select option:checked").InnerTextAsync()
            .ConfigureAwait(false), "Browser virtual rig", StringComparison.Ordinal);
        await composer.GetByRole(AriaRole.Button, new() { Name = "Compose immutable revision" }).ClickAsync()
            .ConfigureAwait(false);
        await page.GetByText("Immutable rig revision composed. Preview before staging.").WaitForAsync()
            .ConfigureAwait(false);

        var preview = page.GetByRole(AriaRole.Region, new() { Name = "Rig preview and selection" });
        StringAssert.Contains(await preview.GetByLabel("Rig revision").Locator("option:checked").InnerTextAsync()
            .ConfigureAwait(false), "Browser virtual rig", StringComparison.Ordinal);
        await preview.GetByRole(AriaRole.Button, new() { Name = "Preview against active schedule" }).ClickAsync()
            .ConfigureAwait(false);
        await preview.GetByText("Contract preview passed.", new() { Exact = false }).WaitForAsync()
            .ConfigureAwait(false);
        await preview.GetByLabel("I acknowledge this rig is unvalidated at runtime; staging requires restart and may fail camera initialization.")
            .CheckAsync().ConfigureAwait(false);
        await preview.GetByRole(AriaRole.Button, new() { Name = "Confirm restart-only stage" }).ClickAsync()
            .ConfigureAwait(false);
        var pending = page.GetByRole(AriaRole.Region, new() { Name = "Pending restart" });
        await pending.GetByRole(AriaRole.Button, new() { Name = "Cancel pending restart" }).WaitForAsync()
            .ConfigureAwait(false);
        Assert.AreEqual("Revision 1", await SelectionFactAsync(selection, "Pending restart").ConfigureAwait(false));

        await CaptureAsync(page, "staged-1440x900").ConfigureAwait(false);

        // Resizing a page or taking a full-page screenshot drops Chromium's touch emulation, so each touch viewport
        // gets its own device context and measures its touch targets before any screenshot.
        foreach (var (width, height) in new[] { (1440, 900), (390, 844), (320, 844) })
        {
            await using var touchContext = await diagnostics.NewContextAsync(new BrowserNewContextOptions
            {
                BaseURL = host.BaseAddress.ToString(),
                ViewportSize = new ViewportSize { Width = width, Height = height },
                HasTouch = true,
                IsMobile = true
            }).ConfigureAwait(false);
            var touch = await touchContext.NewPageAsync().ConfigureAwait(false);
            await OpenCameraRigAsync(touch).ConfigureAwait(false);
            Assert.IsTrue(await touch.EvaluateAsync<bool>("() => matchMedia('(pointer: coarse)').matches").ConfigureAwait(false),
                "Touch-target checks require a coarse-pointer device.");
            var touchPending = touch.GetByRole(AriaRole.Region, new() { Name = "Pending restart" });
            var cancel = touchPending.GetByRole(AriaRole.Button, new() { Name = "Cancel pending restart" });
            await cancel.WaitForAsync().ConfigureAwait(false);
            Assert.IsFalse(await touch.EvaluateAsync<bool>(
                "() => document.documentElement.scrollWidth > innerWidth").ConfigureAwait(false),
                $"Camera & rig overflows the {width}x{height} viewport.");
            var box = await cancel.BoundingBoxAsync().ConfigureAwait(false);
            Assert.IsNotNull(box);
            Assert.IsGreaterThanOrEqualTo(44, box.Width, "Cancel touch target must be at least 44 px wide.");
            Assert.IsGreaterThanOrEqualTo(44, box.Height, "Cancel touch target must be at least 44 px high.");
            await cancel.FocusAsync().ConfigureAwait(false);
            Assert.IsTrue(await cancel.EvaluateAsync<bool>("element => document.activeElement === element")
                .ConfigureAwait(false), "Cancel must be keyboard focusable.");
            await touch.Keyboard.PressAsync("Tab").ConfigureAwait(false);
            Assert.IsFalse(await cancel.EvaluateAsync<bool>("element => document.activeElement === element")
                .ConfigureAwait(false), "Tab must advance from the focused control.");

            var edit = touch.Locator("#rig-optics-edit");
            var editBox = await edit.BoundingBoxAsync().ConfigureAwait(false);
            Assert.IsNotNull(editBox);
            Assert.IsGreaterThanOrEqualTo(44, editBox.Height, "Composer Edit touch target must be at least 44 px high.");
            await CaptureAsync(touch, $"staged-touch-{width}x{height}").ConfigureAwait(false);
            await edit.ClickAsync().ConfigureAwait(false);
            var optics = touch.GetByRole(AriaRole.Dialog, new() { Name = "Edit optics" });
            await optics.GetByLabel("Focal length mm").WaitForAsync().ConfigureAwait(false);
            var dialogBox = await optics.BoundingBoxAsync().ConfigureAwait(false);
            Assert.IsNotNull(dialogBox);
            Assert.IsLessThanOrEqualTo(width, dialogBox.X + dialogBox.Width, $"Editor dialog overflows the {width}px viewport.");
            await CaptureAsync(touch, $"editor-touch-{width}x{height}").ConfigureAwait(false);
            await touch.Keyboard.PressAsync("Escape").ConfigureAwait(false);
            await optics.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden }).ConfigureAwait(false);
            Assert.IsTrue(await edit.EvaluateAsync<bool>("element => document.activeElement === element")
                .ConfigureAwait(false), "Closing the editor must return focus to the button that opened it.");
            if (width != 320)
                continue;

            await cancel.ScrollIntoViewIfNeededAsync().ConfigureAwait(false);
            var touchBox = await cancel.BoundingBoxAsync().ConfigureAwait(false);
            Assert.IsNotNull(touchBox);
            await touch.Touchscreen.TapAsync(touchBox.X + touchBox.Width / 2, touchBox.Y + touchBox.Height / 2)
                .ConfigureAwait(false);
            await cancel.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden })
                .ConfigureAwait(false);
            var touchSelection = touch.GetByRole(AriaRole.Region, new() { Name = "Rig selection" });
            Assert.AreEqual("None", await SelectionFactAsync(touchSelection, "Pending restart").ConfigureAwait(false));
            await CaptureAsync(touch, "cancelled-320x844").ConfigureAwait(false);
        }

        await page.ReloadAsync().ConfigureAwait(false);
        await selection.WaitForAsync().ConfigureAwait(false);
        Assert.AreEqual("None", await SelectionFactAsync(selection, "Pending restart").ConfigureAwait(false));
        Assert.AreEqual(0, await pending.CountAsync().ConfigureAwait(false));

        Assert.AreEqual(0, await page.GetByLabel("Day exposure ms").CountAsync().ConfigureAwait(false));
        await selection.GetByRole(AriaRole.Link, new() { Name = "Capture schedule" }).ClickAsync()
            .ConfigureAwait(false);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Schedule", Level = 1 })
            .WaitForAsync().ConfigureAwait(false);
        await page.Locator("#schedule-edit-open").ClickAsync().ConfigureAwait(false);
        await page.GetByLabel("Day exposure ms").WaitForAsync().ConfigureAwait(false);
        await diagnostics.CompleteAsync().ConfigureAwait(false);
    }

    private static async Task OpenCameraRigAsync(IPage page)
    {
        await page.GotoAsync("/Account/Login").ConfigureAwait(false);
        await page.GetByLabel("Email").FillAsync(CameraAgentKestrelFixture.OwnerEmail).ConfigureAwait(false);
        await page.GetByLabel("Password").FillAsync(CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase))
            .ConfigureAwait(false);
        await page.GotoAsync("/operations/camera").ConfigureAwait(false);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Camera & rig", Level = 1 })
            .WaitForAsync().ConfigureAwait(false);
        await page.Locator(".app-frame[data-interactive='true']").WaitForAsync().ConfigureAwait(false);
        await page.WaitForFunctionAsync("() => document.querySelector('#rig-camera-edit')?.disabled === false")
            .ConfigureAwait(false);
    }

    private static async Task<string> SelectionFactAsync(ILocator selection, string term)
        => (await selection.Locator("dl > div").Filter(new() { Has = selection.Page.Locator("dt", new() { HasTextString = term }) })
            .Locator("dd").InnerTextAsync().ConfigureAwait(false)).Trim();

    private async Task CaptureAsync(IPage page, string name)
    {
        var directory = TestContext.TestRunResultsDirectory ?? Path.GetTempPath();
        var path = Path.Combine(directory, $"camera-rig-{name}-{Guid.NewGuid():N}.png");
        // A full-page capture paints sticky chrome and modal dialogs at the current scroll offset, so pages are
        // captured from the top and an open dialog is captured as the operator sees it, in the viewport.
        var dialogOpen = await page.EvaluateAsync<bool>("() => document.querySelector('dialog[open]') !== null").ConfigureAwait(false);
        if (!dialogOpen)
            await page.EvaluateAsync("() => window.scrollTo(0, 0)").ConfigureAwait(false);
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = !dialogOpen }).ConfigureAwait(false);
        TestContext.AddResultFile(path);
    }
}
