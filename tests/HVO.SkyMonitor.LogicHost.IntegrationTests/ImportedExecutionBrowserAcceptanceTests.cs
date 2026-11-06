using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.LogicHost.Components.Shared;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class ImportedExecutionBrowserAcceptanceTests
{
    public TestContext TestContext { get; set; } = null!;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] MetadataStates = ["Missing", "Unresolved", "Quarantined"];
    private static string EvidenceRoot => Environment.GetEnvironmentVariable("HVO_ISSUE1240_EVIDENCE") ?? "TestResults/issue-1240";

    [TestMethod]
    public async Task ImportedInspector_ResponsiveKeyboardTouchFocusMetadataOnlyAndRevocation()
    {
        var source = await SourceAsync();
        var directory = Path.Combine(EvidenceRoot, source.OutputDirectoryName, source.RunId);
        Directory.CreateDirectory(directory);
        var data = await ExecutionEvidenceIntegrationTests.SeedInspectorAsync(70);
        await using var fixture = await LogicHostUiKestrelFixture.CreateAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await using var context = await diagnostics.NewContextAsync(new() { HasTouch = true, ViewportSize = new() { Width = 1440, Height = 900 } });
        var page = await context.NewPageAsync();
        await LoginAsync(page, new Uri(fixture.BaseAddress, data.Url).AbsoluteUri);
        await Assertions.Expect(page.Locator(".imported-units li")).ToHaveCountAsync(25);
        await page.GetByRole(AriaRole.Link, new() { Name = "Next evidence page", Exact = true }).ClickAsync();
        await ReadyAsync(page);
        page.Url.Should().Contain("unitsPage=");
        await Assertions.Expect(page.Locator($"#imported-unit-{data.ExecutionUnit}")).ToHaveCountAsync(0);
        await page.GetByRole(AriaRole.Link, new() { Name = "First evidence page", Exact = true }).ClickAsync();
        await ReadyAsync(page);
        var target = page.Locator($"#imported-unit-{data.ExecutionUnit}");
        var screens = new List<object>();
        foreach (var (width, height) in new[] { (1440, 900), (390, 844), (844, 390), (320, 700) })
        {
            await page.SetViewportSizeAsync(width, height);
            await target.FocusAsync();
            if (width == 390) await target.TapAsync(); else await page.Keyboard.PressAsync("Enter");
            await ReadyAsync(page);
            await Assertions.Expect(page.Locator(".imported-exact")).ToContainTextAsync(data.ExecutionHash);
            await Assertions.Expect(page.Locator("#imported-unit-heading")).ToBeFocusedAsync();
            var node = page.GetByRole(AriaRole.Link, new() { Name = "Node preview", Exact = true });
            await node.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await ReadyAsync(page);
            await Assertions.Expect(page.Locator(".imported-inputs li")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".imported-outputs li")).ToHaveCountAsync(2);
            var tree = await page.Locator(".imported-inspector").AriaSnapshotAsync();
            tree.Should().Contain("heading \"Imported executions\" [level=2]").And.Contain("Exact immutable unit")
                .And.Contain("Attempts").And.Contain("Exact selected inputs").And.Contain("Compare two explicit executions");
            (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue();
            (await page.Locator("html").GetAttributeAsync("data-theme")).Should().Be("hvo-dark");
            await page.ScreenshotAsync(new() { Path = Path.Combine(directory, $"imported-{width}x{height}.png"), FullPage = true });
            await page.GetByRole(AriaRole.Link, new() { Name = "Back to evidence page", Exact = true }).ClickAsync();
            await ReadyAsync(page);
            await Assertions.Expect(target).ToBeFocusedAsync();
            await Assertions.Expect(page.Locator(".imported-exact")).ToHaveCountAsync(0);
            screens.Add(new { width, height, KeyboardFocusRestored = true, NoHorizontalOverflow = true, ScreenReaderTree = tree });
        }
        await target.ClickAsync();
        await ReadyAsync(page);
        await page.Locator(".imported-artifacts a").First.ClickAsync();
        await ReadyAsync(page);
        long referenceId;
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            referenceId = await db.Set<ImportedExecutionEvidenceArtifact>().Where(a => a.UnitId == data.ExecutionUnit).OrderBy(a => a.Ordinal).Select(a => a.Id).FirstAsync();
            // Keep the synthetic observation scenario stable while each explicit refresh is asserted.
            await db.Set<ExecutionEvidenceArtifactCheck>().Where(a => a.ArtifactReferenceId == referenceId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.NextCheckAtUtc, DateTimeOffset.UtcNow.AddHours(1)));
        }
        foreach (var state in MetadataStates)
        {
            await using (var scope = fixture.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.Add(new ImportedExecutionEvidenceObservation
                {
                    BindingId = data.Binding,
                    ArtifactReferenceId = referenceId,
                    State = state,
                    ReasonCode = "browser-test-" + state,
                    ObservedAtUtc = DateTimeOffset.UtcNow
                });
                await db.SaveChangesAsync();
            }
            await page.GetByRole(AriaRole.Button, new() { Name = "Refresh imported evidence", Exact = true }).ClickAsync();
            await ReadyAsync(page);
            await Assertions.Expect(page.Locator(".imported-observations li").First).ToContainTextAsync(state);
            await Assertions.Expect(page.Locator(".imported-inspector img")).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Open exact capture viewer", Exact = true })).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".imported-exact")).ToContainTextAsync(data.ExecutionHash);
        }
        var imageRequests = await page.EvaluateAsync<JsonElement>("performance.getEntriesByType('resource').filter(r => r.name.includes('/artifacts/') || r.name.includes('/presentation/')).map(r => r.name)");
        imageRequests.GetArrayLength().Should().Be(0);
        // Clear disconnected facts before a server reply, then recover the same immutable selection.
        await page.EvaluateAsync("window.dispatchEvent(new Event('offline'))");
        await Assertions.Expect(page.Locator(".imported-exact")).ToHaveCountAsync(0);
        await page.GetByText("Session unavailable or access was removed.", new() { Exact = false }).WaitForAsync();
        // Online/reconnect notification must trigger a fresh read of the same identities.
        await page.EvaluateAsync("window.dispatchEvent(new Event('online'))");
        await ReadyAsync(page);
        await Assertions.Expect(page.Locator(".imported-exact")).ToContainTextAsync(data.ExecutionHash);
        // The circuit's original principal must not restore data after its browser credential disappears.
        await context.ClearCookiesAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh imported evidence", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".imported-exact")).ToHaveCountAsync(0);
        await page.GetByText("Session unavailable or access was removed.", new() { Exact = false }).WaitForAsync();
        await LoginAsync(page, new Uri(fixture.BaseAddress, data.Url + $"&unit={data.ExecutionUnit}").AbsoluteUri);
        await Assertions.Expect(page.Locator(".imported-exact")).ToContainTextAsync(data.ExecutionHash);
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ObservatoryMemberships
                .Where(m => m.ObservatoryId == data.Observatory && m.UserId == data.User).ExecuteDeleteAsync();
        }
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh imported evidence", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".imported-exact,.imported-artifacts,.imported-observations")).ToHaveCountAsync(0);
        await page.GetByText("Session unavailable or access was removed.", new() { Exact = false }).WaitForAsync();
        await File.WriteAllTextAsync(Path.Combine(directory, "browser-acceptance.json"), JsonSerializer.Serialize(new
        {
            Source = source,
            Utc = DateTimeOffset.UtcNow,
            Browser = browser.Version,
            HistoryUnits = 70,
            SelectedUnit = data.ExecutionUnit,
            data.ExecutionHash,
            Viewports = screens,
            ImageRequests = imageRequests,
            ObservationScenarios = MetadataStates,
            MembershipRemoval = "Refresh clears exact evidence and children",
            BrowserSessionLoss = "Cookie removal clears metadata despite the circuit principal",
            Reconnect = "online event rechecks exact selection"
        }, JsonOptions));
        await diagnostics.CompleteAsync();
    }

    [TestMethod]
    public async Task ImportedInspector_W3MQueryRowsResponseRenderAndHeapAreBounded()
    {
        var source = await SourceAsync();
        var directory = Path.Combine(EvidenceRoot, source.OutputDirectoryName, source.RunId);
        Directory.CreateDirectory(directory);
        // Subscribe before the host and its contexts warm up so EF enables command diagnostics.
        using var probe = new ImportedInspectorProbe();
        await using var fixture = await LogicHostUiKestrelFixture.CreateAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var results = new List<object>();
        foreach (var history in new[] { 1_000, 10_000 })
        {
            var data = await ExecutionEvidenceIntegrationTests.SeedInspectorAsync(history);
            var sqlSamples = new List<object>();
            await using (var scope = fixture.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var service = services.GetRequiredService<IImportedExecutionInspectorReadService>();
                var principal = LogicalCameraArchiveIntegrationTests.Principal(data.User);
                var selected = await service.ReadAsync(principal, data.Observatory, data.Camera, data.Selection with { Unit = data.ExecutionUnit });
                selected.Should().NotBeNull();
                var artifact = selected!.Artifacts!.Artifacts[0].ArtifactReferenceId;
                foreach (var (name, query) in new[] { ("units", data.Selection),
                    ("exact-execution-node", data.Selection with { Unit = data.ExecutionUnit, Node = "preview" }),
                    ("exact-observations", data.Selection with { Unit = data.ExecutionUnit, Artifact = artifact }) })
                {
                    var watch = Stopwatch.StartNew();
                    var snapshot = await probe.RunAsync(() => service.ReadAsync(principal, data.Observatory, data.Camera, query));
                    watch.Stop();
                    snapshot.Should().NotBeNull();
                    snapshot!.Units!.Items.Should().HaveCount(25);
                    var plans = new List<CameraOperationsSql.Plan>();
                    foreach (var command in probe.Commands)
                    {
                        command.Text.TrimStart().Should().StartWith("SELECT");
                        plans.Add(await CameraOperationsSql.ExplainAsync(AssemblyHooks.Fixture.SqlServerConnectionString, command));
                    }
                    // Fixed query/row ceilings detect a history scan; the exact numerical samples are retained too.
                    probe.Commands.Should().NotBeEmpty();
                    probe.Commands.Count.Should().BeLessThanOrEqualTo(32);
                    plans.Sum(p => p.ActualRowsRead).Should().BeLessThan(5_000);
                    sqlSamples.Add(new
                    {
                        Operation = name,
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
            await LoginAsync(page, new Uri(fixture.BaseAddress, data.Url).AbsoluteUri);
            var cdp = await context.NewCDPSessionAsync(page);
            await cdp.SendAsync("Performance.enable");
            var samples = new List<object>();
            for (var run = 0; run < 6; run++)
            {
                var start = Stopwatch.GetTimestamp();
                await page.GotoAsync(new Uri(fixture.BaseAddress, data.Url).AbsoluteUri);
                await ReadyAsync(page);
                var listMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                await cdp.SendAsync("HeapProfiler.collectGarbage");
                var listHeap = await cdp.SendAsync("Runtime.getHeapUsage");
                var navigation = await page.EvaluateAsync<JsonElement>("(() => {const n=performance.getEntriesByType('navigation')[0];return {encoded:n.encodedBodySize,decoded:n.decodedBodySize,transfer:n.transferSize};})()");
                start = Stopwatch.GetTimestamp();
                await page.Locator($"#imported-unit-{data.ExecutionUnit}").ClickAsync();
                await ReadyAsync(page);
                await page.GetByRole(AriaRole.Link, new() { Name = "Node preview", Exact = true }).ClickAsync();
                await ReadyAsync(page);
                var detailMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                await cdp.SendAsync("HeapProfiler.collectGarbage");
                var detailHeap = await cdp.SendAsync("Runtime.getHeapUsage");
                var dom = await cdp.SendAsync("Memory.getDOMCounters");
                await Assertions.Expect(page.Locator(".imported-units li")).ToHaveCountAsync(25);
                await Assertions.Expect(page.Locator(".imported-inspector img")).ToHaveCountAsync(0);
                if (run > 0) samples.Add(new
                {
                    Run = run,
                    ListRenderMs = listMs,
                    DetailRenderMs = detailMs,
                    ListHeap = listHeap,
                    DetailHeap = detailHeap,
                    Dom = dom,
                    Document = navigation
                });
            }
            await page.GetByRole(AriaRole.Link, new() { Name = "Back to evidence page", Exact = true }).ClickAsync();
            await ReadyAsync(page);
            var paging = new List<object>();
            for (var index = 0; index < 10; index++)
            {
                var first = await page.Locator(".imported-units a").First.GetAttributeAsync("id");
                await page.GetByRole(AriaRole.Link, new() { Name = "Next evidence page", Exact = true }).ClickAsync();
                await ReadyAsync(page);
                await Assertions.Expect(page.Locator(".imported-units a").First).Not.ToHaveAttributeAsync("id", first!);
                await Assertions.Expect(page.Locator(".imported-units li")).ToHaveCountAsync(25);
                await cdp.SendAsync("HeapProfiler.collectGarbage");
                paging.Add(new { Page = index + 2, Heap = await cdp.SendAsync("Runtime.getHeapUsage"), Dom = await cdp.SendAsync("Memory.getDOMCounters") });
            }
            results.Add(new
            {
                HistoryUnits = history,
                PageSize = 25,
                Warmup = 1,
                MeasuredRuns = 5,
                ServiceSamples = sqlSamples,
                BrowserSamples = samples,
                ConsecutivePages = paging
            });
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "w3m-costs.json"), JsonSerializer.Serialize(new
        {
            Source = source,
            Utc = DateTimeOffset.UtcNow,
            Browser = browser.Version,
            Method = "Valid sealed metadata histories: selected #538 units use receiver HTTP; bulk availability history is directly seeded. Concurrency 1; 1 warmup + 5 browser runs; explicit 10-page bounded-memory sample. SQL commands from both contexts captured by Activity and replayed with actual plans. Service JSON bytes and HTTP document bytes are distinct; no image bytes requested. Absolute candidate observations, no baseline/improvement claim.",
            Results = results
        }, JsonOptions));
    }

    private static async Task<EvidenceSourceSnapshot> SourceAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Evidence source checkout was not found.");
        return await EvidenceSourceIdentity.CaptureAsync(root.FullName, typeof(ImportedExecutionInspector),
            typeof(ExecutionEvidenceEnvelopeV1), typeof(ImportedExecutionBrowserAcceptanceTests));
    }

    private static Task ReadyAsync(IPage page) => page.Locator(".scope-shell[data-scope-interactive='true'][aria-busy='false'] .imported-inspector[data-scope-interactive='true'][aria-busy='false']").WaitForAsync();
    private static async Task LoginAsync(IPage page, string url)
    {
        await page.GotoAsync(url);
        await page.GetByLabel("Email").FillAsync(TestUsers.Operator.Email);
        await page.GetByLabel("Password").FillAsync(TestUsers.Operator.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
        await ReadyAsync(page);
    }
}
