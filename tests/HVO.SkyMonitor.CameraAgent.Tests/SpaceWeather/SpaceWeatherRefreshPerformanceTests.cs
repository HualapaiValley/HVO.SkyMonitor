using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather;

/// <summary>Offline Tier C evidence; reports provisional ceilings without tuning or treating them as assertions.</summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class SpaceWeatherRefreshPerformanceTests
{
    public TestContext TestContext { get; set; } = null!;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [TestMethod]
    public async Task FullTickRetentionRestartSimulatedDayAndRunnerSweepsAreMeasured()
    {
        var stages = new Dictionary<string, List<Sample>>(StringComparer.Ordinal);
        var input = LargeAlerts();
        var captures = Enum.GetValues<SwpcProduct>().ToDictionary(static product => product, SpaceWeatherTestHost.TestClient.Capture);
        var record = JsonSerializer.Deserialize<JsonElement[]>(SwpcStoreTests.Message())![0].GetRawText();
        var maximumRecords = Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat(record, 2000)) + "]");
        var rejectedRecords = Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat(record, 2001)) + "]");
        Assert.IsTrue(SwpcAlertsParser.Parse(maximumRecords, "application/json").Accepted);
        Assert.AreEqual("too-many-records", SwpcAlertsParser.Parse(rejectedRecords, "application/json").RejectionReason);
        for (var repeat = -1; repeat < 5; repeat++)
        {
            using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false);
            host.Client.Body = product => product == SwpcProduct.Alerts ? input : captures[product];
            await MeasureAsync("fullTick1MiB", async () =>
            {
                Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, (await host.RunAsync().ConfigureAwait(false)).Outcome);
            }, stages, repeat >= 0).ConfigureAwait(false);
            Assert.AreEqual(4, host.Client.Calls);
            using (var runner = host.CreateRunner())
            {
                await MeasureAsync("sweepWithoutDefinition", () => runner.SweepAsync(default), stages, repeat >= 0).ConfigureAwait(false);
                await host.SaveAsync().ConfigureAwait(false);
                await MeasureAsync("sweepWithNotDueDefinition", () => runner.SweepAsync(default), stages, repeat >= 0).ConfigureAwait(false);
            }
            await MeasureAsync("restartRebuild", async () =>
            {
                host.Sqlite.Dispose();
                using var reopened = new SqliteSwpcStore(HVO.SkyMonitor.CameraAgent.Common.DependencyInjection.SpaceWeatherServiceCollectionExtensions.CreateStoreSettings(host.Options.Value),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteSwpcStore>.Instance);
                await reopened.InitializeAsync(default).ConfigureAwait(false);
                var view = await reopened.ReadAsync(host.Clock.GetUtcNow(), default).ConfigureAwait(false);
                Assert.AreEqual(SwpcHash.Sha256UpperHex(input), view.Products[SwpcProduct.Alerts].LastKnownGood!.Sha256);
            }, stages, repeat >= 0).ConfigureAwait(false);
        }
        using var day = new SpaceWeatherTestHost(); await day.InitializeAsync().ConfigureAwait(false);
        day.Client.Body = product => captures[product];
        var beforeDay = DatabaseBytes(day.Root);
        await MeasureAsync("simulatedDay120SecondTicks", async () =>
        {
            for (var tick = 0; tick < 720; tick++)
            {
                await day.RunAsync().ConfigureAwait(false);
                day.Clock.Advance(TimeSpan.FromSeconds(120));
            }
        }, stages).ConfigureAwait(false);
        var growth = DatabaseBytes(day.Root) - beforeDay;
        Assert.IsTrue(day.Client.Calls <= 1152, "Fixed cadence bounds a zero-jitter simulated day.");
        using var history = new SpaceWeatherTestHost(); await history.InitializeAsync().ConfigureAwait(false);
        history.Client.Body = product => captures[product];
        for (var date = 0; date < 35; date++)
        {
            await history.RunAsync().ConfigureAwait(false); history.Clock.Advance(TimeSpan.FromDays(1));
        }
        history.Clock.Advance(TimeSpan.FromDays(35));
        await MeasureAsync("retention35DayHistory", async () =>
        {
            var purged = await history.Store.ApplyRetentionAsync(history.Clock.GetUtcNow(), default).ConfigureAwait(false);
            Assert.IsTrue(purged.Revision >= 0);
            Assert.AreEqual(0L, history.Count("swpc_attempts"));
        }, stages).ConfigureAwait(false);
        await MeasureAsync("alerts2000Records", () =>
        {
            Assert.IsTrue(SwpcAlertsParser.Parse(maximumRecords, "application/json").Accepted);
            return Task.CompletedTask;
        }, stages).ConfigureAwait(false);
        await MeasureAsync("alerts2001RecordsRejected", () =>
        {
            Assert.AreEqual("too-many-records", SwpcAlertsParser.Parse(rejectedRecords, "application/json").RejectionReason);
            return Task.CompletedTask;
        }, stages).ConfigureAwait(false);
        var scenarios = LargestScenarioBodies();
        await MeasureAsync("largestCommittedScenarioBodies", () =>
        {
            foreach (var inputBody in scenarios)
            {
                var view = Policy.SwpcPolicyTestStoreDouble.EmptyView();
                var start = HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Policy.SwpcPolicy.Admit(view, inputBody.Product,
                    SwpcAttemptTrigger.Manual, "performance-scenario", Guid.NewGuid(), SpaceWeatherTestHost.Start).Start!;
                var response = new HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch.SwpcHttpResponse(inputBody.Product,
                    SpaceWeatherTestHost.Start, SpaceWeatherTestHost.Start, HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch.SwpcTransportFailure.None,
                    200, false, HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch.SwpcSource.MediaType(inputBody.Product), body: inputBody.Bytes);
                _ = HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Policy.SwpcPolicy.Classify(start, view.Products[inputBody.Product], response, SpaceWeatherTestHost.Start);
            }
            return Task.CompletedTask;
        }, stages).ConfigureAwait(false);
        await WriteAsync("space-weather-refresh-performance.json", new
        {
            environment = new { os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(), runtime = RuntimeInformation.FrameworkDescription, configuration = "Release", processors = Environment.ProcessorCount, network = false },
            workload = new { tickDecodedAlertsBytes = input.Length, tickSha256 = SwpcHash.Sha256UpperHex(input), maximumRecordsBytes = maximumRecords.Length, rejectedRecordsBytes = rejectedRecords.Length, largestCommittedScenarioBodies = scenarios.Select(static inputBody => new { product = inputBody.Product.Token(), inputBody.Name, bytes = inputBody.Bytes.Length, sha256 = SwpcHash.Sha256UpperHex(inputBody.Bytes) }), warmups = 1, tickRepeats = 5, dayTicks = 720, day.Client.Calls, historyDays = 35 },
            measurements = Summarize(stages),
            databaseGrowthBytesPerSimulatedDay = growth,
            provisionalCeilings = new { tickP95Ms = 50, allocationMultiplier = 8, workingSetDeltaBytes = 32 * 1024 * 1024, databaseGrowthBytesPerDay = 5 * 1024 * 1024 },
            result = "Offline composition measurement. Provisional ceilings are reported, never used to tune frozen storage or policy. Restart rebuild is a new store in the same process; OS page cache is retained. store_bytes telemetry is raw-body bytes; these evidence database sizes include SQLite and sidecars."
        }).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CapturePathWithAndWithoutConcurrentMaximumRateRefreshIsMeasured()
    {
        const int maximumFrames = 2048;
        for (var frameCount = 128; frameCount <= maximumFrames; frameCount *= 2)
        {
            var stages = new Dictionary<string, List<Sample>>(StringComparer.Ordinal);
            using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false);
            var captures = Enum.GetValues<SwpcProduct>().ToDictionary(static product => product, SpaceWeatherTestHost.TestClient.Capture);
            var alerts = LargeAlerts();
            var baseline = await CaptureSeriesAsync("captureBaseline", stages, frameCount).ConfigureAwait(false);
            using var stop = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ticks = new ConcurrentQueue<Tick>();
            var attempts = new ConcurrentQueue<ProviderAttempt>();
            List<string> tickProducts = [];
            host.Client.Fetch = (product, _) =>
            {
                // This stub is entered by the production runner after durable admission, inside RunAsync.
                var timestamp = Stopwatch.GetTimestamp();
                tickProducts.Add(product.Token());
                attempts.Enqueue(new(product.Token(), timestamp));
                started.TrySetResult();
                return ValueTask.FromResult(new HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch.SwpcHttpResponse(product,
                    host.Clock.GetUtcNow(), host.Clock.GetUtcNow(), HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch.SwpcTransportFailure.None,
                    200, false, HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch.SwpcSource.MediaType(product),
                    body: product == SwpcProduct.Alerts ? alerts : captures[product]));
            };
            var concurrent = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    tickProducts = [];
                    var start = Stopwatch.GetTimestamp();
                    await host.RunAsync(SwpcAttemptTrigger.Manual, stop.Token).ConfigureAwait(false);
                    ticks.Enqueue(new(start, Stopwatch.GetTimestamp(), tickProducts.ToArray()));
                    host.Clock.Advance(TimeSpan.FromSeconds(60));
                }
            });
            string[] after;
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                after = await CaptureSeriesAsync("captureWithMaximumRateRefresh", stages, frameCount).ConfigureAwait(false);
            }
            finally
            {
                await stop.CancelAsync().ConfigureAwait(false);
                try { await concurrent.ConfigureAwait(false); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
            CollectionAssert.AreEqual(baseline, after, "Identical capture inputs retain exact pixel checksums under refresh contention.");
            var frames = stages["captureWithMaximumRateRefresh"];
            var windowStart = frames[0].StartTimestamp;
            var windowEnd = frames[^1].EndTimestamp;
            var overlapped = ticks.Where(tick => tick.StartTimestamp >= windowStart && tick.EndTimestamp <= windowEnd).ToArray();
            foreach (var tick in overlapped)
                CollectionAssert.AreEquivalent(Enum.GetValues<SwpcProduct>().Select(static product => product.Token()).ToArray(), tick.Products,
                    "Each completely overlapping tick must attempt every configured product exactly once.");
            if (overlapped.Length < 5 && frameCount < maximumFrames)
            {
                TestContext.WriteLine($"{frameCount} equal-length frames yielded {overlapped.Length} complete ticks; doubling both series.");
                continue;
            }
            Assert.IsGreaterThanOrEqualTo(5, overlapped.Length,
                $"Fewer than five complete four-product ticks within {frameCount} measured frames; bounded maximum {maximumFrames} reached.");
            var seconds = Stopwatch.GetElapsedTime(windowStart, windowEnd).TotalSeconds;
            var providerAttempts = attempts.Count(attempt => attempt.Timestamp >= windowStart && attempt.Timestamp <= windowEnd);
            await WriteAsync("space-weather-capture-isolation.json", new
            {
                workload = "Real VirtualSky capture module, 1280x960 Mono16, fixed in-memory empty catalog, no sensor noise, one warmup per series; compressed manual ticks with four configured products and 1 MiB alerts. Equal frame counts double from 128 to at most 2048 until five complete ticks overlap.",
                measurements = Summarize(stages),
                captureChecksumsEqual = true,
                framesPerSeries = baseline.Length,
                maximumFramesPerSeries = maximumFrames,
                monotonicClockFrequency = Stopwatch.Frequency,
                measuredWindow = new { startTimestamp = windowStart, endTimestamp = windowEnd, seconds },
                ticksOverlapped = overlapped.Length,
                providerAttemptsDuringWindow = providerAttempts,
                ticksPerSecond = overlapped.Length / seconds,
                bytesPerTick = alerts.Length + captures.Where(static pair => pair.Key != SwpcProduct.Alerts).Sum(static pair => pair.Value.Length),
                refreshTicks = ticks.ToArray(),
                providerAttempts = attempts.ToArray(),
                networkRequests = 0,
                attribution = new
                {
                    baseline = CaptureAttribution(stages["captureBaseline"]),
                    concurrent = CaptureAttribution(frames),
                    processors = Environment.ProcessorCount,
                    sharedResources = "Process CPU, managed heap and GC. Capture owns its VirtualSky module, empty catalog and ProjectedSceneStore; it does not use the SWPC command/publication gates or SQLite connection. Monitor contention is process-wide, not a lock-attributed profile."
                },
                backlog = "No capture or processing slot is acquired by SWPC; one serialized refresh and the existing bounded automation worker.",
                result = "Only ticks fully inside the measured frame window count. Per-frame and per-tick monotonic timestamps prove overlap; process CPU, GC pauses, collections and monitor contention include refresh. No existing threshold is changed."
            }).ConfigureAwait(false);
            return;
        }
    }

    private static async Task<string[]> CaptureSeriesAsync(string name, Dictionary<string, List<Sample>> stages, int frameCount)
    {
        var clock = new Fetch.ManualTimeProvider(SpaceWeatherTestHost.Start);
        var module = new VirtualSkyCameraModule(clock, new InMemoryCelestialCatalog([]), new ProjectedSceneStore());
        var config = new CameraModuleConfig(new ObservatoryLocation(35.347, -113.878, 1000, "America/Phoenix"),
            new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(new { seed = 2025, maximumResults = 1, shotNoiseEnabled = false, darkCurrentElectronsPerSecond = 0d })),
            new CameraRigConfig(new SensorProfile("VirtualAsi174Mm", 1280, 960, 5.86, SensorColorMode.Mono, CameraPixelFormat.Mono16,
                SensorResponseMode.Monochrome, 2560, SensorRecipeVersion: "mono16-v1"),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye, 640, 480, 470, CalibrationVersion: "virtual-fisheye-180-equidistant-v1"),
                new RigOrientation(90, 0, 0), new PipelineExposureProfile(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 0, 0)), CapturePipelineConfig.Empty);
        await module.InitializeAsync(config, default).ConfigureAwait(false);
        var hashes = new List<string>();
        for (var index = -1; index < frameCount; index++)
        {
            await MeasureAsync(name, async () =>
            {
                var frame = (await module.CaptureAsync(new(SpaceWeatherTestHost.Start.AddSeconds((index + 1) * 5), TimeSpan.FromSeconds(5), CaptureMode.Still,
                    new(TimeSpan.FromSeconds(1), 0, null, null)), default).ConfigureAwait(false)).Frame!;
                Assert.AreEqual(1280 * 960 * 2, frame.PixelData.Length);
                if (index >= 0) hashes.Add(Convert.ToHexString(SHA256.HashData(frame.PixelData.Span)));
            }, stages, index >= 0).ConfigureAwait(false);
        }
        return hashes.ToArray();
    }

    private sealed record Tick(long StartTimestamp, long EndTimestamp, string[] Products);
    private sealed record ProviderAttempt(string Product, long Timestamp);

    private static object CaptureAttribution(List<Sample> samples)
    {
        var wall = Stopwatch.GetElapsedTime(samples[0].StartTimestamp, samples[^1].EndTimestamp).TotalMilliseconds;
        return new
        {
            wallMs = wall,
            processCpuMs = samples.Sum(static sample => sample.CpuMs),
            processCpuCores = samples.Sum(static sample => sample.CpuMs) / wall,
            gcPauseMs = samples.Sum(static sample => sample.GcPauseMs),
            gcCollections = Enumerable.Range(0, 3).Select(g => samples.Sum(sample => sample.Gc[g])).ToArray(),
            monitorLockContentions = samples.Sum(static sample => sample.MonitorLockContentions)
        };
    }

    private static byte[] LargeAlerts()
    {
        var raw = SpaceWeatherTestHost.TestClient.Capture(SwpcProduct.Alerts);
        var padded = new byte[1024 * 1024]; raw.CopyTo(padded, 0); padded.AsSpan(raw.Length).Fill((byte)' '); return padded;
    }
    private static ScenarioInput[] LargestScenarioBodies()
    {
        var pack = Path.Combine(AppContext.BaseDirectory, "Fixtures", "space-weather", "swpc-v1");
        var inputs = new List<ScenarioInput>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(pack, "scenarios"), "*.json"))
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            foreach (var step in json.RootElement.GetProperty("steps").EnumerateArray())
            {
                if (!step.TryGetProperty("response", out var response) || !step.TryGetProperty("product", out var token)) continue;
                var product = Enum.GetValues<SwpcProduct>().Cast<SwpcProduct?>().FirstOrDefault(p => p!.Value.Token() == token.GetString());
                if (product is null) continue;
                byte[]? bytes = response.TryGetProperty("body", out var body) ? Encoding.UTF8.GetBytes(body.GetString()!)
                    : response.TryGetProperty("bodyBase64", out var encoded) ? Convert.FromBase64String(encoded.GetString()!) : null;
                if (bytes is not null) inputs.Add(new(product.Value, Path.GetFileName(path), bytes));
            }
        }
        return inputs.GroupBy(static inputBody => inputBody.Product).Select(static group => group.MaxBy(static inputBody => inputBody.Bytes.Length)!).ToArray();
    }
    private sealed record ScenarioInput(SwpcProduct Product, string Name, byte[] Bytes);
    private static long DatabaseBytes(string root) => Directory.EnumerateFiles(root, "space-weather.db*", SearchOption.AllDirectories).Sum(static file => new FileInfo(file).Length);
    private static async Task MeasureAsync(string name, Func<Task> action, Dictionary<string, List<Sample>> stages, bool record = true)
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        var working = process.WorkingSet64; var cpu = Environment.CpuUsage.TotalTime; var allocated = GC.GetTotalAllocatedBytes(true);
        var gc = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var pause = GC.GetTotalPauseDuration(); var contention = Monitor.LockContentionCount;
        var start = Stopwatch.GetTimestamp();
        await action().ConfigureAwait(false);
        var end = Stopwatch.GetTimestamp(); var elapsed = Stopwatch.GetElapsedTime(start, end).TotalMilliseconds; process.Refresh();
        var sample = new Sample(elapsed, (Environment.CpuUsage.TotalTime - cpu).TotalMilliseconds, GC.GetTotalAllocatedBytes(true) - allocated,
            process.WorkingSet64 - working, Enumerable.Range(0, 3).Select(g => GC.CollectionCount(g) - gc[g]).ToArray(),
            start, end, (GC.GetTotalPauseDuration() - pause).TotalMilliseconds, Monitor.LockContentionCount - contention);
        if (record) { if (!stages.TryGetValue(name, out var samples)) stages[name] = samples = []; samples.Add(sample); }
    }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible", Justification = "The anonymous evidence projection is serialized immediately and has no public model.")]
    private static object Summarize(Dictionary<string, List<Sample>> stages) => stages.ToDictionary(static pair => pair.Key,
        static pair => new
        {
            count = pair.Value.Count,
            wallMs = Distribution(pair.Value.Select(static x => x.WallMs)),
            cpuMs = Distribution(pair.Value.Select(static x => x.CpuMs)),
            allocatedBytes = Distribution(pair.Value.Select(static x => (double)x.AllocatedBytes)),
            workingSetDeltaBytes = Distribution(pair.Value.Select(static x => (double)x.WorkingSetDeltaBytes)),
            gcCollections = Enumerable.Range(0, 3).Select(g => pair.Value.Sum(s => s.Gc[g])).ToArray(),
            gcPauseMs = pair.Value.Sum(static sample => sample.GcPauseMs),
            monitorLockContentions = pair.Value.Sum(static sample => sample.MonitorLockContentions),
            samples = pair.Value
        });
    private static object Distribution(IEnumerable<double> samples)
    {
        var values = samples.Order().ToArray(); return new { median = values[(values.Length - 1) / 2], p95 = values[(int)Math.Ceiling(values.Length * .95) - 1], maximum = values[^1] };
    }
    private async Task WriteAsync(string file, object evidence)
    {
        var root = Environment.GetEnvironmentVariable("HVO_ISSUE1398_EVIDENCE_ROOT") ?? Path.Combine("TestResults", "issue-1398");
        Directory.CreateDirectory(root); var path = Path.Combine(root, file);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence, JsonOptions)).ConfigureAwait(false); TestContext.AddResultFile(path);
    }
    private sealed record Sample(double WallMs, double CpuMs, long AllocatedBytes, long WorkingSetDeltaBytes, int[] Gc,
        long StartTimestamp, long EndTimestamp, double GcPauseMs, long MonitorLockContentions);
}
