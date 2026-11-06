using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.LogicHost.Components.Shared;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class CentralOperationsWorkspaceBrowserAcceptanceTests
{
    public TestContext TestContext { get; set; } = null!;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string EvidenceRoot => Environment.GetEnvironmentVariable("HVO_ISSUE1241_EVIDENCE") ?? "TestResults/issue-1241";

    [TestMethod]
    public async Task CentralWorkspace_ExactEventChildHistoryQueryResponseDisclosureRenderAndHeapStayBounded()
    {
        var source = await SourceAsync();
        var directory = Path.Combine(EvidenceRoot, source.OutputDirectoryName, source.RunId);
        Directory.CreateDirectory(directory);
        using var probe = new ImportedInspectorProbe();
        await using var fixture = await LogicHostUiKestrelFixture.CreateAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var results = new List<object>();
        foreach (var history in new[] { 100, 1_000 })
        {
            var data = await CameraOperationsSeed.CreateAsync(2, 1, 1, 1);
            await using var scope = fixture.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var registration = await db.LogicalCameraInstallations.Where(i => i.Id == data.CurrentInstallation).Select(i => i.RegistrationId).SingleAsync();
            var seed = CentralTransientPersistenceFixture.Create();
            var seeded = await CentralTransientEventPersistenceIntegrationTests.SeedAsync(db, seed,
                registrationId: registration, logicalCameraInstallationId: data.CurrentInstallation);
            await new CentralTransientEventPersistence(db).AppendAsync(seed.Request with { CentralDerivativeJobId = seeded.JobId }, CancellationToken.None);
            db.ChangeTracker.Clear();
            var id = await db.CentralTransientEvents.Where(e => e.EventId == seed.Event.EventId).Select(e => e.Id).SingleAsync();
            CentralTransientEventAppendResult appended;
            // The actual append owner validates canonical history and persists its immutable relational children.
            // Batching append-only child evidence avoids measuring fixture preparation as UI cost.
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                appended = await new CentralTransientEventVersionAppender(db).AppendGeneratedAsync(id, previous =>
                {
                    var reviews = previous.Reviews.ToList();
                    var notifications = previous.Notifications.ToList();
                    var assessment = previous.Assessments[^1].AssessmentId;
                    for (var index = 0; index < history; index++)
                    {
                        var now = previous.VersionCreatedUtc.AddSeconds(index + 1);
                        reviews.Add(new(Guid.NewGuid(), now, "user:" + data.OwnerId, TransientReviewDisposition.Confirmed,
                            assessment, null, ["review.confirmed"], index == 0 ? null : reviews[^1].ReviewId));
                        notifications.Add(new(Guid.NewGuid(), now, "email", TransientNotificationState.Sent,
                            assessment, null, index == 0 ? null : notifications[^1].NotificationId));
                    }
                    return previous with
                    {
                        EventVersionId = Guid.NewGuid(),
                        Version = previous.Version + 1,
                        PreviousEventVersionId = previous.EventVersionId,
                        PreviousVersionCreatedUtc = previous.VersionCreatedUtc,
                        VersionCreatedUtc = previous.VersionCreatedUtc.AddSeconds(history),
                        Reviews = reviews,
                        Notifications = notifications
                    };
                }, false, CancellationToken.None);
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            db.ChangeTracker.Clear();
            var canonicalHash = appended.Version.CanonicalEventSha256;
            var query = new CentralOperationsQuery { View = "events", Event = id };
            var service = scope.ServiceProvider.GetRequiredService<ICentralOperationsWorkspaceService>();
            var watch = Stopwatch.StartNew();
            var snapshot = await probe.RunAsync(() => service.ReadAsync(CameraOperationsSeed.Principal(data.OwnerId), data.Observatory, data.Camera, query));
            watch.Stop();
            snapshot!.Event!.Evidence.GetProperty("Reviews").GetProperty("Items").GetArrayLength().Should().Be(25);
            snapshot.Event.Evidence.GetProperty("Notifications").GetProperty("Items").GetArrayLength().Should().Be(25);
            var plans = new List<CameraOperationsSql.Plan>();
            foreach (var command in probe.Commands)
                plans.Add(await CameraOperationsSql.ExplainAsync(AssemblyHooks.Fixture.SqlServerConnectionString, command));
            probe.Commands.Count.Should().BeLessThanOrEqualTo(40);
            plans.Sum(p => p.ActualRowsRead).Should().BeLessThan(5_000);
            await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 900 } });
            var page = await context.NewPageAsync();
            var url = new Uri(fixture.BaseAddress, WorkspacePath(data) + $"?view=events&event={id:D}").AbsoluteUri;
            await LoginAsync(page, url);
            var cdp = await context.NewCDPSessionAsync(page);
            var samples = new List<object>();
            for (var run = 0; run < 6; run++)
            {
                var start = Stopwatch.GetTimestamp();
                await page.GotoAsync(url);
                await ReadyAsync(page);
                var renderMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                await page.Locator(".central-event-evidence summary").ClickAsync();
                var disclosure = await page.Locator(".central-event-evidence pre").TextContentAsync();
                using var document = JsonDocument.Parse(disclosure!);
                foreach (var name in new[] { "Reviews", "Notifications" })
                {
                    document.RootElement.GetProperty(name).GetProperty("Items").GetArrayLength().Should().Be(25);
                    document.RootElement.GetProperty(name).GetProperty("Truncated").GetBoolean().Should().BeTrue();
                }
                disclosure.Should().Contain(appended.Event.Reviews[^1].ReviewId.ToString()).And.Contain(appended.Event.Notifications[^1].NotificationId.ToString())
                    .And.NotContain(appended.Event.Reviews[0].ReviewId.ToString()).And.NotContain(appended.Event.Notifications[0].NotificationId.ToString());
                document.RootElement.GetProperty("CurrentAssessment").GetProperty("AssessmentId").GetGuid().Should().Be(snapshot.Event.Summary.ActiveAssessmentId);
                (await page.Locator(".central-workspace code").AllTextContentsAsync()).Should().Contain(snapshot.Event.Summary.ETag);
                await cdp.SendAsync("HeapProfiler.collectGarbage");
                if (run > 0) samples.Add(new
                {
                    Run = run,
                    RenderMs = renderMs,
                    DisclosureCharacters = disclosure!.Length,
                    Heap = await cdp.SendAsync("Runtime.getHeapUsage"),
                    Dom = await cdp.SendAsync("Memory.getDOMCounters"),
                    Document = await page.EvaluateAsync<JsonElement>("(() => { const n=performance.getEntriesByType('navigation')[0]; return {encoded:n.encodedBodySize,decoded:n.decodedBodySize,transfer:n.transferSize}; })()")
                });
            }
            foreach (var (width, height) in new[] { (1440, 900), (390, 844), (844, 390), (320, 700) })
            {
                await page.SetViewportSizeAsync(width, height);
                (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue();
            }
            await page.ScreenshotAsync(new() { Path = Path.Combine(directory, $"exact-event-{history}-320x700.png"), FullPage = true });
            (await db.CentralTransientEventVersions.AsNoTracking().SingleAsync(v => v.EventVersionId == appended.Event.EventVersionId)).CanonicalEventSha256.Should().Be(canonicalHash);
            results.Add(new
            {
                Reviews = history,
                Notifications = history,
                ChildBound = 25,
                Queries = probe.Commands.Count,
                RowsRead = plans.Sum(p => p.ActualRowsRead),
                RowsReturned = plans.Sum(p => p.ResultRows),
                LogicalReads = plans.Sum(p => p.LogicalReads),
                CanonicalBytes = appended.Version.CanonicalEventByteLength,
                CanonicalSha256 = canonicalHash,
                ResponseBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot).Length,
                ServiceMs = watch.Elapsed.TotalMilliseconds,
                Assessment = snapshot.Event.Summary.ActiveAssessmentId,
                snapshot.Event.Summary.ETag,
                Plans = plans,
                Samples = samples
            });
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "exact-event-child-costs.json"), JsonSerializer.Serialize(new
        {
            Source = source,
            Utc = DateTimeOffset.UtcNow,
            Browser = browser.Version,
            Method = "Real immutable event append owner; 100/1000 valid superseding reviews and notifications. One warmup, five measured exact-event renders and expanded disclosures. All child projections bounded to 25; current assessment/strong ETag and canonical checksum verified. SQL reads include the exact canonical document; UI response and DOM retain bounded evidence. No full-history drain or total count.",
            Results = results
        }, JsonOptions));
    }

    [TestMethod]
    public async Task CentralWorkspace_ResponsiveKeyboardTouchFailureReconnectAndAccessLoss()
    {
        var source = await SourceAsync();
        var directory = Path.Combine(EvidenceRoot, source.OutputDirectoryName, source.RunId);
        Directory.CreateDirectory(directory);
        var data = await CameraOperationsSeed.CreateAsync(70, 1, 70, 70, lifecycleFacts: 70, eventEvery: 1);
        await using var fixture = await LogicHostUiKestrelFixture.CreateAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await using var context = await diagnostics.NewContextAsync(new() { HasTouch = true, ViewportSize = new() { Width = 1440, Height = 900 } });
        var page = await context.NewPageAsync();
        var url = new Uri(fixture.BaseAddress, WorkspacePath(data)).AbsoluteUri;
        await LoginAsync(page, url);
        await Assertions.Expect(page.Locator(".central-executions li")).ToHaveCountAsync(25);
        await page.GetByRole(AriaRole.Link, new() { Name = "Next executions page", Exact = true }).ClickAsync();
        await ReadyAsync(page);
        page.Url.Should().Contain("page=");
        await Assertions.Expect(page.Locator(".central-executions li")).ToHaveCountAsync(25);
        await page.GetByRole(AriaRole.Link, new() { Name = "First executions page", Exact = true }).ClickAsync();
        await ReadyAsync(page);
        var target = page.Locator($"#central-execution-{data.InspectedExecution:D}");
        var screens = new List<object>();
        foreach (var (width, height) in new[] { (1440, 900), (390, 844), (844, 390), (320, 700) })
        {
            await page.SetViewportSizeAsync(width, height);
            await target.FocusAsync();
            if (width == 390) await target.TapAsync(); else await page.Keyboard.PressAsync("Enter");
            await ReadyAsync(page);
            await Assertions.Expect(page.Locator("#central-execution-heading")).ToBeFocusedAsync();
            await Assertions.Expect(page.Locator(".central-exact")).ToContainTextAsync(data.InspectedExecution.ToString("D"));
            await Assertions.Expect(page.Locator(".central-nodes li")).ToHaveCountAsync(3);
            var tree = await page.Locator(".central-workspace").AriaSnapshotAsync();
            tree.Should().Contain("Central operations").And.Contain("Frozen exact sources").And.Contain("Central nodes");
            (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue();
            (await page.Locator("html").GetAttributeAsync("data-theme")).Should().Be("hvo-dark");
            await page.ScreenshotAsync(new() { Path = System.IO.Path.Combine(directory, $"central-{width}x{height}.png"), FullPage = true });
            await page.GetByRole(AriaRole.Link, new() { Name = "Back to execution page", Exact = true }).ClickAsync();
            await ReadyAsync(page);
            await Assertions.Expect(target).ToBeFocusedAsync();
            screens.Add(new { width, height, KeyboardAndTouch = true, FocusRestored = true, NoHorizontalOverflow = true, ScreenReaderTree = tree });
        }
        await page.GetByRole(AriaRole.Link, new() { Name = "Delivery facts", Exact = true }).ClickAsync();
        await ReadyAsync(page);
        await Assertions.Expect(page.Locator(".central-proposals li")).ToHaveCountAsync(25);
        await page.Locator($"#central-proposal-{data.FactProposal:D}").ClickAsync();
        await ReadyAsync(page);
        await Assertions.Expect(page.Locator(".central-facts li")).ToHaveCountAsync(25);
        await page.GetByRole(AriaRole.Link, new() { Name = "Next facts page", Exact = true }).ClickAsync();
        await ReadyAsync(page);
        await Assertions.Expect(page.Locator("#central-facts-heading")).ToBeFocusedAsync();
        await Assertions.Expect(page.Locator(".central-facts li")).ToHaveCountAsync(25);
        await page.EvaluateAsync("window.dispatchEvent(new Event('offline'))");
        await Assertions.Expect(page.Locator(".central-protected")).ToHaveCountAsync(0);
        await page.EvaluateAsync("window.dispatchEvent(new Event('online'))");
        await ReadyAsync(page);
        await Assertions.Expect(page.Locator(".central-facts li")).ToHaveCountAsync(25);
        // A failing fresh HTTP credential/scope check must clear the retained circuit projection.
        var checkRoute = $"**/api/v1.0/observatories/{data.Observatory:D}/cameras/{data.Camera:D}/processing-graph-executions?take=1";
        await page.RouteAsync(checkRoute, route => route.AbortAsync());
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh central operations", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".central-protected")).ToHaveCountAsync(0);
        await page.GetByText("Session unavailable or access was removed.", new() { Exact = false }).WaitForAsync();
        await page.UnrouteAsync(checkRoute);
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh central operations", Exact = true }).ClickAsync();
        await ReadyAsync(page);
        await Assertions.Expect(page.Locator(".central-facts li")).ToHaveCountAsync(25);
        await context.ClearCookiesAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh central operations", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".central-protected")).ToHaveCountAsync(0);
        await LoginAsync(page, url);
        await using (var scope = fixture.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ObservatoryMemberships
                .Where(m => m.ObservatoryId == data.Observatory && m.UserId == data.OwnerId).ExecuteDeleteAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh central operations", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".central-protected")).ToHaveCountAsync(0);
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "browser-acceptance.json"), JsonSerializer.Serialize(new
        {
            Source = source,
            Utc = DateTimeOffset.UtcNow,
            Browser = browser.Version,
            HistoryExecutions = 70,
            ProposalLifecycleFacts = 70,
            Viewports = screens,
            Reconnect = "Suspends protected DOM then rechecks same exact proposal and fact page",
            FailedRead = "HTTP scope-check failure clears evidence; explicit refresh recovers",
            CookieAndMembershipLoss = "Clear protected facts despite retained circuit principal"
        }, JsonOptions));
        await diagnostics.CompleteAsync();
    }

    [TestMethod]
    public async Task CentralWorkspace_W3MQueryRowsResponseRenderAndHeapStayBounded()
    {
        var source = await SourceAsync();
        var directory = System.IO.Path.Combine(EvidenceRoot, source.OutputDirectoryName, source.RunId);
        Directory.CreateDirectory(directory);
        using var probe = new ImportedInspectorProbe();
        await using var fixture = await LogicHostUiKestrelFixture.CreateAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var results = new List<object>();
        foreach (var history in new[] { 1_000, 10_000 })
        {
            var data = await CameraOperationsSeed.CreateAsync(history, 10, history, history, lifecycleFacts: history, eventEvery: 10);
            var serviceSamples = new List<object>();
            await using (var scope = fixture.Services.CreateAsyncScope())
            {
                var service = scope.ServiceProvider.GetRequiredService<ICentralOperationsWorkspaceService>();
                foreach (var query in new CentralOperationsQuery[] { new(), new() { Execution = data.InspectedExecution },
                    new() { View = "jobs", Job = data.InspectedJob }, new() { View = "delivery", Proposal = data.FactProposal }, new() { View = "events" } })
                {
                    var watch = Stopwatch.StartNew();
                    var snapshot = await probe.RunAsync(() => service.ReadAsync(CameraOperationsSeed.Principal(data.OwnerId), data.Observatory, data.Camera, query));
                    watch.Stop();
                    snapshot.Should().NotBeNull();
                    var plans = new List<CameraOperationsSql.Plan>();
                    foreach (var command in probe.Commands)
                    {
                        command.Text.TrimStart().Should().StartWith("SELECT");
                        plans.Add(await CameraOperationsSql.ExplainAsync(AssemblyHooks.Fixture.SqlServerConnectionString, command));
                    }
                    probe.Commands.Count.Should().BeLessThanOrEqualTo(40);
                    plans.Sum(p => p.ActualRowsRead).Should().BeLessThan(5_000);
                    serviceSamples.Add(new
                    {
                        Selection = query,
                        Queries = probe.Commands.Count,
                        RowsReturned = plans.Sum(p => p.ResultRows),
                        RowsRead = plans.Sum(p => p.ActualRowsRead),
                        LogicalReads = plans.Sum(p => p.LogicalReads),
                        ResponseBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot).Length,
                        ServiceMilliseconds = watch.Elapsed.TotalMilliseconds,
                        Plans = plans
                    });
                }
            }
            await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 900 } });
            var page = await context.NewPageAsync();
            var url = new Uri(fixture.BaseAddress, WorkspacePath(data)).AbsoluteUri;
            await LoginAsync(page, url);
            var cdp = await context.NewCDPSessionAsync(page);
            var samples = new List<object>();
            for (var run = 0; run < 6; run++)
            {
                var start = Stopwatch.GetTimestamp();
                await page.GotoAsync(url);
                await ReadyAsync(page);
                var listMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                await cdp.SendAsync("HeapProfiler.collectGarbage");
                var listHeap = await cdp.SendAsync("Runtime.getHeapUsage");
                var document = await page.EvaluateAsync<JsonElement>("(() => { const n=performance.getEntriesByType('navigation')[0]; return {encoded:n.encodedBodySize,decoded:n.decodedBodySize,transfer:n.transferSize}; })()");
                start = Stopwatch.GetTimestamp();
                await page.Locator($"#central-execution-{data.InspectedExecution:D}").ClickAsync();
                await ReadyAsync(page);
                var detailMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                await cdp.SendAsync("HeapProfiler.collectGarbage");
                if (run > 0) samples.Add(new
                {
                    Run = run,
                    ListRenderMs = listMs,
                    DetailRenderMs = detailMs,
                    ListHeap = listHeap,
                    DetailHeap = await cdp.SendAsync("Runtime.getHeapUsage"),
                    Dom = await cdp.SendAsync("Memory.getDOMCounters"),
                    Document = document
                });
            }
            await page.GetByRole(AriaRole.Link, new() { Name = "Back to execution page", Exact = true }).ClickAsync();
            await ReadyAsync(page);
            var paging = new List<object>();
            for (var index = 0; index < 10; index++)
            {
                await page.GetByRole(AriaRole.Link, new() { Name = "Next executions page", Exact = true }).ClickAsync();
                await ReadyAsync(page);
                await Assertions.Expect(page.Locator(".central-executions li")).ToHaveCountAsync(25);
                await cdp.SendAsync("HeapProfiler.collectGarbage");
                paging.Add(new { Page = index + 2, Heap = await cdp.SendAsync("Runtime.getHeapUsage"), Dom = await cdp.SendAsync("Memory.getDOMCounters") });
            }
            await Assertions.Expect(page.Locator(".central-workspace img")).ToHaveCountAsync(0);
            results.Add(new
            {
                History = history,
                PageSize = 25,
                Warmup = 1,
                MeasuredRuns = 5,
                Concurrency = 1,
                ServiceSamples = serviceSamples,
                BrowserSamples = samples,
                ConsecutivePages = paging
            });
        }
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "w3m-costs.json"), JsonSerializer.Serialize(new
        {
            Source = source,
            Utc = DateTimeOffset.UtcNow,
            Browser = browser.Version,
            Method = "Absolute candidate baseline for the new composed UI. Store-valid metadata seeding reuses #1237: retained/replaced installations, equal timestamps, executions, jobs, proposals, lifecycle facts, events. Concurrency 1, one browser warmup + five measured runs; ten consecutive pages. SQL command plans report actual rows read. Service JSON bytes differ from HTTP document bytes. No artifact image bytes fetched.",
            Results = results
        }, JsonOptions));
    }

    private static string WorkspacePath(CameraOperationsSeed data) => $"/app/network/observatories/{data.Observatory:D}/cameras/{data.Camera:D}/central";
    private static Task ReadyAsync(IPage page) => page.Locator(".scope-shell[data-scope-interactive='true'][aria-busy='false'] .central-workspace[data-scope-interactive='true'][aria-busy='false']").WaitForAsync();
    private static async Task LoginAsync(IPage page, string url)
    {
        await page.GotoAsync(url);
        await page.GetByLabel("Email").FillAsync(TestUsers.Operator.Email);
        await page.GetByLabel("Password").FillAsync(TestUsers.Operator.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
        await ReadyAsync(page);
    }
    private static async Task<EvidenceSourceSnapshot> SourceAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(System.IO.Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Evidence source checkout was not found.");
        return await EvidenceSourceIdentity.CaptureAsync(root.FullName, typeof(CentralOperationsWorkspace), typeof(CentralOperationsWorkspaceService), typeof(CentralOperationsWorkspaceBrowserAcceptanceTests));
    }
}
