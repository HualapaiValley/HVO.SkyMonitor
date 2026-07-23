using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Configured async disposal would hide the strongly typed browser and host fixtures used throughout each acceptance scope.")]
public sealed class CameraAgentBrowserAcceptanceTests
{
    private const float DefaultTimeoutMilliseconds = 45_000;

    [TestMethod]
    [TestCategory("Manual")]
    [DoNotParallelize]
    public async Task OwnerOperationsGalleryAndResponsiveAcceptanceAsync()
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        if (!File.Exists(playwright.Chromium.ExecutablePath))
        {
            Assert.Inconclusive(
                "Pinned Playwright Chromium is absent. Run `scripts/test:cameraagent-ui-106 --install-browser` from the repository root.");
        }

        await using var host = await CameraAgentKestrelFixture.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        }).ConfigureAwait(false);

        await AssertAnonymousAndNonOwnerAuthorizationAsync(browser, host.BaseAddress).ConfigureAwait(false);

        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            ColorScheme = ColorScheme.Dark,
            ReducedMotion = ReducedMotion.Reduce
        }).ConfigureAwait(false);
        context.SetDefaultTimeout(DefaultTimeoutMilliseconds);
        context.SetDefaultNavigationTimeout(DefaultTimeoutMilliseconds);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        var browserErrors = new List<string>();
        var previewFailures = new List<string>();
        page.PageError += (_, error) => browserErrors.Add(error);
        page.Response += (_, response) =>
        {
            if (response.Url.Contains("/preview", StringComparison.OrdinalIgnoreCase) && !response.Ok)
            {
                previewFailures.Add($"{response.Status}:{new Uri(response.Url).AbsolutePath}");
            }
        };

        await LoginAsync(
            page,
            CameraAgentKestrelFixture.OwnerEmail,
            CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
        await AssertOperationsAndCaptureControlAsync(page).ConfigureAwait(false);
        var detailUrl = await AssertGalleryAsync(page, context, host).ConfigureAwait(false);
        await AssertQuarantineAsync(page).ConfigureAwait(false);
        await AssertSystemNavigationAsync(page).ConfigureAwait(false);
        await AssertResponsiveAndAccessibleAsync(page, detailUrl).ConfigureAwait(false);

        Assert.IsEmpty(browserErrors, string.Join(Environment.NewLine, browserErrors));
        Assert.IsEmpty(previewFailures, string.Join(Environment.NewLine, previewFailures));
    }

    private static async Task AssertAnonymousAndNonOwnerAuthorizationAsync(IBrowser browser, Uri baseAddress)
    {
        await using (var anonymous = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = baseAddress.ToString()
        }).ConfigureAwait(false))
        {
            var page = await anonymous.NewPageAsync().ConfigureAwait(false);
            await page.GotoAsync("/gallery").ConfigureAwait(false);
            await page.WaitForURLAsync(url => url.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase) &&
                url.Contains("returnUrl=", StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);
            Assert.AreEqual("Log in", await page.GetByRole(AriaRole.Heading, new() { Level = 1 }).InnerTextAsync().ConfigureAwait(false));

            var response = await anonymous.APIRequest.GetAsync("/api/v1/operations/gallery").ConfigureAwait(false);
            try
            {
                Assert.AreEqual(401, response.Status);
            }
            finally
            {
                await response.DisposeAsync().ConfigureAwait(false);
            }
        }

        await using var nonOwner = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = baseAddress.ToString()
        }).ConfigureAwait(false);
        var nonOwnerPage = await nonOwner.NewPageAsync().ConfigureAwait(false);
        await LoginAsync(
            nonOwnerPage,
            CameraAgentKestrelFixture.NonOwnerEmail,
            CameraAgentKestrelFixture.NonOwnerPassword).ConfigureAwait(false);
        await nonOwnerPage.GotoAsync("/gallery").ConfigureAwait(false);
        await nonOwnerPage.WaitForURLAsync(url => url.Contains("/Account/AccessDenied", StringComparison.OrdinalIgnoreCase))
            .ConfigureAwait(false);
        Assert.AreEqual(
            "Access denied",
            await nonOwnerPage.GetByRole(AriaRole.Heading, new() { Level = 1 }).InnerTextAsync().ConfigureAwait(false));
    }

    private static async Task LoginAsync(IPage page, string email, string password)
    {
        await page.GotoAsync("/Account/Login").ConfigureAwait(false);
        await page.GetByLabel("Email").FillAsync(email).ConfigureAwait(false);
        await page.GetByLabel("Password").FillAsync(password).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase))
            .ConfigureAwait(false);
    }

    private static async Task AssertOperationsAndCaptureControlAsync(IPage page)
    {
        await page.GotoAsync("/operations").ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Capture operations", Level = 1 }))
            .ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Navigation)).ConfigureAwait(false);
        await VisibleAsync(page.Locator("main#mainContent")).ConfigureAwait(false);
        await VisibleAsync(page.Locator(".heading-status .state-chip")).ConfigureAwait(false);
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            await page.Locator(".heading-status .state-chip").InnerTextAsync().ConfigureAwait(false)));

        var action = page.Locator("#capture-action");
        await VisibleAsync(action).ConfigureAwait(false);
        var dialog = page.Locator("dialog.confirmation");
        await OpenDialogAsync(action, dialog).ConfigureAwait(false);
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.querySelector('dialog')?.contains(document.activeElement) === true")
            .ConfigureAwait(false));
        await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden }).ConfigureAwait(false);
        Assert.AreEqual("capture-action", await page.EvaluateAsync<string>("() => document.activeElement?.id || ''").ConfigureAwait(false));

        await OpenDialogAsync(action, dialog).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm pause capture" }).ClickAsync().ConfigureAwait(false);
        await VisibleAsync(page.Locator(".receipt[role='status']")).ConfigureAwait(false);
        StringAssert.Contains(
            await page.Locator(".receipt[role='status']").InnerTextAsync().ConfigureAwait(false),
            "Current state: Paused",
            StringComparison.Ordinal);
        Assert.AreEqual("Review resume", await action.InnerTextAsync().ConfigureAwait(false));

        await OpenDialogAsync(action, dialog).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm resume capture" }).ClickAsync().ConfigureAwait(false);
        await VisibleAsync(page.Locator(".receipt[role='status']")).ConfigureAwait(false);
        StringAssert.Contains(
            await page.Locator(".receipt[role='status']").InnerTextAsync().ConfigureAwait(false),
            "Current state: Running",
            StringComparison.Ordinal);
    }

    private static async Task<string> AssertGalleryAsync(
        IPage page,
        IBrowserContext ownerContext,
        CameraAgentKestrelFixture host)
    {
        await WaitForGalleryCapturesAsync(page, minimumCards: 24).ConfigureAwait(false);
        await AssertPageStructureAsync(page, requireForm: true).ConfigureAwait(false);
        var evidenceBadge = page.Locator(".capture-card .evidence").First;
        await VisibleAsync(evidenceBadge).ConfigureAwait(false);
        Assert.AreEqual("Developer fixture", await evidenceBadge.InnerTextAsync().ConfigureAwait(false));
        await VisibleAsync(page.GetByText("Older captures", new() { Exact = true })).ConfigureAwait(false);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await page.GetByLabel("Evidence origin").SelectOptionAsync("DeveloperFixture").ConfigureAwait(false);
            await page.GetByLabel("Page size").SelectOptionAsync("24").ConfigureAwait(false);
            await page.GetByRole(AriaRole.Button, new() { Name = "Apply filters" }).ClickAsync().ConfigureAwait(false);
            try
            {
                await page.WaitForURLAsync(url => new Uri(url).Query.Contains("origin=DeveloperFixture", StringComparison.Ordinal),
                    new PageWaitForURLOptions { Timeout = 2_000 }).ConfigureAwait(false);
                break;
            }
            catch (TimeoutException) when (attempt < 9)
            {
                await Task.Delay(250).ConfigureAwait(false);
            }
        }
        await VisibleAsync(page.Locator(".capture-card").First).ConfigureAwait(false);
        await AssertVisibleImagesDecodeAsync(page).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Older captures" }).ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(url => new Uri(url).Query.Contains("origin=DeveloperFixture", StringComparison.Ordinal) &&
            new Uri(url).Query.Contains("cursor=", StringComparison.Ordinal)).ConfigureAwait(false);

        var detailLink = page.Locator(".capture-card a[aria-label^='Open capture']").First;
        var detailUrl = await detailLink.GetAttributeAsync("href").ConfigureAwait(false);
        Assert.IsNotNull(detailUrl);
        StringAssert.Contains(detailUrl, "returnUrl=", StringComparison.Ordinal);
        await detailLink.ClickAsync().ConfigureAwait(false);
        await VisibleAsync(page.GetByText("Capture detail", new() { Exact = true })).ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Artifacts", Level = 2 })).ConfigureAwait(false);

        var image = page.Locator(".detail-hero img");
        await VisibleAsync(image).ConfigureAwait(false);
        Assert.IsTrue(await image.EvaluateAsync<bool>("image => image.complete && image.naturalWidth > 0").ConfigureAwait(false));
        Assert.AreEqual("contain", await image.EvaluateAsync<string>("image => getComputedStyle(image).objectFit").ConfigureAwait(false));
        Assert.IsTrue(await image.EvaluateAsync<bool>("image => image.getBoundingClientRect().width <= innerWidth && image.getBoundingClientRect().height <= innerHeight")
            .ConfigureAwait(false));

        var previewUrl = await image.GetAttributeAsync("src").ConfigureAwait(false);
        var contentUrl = await page.Locator("a[download]").First.GetAttributeAsync("href").ConfigureAwait(false);
        Assert.IsNotNull(previewUrl);
        Assert.IsNotNull(contentUrl);
        await AssertCookieProtectedContentAsync(ownerContext, previewUrl, "image/jpeg").ConfigureAwait(false);
        await AssertCookieProtectedContentAsync(ownerContext, contentUrl, null).ConfigureAwait(false);

        var bodyText = await page.Locator("body").InnerTextAsync().ConfigureAwait(false);
        foreach (var prohibited in new[]
        {
            host.Root,
            CameraAgentKestrelFixture.OwnerPassword,
            CameraAgentKestrelFixture.NonOwnerPassword,
            "leaseToken",
            "clientSecret",
            "DataProtection-Keys"
        })
        {
            Assert.IsFalse(bodyText.Contains(prohibited, StringComparison.OrdinalIgnoreCase), prohibited);
        }

        await page.GetByRole(AriaRole.Link, new() { Name = "Gallery results", Exact = true }).ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(url => new Uri(url).Query.Contains("origin=DeveloperFixture", StringComparison.Ordinal) &&
            new Uri(url).Query.Contains("cursor=", StringComparison.Ordinal)).ConfigureAwait(false);
        return detailUrl;
    }

    private static async Task AssertCookieProtectedContentAsync(
        IBrowserContext context,
        string relativeUrl,
        string? expectedContentType)
    {
        var response = await context.APIRequest.GetAsync(relativeUrl).ConfigureAwait(false);
        try
        {
            Assert.AreEqual(200, response.Status, relativeUrl);
            Assert.IsGreaterThan(0, (await response.BodyAsync().ConfigureAwait(false)).Length, relativeUrl);
            if (expectedContentType is not null)
            {
                Assert.IsTrue(response.Headers.TryGetValue("content-type", out var contentType));
                StringAssert.StartsWith(contentType, expectedContentType, StringComparison.OrdinalIgnoreCase);
            }
            Assert.IsTrue(response.Headers.TryGetValue("vary", out var vary));
            StringAssert.Contains(vary, "Cookie", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await response.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task AssertSystemNavigationAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Link, new() { Name = "System", Exact = true }).ClickAsync().ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "System snapshot", Level = 1 }))
            .ConfigureAwait(false);
        await VisibleAsync(page.GetByText("Startup truth:", new() { Exact = true })).ConfigureAwait(false);
        await AssertPageStructureAsync(page, requireForm: false).ConfigureAwait(false);
    }

    private static async Task AssertQuarantineAsync(IPage page)
    {
        await page.GotoAsync("/operations/quarantine?kind=Artifact").ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Heading, new() { Name = "Quarantine", Level = 1 })).ConfigureAwait(false);
        await VisibleAsync(page.GetByRole(AriaRole.Button, new() { Name = "Older records" })).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Older records" }).ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(url => new Uri(url).Query.Contains("cursor=", StringComparison.Ordinal)).ConfigureAwait(false);

        var action = page.GetByRole(AriaRole.Button, new() { Name = "Review abandon" }).First;
        await VisibleAsync(action).ConfigureAwait(false);
        var triggerId = await action.GetAttributeAsync("id").ConfigureAwait(false);
        var dialog = page.Locator("dialog.confirmation");
        await OpenDialogAsync(action, dialog).ConfigureAwait(false);
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.querySelector('dialog')?.contains(document.activeElement) === true")
            .ConfigureAwait(false));
        await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden }).ConfigureAwait(false);
        Assert.AreEqual(triggerId, await page.EvaluateAsync<string>("() => document.activeElement?.id || ''").ConfigureAwait(false));
        await OpenDialogAsync(action, dialog).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm abandon" }).ClickAsync().ConfigureAwait(false);
        await VisibleAsync(page.Locator(".receipt[role='status']")).ConfigureAwait(false);
    }

    private static async Task AssertResponsiveAndAccessibleAsync(IPage page, string detailUrl)
    {
        var viewports = new[]
        {
            new ViewportSize { Width = 1440, Height = 900 },
            new ViewportSize { Width = 820, Height = 1180 },
            new ViewportSize { Width = 390, Height = 844 },
            new ViewportSize { Width = 844, Height = 390 },
            new ViewportSize { Width = 320, Height = 700 }
        };
        var routes = new[] { "/operations", "/operations/quarantine?kind=Artifact", "/gallery?pageSize=24", detailUrl, "/system" };
        foreach (var viewport in viewports)
        {
            await page.SetViewportSizeAsync(viewport.Width, viewport.Height).ConfigureAwait(false);
            foreach (var route in routes)
            {
                await page.GotoAsync(route).ConfigureAwait(false);
                await VisibleAsync(page.Locator("main#mainContent h1").First).ConfigureAwait(false);
                Assert.IsTrue(await page.EvaluateAsync<bool>(
                    "() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1")
                    .ConfigureAwait(false), $"Horizontal overflow at {viewport.Width}x{viewport.Height} on {route}.");
                await AssertPageStructureAsync(page, route.StartsWith("/gallery?", StringComparison.Ordinal))
                    .ConfigureAwait(false);
                await AssertComputedContrastAsync(page, route, viewport).ConfigureAwait(false);
            }
        }
    }

    private static async Task AssertPageStructureAsync(IPage page, bool requireForm)
    {
        Assert.AreEqual(1, await page.Locator("main").CountAsync().ConfigureAwait(false));
        Assert.AreEqual(1, await page.Locator("main h1").CountAsync().ConfigureAwait(false));
        Assert.IsGreaterThan(0, await page.Locator("header").CountAsync().ConfigureAwait(false));
        Assert.IsTrue(await page.EvaluateAsync<bool>("""
            () => [...document.querySelectorAll('a[href], button, img')]
                .filter(element => element.getClientRects().length > 0)
                .every(element => {
                    if (element.tagName === 'IMG') return Boolean(element.getAttribute('alt'));
                    return Boolean((element.getAttribute('aria-label') || element.textContent || '').trim());
                })
            """).ConfigureAwait(false));
        if (requireForm)
        {
            Assert.IsGreaterThan(0, await page.Locator("form[aria-label]").CountAsync().ConfigureAwait(false));
            Assert.IsTrue(await page.EvaluateAsync<bool>("""
                () => [...document.querySelectorAll('form[aria-label] input, form[aria-label] select')]
                    .every(element => element.labels?.length > 0 || Boolean(element.getAttribute('aria-label')))
                """).ConfigureAwait(false));
        }
    }

    private static async Task WaitForGalleryCapturesAsync(IPage page, int minimumCards)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await page.GotoAsync("/gallery").ConfigureAwait(false);
            await page.Locator(".gallery-state, .capture-grid").First.WaitForAsync().ConfigureAwait(false);
            if (await page.Locator(".capture-card").CountAsync().ConfigureAwait(false) >= minimumCards &&
                await page.GetByRole(AriaRole.Button, new() { Name = "Older captures" }).CountAsync().ConfigureAwait(false) > 0)
            {
                return;
            }
            await Task.Delay(500).ConfigureAwait(false);
        }
        Assert.Fail($"The real RandomImage pipeline did not produce {minimumCards} durable gallery captures within 45 seconds.");
    }

    private static async Task AssertVisibleImagesDecodeAsync(IPage page)
    {
        await page.WaitForFunctionAsync("""
            () => {
              const images = [...document.querySelectorAll('.capture-card img')]
                .filter(image => image.getClientRects().length > 0);
              return images.length > 0 && images.every(image => image.complete && image.naturalWidth > 0);
            }
            """).ConfigureAwait(false);
    }

    private static async Task AssertComputedContrastAsync(IPage page, string route, ViewportSize viewport)
    {
        var result = await page.EvaluateAsync<string>("""
            () => {
              const parse = value => {
                const channels = (value.match(/[\d.]+/g) || []).map(Number);
                return { rgb: channels.slice(0, 3), alpha: channels.length > 3 ? channels[3] : 1 };
              };
              const over = (top, bottom) => {
                const alpha = top.alpha + bottom.alpha * (1 - top.alpha);
                return {
                  rgb: top.rgb.map((channel, index) =>
                    alpha === 0 ? 0 : (channel * top.alpha + bottom.rgb[index] * bottom.alpha * (1 - top.alpha)) / alpha),
                  alpha
                };
              };
              const background = element => {
                let color = { rgb: [0, 0, 0], alpha: 0 };
                for (let current = element; current && color.alpha < 1; current = current.parentElement) {
                  color = over(color, parse(getComputedStyle(current).backgroundColor));
                }
                return over(color, { rgb: [255, 255, 255], alpha: 1 });
              };
              const luminance = color => {
                const linear = color.rgb.map(value => { const c = value / 255; return c <= .04045 ? c / 12.92 : ((c + .055) / 1.055) ** 2.4; });
                return .2126 * linear[0] + .7152 * linear[1] + .0722 * linear[2];
              };
              const measure = element => {
                const backdrop = background(element);
                const style = getComputedStyle(element);
                const foreground = over(parse(style.color), backdrop);
                const foregroundLuminance = luminance(foreground);
                const backgroundLuminance = luminance(backdrop);
                return {
                  ratio: (Math.max(foregroundLuminance, backgroundLuminance) + .05) /
                    (Math.min(foregroundLuminance, backgroundLuminance) + .05),
                  foreground: style.color,
                  background: style.backgroundColor,
                  backdrop: backdrop.rgb.map(Math.round).join(',')
                };
              };
              const pairs = [...document.querySelectorAll('main p, main dd, main .state-chip, main button')]
                .filter(element => element.getClientRects().length > 0);
              const measured = pairs.map(element => Object.assign(measure(element), {
                  element: `${element.tagName.toLowerCase()}.${[...element.classList].join('.')}`,
                  text: (element.textContent || '').trim().slice(0, 80)
                })).sort((left, right) => left.ratio - right.ratio);
              return JSON.stringify(measured[0] || { ratio: 21, element: 'none', text: '' });
            }
            """).ConfigureAwait(false);
        using var measurement = JsonDocument.Parse(result);
        var minimum = measurement.RootElement.GetProperty("ratio").GetDouble();
        var element = measurement.RootElement.GetProperty("element").GetString();
        var text = measurement.RootElement.GetProperty("text").GetString();
        var foreground = measurement.RootElement.GetProperty("foreground").GetString();
        var background = measurement.RootElement.GetProperty("background").GetString();
        var backdrop = measurement.RootElement.GetProperty("backdrop").GetString();
        Assert.IsGreaterThanOrEqualTo(4.5, minimum,
            $"Visible primary text, status, and control pairs require deterministic 4.5:1 contrast. " +
            $"Route {route} at {viewport.Width}x{viewport.Height}; {element}: '{text}'; " +
            $"foreground {foreground}, background {background}, composited backdrop rgb({backdrop}).");
    }

    private static async Task OpenDialogAsync(ILocator trigger, ILocator dialog)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await trigger.ClickAsync().ConfigureAwait(false);
            try
            {
                await dialog.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 2_000
                }).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException) when (attempt < 9)
            {
                await Task.Delay(250).ConfigureAwait(false);
            }
        }
    }

    private static Task VisibleAsync(ILocator locator) => locator.WaitForAsync(new LocatorWaitForOptions
    {
        State = WaitForSelectorState.Visible,
        Timeout = DefaultTimeoutMilliseconds
    });
}
