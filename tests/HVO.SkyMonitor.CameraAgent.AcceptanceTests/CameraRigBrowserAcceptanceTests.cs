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
            HasTouch = true
        }).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        await page.GotoAsync("/Account/Login").ConfigureAwait(false);
        await page.GetByLabel("Email").FillAsync(CameraAgentKestrelFixture.OwnerEmail).ConfigureAwait(false);
        await page.GetByLabel("Password").FillAsync(CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase))
            .ConfigureAwait(false);
        await page.GotoAsync("/operations/camera").ConfigureAwait(false);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Camera & rig", Level = 1 })
            .WaitForAsync().ConfigureAwait(false);
        var selection = page.GetByRole(AriaRole.Region, new() { Name = "Rig selection" });
        await selection.WaitForAsync().ConfigureAwait(false);
        StringAssert.Contains(await selection.InnerTextAsync().ConfigureAwait(false), "Active rig", StringComparison.Ordinal);
        StringAssert.Contains(await selection.InnerTextAsync().ConfigureAwait(false), "Pending restart", StringComparison.Ordinal);
        await page.GetByRole(AriaRole.Region, new() { Name = "Equipment editor" }).WaitForAsync()
            .ConfigureAwait(false);
        await CaptureAsync(page, "before-1440x900").ConfigureAwait(false);

        var equipment = page.GetByRole(AriaRole.Region, new() { Name = "Equipment editor" });
        await equipment.GetByLabel("Equipment kind").SelectOptionAsync("camera").ConfigureAwait(false);
        await equipment.GetByLabel("Basis revision").SelectOptionAsync(new SelectOptionValue { Index = 1 })
            .ConfigureAwait(false);
        await equipment.GetByLabel("Module type").WaitForAsync().ConfigureAwait(false);
        Assert.AreEqual("VirtualSky", await equipment.GetByLabel("Module type").InputValueAsync().ConfigureAwait(false));
        await equipment.GetByLabel("Equipment name").FillAsync("Browser virtual camera").ConfigureAwait(false);
        await equipment.GetByRole(AriaRole.Button, new() { Name = "Create duplicate equipment" }).ClickAsync()
            .ConfigureAwait(false);
        await page.GetByText("Equipment revision saved. Compose a rig to use it.").WaitForAsync()
            .ConfigureAwait(false);

        var composer = page.GetByRole(AriaRole.Region, new() { Name = "Rig composer" });
        await composer.GetByLabel("Create new named rig").CheckAsync().ConfigureAwait(false);
        await composer.GetByLabel("Rig name").FillAsync("Browser virtual rig").ConfigureAwait(false);
        await composer.GetByRole(AriaRole.Button, new() { Name = "Save rig name" }).ClickAsync()
            .ConfigureAwait(false);
        await page.GetByText("Rig name saved.").WaitForAsync().ConfigureAwait(false);
        StringAssert.Contains(await composer.GetByLabel("Camera").Locator("option:checked").InnerTextAsync()
            .ConfigureAwait(false), "Browser virtual camera", StringComparison.Ordinal);
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
        await selection.GetByRole(AriaRole.Button, new() { Name = "Cancel pending restart" }).WaitForAsync()
            .ConfigureAwait(false);
        StringAssert.Contains(await selection.InnerTextAsync().ConfigureAwait(false), "Restart required", StringComparison.Ordinal);

        foreach (var (width, height) in new[] { (1440, 900), (390, 844), (320, 844) })
        {
            await page.SetViewportSizeAsync(width, height).ConfigureAwait(false);
            await CaptureAsync(page, $"staged-{width}x{height}").ConfigureAwait(false);
            Assert.IsFalse(await page.EvaluateAsync<bool>(
                "() => document.documentElement.scrollWidth > innerWidth").ConfigureAwait(false),
                $"Camera & rig overflows the {width}x{height} viewport.");
            var cancel = selection.GetByRole(AriaRole.Button, new() { Name = "Cancel pending restart" });
            var box = await cancel.BoundingBoxAsync().ConfigureAwait(false);
            Assert.IsNotNull(box);
            Assert.IsGreaterThanOrEqualTo(44, box.Width, "Cancel touch target must be at least 44 px wide.");
            Assert.IsGreaterThanOrEqualTo(44, box.Height, "Cancel touch target must be at least 44 px high.");
            await cancel.FocusAsync().ConfigureAwait(false);
            Assert.IsTrue(await cancel.EvaluateAsync<bool>("element => document.activeElement === element")
                .ConfigureAwait(false), "Cancel must be keyboard focusable.");
            await page.Keyboard.PressAsync("Tab").ConfigureAwait(false);
            Assert.IsFalse(await cancel.EvaluateAsync<bool>("element => document.activeElement === element")
                .ConfigureAwait(false), "Tab must advance from the focused control.");
        }

        var cancelButton = selection.GetByRole(AriaRole.Button, new() { Name = "Cancel pending restart" });
        await cancelButton.ScrollIntoViewIfNeededAsync().ConfigureAwait(false);
        var touchBox = await cancelButton.BoundingBoxAsync().ConfigureAwait(false);
        Assert.IsNotNull(touchBox);
        await page.Touchscreen.TapAsync(touchBox.X + touchBox.Width / 2, touchBox.Y + touchBox.Height / 2)
            .ConfigureAwait(false);
        await cancelButton.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden })
            .ConfigureAwait(false);
        StringAssert.Contains(await selection.InnerTextAsync().ConfigureAwait(false), "No restart staged", StringComparison.Ordinal);
        await page.ReloadAsync().ConfigureAwait(false);
        await page.GetByRole(AriaRole.Region, new() { Name = "Rig selection" }).WaitForAsync()
            .ConfigureAwait(false);
        StringAssert.Contains(await selection.InnerTextAsync().ConfigureAwait(false), "No restart staged", StringComparison.Ordinal);
        await CaptureAsync(page, "cancelled-320x844").ConfigureAwait(false);

        Assert.AreEqual(0, await page.GetByLabel("Day exposure ms").CountAsync().ConfigureAwait(false));
        await selection.GetByRole(AriaRole.Link, new() { Name = "capture schedule" }).ClickAsync()
            .ConfigureAwait(false);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Capture schedule", Level = 1 })
            .WaitForAsync().ConfigureAwait(false);
        await page.GetByLabel("Day exposure ms").WaitForAsync().ConfigureAwait(false);
        await diagnostics.CompleteAsync().ConfigureAwait(false);
    }

    private async Task CaptureAsync(IPage page, string name)
    {
        var directory = TestContext.TestRunResultsDirectory ?? Path.GetTempPath();
        var path = Path.Combine(directory, $"camera-rig-{name}-{Guid.NewGuid():N}.png");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true }).ConfigureAwait(false);
        TestContext.AddResultFile(path);
    }
}
