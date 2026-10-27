using FluentAssertions;
using HVO.SkyMonitor.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class CurrentSkyBrowserAcceptanceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task CurrentSky_ExactPreviewKeyboardTouchResponsiveAndReconnectDenial()
    {
        await using var fixture = await LogicHostUiKestrelFixture.CreateAsync();
        LogicalCameraArchiveIntegrationTests.Dataset data;
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            data = await LogicalCameraArchiveIntegrationTests.SeedAsync(scope.ServiceProvider, 2);
            await LogicalCameraArchiveIntegrationTests.AddPreviewAsync(scope.ServiceProvider, data.Frames[0], true);
        }
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { HasTouch = true, ViewportSize = new() { Width = 1440, Height = 900 } });
        var page = await context.NewPageAsync();
        IWebSocketRoute? socket = null;
        TaskCompletionSource? gate = null;
        var reconnectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteWebSocketAsync("**/_blazor*", route =>
        {
            var server = route.ConnectToServer();
            if (gate is not null)
            {
                reconnectStarted.TrySetResult();
                var wait = gate.Task;
                route.OnMessage(async frame =>
                {
                    await wait;
                    if (frame.Binary is { } binary) server.Send(binary);
                    else server.Send(frame.Text!);
                });
            }
            socket = server;
        });
        var url = new Uri(fixture.BaseAddress, $"/app/network/observatories/{data.Observatory:D}/cameras/{data.Camera:D}/current").AbsoluteUri;
        await page.GotoAsync(url);
        await page.GetByLabel("Email").FillAsync(TestUsers.Operator.Email);
        await page.GetByLabel("Password").FillAsync(TestUsers.Operator.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
        await page.Locator(".current-sky[data-scope-interactive='true']").WaitForAsync();
        await Assertions.Expect(page.Locator(".current-sky img")).ToBeVisibleAsync();
        var imagePath = await page.Locator(".current-sky img").GetAttributeAsync("src");
        imagePath.Should().Contain("/artifacts/");
        foreach (var (width, height) in new[] { (1440, 900), (390, 844), (844, 390), (320, 700) })
        {
            await page.SetViewportSizeAsync(width, height);
            var refresh = page.GetByRole(AriaRole.Button, new() { Name = "Refresh Current Sky", Exact = true });
            var before = int.Parse((await page.Locator(".current-sky").GetAttributeAsync("data-selection-generation"))!, System.Globalization.CultureInfo.InvariantCulture);
            await refresh.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await page.WaitForFunctionAsync("""
                previous => {
                    const root = document.querySelector('.current-sky');
                    return Number(root?.dataset.selectionGeneration) > previous &&
                        root.getAttribute('aria-busy') === 'false' && root.querySelector('img');
                }
                """, before);
            await Assertions.Expect(page.Locator(".current-sky img")).ToHaveAttributeAsync("src", imagePath!);
            await Assertions.Expect(refresh).ToBeEnabledAsync();
            await Assertions.Expect(refresh).ToBeFocusedAsync();
            (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue();
            var tree = await page.Locator(".current-sky").AriaSnapshotAsync();
            tree.Should().Contain("heading \"Current Sky\" [level=2]").And.Contain("status:").And.Contain("Selected Preview")
                .And.Contain("heading \"Capture provenance\" [level=4]");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 3 }))
                .ToHaveTextAsync($"Capture {data.Frames[0].CapturedAtUtc:u}");
            await Assertions.Expect(page.Locator(".scope-content h1")).ToHaveCountAsync(1);
            if (width == 390)
            {
                await page.Locator(".current-sky details summary").TapAsync();
                await Assertions.Expect(page.Locator(".current-sky details")).ToHaveAttributeAsync("open", "");
            }
        }
        // The same viewer remains the page heading outside the scoped workspace.
        await page.GotoAsync(new Uri(fixture.BaseAddress, $"/app/captures/{data.Frames[0].Id:D}").AbsoluteUri);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 }))
            .ToHaveTextAsync($"Capture {data.Frames[0].CapturedAtUtc:u}");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Capture provenance", Exact = true, Level = 2 }))
            .ToBeVisibleAsync();
        foreach (var (width, height) in new[] { (1440, 900), (390, 844), (844, 390), (320, 700) })
        {
            await page.SetViewportSizeAsync(width, height);
            (await page.Locator("main").AriaSnapshotAsync()).Should().Contain("heading \"Capture provenance\" [level=2]");
            (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue();
        }
        await page.GotoAsync(url);
        await page.Locator(".current-sky[data-scope-interactive='true']").WaitForAsync();
        await Assertions.Expect(page.Locator(".current-sky img")).ToHaveAttributeAsync("src", imagePath!);
        // Real circuit recovery must recheck membership before reusing any selected image.
        socket.Should().NotBeNull();
        gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await socket!.CloseAsync();
        await reconnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            await using var scope = fixture.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.ObservatoryMemberships.Where(m => m.ObservatoryId == data.Observatory && m.UserId == data.Owner).ExecuteDeleteAsync();
        }
        finally { gate.TrySetResult(); }
        await page.GetByText("Scope not found or access was removed.", new() { Exact = true }).WaitForAsync(new() { Timeout = 15000 });
        (await page.Locator(".current-sky img").CountAsync()).Should().Be(0);
        (await page.ContentAsync()).Should().NotContain(imagePath!);
        TestContext.WriteLine("Four viewports: keyboard refresh, stable focus, touch disclosure, accessibility tree and no horizontal overflow; real websocket reconnect removed revoked content.");
    }
}
