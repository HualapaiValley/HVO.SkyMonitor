using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class CameraAgentGalleryPerformanceTests
{
    private const int PageSize = 50;
    private const int MaximumResponseBytes = 512 * 1024;
    private const double MaximumP95Milliseconds = 5_000;

    // Browser-driven latency is load-sensitive in a way the read-model measurements are not: under
    // contention the two browser families move +41.6% and +46.0% while the SQLite and Kestrel
    // families stay flat (see #785). That sensitivity does not make every measurement taken on a busy
    // host worthless, and an earlier revision of this file was wrong to treat it as though it did.
    // Contention makes a deadline harder to meet, so it biases a wall-clock bound against the pass: a
    // p95 that clears the bound while the host is contended cleared it with less machine available
    // than an idle run had, and is the stronger of the two results. Refusing it would discard the
    // stronger measurement and keep the weaker one. What contention cannot support is a MISS, which a
    // slow product and a busy host produce identically.
    //
    // So this ceiling does not decide whether to measure or whether to assert. It decides whether a
    // miss is attributable. A met bound is asserted at any load; a missed bound on a quiet host is a
    // real failure; a missed bound above the ceiling is refused as unattributable, and Inconclusive is
    // what scripts/lib/trx-evidence.sh refuses as "proved nothing". Being wrong about the constant is
    // safe in both directions: too strict turns a genuine failure on a moderately busy host into a
    // loud refusal the operator must reproduce quietly, and too lenient reports a contended miss as a
    // red, which is exactly what this file did unconditionally before. Neither direction can
    // manufacture a false pass, which is why a constant is defensible here and is not for the latency
    // bound itself.
    //
    // Where the ceiling applies takes two premises, and the mechanical one alone does not reach the
    // answer. The mechanical condition is that an assertion compares a measured quantity to a FIXED
    // bound that contention pushes toward the miss, and that only the miss is refused. Fixed is
    // load-bearing: AssertScaling compares a median against a multiple of another median plus slack,
    // so its bound is computed from a measurement rather than fixed, and dropping the word would
    // pull it in and make the count eight. Direction is not the reason and would not carry it: the
    // scenarios run in sequence, so contention present for the later page and absent for the first
    // inflates only the measured side and pushes that check toward the miss. It is not limited
    // to elapsed time, and the per-session working-set bound is gated under it for the same
    // directional reason. Enumerated at this head rather than reasoned about, the condition selects
    // seven assertions. Four are gated: the browser render p95 and per-session working set, and the
    // preview-failure p95 and per-session working set. Three are not: the read-model p95 against
    // this same constant in MeasureAsync, the 256 MiB working-set growth bound in that same method,
    // and the two-second post-cancellation bound in AssertCancellationAsync. The four gated
    // assertions are reached through two gate call sites, so "four" and "two" are counts of
    // different things and this comment states which each time.
    //
    // Two earlier revisions of this comment got those counts wrong. The first said the condition
    // described the two gated call sites and nowhere else. The second corrected that to four, but
    // counted only the elapsed-time bounds while stating a quantity-general rule, which silently
    // dropped the working-set assertions the rule reaches. Both came from reasoning about the file
    // instead of enumerating it, which is the defect this branch exists to prevent, one level up: a
    // property of the sample reported as a property of the population. The count is now taken from
    // the file, and a later revision that changes the assertions must retake it rather than adjust
    // the number.
    //
    // The second premise is #785's stability table, and it is load-bearing rather than
    // corroborating. All three ungated assertions measure the SQLite family through
    // SqliteCameraAgentGallery, and #785 measured that family flat under the contention that moved
    // the two browser families more than forty percent. A bound whose measured quantity does not
    // move under load has no attribution problem for a ceiling to solve, so gating it would refuse
    // runs to guard against a confusion that family has been shown not to produce. That is also why
    // the gate sits at a call site rather than at each assertion: the two call sites are the two
    // families #785 measured moving, and every bound asserted at them is adjudicated together. It
    // is a narrower rule than a purely mechanical one and it is the honest one; a condition that
    // selects seven assertions while the gate covers four is not mechanical, and calling it
    // mechanical would be the wrong-standard error in a file written to state the standard.
    //
    // One asymmetry follows from that and is recorded rather than resolved. #785 measured latency,
    // not memory. The per-session working-set bound is gated because it is asserted at a call site
    // the stability table selected, and the 256 MiB growth bound is not because its call site is in
    // a family that measured flat. Neither placement rests on a measurement of whether working-set
    // growth moves under contention, because nobody has made one. Making it could move the 256 MiB
    // bound into the gate or the per-session bound out of it. Both are code changes and neither is
    // this branch's.
    //
    // Where a timing is recorded and asserted against nothing, the load belongs BESIDE the number
    // rather than in a refusal: host load moves the number and moves no verdict, so refusing the run
    // would discard evidence to protect a conclusion nobody drew. Most of this file's recorded
    // timings are in that position, not few. Enumerated from the measurement records, ten timing
    // fields outside MeasureHttpApiAsync are serialised into the evidence document and compared to
    // nothing: wall and CPU from MeasureAsync, and wall, median, maximum and CPU from each of the
    // two browser measurements. CPU milliseconds are counted because the condition above is not
    // limited to elapsed time and uses that generality to pull the working-set bounds in; a
    // duration in milliseconds cannot then be left out of the same count. MeasureAsync's median is
    // NOT in the set, because AssertScaling compares it against a multiple of the first-page median
    // plus slack. Two earlier revisions listed it as unasserted, which is what comes of counting
    // the methods that assert timings instead of the fields that are asserted.
    //
    // What is true of MeasureHttpApiAsync alone is narrower still, and two earlier revisions
    // overstated it. It is not the only method here that asserts no timing: MeasurePreviewCacheAsync,
    // CollectPlansAsync and AssertCompletePagingAsync all assert, and none of them asserts a timing.
    // It is the only method that RECORDS a timing and asserts none, and its p95 is the only recorded
    // p95 that nothing asserts.
    // Naming a test is never the test: a name that says "responsive" can mean layout across
    // viewports rather than any deadline at all, and keying a refusal on the name would misfire on
    // it.
    //
    // The bounds refused here are the p95 and the per-session working set, which is bytes rather than
    // milliseconds. It is included because the direction is what matters and memory pressure pushes
    // growth toward its miss the same way contention pushes latency toward its own; see the note on
    // RefuseUnattributableBrowserMiss for why that is an argument from direction and not from a
    // measurement.
    //
    // What no load ceiling addresses, and this one does not claim to: a race that only appears when
    // the machine is fast. That is the opposite shape from a deadline, it is not what a latency budget
    // is asked to show, and refusing slow-host measurements does nothing about it. Nor does it reach a
    // deadline enforced by a timeout or a cancellation token rather than by an assertion: that is the
    // same directional shape wearing a different mechanism, it never reaches this method, and nothing
    // in this branch covers it. LogicHostDependencyOutageAcceptanceTests is the worked example, checked
    // rather than taken on description: a sixty-second recovery assertion, and around it a Timeout of
    // 480 seconds on the class, two twelve-second CancellationTokenSource deadlines on the health
    // polls, and a ten-second one on a request. Only the sixty-second bound is an assertion. A
    // twelve-second client deadline tripping under contention is exactly the unattributable failure a
    // ceiling exists to catch, and it never reaches an assert to be gated at.
    //
    // So the condition to encode, when something encodes it, is not the one this file implements:
    // refuse a contended measurement wherever an elapsed-time bound governs the outcome, whether that
    // bound is enforced by an assertion, a timeout, or a cancellation token. That is deliberately not
    // implemented here. Widening this branch to chase it would be the reflex the repository's filing
    // and scope discipline exists to stop, and the rule has been restated three times in a morning, so
    // it is written down rather than built.
    private const double MaximumAdmissibleLoadPerCore = 0.40;
    private const string RefusalFileName = "cameraagent-gallery-performance-refusal.json";
    private const string EvidenceFileName = "cameraagent-gallery-performance.json";
    private const long MaximumWorkingSetGrowthBytes = 256L * 1024 * 1024;
    private const long MaximumRenderedPageBytes = 1024L * 1024;
    private const long MaximumPerSessionWorkingSetBytes = 32L * 1024 * 1024;
    private static readonly int[] CaptureCounts = [1_000, 10_000];
    private static readonly int[] ConcurrencyLevels = [1, 10, 50];
    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Manual")]
    [DoNotParallelize]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each asynchronously created performance fixture is immediately owned by a using declaration for its workload iteration.")]
    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Await-using must retain the strongly typed Kestrel fixture for authenticated HTTP measurements.")]
    public async Task GalleryReadModelRecordsIssue106AcceptanceEvidenceAsync()
    {
        // Sampled before any workload so it reflects the host we inherited rather than the load this
        // test itself generates; by the browser phase the run has driven load well above the ceiling.
        var contention = SampleHostContention();
        var allMeasurements = new List<GalleryMeasurement>();
        var plans = new List<SqlPlanEvidence>();
        var correctness = new List<CorrectnessEvidence>();
        var apiMeasurements = new List<ApiMeasurement>();
        var browserMeasurements = new List<BrowserRenderMeasurement>();
        var browserPreviewFailureMeasurements = new List<BrowserPreviewFailureMeasurement>();
        PreviewCacheEvidence? previewCache = null;
        string? sqliteVersion = null;
        var evidenceLabel = ReadEvidenceLabel();
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await using var browser = await LaunchOrWithdrawStaleEvidenceAsync(playwright).ConfigureAwait(false);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);

        foreach (var captureCount in CaptureCounts)
        {
            using var fixture = await GalleryPerformanceFixture.CreateAsync(captureCount).ConfigureAwait(false);
            await using var host = await CameraAgentKestrelFixture.CreateAsync(services =>
            {
                services.RemoveAll<ICameraAgentGallery>();
                services.AddSingleton<ICameraAgentGallery>(fixture.Gallery);
            }).ConfigureAwait(false);
            using var ownerClient = await host.CreateOwnerClientAsync().ConfigureAwait(false);
            sqliteVersion ??= await fixture.ReadSqliteVersionAsync().ConfigureAwait(false);
            plans.AddRange(await CollectPlansAsync(fixture, captureCount).ConfigureAwait(false));
            var cursors = await BuildCursorsAsync(fixture.Gallery, captureCount).ConfigureAwait(false);
            correctness.Add(await AssertCompletePagingAsync(fixture.Gallery, captureCount).ConfigureAwait(false));
            await AssertCancellationAsync(fixture.Gallery).ConfigureAwait(false);

            var firstExpected = await fixture.Gallery.GetPageAsync(
                new CameraAgentGalleryQuery(PageSize: PageSize), CancellationToken.None).ConfigureAwait(false);
            var middleExpected = await fixture.Gallery.GetPageAsync(
                new CameraAgentGalleryQuery(PageSize: PageSize, Cursor: cursors.Middle), CancellationToken.None).ConfigureAwait(false);
            var laterExpected = await fixture.Gallery.GetPageAsync(
                new CameraAgentGalleryQuery(PageSize: PageSize, Cursor: cursors.Later), CancellationToken.None).ConfigureAwait(false);
            var filteredExpected = await fixture.Gallery.GetPageAsync(
                new CameraAgentGalleryQuery(PageSize: PageSize, EvidenceOrigin: GalleryEvidenceOrigin.DeveloperFixture),
                CancellationToken.None).ConfigureAwait(false);
            var detailId = middleExpected.Items[middleExpected.Items.Count / 2].CaptureId;
            var detailExpected = await fixture.Gallery.GetCaptureAsync(detailId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(detailExpected);

            var scenarios = new[]
            {
                PageScenario("first", new CameraAgentGalleryQuery(PageSize: PageSize), firstExpected),
                PageScenario("middle", new CameraAgentGalleryQuery(PageSize: PageSize, Cursor: cursors.Middle), middleExpected),
                PageScenario("later", new CameraAgentGalleryQuery(PageSize: PageSize, Cursor: cursors.Later), laterExpected),
                PageScenario("filtered-developer-fixture", new CameraAgentGalleryQuery(
                    PageSize: PageSize, EvidenceOrigin: GalleryEvidenceOrigin.DeveloperFixture), filteredExpected),
                DetailScenario(detailId, detailExpected)
            };

            foreach (var scenario in scenarios)
            {
                await scenario.Execute(fixture.Gallery, CancellationToken.None).ConfigureAwait(false);
                foreach (var concurrency in ConcurrencyLevels)
                {
                    allMeasurements.Add(await MeasureAsync(
                        fixture.Gallery,
                        captureCount,
                        scenario,
                        concurrency).ConfigureAwait(false));
                }
            }
            foreach (var concurrency in ConcurrencyLevels)
            {
                apiMeasurements.Add(await MeasureHttpApiAsync(
                    ownerClient, captureCount, concurrency).ConfigureAwait(false));
            }
            var browserSessionMeasurements = await MeasureBrowserSessionsAsync(
                diagnostics, host.BaseAddress, captureCount, evidenceLabel != "baseline", contention).ConfigureAwait(false);
            browserMeasurements.AddRange(browserSessionMeasurements.Renders);
            browserPreviewFailureMeasurements.AddRange(browserSessionMeasurements.PreviewFailures);

            previewCache ??= await MeasurePreviewCacheAsync(fixture).ConfigureAwait(false);
        }

        AssertScaling(allMeasurements);

        // A second sample, recorded and never adjudicated. L1 of the #787 review is right that one
        // pre-workload reading is blind to contention that starts after it: measured RMS divergence
        // from the instantaneous runnable count was 1.454/core against a 0.40 ceiling. It does not
        // follow that this sample can gate anything. By this point the run has driven load well above
        // the ceiling by itself, and load1 cannot separate contention that arrived mid-run from load
        // this test generated. The ceiling scales with core count, so a gate on this sample would
        // have its refusal rate governed by the size of the machine rather than by inherited
        // contention, which is what makes it uninformative rather than merely strict. Recording it
        // makes the blindness visible to whoever reads the evidence instead of pretending it was
        // closed.
        var postWorkloadContention = SampleHostContention();
        var evidence = new
        {
            SchemaVersion = "cameraagent-archive-441-performance-v2",
            Revision = ReadRevisionEvidence(),
            RecordedUtc = DateTimeOffset.UtcNow,
            Command = "dotnet test tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests/HVO.SkyMonitor.CameraAgent.AcceptanceTests.csproj --configuration Release --filter FullyQualifiedName~CameraAgentGalleryPerformanceTests",
            Environment = new
            {
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Framework = RuntimeInformation.FrameworkDescription,
                ProcessorCount = Environment.ProcessorCount,
                ServerGc = System.Runtime.GCSettings.IsServerGC,
                SqliteVersion = sqliteVersion,
                PreWorkloadLoadPerCore = contention.LoadPerCore,
                PostWorkloadLoadPerCore = postWorkloadContention.LoadPerCore,
                // The note travels with the document because the reason this figure is not
                // comparable with the ceiling beside it lives in a source comment, and the JSON
                // outlives the branch and is read by people and scripts that do not have this
                // file open. Without it a reader sees a post-workload figure several times the
                // ceiling and concludes the run was contaminated, which is a well-formed and
                // plausible wrong answer produced from correctly recorded data.
                PostWorkloadLoadPerCoreNote =
                    "Recorded, never adjudicated. This sample is dominated by the workload this run "
                    + "generated, and load1 carries no decomposition that could separate that from "
                    + "inherited contention, so it is not comparable with ContentionCeilingPerCore. "
                    + "Only PreWorkloadLoadPerCore is compared with ContentionCeilingPerCore, and "
                    + "only to decide whether a MISSED bound is attributable to the product.",
                ContentionCeilingPerCore = MaximumAdmissibleLoadPerCore,
                ContentionProcessorCount = contention.ProcessorCount,
                ContentionProcessorCountSource = contention.ProcessorCountSource
            },
            Workload = new
            {
                CaptureCounts,
                ConcurrencyLevels,
                PageSize,
                Origins = new[] { "Simulated", "DeveloperFixture", "Unknown" },
                ProcessingStatuses = new[] { "Completed", "RetryableFailure", "TerminalFailure" },
                DerivedOutputsPerCapture = 1,
                Scenarios = new[] { "first", "middle", "later", "filtered-developer-fixture", "detail" },
                Persistence = "production raw-ingress and capture-processing SQLite schemas with production gallery read service"
            },
            Thresholds = new
            {
                MaximumResponseBytes,
                MaximumP95Milliseconds,
                MaximumWorkingSetGrowthBytes,
                MaximumRenderedPageBytes,
                MaximumPerSessionWorkingSetBytes,
                LaterPageMultiplier = 4,
                LaterPageAllowanceMilliseconds = 25,
                HistoryAllocationMultiplier = 2,
                HistoryAllocationAllowanceBytes = 4 * 1024 * 1024
            },
            SqlPlans = plans,
            Measurements = allMeasurements,
            AuthenticatedKestrelApiMeasurements = apiMeasurements,
            AuthenticatedBrowserMeasurements = browserMeasurements,
            AuthenticatedBrowserPreviewFailureMeasurements = browserPreviewFailureMeasurements,
            Baseline = new
            {
                Status = evidenceLabel == "baseline" ? "Captured" : "Candidate",
                EvidenceLabel = evidenceLabel,
                Comparison = "Compare equivalent issue-441 baseline and candidate files by capture count and concurrency."
            },
            Correctness = correctness,
            Cancellation = new { PreCancelledReadThrows = true, MaximumObservedMilliseconds = 2_000 },
            PreviewCache = previewCache,
            Privacy = new
            {
                InternalPathsEmitted = false,
                SecretsEmitted = false,
                SqlValues = "fixed synthetic identities only"
            }
        };

        var outputDirectory = Path.Combine(GetRepositoryRoot(), "TestResults", "issue-441", evidenceLabel);
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, EvidenceFileName);
        var evidenceBytes = JsonSerializer.SerializeToUtf8Bytes(evidence, EvidenceJson);
        await File.WriteAllBytesAsync(outputPath, evidenceBytes).ConfigureAwait(false);
        // A refusal document from an earlier contended run would otherwise sit beside admissible
        // evidence and read as current.
        File.Delete(Path.Combine(outputDirectory, RefusalFileName));
        TestContext.WriteLine($"Issue #441 performance evidence: {outputPath}");
        TestContext.WriteLine($"Issue #441 performance evidence SHA-256: {Convert.ToHexString(SHA256.HashData(evidenceBytes))}");
        await diagnostics.CompleteAsync().ConfigureAwait(false);
    }

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Await-using must retain the strongly typed Playwright context for the measured session scope.")]
    private static async Task<BrowserSessionMeasurements> MeasureBrowserSessionsAsync(
        PlaywrightDiagnostics diagnostics,
        Uri baseAddress,
        int captureCount,
        bool measurePreviewFailures,
        HostContention contention)
    {
        await using var context = await diagnostics.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = baseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            ColorScheme = ColorScheme.Dark,
            ReducedMotion = ReducedMotion.Reduce
        }).ConfigureAwait(false);
        context.SetDefaultTimeout(45_000);
        context.SetDefaultNavigationTimeout(45_000);
        var loginPage = await context.NewPageAsync().ConfigureAwait(false);
        await LoginAsync(loginPage).ConfigureAwait(false);
        await loginPage.CloseAsync().ConfigureAwait(false);
        await context.RouteAsync(
            "**/api/v1/operations/artifacts/*/preview",
            static route => route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "image/svg+xml",
                Body = "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'/>",
            })).ConfigureAwait(false);
        var measurements = new List<BrowserRenderMeasurement>();
        var previewFailureMeasurements = new List<BrowserPreviewFailureMeasurement>();
        foreach (var concurrency in ConcurrencyLevels)
        {
            ForceCollection();
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var workingSetBefore = process.WorkingSet64;
            var rssBefore = ReadRssBytes();
            var cpuBefore = process.TotalProcessorTime;
            var allocationsBefore = GC.GetTotalAllocatedBytes(precise: true);
            var pages = await Task.WhenAll(Enumerable.Range(0, concurrency)
                .Select(_ => context.NewPageAsync())).ConfigureAwait(false);
            var overall = Stopwatch.StartNew();
            var renders = await Task.WhenAll(pages.Select(async page =>
            {
                var timer = Stopwatch.StartNew();
                await page.GotoAsync("/gallery?pageSize=50").ConfigureAwait(false);
                await page.Locator(".capture-card").First.WaitForAsync().ConfigureAwait(false);
                timer.Stop();
                var cardCount = await page.Locator(".capture-card").CountAsync().ConfigureAwait(false);
                var content = await page.ContentAsync().ConfigureAwait(false);
                return new BrowserRenderResult(
                    timer.Elapsed.TotalMilliseconds,
                    System.Text.Encoding.UTF8.GetByteCount(content),
                    cardCount);
            })).ConfigureAwait(false);
            overall.Stop();
            process.Refresh();
            var workingSetAfter = process.WorkingSet64;
            var rssAfter = ReadRssBytes();
            var cpuAfter = process.TotalProcessorTime;
            var allocationsAfter = GC.GetTotalAllocatedBytes(precise: true);
            Assert.IsTrue(renders.All(static render => render.CardCount == PageSize));
            Assert.IsTrue(renders.All(static render => render.RenderedBytes <= MaximumRenderedPageBytes));
            var workingSetGrowth = Math.Max(0, workingSetAfter - workingSetBefore);
            var perSessionGrowth = workingSetGrowth / concurrency;
            var latencies = renders.Select(static render => render.ElapsedMilliseconds).Order().ToArray();
            RefuseUnattributableBrowserMiss(
                contention, Percentile(latencies, 0.95), perSessionGrowth, "browser render sessions");
            Assert.IsLessThanOrEqualTo(MaximumPerSessionWorkingSetBytes, perSessionGrowth);
            Assert.IsLessThanOrEqualTo(MaximumP95Milliseconds, Percentile(latencies, 0.95), "browser render sessions");
            measurements.Add(new BrowserRenderMeasurement(
                captureCount,
                concurrency,
                renders.Length,
                overall.Elapsed.TotalMilliseconds,
                Percentile(latencies, 0.5),
                latencies.Length >= 30 ? Percentile(latencies, 0.95) : null,
                latencies[^1],
                renders.Min(static render => render.RenderedBytes),
                renders.Max(static render => render.RenderedBytes),
                renders[0].CardCount,
                (cpuAfter - cpuBefore).TotalMilliseconds,
                Math.Max(0, allocationsAfter - allocationsBefore),
                workingSetBefore,
                workingSetAfter,
                rssBefore,
                rssAfter,
                perSessionGrowth,
                concurrency / Math.Max(overall.Elapsed.TotalSeconds, 0.000_001)));
            if (measurePreviewFailures)
            {
                previewFailureMeasurements.Add(await MeasurePreviewFailuresAsync(
                    pages,
                    captureCount,
                    concurrency,
                    workingSetBefore,
                    rssBefore,
                    contention).ConfigureAwait(false));
            }
            await Task.WhenAll(pages.Select(static page => page.CloseAsync())).ConfigureAwait(false);
            await Task.Delay(250).ConfigureAwait(false);
        }
        var result = new BrowserSessionMeasurements(measurements, previewFailureMeasurements);
        await diagnostics.ReleaseAsync(context).ConfigureAwait(false);
        return result;
    }

    private static async Task<BrowserPreviewFailureMeasurement> MeasurePreviewFailuresAsync(
        IPage[] pages,
        int captureCount,
        int concurrency,
        long sessionWorkingSetBefore,
        long sessionRssBefore,
        HostContention contention)
    {
        foreach (var page in pages)
        {
            Assert.AreEqual(PageSize, await page.Locator(".capture-card__image img").CountAsync().ConfigureAwait(false));
            Assert.AreEqual(0, await page.GetByRole(AriaRole.Button, new()
            {
                Name = "Try preview again",
                Exact = true
            }).CountAsync().ConfigureAwait(false));
        }
        ForceCollection();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocationsBefore = GC.GetTotalAllocatedBytes(precise: true);
        var overall = Stopwatch.StartNew();
        var latencies = await Task.WhenAll(pages.Select(async page =>
        {
            var timer = Stopwatch.StartNew();
            await page.WaitForFunctionAsync("""
                () => {
                    document.querySelectorAll('.capture-card__image img')
                        .forEach(image => image.dispatchEvent(new Event('error')));
                    return [...document.querySelectorAll('.capture-card__actions button')]
                        .filter(button => button.textContent.includes('Try preview again')).length === 50;
                }
                """).ConfigureAwait(false);
            timer.Stop();
            return timer.Elapsed.TotalMilliseconds;
        })).ConfigureAwait(false);
        overall.Stop();
        process.Refresh();
        var workingSetAfter = process.WorkingSet64;
        var rssAfter = ReadRssBytes();
        var cpuAfter = process.TotalProcessorTime;
        var allocationsAfter = GC.GetTotalAllocatedBytes(precise: true);
        var workingSetGrowth = Math.Max(0, workingSetAfter - sessionWorkingSetBefore);
        var perSessionGrowth = workingSetGrowth / concurrency;
        Array.Sort(latencies);
        RefuseUnattributableBrowserMiss(
            contention, Percentile(latencies, 0.95), perSessionGrowth, "browser preview failures");
        Assert.IsLessThanOrEqualTo(MaximumPerSessionWorkingSetBytes, perSessionGrowth);
        Assert.IsLessThanOrEqualTo(MaximumP95Milliseconds, Percentile(latencies, 0.95), "browser preview failures");
        return new BrowserPreviewFailureMeasurement(
            captureCount,
            concurrency,
            pages.Length,
            overall.Elapsed.TotalMilliseconds,
            Percentile(latencies, 0.5),
            latencies.Length >= 30 ? Percentile(latencies, 0.95) : null,
            latencies[^1],
            PageSize,
            (cpuAfter - cpuBefore).TotalMilliseconds,
            Math.Max(0, allocationsAfter - allocationsBefore),
            sessionWorkingSetBefore,
            workingSetAfter,
            sessionRssBefore,
            rssAfter,
            perSessionGrowth);
    }

    private static async Task LoginAsync(IPage page)
    {
        await page.GotoAsync("/Account/Login").ConfigureAwait(false);
        await page.GetByLabel("Email").FillAsync(CameraAgentKestrelFixture.OwnerEmail).ConfigureAwait(false);
        await page.GetByLabel("Password").FillAsync(CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase))
            .ConfigureAwait(false);
    }

    private static async Task<ApiMeasurement> MeasureHttpApiAsync(
        HttpClient client,
        int captureCount,
        int concurrency)
    {
        var overall = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            var timer = Stopwatch.StartNew();
            using var response = await client.GetAsync(
                new Uri("/api/v1/operations/gallery?pageSize=50", UriKind.Relative),
                HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            var payload = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            timer.Stop();
            Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
            Assert.IsLessThanOrEqualTo(MaximumResponseBytes, payload.Length);
            return new ResponseMeasurement(
                timer.Elapsed.TotalMilliseconds,
                payload.Length,
                Convert.ToHexString(SHA256.HashData(payload)));
        }).ToArray();
        var responses = await Task.WhenAll(tasks).ConfigureAwait(false);
        overall.Stop();
        Assert.HasCount(1, responses.Select(static response => response.Sha256).Distinct().ToArray());
        var latencies = responses.Select(static response => response.ElapsedMilliseconds).Order().ToArray();
        return new ApiMeasurement(
            captureCount,
            concurrency,
            responses[0].Bytes,
            Percentile(latencies, 0.5),
            Percentile(latencies, 0.95),
            concurrency / Math.Max(overall.Elapsed.TotalSeconds, 0.000_001),
            responses[0].Sha256);
    }

    private static PerformanceScenario PageScenario(
        string name,
        CameraAgentGalleryQuery query,
        CameraAgentGalleryPage expected)
    {
        var expectedIds = expected.Items.Select(static item => item.CaptureId).ToArray();
        return new PerformanceScenario(name, async (gallery, cancellationToken) =>
        {
            var page = await gallery.GetPageAsync(query, cancellationToken).ConfigureAwait(false);
            Assert.IsLessThanOrEqualTo(PageSize, page.Items.Count);
            CollectionAssert.AreEqual(expectedIds, page.Items.Select(static item => item.CaptureId).ToArray());
            Assert.AreEqual(page.Items.Count, page.Items.Select(static item => item.CaptureId).Distinct().Count());
            foreach (var capture in page.Items)
            {
                Assert.HasCount(2, capture.Artifacts);
                Assert.HasCount(1, capture.ProcessingNodes);
                Assert.IsTrue(capture.Artifacts.All(static artifact =>
                    artifact.ChecksumSha256.Length == 64 && artifact.ChecksumSha256.All(Uri.IsHexDigit)));
                var raw = capture.Artifacts.Single(static artifact => artifact.Role == FrameArtifactRole.Raw);
                var preview = capture.Artifacts.Single(static artifact => artifact.Role == FrameArtifactRole.Preview);
                CollectionAssert.AreEqual(new[] { raw.ArtifactId }, preview.SourceArtifactIds.ToArray());
            }
            return page;
        });
    }

    private static PerformanceScenario DetailScenario(Guid captureId, CameraAgentGalleryCapture expected)
        => new("detail", async (gallery, cancellationToken) =>
        {
            var detail = await gallery.GetCaptureAsync(captureId, cancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(detail);
            Assert.AreEqual(expected.CaptureId, detail.CaptureId);
            Assert.AreEqual(expected.CaptureSequence, detail.CaptureSequence);
            Assert.IsNotNull(detail.Detail);
            Assert.AreEqual("Available", detail.Detail.EvidenceAvailability);
            Assert.AreEqual(ArtifactManifestV2.CurrentSchemaVersion, detail.Detail.ManifestSchemaVersion);
            var raw = detail.Artifacts.Single(static artifact => artifact.Role == FrameArtifactRole.Raw);
            var preview = detail.Artifacts.Single(static artifact => artifact.Role == FrameArtifactRole.Preview);
            CollectionAssert.AreEqual(new[] { raw.ArtifactId }, preview.SourceArtifactIds.ToArray());
            Assert.IsNotNull(preview.Recipe);
            Assert.AreEqual("gallery-preview", preview.Recipe.Name);
            return detail;
        });

    private static async Task<GalleryMeasurement> MeasureAsync(
        SqliteCameraAgentGallery gallery,
        int captureCount,
        PerformanceScenario scenario,
        int concurrency)
    {
        ForceCollection();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSetBefore = process.WorkingSet64;
        var rssBefore = ReadRssBytes();
        var cpuBefore = process.TotalProcessorTime;
        var allocationsBefore = GC.GetTotalAllocatedBytes(precise: true);
        var overall = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            var timer = Stopwatch.StartNew();
            var result = await scenario.Execute(gallery, CancellationToken.None).ConfigureAwait(false);
            var serialized = JsonSerializer.SerializeToUtf8Bytes(result, result.GetType(), EvidenceJson);
            timer.Stop();
            return new ResponseMeasurement(
                timer.Elapsed.TotalMilliseconds,
                serialized.Length,
                Convert.ToHexString(SHA256.HashData(serialized)));
        }).ToArray();
        var responses = await Task.WhenAll(tasks).ConfigureAwait(false);
        overall.Stop();
        process.Refresh();
        var workingSetAfter = process.WorkingSet64;
        var rssAfter = ReadRssBytes();
        var cpuAfter = process.TotalProcessorTime;
        var allocationsAfter = GC.GetTotalAllocatedBytes(precise: true);

        Assert.IsTrue(responses.All(response => response.Bytes <= MaximumResponseBytes));
        Assert.HasCount(1, responses.Select(static response => response.Sha256).Distinct().ToArray());
        var latencies = responses.Select(static response => response.ElapsedMilliseconds).Order().ToArray();
        var p95 = Percentile(latencies, 0.95);
        Assert.IsLessThanOrEqualTo(MaximumP95Milliseconds, p95,
            $"{captureCount}/{scenario.Name}/{concurrency} p95 exceeded its acceptance bound.");
        var workingSetGrowth = Math.Max(0, workingSetAfter - workingSetBefore);
        Assert.IsLessThanOrEqualTo(MaximumWorkingSetGrowthBytes, workingSetGrowth);

        return new GalleryMeasurement(
            captureCount,
            scenario.Name,
            concurrency,
            overall.Elapsed.TotalMilliseconds,
            Percentile(latencies, 0.5),
            p95,
            responses.Min(static response => response.Bytes),
            responses.Max(static response => response.Bytes),
            (cpuAfter - cpuBefore).TotalMilliseconds,
            Math.Max(0, allocationsAfter - allocationsBefore),
            workingSetBefore,
            workingSetAfter,
            rssBefore,
            rssAfter,
            responses[0].Sha256);
    }

    private static async Task<GalleryCursors> BuildCursorsAsync(SqliteCameraAgentGallery gallery, int captureCount)
    {
        string? cursor = null;
        string? middle = null;
        string? later = null;
        var traversed = 0;
        while (traversed < captureCount)
        {
            if (traversed == captureCount / 2)
            {
                middle = cursor;
            }
            if (traversed == captureCount * 9 / 10)
            {
                later = cursor;
            }
            var page = await gallery.GetPageAsync(
                new CameraAgentGalleryQuery(PageSize: PageSize, Cursor: cursor), CancellationToken.None).ConfigureAwait(false);
            traversed += page.Items.Count;
            cursor = page.NextCursor;
            if (cursor is null)
            {
                break;
            }
        }
        Assert.IsNotNull(middle);
        Assert.IsNotNull(later);
        return new GalleryCursors(middle, later);
    }

    private static async Task<CorrectnessEvidence> AssertCompletePagingAsync(
        SqliteCameraAgentGallery gallery,
        int captureCount)
    {
        var sequences = new List<long>(captureCount);
        string? cursor = null;
        do
        {
            var page = await gallery.GetPageAsync(
                new CameraAgentGalleryQuery(PageSize: 100, Cursor: cursor), CancellationToken.None).ConfigureAwait(false);
            sequences.AddRange(page.Items.Select(static item => item.CaptureSequence));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        CollectionAssert.AreEqual(
            Enumerable.Range(1, captureCount).Reverse().Select(static value => (long)value).ToArray(),
            sequences.ToArray());
        Assert.AreEqual(captureCount, sequences.Distinct().Count());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(sequences);
        return new CorrectnessEvidence(
            captureCount,
            sequences.Count,
            sequences.Distinct().Count(),
            sequences[0],
            sequences[^1],
            Convert.ToHexString(SHA256.HashData(bytes)),
            "exact descending keyset sequence with no duplicates or gaps; raw/preview checksum and lineage asserted per response");
    }

    private static async Task AssertCancellationAsync(SqliteCameraAgentGallery gallery)
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        var timer = Stopwatch.StartNew();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await gallery.GetPageAsync(new CameraAgentGalleryQuery(PageSize: 100), cancellation.Token)
                .ConfigureAwait(false)).ConfigureAwait(false);
        timer.Stop();
        Assert.IsLessThanOrEqualTo(2_000, timer.Elapsed.TotalMilliseconds);
    }

    private static async Task<IReadOnlyList<SqlPlanEvidence>> CollectPlansAsync(
        GalleryPerformanceFixture fixture,
        int captureCount)
    {
        var middle = captureCount / 2;
        var statements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["canonical-keyset"] = $"SELECT raw_capture_row_id FROM raw_captures AS raw WHERE (raw.capture_sequence < {middle} OR (raw.capture_sequence = {middle} AND raw.raw_capture_row_id < {middle})) ORDER BY raw.capture_sequence DESC, raw.raw_capture_row_id DESC LIMIT 51;",
            ["origin-filter"] = "SELECT raw_capture_row_id FROM raw_captures AS raw WHERE raw.evidence_origin = 'DeveloperFixture' ORDER BY raw.capture_sequence DESC, raw.raw_capture_row_id DESC LIMIT 51;",
            ["status-filter"] = "SELECT raw_capture_row_id FROM raw_captures AS raw WHERE EXISTS (SELECT 1 FROM processing_nodes AS node WHERE node.capture_id = raw.capture_id AND node.status = 'Completed') ORDER BY raw.capture_sequence DESC, raw.raw_capture_row_id DESC LIMIT 51;"
        };
        var evidence = new List<SqlPlanEvidence>();
        foreach (var statement in statements)
        {
            var plan = await fixture.ExplainAsync(statement.Value).ConfigureAwait(false);
            Assert.IsFalse(plan.Any(static detail => detail.Contains("USE TEMP B-TREE", StringComparison.OrdinalIgnoreCase)),
                string.Join(" | ", plan));
            Assert.IsFalse(plan.Any(static detail => detail.StartsWith("SCAN raw", StringComparison.OrdinalIgnoreCase) &&
                !detail.Contains("USING", StringComparison.OrdinalIgnoreCase)), string.Join(" | ", plan));
            evidence.Add(new SqlPlanEvidence(captureCount, statement.Key, statement.Value, plan));
        }
        return evidence;
    }

    private static async Task<PreviewCacheEvidence> MeasurePreviewCacheAsync(GalleryPerformanceFixture fixture)
    {
        var encoder = new CountingPreviewEncoder();
        using var store = new SqliteCaptureProcessingStore(fixture.Options);
        using var service = new CameraAgentArtifactService(fixture.Options, store, encoder);
        var artifact = fixture.PreviewSeeds[0];
        var first = await service.GetPreviewAsync(artifact.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, first.Status);
        var firstChecksum = first.ChecksumSha256;
        for (var index = 0; index < 20; index++)
        {
            var cached = await service.GetPreviewAsync(artifact.ArtifactId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Found, cached.Status);
            Assert.AreEqual(firstChecksum, cached.ChecksumSha256);
            CollectionAssert.AreEqual(first.Content.ToArray(), cached.Content.ToArray());
        }
        Assert.AreEqual(1, encoder.Count);

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 50).Select(async _ =>
            await service.GetPreviewAsync(artifact.ArtifactId, CancellationToken.None).ConfigureAwait(false)))
            .ConfigureAwait(false);
        Assert.IsTrue(concurrent.All(static result => result.Status == CameraAgentArtifactReadStatus.Found));
        Assert.HasCount(1, concurrent.Select(static result => result.ChecksumSha256).Distinct().ToArray());
        Assert.IsTrue(concurrent.All(result => result.Content.Span.SequenceEqual(first.Content.Span)));
        Assert.AreEqual(1, encoder.Count);
        return new PreviewCacheEvidence(
            1,
            20,
            50,
            concurrent.Count(static result => result.Status == CameraAgentArtifactReadStatus.Found),
            0,
            encoder.Count,
            first.Content.Length,
            firstChecksum!,
            "CameraAgentArtifactServiceTests.PreviewAllowsOnlyDurablePreviewRolesAndCachesByChecksumAsync",
            "CameraAgentArtifactServiceTests.PreviewDimensionsBytesAndConcurrencyAreBoundedAsync");
    }

    private static void AssertScaling(IReadOnlyList<GalleryMeasurement> measurements)
    {
        foreach (var captureCount in CaptureCounts)
        {
            foreach (var concurrency in ConcurrencyLevels)
            {
                var first = Find(measurements, captureCount, "first", concurrency);
                var later = Find(measurements, captureCount, "later", concurrency);
                Assert.IsLessThanOrEqualTo(
                    first.MedianMilliseconds * 4 + 25,
                    later.MedianMilliseconds,
                    $"Later-page cost scaled with preceding history for {captureCount}/{concurrency}.");
            }
        }

        foreach (var concurrency in ConcurrencyLevels)
        {
            var small = Find(measurements, 1_000, "first", concurrency);
            var large = Find(measurements, 10_000, "first", concurrency);
            Assert.IsLessThanOrEqualTo(
                small.AllocatedBytes * 2 + 4 * 1024 * 1024,
                large.AllocatedBytes,
                $"Page allocation scaled materially with gallery history at concurrency {concurrency}.");
        }
    }

    private static GalleryMeasurement Find(
        IReadOnlyList<GalleryMeasurement> measurements,
        int captureCount,
        string scenario,
        int concurrency)
        => measurements.Single(item => item.CaptureCount == captureCount &&
            item.Scenario == scenario && item.Concurrency == concurrency);

    // Called before either bound is adjudicated, and refuses only a MISS. A met bound is admissible at
    // any load, for the reason recorded at MaximumAdmissibleLoadPerCore: contention biases a deadline
    // against the pass, so a pass taken under contention is stronger than one taken idle and refusing
    // it would throw away the better result.
    //
    // Both bounds are adjudicated here rather than the p95 alone, because MaximumPerSessionWorkingSetBytes
    // is 32 MiB per session and a host under memory pressure — swapping, GC under stress, the condition
    // the ceiling exists for — can miss it for the host's reasons rather than the product's. #785
    // measured latency under contention and is silent about working set, so this does not claim working
    // set is load-sensitive; it declines to claim the opposite. The card-count and rendered-byte
    // assertions above stay ungated deliberately: a busy host does not change how many cards a page
    // contains.
    private static void RefuseUnattributableBrowserMiss(
        HostContention contention, double p95, long perSessionGrowthBytes, string scenario)
    {
        var missed = new List<string>();
        if (p95 > MaximumP95Milliseconds)
        {
            missed.Add($"p95 {p95:F1} ms against the {MaximumP95Milliseconds:F0} ms bound");
        }
        if (perSessionGrowthBytes > MaximumPerSessionWorkingSetBytes)
        {
            missed.Add(
                $"per-session working-set growth {perSessionGrowthBytes} bytes against the " +
                $"{MaximumPerSessionWorkingSetBytes} byte bound");
        }

        // Nothing missed, or the host was quiet enough for the miss to be the product's: in both cases
        // the caller's assertions adjudicate, and a quiet-host miss is a real red.
        if (missed.Count == 0
            || contention.LoadPerCore is not { } loadPerCore
            || loadPerCore <= MaximumAdmissibleLoadPerCore)
        {
            return;
        }

        var reason =
            $"This browser measurement missed a bound on a contended host, so the miss is " +
            $"unattributable: {string.Join(" and ", missed)} for {scenario}. The one-minute load " +
            $"average before the workload started was {loadPerCore:F2} per core across " +
            $"{contention.ProcessorCount} cores ({contention.ProcessorCountSource}), above the " +
            $"{MaximumAdmissibleLoadPerCore:F2} ceiling. Browser-driven p95 moves by more than 40% " +
            $"under contention, so a slow product and a busy host produce the same red and this run " +
            $"cannot tell them apart. Had the bounds been met at this load the run would have passed, " +
            $"and that pass would have been stronger than an idle one. This is not a latency " +
            $"regression, and it is not evidence against one either. Re-run on a quiet host.";

        // The refusal is persisted here because Assert.Inconclusive unwinds past the evidence writer at
        // the end of the test method, so a refused run would otherwise leave no durable record at all
        // and PreWorkloadLoadPerCore would only ever be published with an admissible value. See #802
        // for why the TRX alone is not sufficient: MSTest serialises Assert.Inconclusive as
        // outcome="NotExecuted", indistinguishable from a skipped test except in the message body.
        WriteRefusalEvidence(contention, p95, perSessionGrowthBytes, scenario, missed, reason);
        Assert.Inconclusive(reason);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A refusal that cannot be written is still a refusal; no failure of this best-effort writer may convert one into a test failure.")]
    private static void WriteRefusalEvidence(
        HostContention contention,
        double p95,
        long perSessionGrowthBytes,
        string scenario,
        IReadOnlyList<string> missed,
        string reason)
    {
        try
        {
            var directory = Path.Combine(
                GetRepositoryRoot(), "TestResults", "issue-441", ReadEvidenceLabel());
            Directory.CreateDirectory(directory);
            var refusal = new
            {
                SchemaVersion = "cameraagent-archive-441-performance-refusal-v1",
                RecordedUtc = DateTimeOffset.UtcNow,
                Outcome = "RefusedUnattributableMiss",
                Scenario = scenario,
                MissedBounds = missed,
                Reason = reason,
                PreWorkloadLoadPerCore = contention.LoadPerCore,
                ContentionCeilingPerCore = MaximumAdmissibleLoadPerCore,
                ContentionProcessorCount = contention.ProcessorCount,
                ContentionProcessorCountSource = contention.ProcessorCountSource,
                ObservedP95Milliseconds = p95,
                ObservedPerSessionWorkingSetGrowthBytes = perSessionGrowthBytes
            };
            File.WriteAllBytes(
                Path.Combine(directory, RefusalFileName),
                JsonSerializer.SerializeToUtf8Bytes(refusal, EvidenceJson));
            // The mirror of the success path's delete, and the stronger of the two cases. An
            // evidence document from an earlier admissible run would otherwise sit beside this
            // refusal and read as current, and it is the file that gets hashed, published and read
            // by scripts while nothing yet consumes the refusal. The runner clears only its own TRX,
            // under a different root, so nothing else removes it.
            File.Delete(Path.Combine(directory, EvidenceFileName));
        }
        catch (Exception exception)
        {
            // A refusal that cannot be written is still a refusal; never convert this into a failure
            // that would be read as a latency regression. The catch is deliberately total because an
            // enumerated list was the wrong shape for an absolute promise: it named IOException and
            // UnauthorizedAccessException while the first statement in the try was GetRepositoryRoot,
            // which throws InvalidOperationException, and Directory.CreateDirectory can raise
            // NotSupportedException or ArgumentException. Any of those escaping would have skipped
            // the Assert.Inconclusive on the caller's next line and reported a correctly refused run
            // as a hard failure, which is the one outcome this method exists to prevent.
            //
            // Only the durable copy is lost. The reason still reaches the operator through
            // Assert.Inconclusive, and the swallow is announced rather than silent.
            Console.Error.WriteLine(
                $"Refusal evidence could not be written ({exception.GetType().Name}: {exception.Message}). "
                + "The refusal itself is unaffected.");
        }
    }

    private static string ReadEvidenceLabel()
        => string.Equals(
            Environment.GetEnvironmentVariable("HVO_GALLERY_EVIDENCE_LABEL"),
            "baseline",
            StringComparison.Ordinal)
            ? "baseline"
            : "candidate";

    private static HostContention SampleHostContention()
    {
        // The numerator from /proc/loadavg is host-wide and is not namespaced, so the denominator has
        // to be host-wide too. Environment.ProcessorCount honours a cgroup CPU quota, which pairs a
        // host numerator with a container denominator: inside `docker run --cpus=2` on an eight-core
        // host the ratio reads four times high and every run is refused, which is L3 of the #787
        // review. Counting the CPUs the kernel reports online keeps both halves on the same machine.
        // A container on a host-relatively busy host is still refused, and correctly so — the
        // contention is real and the measurement is contaminated whether or not the quota hides it.
        //
        // One band is admitted that should not be, and naming it is cheaper than pretending the
        // pairing is unconditional. On an eight-core host with the test in `--cpus=2` and external
        // load1 of 3.0 the gate computes 3.0/8 = 0.375, admits, and then measures browser p95 while
        // holding two cores against three runnable competitors. The old pairing refused that case at
        // 3.0/2 = 1.5, correctly but for the wrong reason, so this is not an argument for going back.
        // Nothing establishes that min(online, quota) is the better denominator either: a quota is
        // not a cpuset and external load is not pinned away from the test. Measured 2026-09-09,
        // nothing in this repository runs this class under a CPU quota — it is class-level Manual,
        // every CI filter carries TestCategory!=Manual, and scripts/test:cameraagent-ui is referenced
        // only by the manual command in docs/validation/cameraagent-ui-106.md and by CI's `bash -n`
        // syntax check, which parses without executing. A containerised runner for this suite would
        // reopen the band, so re-measure rather than trust that sentence.
        var onlineProcessors = ReadOnlineProcessorCount();
        var processorCount = onlineProcessors ?? Environment.ProcessorCount;
        // The caveat is a statement about cgroups, so it is only true where cgroups exist. Published
        // unconditionally it told every macOS and Windows reader that the count might be narrowed by
        // a quota mechanism their platform does not have, in a document written to outlive the branch
        // and be read without the source open.
        var processorCountSource = onlineProcessors is not null
            ? "/sys/devices/system/cpu/online"
            : OperatingSystem.IsLinux()
                ? "Environment.ProcessorCount, cgroup-quota-aware and possibly narrower than the host"
                : "Environment.ProcessorCount; this platform has no cgroup CPU quota to narrow it";

        // /proc/loadavg is Linux-only. Where it is unavailable the precondition cannot be evaluated and
        // the bound is asserted exactly as it was before, so no platform loses coverage it already had.
        try
        {
            if (File.Exists("/proc/loadavg"))
            {
                var fields = File.ReadAllText("/proc/loadavg")
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length > 0
                    && double.TryParse(fields[0], System.Globalization.CultureInfo.InvariantCulture, out var oneMinute)
                    && processorCount > 0)
                {
                    return new HostContention(
                        oneMinute / processorCount, processorCount, processorCountSource);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return new HostContention(null, processorCount, processorCountSource);
    }

    // Parses the kernel's online-CPU list, "0-11" or "0-3,8-11". Returns null rather than a guess when
    // the file is absent or unparseable, so the caller falls back explicitly and records that it did.
    private static int? ReadOnlineProcessorCount()
    {
        try
        {
            const string OnlinePath = "/sys/devices/system/cpu/online";
            if (!File.Exists(OnlinePath))
            {
                return null;
            }

            var total = 0;
            foreach (var range in File.ReadAllText(OnlinePath).Trim()
                .Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var bounds = range.Split('-', StringSplitOptions.RemoveEmptyEntries);
                if (bounds.Length == 1
                    && int.TryParse(bounds[0], System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    total++;
                }
                else if (bounds.Length == 2
                    && int.TryParse(bounds[0], System.Globalization.CultureInfo.InvariantCulture, out var first)
                    && int.TryParse(bounds[1], System.Globalization.CultureInfo.InvariantCulture, out var last)
                    && last >= first)
                {
                    total += last - first + 1;
                }
                else
                {
                    return null;
                }
            }

            return total > 0 ? total : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        var index = Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static long ReadRssBytes()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/self/statm"))
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            return process.WorkingSet64;
        }
        var fields = File.ReadAllText("/proc/self/statm").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 1 && long.TryParse(fields[1], out var pages)
            ? checked(pages * Environment.SystemPageSize)
            : 0;
    }

    /// <summary>
    /// Starts the pinned browser for this run, and if the launch is refused as an environment
    /// problem, withdraws the previous run's evidence document before the Inconclusive unwinds.
    /// </summary>
    /// <remarks>
    /// Without this, a refusal at the launch returns before the writer at the end of the method and
    /// leaves <c>cameraagent-gallery-performance.json</c> from an earlier admissible run sitting in
    /// the output directory, where it reads as this run's result. That is the same hazard the
    /// refusal path already guards against, and its comment there states the reason: the evidence
    /// document is the file that gets hashed, published and read, while nothing yet consumes the
    /// refusal, and the runner clears only its own TRX under a different root.
    /// <para>
    /// Withdrawing is all this does. It does not synthesise a refusal document, because a run that
    /// never started a browser has no p95, no scenario and no missed bounds to record, and the
    /// refusal schema is about an unattributable miss rather than an absent browser. Inventing
    /// values to fill that shape would be the defect this pull request exists to remove.
    /// </para>
    /// </remarks>
    private static async Task<IBrowser> LaunchOrWithdrawStaleEvidenceAsync(IPlaywright playwright)
        => await RunOrWithdrawStaleEvidenceAsync(
            playwright.LaunchOrInconclusiveAsync,
            () => WithdrawStaleEvidence(GetRepositoryRoot())).ConfigureAwait(false);

    internal static async Task<T> RunOrWithdrawStaleEvidenceAsync<T>(
        Func<Task<T>> run,
        Action withdraw)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(withdraw);

        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (AssertInconclusiveException)
        {
            withdraw();
            throw;
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A browser that cannot start is still Inconclusive; no failure of this best-effort withdrawal may convert that into a test failure.")]
    internal static void WithdrawStaleEvidence(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        try
        {
            File.Delete(Path.Combine(
                repositoryRoot, "TestResults", "issue-441", ReadEvidenceLabel(), EvidenceFileName));
        }
        catch (Exception exception)
        {
            // Total for the same reason the refusal writer's catch is total: GetRepositoryRoot throws
            // InvalidOperationException and the path helpers throw several more, and any of them
            // escaping here would convert a correctly refused run into a hard failure. Announced
            // rather than swallowed silently, because a stale document that survives is exactly the
            // condition an operator needs told.
            Console.Error.WriteLine(
                $"Previous evidence could not be withdrawn ({exception.GetType().Name}: {exception.Message}). "
                + "A document from an earlier run may remain and must not be read as this run's result.");
        }
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static object ReadRevisionEvidence()
    {
        var root = GetRepositoryRoot();
        var head = RunGit(root, "rev-parse HEAD").Trim();
        var trackedDiff = RunGitBytes(root, "diff --binary HEAD");
        var status = RunGitBytes(root, "status --porcelain=v1 --untracked-files=all");
        using var combined = new MemoryStream();
        combined.Write(trackedDiff);
        combined.Write(status);
        var untracked = System.Text.Encoding.UTF8.GetString(status)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(static line => line.StartsWith("?? ", StringComparison.Ordinal))
            .Select(static line => line[3..])
            .Order(StringComparer.Ordinal);
        foreach (var relativePath in untracked)
        {
            var pathBytes = System.Text.Encoding.UTF8.GetBytes(relativePath);
            combined.Write(pathBytes);
            combined.Write(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, relativePath))));
        }
        return new
        {
            Head = head,
            Dirty = status.Length > 0,
            DirtyDiffSha256 = Convert.ToHexString(SHA256.HashData(combined.ToArray())),
            Algorithm = "SHA-256(tracked binary diff, porcelain status, and sorted untracked path/content hashes)"
        };
    }

    private static string RunGit(string workingDirectory, string arguments) =>
        System.Text.Encoding.UTF8.GetString(RunGitBytes(workingDirectory, arguments));

    private static byte[] RunGitBytes(string workingDirectory, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Could not start git for evidence revision binding.");
        using var memory = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(memory);
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Could not bind performance evidence to the repository revision.");
        }
        return memory.ToArray();
    }

    private sealed record PerformanceScenario(
        string Name,
        Func<SqliteCameraAgentGallery, CancellationToken, Task<object>> Execute);

    private sealed record HostContention(
        double? LoadPerCore, int ProcessorCount, string ProcessorCountSource);

    private sealed record GalleryCursors(string Middle, string Later);

    private sealed record ResponseMeasurement(double ElapsedMilliseconds, int Bytes, string Sha256);

    private sealed record GalleryMeasurement(
        int CaptureCount,
        string Scenario,
        int Concurrency,
        double WallMilliseconds,
        double MedianMilliseconds,
        double P95Milliseconds,
        int MinimumResponseBytes,
        int MaximumResponseBytes,
        double CpuMilliseconds,
        long AllocatedBytes,
        long WorkingSetBeforeBytes,
        long WorkingSetAfterBytes,
        long RssBeforeBytes,
        long RssAfterBytes,
        string ResponseSha256);

    private sealed record ApiMeasurement(
        int CaptureCount,
        int Concurrency,
        int ResponseBytes,
        double MedianMilliseconds,
        double P95Milliseconds,
        double ThroughputRequestsPerSecond,
        string ResponseSha256);

    private sealed record BrowserRenderResult(
        double ElapsedMilliseconds,
        int RenderedBytes,
        int CardCount);

    private sealed record BrowserRenderMeasurement(
        int CaptureCount,
        int Concurrency,
        int SampleCount,
        double WallMilliseconds,
        double MedianMilliseconds,
        double? P95Milliseconds,
        double MaximumMilliseconds,
        int MinimumRenderedBytes,
        int MaximumRenderedBytes,
        int CardCount,
        double CpuMilliseconds,
        long AllocatedBytes,
        long WorkingSetBeforeBytes,
        long WorkingSetAfterBytes,
        long RssBeforeBytes,
        long RssAfterBytes,
        long PerSessionWorkingSetGrowthBytes,
        double ThroughputPagesPerSecond);

    private sealed record BrowserSessionMeasurements(
        IReadOnlyList<BrowserRenderMeasurement> Renders,
        IReadOnlyList<BrowserPreviewFailureMeasurement> PreviewFailures);

    private sealed record BrowserPreviewFailureMeasurement(
        int CaptureCount,
        int Concurrency,
        int SampleCount,
        double WallMilliseconds,
        double MedianMilliseconds,
        double? P95Milliseconds,
        double MaximumMilliseconds,
        int FailedPreviewsPerPage,
        double CpuMilliseconds,
        long AllocatedBytes,
        long WorkingSetBeforeBytes,
        long WorkingSetAfterBytes,
        long RssBeforeBytes,
        long RssAfterBytes,
        long PerSessionWorkingSetGrowthBytes);

    private sealed record SqlPlanEvidence(
        int CaptureCount,
        string Name,
        string Statement,
        IReadOnlyList<string> Plan);

    private sealed record CorrectnessEvidence(
        int CaptureCount,
        int ObservedCount,
        int DistinctCount,
        long FirstSequence,
        long LastSequence,
        string SequenceSha256,
        string Invariants);

    private sealed record PreviewCacheEvidence(
        int WarmEncodes,
        int SequentialCacheReads,
        int ConcurrentRequests,
        int ConcurrentFound,
        int ConcurrentRejectedByBound,
        int TotalEncoderCalls,
        int EncodedBytes,
        string EncodedSha256,
        string CacheTest,
        string ConcurrencyBoundTest);

    private sealed class CountingPreviewEncoder : ICameraAgentPreviewEncoder
    {
        internal int Count { get; private set; }

        public CameraAgentEncodedPreview Encode(
            FrameLayoutDescriptor layout,
            ReadOnlyMemory<byte> payload,
            int maximumDimension,
            int maximumEncodedBytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count++;
            var scale = Math.Min(1d, Math.Min(
                (double)maximumDimension / layout.Width,
                (double)maximumDimension / layout.Height));
            return new(
                [0xFF, 0xD8, .. payload.ToArray(), 0xFF, 0xD9],
                Math.Max(1, (int)Math.Floor(layout.Width * scale)),
                Math.Max(1, (int)Math.Floor(layout.Height * scale)));
        }
    }
}
