using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Async disposal keeps the browser fixture scope readable.")]
public sealed partial class ObservatoryLocationBrowserAcceptanceTests
{
    private const string TileHost = "tile.openstreetmap.org";

    // A 1x1 PNG stands in for every map tile, so the online map renders without the test leaving the machine.
    private static readonly byte[] TilePng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    // The same control set and floor the operations responsive walk applies to workspace form routes.
    private const string UndersizedControlsScript = """
        () => [...document.querySelectorAll('.operations-content input:not([type=checkbox]), .operations-content select, .operations-content button, .operations-content a.btn')]
            .filter(element => element.getClientRects().length > 0 && element.getBoundingClientRect().height < 44)
            .slice(0, 5)
            .map(element => `${element.tagName.toLowerCase()} "${(element.getAttribute('aria-label') || element.textContent || '').trim().slice(0, 30)}" h=${Math.round(element.getBoundingClientRect().height)}`)
            .join(' | ')
        """;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task OwnerNamesTheSitePagesTheSceneAndFallsBackOfflineAcrossViewportsAsync()
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
            ColorScheme = ColorScheme.Dark,
            ReducedMotion = ReducedMotion.Reduce
        }).ConfigureAwait(false);
        var externalHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tilesReachable = true;
        await context.RouteAsync(
            url => !url.StartsWith(host.BaseAddress.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase),
            async route =>
            {
                var requested = new Uri(route.Request.Url);
                lock (externalHosts)
                {
                    externalHosts.Add(requested.Host);
                }
                if (tilesReachable && string.Equals(requested.Host, TileHost, StringComparison.OrdinalIgnoreCase))
                {
                    await route.FulfillAsync(new RouteFulfillOptions { ContentType = "image/png", BodyBytes = TilePng })
                        .ConfigureAwait(false);
                    return;
                }
                await route.AbortAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        var browserErrors = new List<string>();
        page.PageError += (_, error) => browserErrors.Add(error);

        await OpenSiteAsync(page).ConfigureAwait(false);
        var map = page.Locator(".site-map");
        await page.WaitForFunctionAsync(
            """
            () => {
              const tiles = [...document.querySelectorAll('.site-map-tiles img')];
              return tiles.length > 0 && tiles.every(tile => tile.complete && tile.naturalWidth > 0);
            }
            """).ConfigureAwait(false);
        Assert.IsNull(await map.GetAttributeAsync("data-tiles").ConfigureAwait(false), "Reachable tiles must keep the online map.");
        await page.GetByText($"Map tiles are loaded by your browser from {TileHost}", new() { Exact = false })
            .WaitForAsync().ConfigureAwait(false);
        lock (externalHosts)
        {
            CollectionAssert.AreEquivalent(new[] { TileHost }, externalHosts.ToArray(),
                "The browser may fetch map tiles and nothing else from outside the CameraAgent.");
        }

        // Identifiers are not operator vocabulary: neither the page nor the sidebar may show a GUID.
        var readable = await page.Locator("body").InnerTextAsync().ConfigureAwait(false);
        Assert.IsFalse(GuidPattern().IsMatch(readable), $"The page shows an identifier: {GuidPattern().Match(readable).Value}");

        // The visible scene is a paged table. The fixture catalog holds nine bright stars, so every visible one fits on
        // the first page; paging across pages is covered by the component tests over the full fixture sky.
        var pager = page.GetByRole(AriaRole.Navigation, new() { Name = "Visible object pages" });
        var rows = await page.Locator(".site-scene-wrap tbody tr").CountAsync().ConfigureAwait(false);
        Assert.IsGreaterThan(0, rows, "The fixture sky must project at least one visible star.");
        Assert.AreEqual($"1-{rows} of {rows}", (await pager.GetByRole(AriaRole.Status).InnerTextAsync().ConfigureAwait(false)).Trim());
        Assert.IsTrue(await pager.GetByRole(AriaRole.Button, new() { Name = "Previous" }).IsDisabledAsync().ConfigureAwait(false));
        Assert.IsTrue(await pager.GetByRole(AriaRole.Button, new() { Name = "Next" }).IsDisabledAsync().ConfigureAwait(false));
        Assert.AreEqual(rows, await page.Locator(".site-dial-object.on-page").CountAsync().ConfigureAwait(false),
            "The sky plot highlights exactly the objects on the current table page.");
        await CaptureAsync(page, "site-1440x900").ConfigureAwait(false);

        // Naming the camera is recorded as a profile revision and renames the workspace sidebar at once.
        var editProfile = page.Locator("#site-edit-profile");
        await editProfile.ClickAsync().ConfigureAwait(false);
        var dialog = page.Locator("dialog.site-dialog");
        await dialog.WaitForAsync().ConfigureAwait(false);
        await page.Locator("#site-observatory-name").FillAsync("Hualapai Valley Observatory").ConfigureAwait(false);
        await page.Locator("#site-camera-name").FillAsync("East dome camera").ConfigureAwait(false);
        await page.Locator("#site-owner-name").FillAsync("Night Owner").ConfigureAwait(false);
        await CaptureAsync(page, "profile-dialog-1440x900").ConfigureAwait(false);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Save profile" }).ClickAsync().ConfigureAwait(false);
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden }).ConfigureAwait(false);
        await page.GetByText("Saved site profile revision 1.").WaitForAsync().ConfigureAwait(false);
        // The sidebar reads "<camera name> / <module type>".
        await page.WaitForFunctionAsync(
            "() => document.querySelector('.operations-scope small')?.textContent.trim().startsWith('East dome camera / ')")
            .ConfigureAwait(false);
        var profileHistory = page.Locator("section[aria-labelledby='site-profile-history-heading']");
        StringAssert.Contains(await profileHistory.InnerTextAsync().ConfigureAwait(false), "Hualapai Valley Observatory",
            StringComparison.Ordinal);
        Assert.AreEqual("Hualapai Valley Observatory", (await page.Locator(".ops-map-label").InnerTextAsync()
            .ConfigureAwait(false)).Trim());
        await CaptureAsync(page, "profile-saved-1440x900").ConfigureAwait(false);

        // A site without map egress keeps the page whole and says why the schematic is shown.
        tilesReachable = false;
        await page.ReloadAsync().ConfigureAwait(false);
        await page.Locator(".site-map[data-tiles='offline']").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached })
            .ConfigureAwait(false);
        await page.GetByText("Map tiles could not be loaded, so the offline schematic is shown.", new() { Exact = false })
            .WaitForAsync().ConfigureAwait(false);
        await page.Locator("#site-scene-heading").WaitForAsync().ConfigureAwait(false);
        await CaptureAsync(page, "offline-1440x900").ConfigureAwait(false);

        // The operations walk measures narrow workspace forms with a fine pointer; touch devices get their own context,
        // because resizing or a full-page screenshot drops Chromium's touch emulation.
        foreach (var (width, height) in new[] { (390, 844), (320, 844) })
        {
            await page.SetViewportSizeAsync(width, height).ConfigureAwait(false);
            await page.ReloadAsync().ConfigureAwait(false);
            await page.Locator("#site-scene-heading").WaitForAsync().ConfigureAwait(false);
            // Measure the interactive page, not the prerendered one it replaces.
            await page.Locator(".app-frame[data-interactive='true']").WaitForAsync().ConfigureAwait(false);
            await AssertFitsAsync(page, width, height, "fine pointer").ConfigureAwait(false);
            await CaptureAsync(page, $"site-{width}x{height}").ConfigureAwait(false);

            await using var touchContext = await diagnostics.NewContextAsync(new BrowserNewContextOptions
            {
                BaseURL = host.BaseAddress.ToString(),
                ViewportSize = new ViewportSize { Width = width, Height = height },
                ColorScheme = ColorScheme.Dark,
                HasTouch = true,
                IsMobile = true
            }).ConfigureAwait(false);
            await touchContext.RouteAsync($"https://{TileHost}/**", route => route.AbortAsync()).ConfigureAwait(false);
            var touch = await touchContext.NewPageAsync().ConfigureAwait(false);
            await OpenSiteAsync(touch).ConfigureAwait(false);
            Assert.IsTrue(await touch.EvaluateAsync<bool>("() => matchMedia('(pointer: coarse)').matches").ConfigureAwait(false),
                "Touch-target checks require a coarse-pointer device.");
            await AssertFitsAsync(touch, width, height, "touch").ConfigureAwait(false);
            await touch.Locator("#site-edit-profile").ClickAsync().ConfigureAwait(false);
            var touchDialog = touch.Locator("dialog.site-dialog");
            await touchDialog.WaitForAsync().ConfigureAwait(false);
            var dialogBox = await touchDialog.BoundingBoxAsync().ConfigureAwait(false);
            Assert.IsNotNull(dialogBox);
            Assert.IsLessThanOrEqualTo(width, dialogBox.X + dialogBox.Width, $"The profile dialog overflows the {width}px viewport.");
            Assert.AreEqual("East dome camera", await touch.Locator("#site-camera-name").InputValueAsync().ConfigureAwait(false),
                "The dialog must open on the saved profile.");
            await CaptureAsync(touch, $"profile-dialog-touch-{width}x{height}").ConfigureAwait(false);
            await touch.Keyboard.PressAsync("Escape").ConfigureAwait(false);
            await touchDialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden }).ConfigureAwait(false);
            Assert.IsTrue(await touch.Locator("#site-edit-profile").EvaluateAsync<bool>("element => document.activeElement === element")
                .ConfigureAwait(false), "Closing the dialog must return focus to the button that opened it.");
        }

        Assert.IsEmpty(browserErrors, string.Join(Environment.NewLine, browserErrors));
        await diagnostics.CompleteAsync().ConfigureAwait(false);
    }

    private static async Task OpenSiteAsync(IPage page)
    {
        await page.GotoAsync("/Account/Login").ConfigureAwait(false);
        await page.GetByLabel("Email").FillAsync(CameraAgentKestrelFixture.OwnerEmail).ConfigureAwait(false);
        await page.GetByLabel("Password").FillAsync(CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase))
            .ConfigureAwait(false);
        await page.GotoAsync("/operations/site").ConfigureAwait(false);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Observatory & location", Level = 1 })
            .WaitForAsync().ConfigureAwait(false);
        await page.Locator("#site-scene-heading").WaitForAsync().ConfigureAwait(false);
        // Prerendered buttons exist before the circuit attaches their handlers; a click before then is lost.
        await page.Locator(".app-frame[data-interactive='true']").WaitForAsync().ConfigureAwait(false);
    }

    // Names the elements that reach past the viewport edge, so a failure says what to fix; empty when the page fits.
    private const string OverflowingElementsScript = """
        () => {
            const root = document.documentElement;
            if (root.scrollWidth <= root.clientWidth + 1) return '';
            return [...document.querySelectorAll('body *')]
                .filter(element => element.getClientRects().length > 0 && element.getBoundingClientRect().right > root.clientWidth + 1)
                .filter(element => ![...element.children].some(child => child.getBoundingClientRect().right > root.clientWidth + 1))
                .slice(0, 5)
                .map(element => `${element.tagName.toLowerCase()}.${[...element.classList].join('.')} right=${Math.round(element.getBoundingClientRect().right)}`)
                .join(' | ') || `scrollWidth=${root.scrollWidth}`;
        }
        """;

    private static async Task AssertFitsAsync(IPage page, int width, int height, string pointer)
    {
        var overflowing = await page.EvaluateAsync<string>(OverflowingElementsScript).ConfigureAwait(false);
        Assert.AreEqual(string.Empty, overflowing, $"Observatory & location overflows the {width}x{height} viewport ({pointer}): {overflowing}");
        var undersized = await page.EvaluateAsync<string>(UndersizedControlsScript).ConfigureAwait(false);
        Assert.AreEqual(string.Empty, undersized, $"Controls under 44 CSS pixels at {width}x{height} ({pointer}): {undersized}");
    }

    private async Task CaptureAsync(IPage page, string name)
    {
        var directory = TestContext.TestRunResultsDirectory ?? Path.GetTempPath();
        var path = Path.Combine(directory, $"observatory-location-{name}-{Guid.NewGuid():N}.png");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true }).ConfigureAwait(false);
        TestContext.AddResultFile(path);
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();
}
