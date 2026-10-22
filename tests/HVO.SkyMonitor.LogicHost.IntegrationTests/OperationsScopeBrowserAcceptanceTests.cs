using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class OperationsScopeBrowserAcceptanceTests
{
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { WriteIndented = true };
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Issue1238_ScopesStayBoundedAuthorizedAndAccessibleAcrossNavigationAndReconnect()
    {
        var scopeReads = new ScopeSqlProbe();
        await using var fixture = await LogicHostUiKestrelFixture.CreateAsync(
            configureDatabase: options => options.AddInterceptors(scopeReads));
        var dataset = await SeedAsync(fixture.Services);
        var sql = await MeasureSqlAsync(fixture.Services, dataset);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await using var context = await diagnostics.NewContextAsync(new()
        {
            ViewportSize = new() { Width = 1440, Height = 900 },
            HasTouch = true,
            ColorScheme = ColorScheme.Dark,
            ReducedMotion = ReducedMotion.Reduce
        });
        var page = await context.NewPageAsync();
        var pageErrors = new ConcurrentQueue<string>();
        page.PageError += (_, error) => pageErrors.Enqueue(error);
        var socketCount = 0;
        IWebSocketRoute? socket = null;
        TaskCompletionSource? reconnect = null;
        TaskCompletionSource? allowReconnect = null;
        TaskCompletionSource? reconnectAttempt = null;
        await page.RouteWebSocketAsync("**/_blazor*", route =>
        {
            // Install forwarding immediately so the SignalR handshake cannot be lost.
            // Only replacement-connection frames wait for the membership revocation.
            var server = route.ConnectToServer();
            if (allowReconnect is not null)
            {
                reconnectAttempt!.TrySetResult();
                var release = allowReconnect.Task;
                route.OnMessage(async frame =>
                {
                    await release;
                    if (frame.Binary is { } binary) server.Send(binary);
                    else server.Send(frame.Text!);
                });
            }
            socket = server;
            Interlocked.Increment(ref socketCount);
            reconnect?.TrySetResult();
        });
        await page.GotoAsync(new Uri(fixture.BaseAddress, "/app/network").AbsoluteUri);
        await page.GetByLabel("Email").FillAsync(TestUsers.Operator.Email);
        await page.GetByLabel("Password").FillAsync(TestUsers.Operator.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
        await ReadyAsync(page);
        await Assertions.Expect(page.Locator("#scope-heading")).ToBeFocusedAsync();
        var forward = scopeReads.HoldNextRead();
        try
        {
            await page.Locator($"#scope-observatory-{dataset.ThousandObservatoryId:D}").FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await forward.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await page.GetByText("Checking scope access…", new() { Exact = true }).WaitForAsync();
        }
        finally { forward.Released.TrySetResult(); }
        await ReadyAsync(page);
        await Assertions.Expect(page.Locator("#scope-heading")).ToBeFocusedAsync();

        var root = Path.Combine(TestContext.TestRunDirectory!, "issue-1238");
        Directory.CreateDirectory(root);
        var routes = new List<object>();
        var lastNavigationStarted = Stopwatch.GetTimestamp();
        foreach (var (width, height) in new[] { (1440, 900), (390, 844), (844, 390), (320, 700) })
        {
            await page.SetViewportSizeAsync(width, height);
            var started = Stopwatch.GetTimestamp();
            lastNavigationStarted = started;
            scopeReads.Commands.Clear();
            var response = await page.GotoAsync(new Uri(fixture.BaseAddress, ScopePath(dataset.ThousandObservatoryId)).AbsoluteUri);
            await ReadyAsync(page);
            var interactiveReadyMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var responseBytes = (await response!.BodyAsync()).LongLength;
            var observatorySql = CompleteScopeLoad(scopeReads);
            await AssertAccessibleAsync(page);
            (await page.Locator("nav[aria-label='Observatory scopes'] > a").CountAsync()).Should().Be(25);
            (await page.Locator("nav[aria-label='Camera scopes'] > a").CountAsync()).Should().Be(25);
            var initialCamera = await page.Locator("nav[aria-label='Camera scopes'] > a").First.TextContentAsync();
            await page.GetByRole(AriaRole.Link, new() { Name = "Next camera page", Exact = true }).ClickAsync();
            await page.WaitForURLAsync(url => url.Contains("cameraPage=", StringComparison.Ordinal));
            await Assertions.Expect(page.Locator("nav[aria-label='Camera scopes'] > a").First).Not.ToHaveTextAsync(initialCamera!);
            await ReadyAsync(page);
            var secondPageUrl = page.Url;
            (await page.Locator("nav[aria-label='Camera scopes'] > a").CountAsync()).Should().Be(25);
            (await page.Locator("nav[aria-label='Camera scopes'] > a").First.TextContentAsync()).Should().NotBe(initialCamera);
            await page.WaitForFunctionAsync("document.activeElement.id === 'scope-cameras'");
            await Assertions.Expect(page.Locator("#scope-cameras")).ToBeInViewportAsync();
            var cameraLink = page.Locator("nav[aria-label='Camera scopes'] > a").First;
            var expectedFocus = await cameraLink.GetAttributeAsync("id");
            var cameraName = await cameraLink.TextContentAsync();
            scopeReads.Commands.Clear();
            var cameraRead = scopeReads.HoldNextRead();
            try
            {
                await cameraLink.FocusAsync();
                await page.Keyboard.PressAsync("Enter");
                await cameraRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await page.GetByText("Checking scope access…", new() { Exact = true }).WaitForAsync();
            }
            finally { cameraRead.Released.TrySetResult(); }
            await Assertions.Expect(page.Locator(".scope-content h1")).ToHaveTextAsync(cameraName!);
            await ReadyAsync(page);
            await Assertions.Expect(page.Locator("#scope-heading")).ToBeFocusedAsync();
            var cameraSql = CompleteScopeLoad(scopeReads);
            (await page.Locator(".scope-content h1").TextContentAsync()).Should().Be(cameraName);
            await page.GoBackAsync();
            await page.WaitForURLAsync(secondPageUrl);
            await ReadyAsync(page);
            page.Url.Should().Be(secondPageUrl);
            await page.WaitForFunctionAsync("id => document.activeElement.id === id", expectedFocus);
            await Assertions.Expect(page.Locator($"#{expectedFocus}")).ToBeInViewportAsync();
            if (width == 390)
            {
                await page.Locator(".scope-picker summary").TapAsync();
                (await page.Locator(".scope-picker").GetAttributeAsync("open")).Should().BeNull();
                await page.Locator(".scope-picker summary").TapAsync();
            }
            await AssertAccessibleAsync(page);
            var screenshot = Path.Combine(root, $"scope-{width}x{height}.png");
            await page.ScreenshotAsync(new() { Path = screenshot, FullPage = true });
            TestContext.AddResultFile(screenshot);
            routes.Add(new
            {
                Width = width,
                Height = height,
                InteractiveReadyMilliseconds = interactiveReadyMilliseconds,
                NavigationAndHistoryMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                InitialResponseBytes = responseBytes,
                CompleteObservatoryScopeSql = observatorySql,
                CompleteCameraScopeSql = cameraSql,
                HtmlBytes = Encoding.UTF8.GetByteCount(await page.ContentAsync()),
                JavaScriptHeapBytes = await HeapBytesAsync(page),
                ObservatoryItems = await page.Locator("nav[aria-label='Observatory scopes'] > a").CountAsync(),
                CameraItems = await page.Locator("nav[aria-label='Camera scopes'] > a").CountAsync()
            });
        }

        // Close the actual transport. A new websocket and a working server event prove
        // circuit recovery, rather than treating a synthetic browser event as reconnect.
        socket.Should().NotBeNull();
        var beforeReconnect = Volatile.Read(ref socketCount);
        reconnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
        allowReconnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
        reconnectAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.EvaluateAsync("""
            () => {
                window.scopeReconnectStates = [];
                document.getElementById('components-reconnect-modal')
                    .addEventListener('components-reconnect-state-changed', event =>
                        window.scopeReconnectStates.push(event.detail.state));
            }
            """);
        await socket!.CloseAsync();
        await reconnectAttempt.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.ObservatoryMemberships.Remove(await db.ObservatoryMemberships.SingleAsync(m =>
                m.ObservatoryId == dataset.ThousandObservatoryId && m.UserId == dataset.UserId));
            await db.SaveChangesAsync();
        }
        allowReconnect.TrySetResult();
        await reconnect.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Volatile.Read(ref socketCount).Should().BeGreaterThan(beforeReconnect);
        await page.WaitForFunctionAsync("window.scopeReconnectStates.includes('hide')", null, new() { Timeout = 10000 });
        var reconnectStates = await page.EvaluateAsync<string[]>("window.scopeReconnectStates");
        await page.GetByText("Scope not found or access was removed.", new() { Exact = true })
            .WaitForAsync(new() { Timeout = 10000 });
        var reconnectWindowSeconds = Stopwatch.GetElapsedTime(lastNavigationStarted).TotalSeconds;
        reconnectWindowSeconds.Should().BeLessThan(30,
            "automatic removal must occur before this component's first periodic revalidation can fire");
        (await page.Locator(".scope-shell").TextContentAsync()).Should().NotContain("Scope 1k").And.NotContain("Camera 000");
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.ObservatoryMemberships.Add(new ObservatoryMembership
            {
                ObservatoryId = dataset.ThousandObservatoryId,
                UserId = dataset.UserId,
                Role = ObservatoryMembershipRole.Owner
            });
            await db.SaveChangesAsync();
        }
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh access", Exact = true }).ClickAsync();
        await ReadyAsync(page);

        await page.GotoAsync(new Uri(fixture.BaseAddress,
            $"{ScopePath(dataset.ThousandObservatoryId)}/cameras/{dataset.ForeignCameraId:D}").AbsoluteUri);
        await page.GetByText("Scope not found or access was removed.", new() { Exact = true }).WaitForAsync();
        (await page.Locator(".scope-shell").TextContentAsync()).Should()
            .NotContain("Scope foreign station").And.NotContain("Scope foreign camera");
        await page.GotoAsync(new Uri(fixture.BaseAddress,
            $"{ScopePath(dataset.ThousandObservatoryId)}?cameraPage=invalid").AbsoluteUri);
        await page.GetByText("This navigation page is no longer available. Start from the first page.", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Start from the first page", Exact = true }).ClickAsync();
        await ReadyAsync(page);
        (await page.Locator("nav[aria-label='Camera scopes'] > a").CountAsync()).Should().Be(25);

        scopeReads.Commands.Clear();
        await page.GotoAsync(new Uri(fixture.BaseAddress, ScopePath(dataset.TenThousandObservatoryId)).AbsoluteUri);
        await ReadyAsync(page);
        var tenThousand = new
        {
            CompleteScopeSql = CompleteScopeLoad(scopeReads),
            HtmlBytes = Encoding.UTF8.GetByteCount(await page.ContentAsync()),
            JavaScriptHeapBytes = await HeapBytesAsync(page),
            CameraItems = await page.Locator("nav[aria-label='Camera scopes'] > a").CountAsync()
        };
        tenThousand.CameraItems.Should().Be(25);
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.ObservatoryMemberships.Remove(await db.ObservatoryMemberships.SingleAsync(m =>
                m.ObservatoryId == dataset.TenThousandObservatoryId && m.UserId == dataset.UserId));
            await db.SaveChangesAsync();
        }
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh access", Exact = true }).ClickAsync();
        await page.GetByText("Scope not found or access was removed.", new() { Exact = true }).WaitForAsync();
        (await page.Locator(".scope-shell").TextContentAsync()).Should().NotContain("Scope 10k").And.NotContain("Camera 00000");

        foreach (var (email, password, role) in new[]
        {
            (TestUsers.Viewer.Email, TestUsers.Viewer.Password, "Viewer"),
            (TestUsers.Regular.Email, TestUsers.Regular.Password, "Manager")
        })
        {
            await using var roleContext = await diagnostics.NewContextAsync();
            var rolePage = await roleContext.NewPageAsync();
            await rolePage.GotoAsync(new Uri(fixture.BaseAddress, ScopePath(dataset.ThousandObservatoryId)).AbsoluteUri);
            await rolePage.GetByLabel("Email").FillAsync(email);
            await rolePage.GetByLabel("Password").FillAsync(password);
            await rolePage.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
            await ReadyAsync(rolePage);
            (await rolePage.Locator(".scope-role").TextContentAsync()).Should().Contain(role);
            await rolePage.GotoAsync(new Uri(fixture.BaseAddress,
                $"{ScopePath(dataset.ThousandObservatoryId)}/cameras/{dataset.ForeignCameraId:D}").AbsoluteUri);
            await rolePage.GetByText("Scope not found or access was removed.", new() { Exact = true }).WaitForAsync();
            (await rolePage.Locator(".scope-shell").TextContentAsync()).Should().NotContain("Scope foreign");
        }

        var evidence = Path.Combine(root, "acceptance.json");
        await File.WriteAllTextAsync(evidence, JsonSerializer.Serialize(new
        {
            Schema = "hvo-operations-scope-1238-v1",
            Revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),
            Browser = browser.Version,
            Sql = sql,
            Routes = routes,
            TenThousand = tenThousand,
            Reconnected = Volatile.Read(ref socketCount) > beforeReconnect,
            AutomaticReconnectRevalidation = true,
            ReconnectStates = reconnectStates,
            ReconnectWindowBeforeFirstPeriodicTickSeconds = reconnectWindowSeconds,
            PageErrors = pageErrors.ToArray(),
            ScopePairDenial = true,
            MalformedCursorRecovery = true,
            RemovedMembershipDenial = true
        }, EvidenceJsonOptions));
        TestContext.AddResultFile(evidence);
        pageErrors.Should().BeEmpty();
        await diagnostics.CompleteAsync();
    }

    private static async Task ReadyAsync(IPage page)
        => await page.Locator(".scope-shell[data-scope-interactive='true'][aria-busy='false'] .scope-content").WaitForAsync();

    private static SqlRead[] CompleteScopeLoad(ScopeSqlProbe probe)
    {
        var commands = probe.Commands.ToArray();
        commands.Length.Should().BeInRange(3, 18,
            "the actual shell includes all scope lookups and choices, with prerender/interactive and one coalesced focus refresh");
        commands.Should().OnlyContain(command => command.ReadCalls <= 52,
            "even installation detail is bounded to 50 rows plus continuation/read completion");
        return commands;
    }

    private static async Task AssertAccessibleAsync(IPage page)
    {
        (await page.Locator("html").GetAttributeAsync("data-theme")).Should().Be("hvo-dark");
        (await page.Locator("h1").CountAsync()).Should().Be(1);
        (await page.Locator("nav[aria-label='Breadcrumb'] [aria-current='page']").CountAsync()).Should().Be(1);
        (await page.Locator("nav[aria-label='Workspace sections'] [aria-current='page']").CountAsync()).Should().Be(1);
        (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth + 1")).Should().BeFalse();
        var unnamed = await page.EvaluateAsync<int>("""
            () => [...document.querySelectorAll('.scope-shell a,.scope-shell button,.scope-shell summary')]
              .filter(e => e.getClientRects().length && !e.disabled && !e.textContent.trim() && !e.getAttribute('aria-label')).length
            """);
        unnamed.Should().Be(0);
        var minimumContrast = await page.EvaluateAsync<double>("""
            () => {
                const rgba = color => color.match(/[\d.]+/g).map(Number);
                const background = rgba(getComputedStyle(document.body).backgroundColor);
                const luminance = rgb => rgb.slice(0, 3).map(v => {
                    v /= 255;
                    return v <= .04045 ? v / 12.92 : ((v + .055) / 1.055) ** 2.4;
                }).reduce((sum, value, i) => sum + value * [.2126, .7152, .0722][i], 0);
                const base = luminance(background);
                return Math.min(...[...document.querySelectorAll('.scope-role,.scope-guidance')].map(e => {
                    const color = rgba(getComputedStyle(e).color), alpha = color[3] ?? 1;
                    const foreground = luminance(color.slice(0, 3).map((v, i) => v * alpha + background[i] * (1 - alpha)));
                    return (Math.max(base, foreground) + .05) / (Math.min(base, foreground) + .05);
                }));
            }
            """);
        minimumContrast.Should().BeGreaterThanOrEqualTo(4.5, "scope descriptions must remain readable in the HVO dark theme");
        (await page.Locator(".scope-shell").AriaSnapshotAsync()).Should()
            .Contain("navigation \"Breadcrumb\"").And.Contain("navigation \"Observatory scopes\"");
    }

    private static async Task<long> HeapBytesAsync(IPage page)
    {
        var session = await page.Context.NewCDPSessionAsync(page);
        try
        {
            await session.SendAsync("Performance.enable");
            var result = await session.SendAsync("Performance.getMetrics");
            return (long)result!.Value.GetProperty("metrics").EnumerateArray()
                .Single(m => m.GetProperty("name").GetString() == "JSHeapUsedSize").GetProperty("value").GetDouble();
        }
        finally { await session.DetachAsync(); }
    }

    private static async Task<object[]> MeasureSqlAsync(IServiceProvider services, Dataset dataset)
    {
        await using var setup = services.CreateAsyncScope();
        var connection = setup.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.GetConnectionString();
        var results = new List<object>();
        foreach (var (size, id) in new[] { (1000, dataset.ThousandObservatoryId), (10000, dataset.TenThousandObservatoryId) })
        {
            for (var sample = 0; sample < 6; sample++)
            {
                var probe = new ScopeSqlProbe();
                await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseSqlServer(connection).AddInterceptors(probe).Options);
                var service = new NetworkOperationsReadService(db);
                var started = Stopwatch.GetTimestamp();
                var page = await service.ListObservatoryCamerasAsync(dataset.UserId, id, 25, null);
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                page.Items.Should().HaveCount(25);
                page.NextCursor.Should().NotBeNull();
                probe.Commands.Should().HaveCount(1);
                probe.Commands.Should().OnlyContain(c => c.ReadCalls <= 27);
                if (sample > 0) results.Add(new { Cameras = size, Sample = sample, Milliseconds = elapsed, probe.Commands });
            }
        }
        return results.ToArray();
    }

    private static string ScopePath(Guid id) => $"/app/network/observatories/{id:D}";

    private static async Task<Dataset> SeedAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userId = await db.Users.Where(u => u.Email == TestUsers.Operator.Email).Select(u => u.Id).SingleAsync();
        var viewerId = await db.Users.Where(u => u.Email == TestUsers.Viewer.Email).Select(u => u.Id).SingleAsync();
        var managerId = await db.Users.Where(u => u.Email == TestUsers.Regular.Email).Select(u => u.Id).SingleAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var foreignCamera = Guid.NewGuid();
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        for (var station = 0; station < 64; station++)
        {
            var id = station == 0 ? first : station == 1 ? second : station == 2 ? foreign : Guid.NewGuid();
            db.Observatories.Add(new Observatory { Id = id, Name = station == 0 ? "Scope 1k" : station == 1 ? "Scope 10k" : station == 2 ? "Scope foreign station" : $"Scope station {station:D3}", OwnerUserId = userId });
            db.ObservatoryMemberships.Add(new ObservatoryMembership { ObservatoryId = id, UserId = userId, Role = ObservatoryMembershipRole.Owner });
            if (station == 0)
            {
                db.ObservatoryMemberships.AddRange(
                    new ObservatoryMembership { ObservatoryId = id, UserId = viewerId, Role = ObservatoryMembershipRole.Viewer },
                    new ObservatoryMembership { ObservatoryId = id, UserId = managerId, Role = ObservatoryMembershipRole.Manager });
            }
            var cameras = station == 0 ? 1000 : station == 1 ? 10000 : station == 2 ? 1 : 0;
            for (var i = 0; i < cameras; i++)
                db.LogicalCameras.Add(new LogicalCamera
                {
                    Id = station == 2 ? foreignCamera : Guid.NewGuid(),
                    ObservatoryId = id,
                    Name = station == 2 ? "Scope foreign camera" : $"Camera {i:D5}",
                    Slug = $"camera-{i:D5}",
                    CreatedByUserId = userId
                });
        }
        await db.SaveChangesAsync();
        return new(userId, first, second, foreignCamera);
    }

    private sealed record Dataset(string UserId, Guid ThousandObservatoryId, Guid TenThousandObservatoryId, Guid ForeignCameraId);

    private sealed class ScopeSqlProbe : DbCommandInterceptor
    {
        private ReadGate? nextRead;
        internal ConcurrentQueue<SqlRead> Commands { get; } = [];
        internal ReadGate HoldNextRead()
        {
            var gate = new ReadGate();
            Interlocked.Exchange(ref nextRead, gate).Should().BeNull();
            return gate;
        }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (IsScopeRead(command.CommandText) && Interlocked.Exchange(ref nextRead, null) is { } gate)
            {
                gate.Started.TrySetResult();
                await gate.Released.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
        public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
        {
            if (IsScopeRead(command.CommandText))
                Commands.Enqueue(new(eventData.ReadCount, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.CommandText)))));
            return result;
        }
        private static bool IsScopeRead(string sql) => sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            && (sql.Contains("[ObservatoryMemberships]", StringComparison.Ordinal)
                || sql.Contains("[LogicalCameras]", StringComparison.Ordinal)
                || sql.Contains("[LogicalCameraInstallations]", StringComparison.Ordinal));
    }
    private sealed class ReadGate
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed record SqlRead(int ReadCalls, string QuerySha256);
}
