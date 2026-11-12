using System.Diagnostics;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

public sealed partial class InstallationVerificationReadTests
{
    [TestMethod]
    [TestCategory("Integration")]
    [DataRow("journal-type")]
    [DataRow("quarantine-torn")]
    [DataRow("quarantine-schema")]
    [DataRow("raw-torn")]
    public async Task CorruptRecentOrQuarantinedEvidenceKeepsStartupAndRecoveryAvailable(string fault)
    {
        var now = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        await using var fixture = await InstallationReadAttributionFixture.CreateAsync(
            Environment.GetEnvironmentVariable("HVO_INSTALLATION_READ_EVIDENCE_DIR") ?? Path.GetTempPath(), solar: false,
            completeOwner: true, startupServices: true, clock: new StartupClock(now)).ConfigureAwait(false);
        _ = await fixture.SeedProductionStartupAsync(1, now).ConfigureAwait(false);
        await fixture.Host.RunWithHostStoppedAsync(async stoppedRoot =>
        {
            if (fault == "journal-type")
            {
                using var connection = new SqliteConnection($"Data Source={fixture.JournalPath};Pooling=False");
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                // Deliberate fixture-only schema tamper: a genuine strict v15
                // table correctly refuses the malformed value at write time.
                command.CommandText = "PRAGMA writable_schema=ON; UPDATE sqlite_schema SET sql=replace(sql,' STRICT','') WHERE type='table' AND name='raw_captures'; PRAGMA writable_schema=OFF;";
                _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                await connection.CloseAsync().ConfigureAwait(false);
                await connection.OpenAsync().ConfigureAwait(false);
                command.CommandText = "UPDATE raw_captures SET manifest_json='{}';";
                _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            else
            {
                var path = fault == "raw-torn"
                    ? Directory.EnumerateFiles(Path.Combine(fixture.IngressRoot, "frames"), "*.json", SearchOption.AllDirectories)
                        .Single(path => Path.GetFileName(Path.GetDirectoryName(path)) == "Raw")
                    : Path.Combine(fixture.IngressRoot, "quarantine", "evidence.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, fault == "quarantine-schema" ? "{\"schemaVersion\":\"v2\"}" : "{").ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        using var client = fixture.CreateClient();
        using var alive = await client.GetAsync(new Uri("/alive", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(System.Net.HttpStatusCode.OK, alive.StatusCode);
        using var owner = await fixture.Host.CreateOwnerClientAsync().ConfigureAwait(false);
        using var recovery = await owner.GetAsync(new Uri("/system", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(System.Net.HttpStatusCode.OK, recovery.StatusCode);
        using var refused = new HttpRequestMessage(HttpMethod.Get, "/api/internal/owner-bootstrap/installation-verification");
        refused.Headers.Add("X-HVO-Installation-Token", CameraAgentKestrelFixture.InstallationVerificationToken);
        using var response = await client.SendAsync(refused).ConfigureAwait(false);
        Assert.AreEqual(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [TestMethod]
    [TestCategory("Manual")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each successfully constructed host is disposed by await using.")]
    [SuppressMessage("Reliability", "CA2025:Do not pass IDisposable instances into unawaited tasks", Justification = "Startup tasks are always stopped and drained before fixture disposal.")]
    public async Task ProductionFanoutStartupAndNativeInstallerHealthCampaign()
    {
        var output = Environment.GetEnvironmentVariable("HVO_INSTALLATION_READ_EVIDENCE_DIR")
            ?? throw new InvalidOperationException(InstallationReadAttributionFixture.EvidenceRootGuidance);
        var mode = Environment.GetEnvironmentVariable("HVO_STARTUP_MODE") ?? "candidate";
        var startupOnly = Environment.GetEnvironmentVariable("HVO_STARTUP_ONLY") == "1";
        var counts = (Environment.GetEnvironmentVariable("HVO_STARTUP_COUNTS") ?? "45000,129600")
            .Split(',').Select(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.IsTrue(counts.All(value => value > 0 && value <= 129_600));
        var now = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        var cases = new List<object>();
        foreach (var count in counts)
        {
            using var recent = new RecentStartupMeasurements();
            await using var fixture = await InstallationReadAttributionFixture.CreateAsync(output, solar: true,
                completeOwner: true, startupServices: true, clock: new StartupClock(now)).ConfigureAwait(false);
            fixture.Host.PreservePortOnRestart();
            using var healthReader = fixture.CreateClient();
            using var response = await healthReader.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
            using var initial = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var catalog = initial.RootElement.GetProperty("checks").EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == "catalog").GetProperty("data").Clone();
            var native = new NativeStartupClient(fixture.Host.BaseAddress, catalog);
            var starts = new List<object>();
            async Task<bool> StartAsync(bool construct)
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var timer = new Stopwatch();
                Task<(bool Success, double ElapsedMs)>? health = null;
                long allocated = 0;
                var recentBefore = recent.Windows.Count;
                using var process = Process.GetCurrentProcess();
                var cpu = TimeSpan.Zero;
                var io = ReadIo();
                void BeforeStart()
                {
                    allocated = GC.GetTotalAllocatedBytes(true);
                    cpu = process.TotalProcessorTime;
                    io = ReadIo();
                    timer.Start();
                    health = native.WaitForHealthAsync();
                    entered.TrySetResult();
                }
                Task startup = construct
                    ? Task.Run(async () => _ = await fixture.SeedProductionStartupAsync(count, now, BeforeStart).ConfigureAwait(false))
                    : Task.Run(async () => await fixture.Host.RunWithHostStoppedAsync(_ => { BeforeStart(); return Task.CompletedTask; }).ConfigureAwait(false));
                try
                {
                    _ = await Task.WhenAny(entered.Task, startup).ConfigureAwait(false);
                    if (startup.IsFaulted && mode != "reviewed-baseline") await startup.ConfigureAwait(false);
                    await entered.Task.WaitAsync(TimeSpan.FromMinutes(30)).ConfigureAwait(false);
                    _ = await Task.WhenAny(startup, health!).ConfigureAwait(false);
                    if (startup.IsFaulted && mode != "reviewed-baseline") await startup.ConfigureAwait(false);
                    var ready = await health!.ConfigureAwait(false);
                    if (!ready.Success)
                    {
                        Assert.AreEqual("reviewed-baseline", mode, "The candidate must pass the actual three-minute installer health wait.");
                        // Preserve the observed lower bound; do not spend unbounded time
                        // completing a startup the real installer already rejected.
                        fixture.StopStartupForBudgetProbe();
                        try { await startup.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
                        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException) { }
                        starts.Add(new
                        {
                            Kind = construct ? "legacy-adoption" : "restart",
                            Healthy = false,
                            TimeToHealthyLowerBoundMs = ready.ElapsedMs,
                            HealthBudgetMs = 180_000,
                            ProtectCalls = fixture.StartupProtection?.Protects,
                            UnprotectCalls = fixture.StartupProtection?.Unprotects
                        });
                        return false;
                    }
                    await startup.ConfigureAwait(false);
                    var hostStartedMs = timer.Elapsed.TotalMilliseconds;
                    using var readinessResponse = await healthReader.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
                    using var readiness = JsonDocument.Parse(await readinessResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
                    var healthStatus = readiness.RootElement.GetProperty("status").GetString();
                    using var probe = new HttpRequestMessage(HttpMethod.Get, "/api/internal/owner-bootstrap/installation-verification");
                    probe.Headers.Add("X-HVO-Installation-Token", CameraAgentKestrelFixture.InstallationVerificationToken);
                    using var pending = await healthReader.SendAsync(probe).ConfigureAwait(false);
                    var qualificationAtHealth = pending.IsSuccessStatusCode ? "qualified" : pending.Headers.GetValues("X-HVO-Installation-Verification").Single();
                    Assert.IsTrue(qualificationAtHealth is "qualified" or "scrub-pending");
                    if (startupOnly)
                    {
                        starts.Add(new
                        {
                            Kind = construct ? "legacy-adoption" : "restart",
                            Healthy = true,
                            TimeToHealthyMs = ready.ElapsedMs,
                            HealthStatusAtInstallerAcceptance = healthStatus,
                            QualificationAtHealth = qualificationAtHealth,
                            RecentWindows = recent.Windows.Skip(recentBefore).ToArray()
                        });
                        return true;
                    }
                    var protectedStart = Stopwatch.GetTimestamp();
                    var timedOutPending = false;
                    try
                    {
                        var state = await native.ReadInstallationStateAsync().ConfigureAwait(false);
                        Assert.IsFalse(string.IsNullOrEmpty(state));
                    }
                    catch (Exception exception) when (mode == "candidate" && exception.GetType().Name == "InstallerException" &&
                        exception.Message == "CameraAgent installation verification request timed out.")
                    {
                        // A healthy recent window is not whole-corpus admission.
                        // The actual client must refuse after its unchanged finite
                        // budget; the host's background work survives that caller.
                        timedOutPending = true;
                    }
                    var protectedMs = Stopwatch.GetElapsedTime(protectedStart).TotalMilliseconds;
#if !HVO_BASELINE
                    if (timedOutPending)
                    {
                        Assert.IsGreaterThanOrEqualTo(119_000d, protectedMs);
                        _ = await fixture.Loader.LoadForRetainedIntegrityScrubAsync(CancellationToken.None)
                            .WaitAsync(TimeSpan.FromMinutes(30)).ConfigureAwait(false);
                        Assert.IsFalse(string.IsNullOrEmpty(await native.ReadInstallationStateAsync().ConfigureAwait(false)));
                    }
#endif
                    process.Refresh();
                    Assert.IsTrue(process.PeakWorkingSet64 < 4L * 1024 * 1024 * 1024,
                        "The fixture and host together must stay below the product's four-GiB memory limit.");
                    starts.Add(new
                    {
                        Kind = construct ? "legacy-adoption" : "restart",
                        Healthy = true,
                        HostStartedMs = hostStartedMs,
                        TimeToHealthyMs = ready.ElapsedMs,
                        HealthStatusAtInstallerAcceptance = healthStatus,
                        QualificationAtHealth = qualificationAtHealth,
                        RecentWindows = recent.Windows.Skip(recentBefore).ToArray(),
                        ProtectedReadMs = protectedMs,
                        ProtectedReadTimedOutPending = timedOutPending,
                        FullyQualifiedMs = timer.Elapsed.TotalMilliseconds,
                        ProtectedBudgetMs = 120_000,
                        CpuMsThroughQualification = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                        AllocatedBytesThroughQualification = GC.GetTotalAllocatedBytes(true) - allocated,
                        process.WorkingSet64,
                        process.PeakWorkingSet64,
                        IoDelta = IoDelta(io),
                        ProtectCalls = fixture.StartupProtection?.Protects,
                        UnprotectCalls = fixture.StartupProtection?.Unprotects
                    });
                    return true;
                }
                finally
                {
                    if (!startup.IsCompleted)
                    {
                        fixture.StopStartupForBudgetProbe();
                        await startup.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                    }
                }
            }
            var completed = await StartAsync(construct: true).ConfigureAwait(false);
            string? before = null;
            if (completed)
            {
                if (!startupOnly) before = await fixture.CaptureInputCommitmentAsync().ConfigureAwait(false);
                _ = await StartAsync(construct: false).ConfigureAwait(false);
                if (!startupOnly) Assert.AreEqual(before, await fixture.CaptureInputCommitmentAsync().ConfigureAwait(false), "Startup must not mutate retained capture metadata.");
            }
            cases.Add(new { Corpus = fixture.LastProductionCorpus, Starts = starts, RetainedCommitment = before });
            await File.WriteAllTextAsync(Path.Combine(output, "installation-startup-scale.json"), JsonSerializer.Serialize(new
            {
                Revision = Environment.GetEnvironmentVariable("HVO_ATTRIBUTION_REVISION"),
                Mode = mode,
                StartupOnly = startupOnly,
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                ProcessorCount = Environment.ProcessorCount,
                HeapAvailableBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                Cases = cases
            }, InstallationReadAttributionFixture.EvidenceJson)).ConfigureAwait(false);
        }
    }

    // Sample one aggregate startup span, never the full scrub's per-file stages.
    // Instrumentation stays constant as the older retained cohort grows.
    private sealed class RecentStartupMeasurements : IDisposable
    {
        private readonly ActivityListener _listener;
        internal ConcurrentQueue<RecentWindow> Windows { get; } = new();
        internal RecentStartupMeasurements()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == DeploymentLocationTelemetry.AttributionActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> options) => options.Name == "deployment-location.validate-startup-recent"
                    ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
                SampleUsingParentId = (ref ActivityCreationOptions<string> options) => options.Name == "deployment-location.validate-startup-recent"
                    ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
                ActivityStopped = activity => Windows.Enqueue(new RecentWindow(activity.Duration.TotalMilliseconds,
                    Convert.ToInt32(activity.GetTagItem("startup.recent.entries"), System.Globalization.CultureInfo.InvariantCulture),
                    Convert.ToInt64(activity.GetTagItem("startup.recent.bytes"), System.Globalization.CultureInfo.InvariantCulture)))
            };
            ActivitySource.AddActivityListener(_listener);
        }
        public void Dispose() => _listener.Dispose();
    }

    private sealed record RecentWindow(double ElapsedMs, int Entries, long Bytes);

    private sealed class StartupClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // Uses the actual installer HTTP client without introducing an Acceptance ->
    // CLI project dependency or replacing its three-minute/120-second budgets.
    private sealed class NativeStartupClient
    {
        private readonly object _client;
        private readonly object _catalog;
        private readonly MethodInfo _health;
        private readonly MethodInfo _read;
        internal NativeStartupClient(Uri address, JsonElement data)
        {
            var directory = Environment.GetEnvironmentVariable("HVO_STARTUP_CLI_DIRECTORY")
                ?? throw new InvalidOperationException("Provide the locally built installer client directory.");
            var assembly = Assembly.LoadFrom(Path.Combine(directory, "HVO.SkyMonitor.Deployment.Cli.dll"));
            var client = assembly.GetType("HVO.SkyMonitor.Deployment.OwnerBootstrapClient", throwOnError: true)!;
            _client = Activator.CreateInstance(client, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, [address, null, null], null)!;
            var contract = assembly.GetReferencedAssemblies().Single(item => item.Name == "HVO.SkyMonitor.Deployment.Contracts");
            var catalogType = Assembly.Load(contract).GetType("HVO.SkyMonitor.Deployment.Contracts.ApprovedCatalogContract", throwOnError: true)!;
            var package = data.TryGetProperty("PackageVersion", out var version) ? version.GetString()! : "fixture-1";
            var prefix = package.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            _catalog = Activator.CreateInstance(catalogType,
                [data.GetProperty("CatalogId").GetString()!, "fixture", prefix, "fixture", data.GetProperty("CatalogVersion").GetString()!,
                 1, data.GetProperty("SchemaVersion").GetString()!, data.GetProperty("PreprocessingVersion").GetString()!,
                 "catalog.sqlite", new string('0', 64), 1L, data.GetProperty("RowCount").GetInt64(), "fixture", "LICENSE", "NOTICE", "fixture", new string('0', 64)])!;
            _health = client.GetMethod("WaitForHealthAsync")!;
            _read = client.GetMethod("ReadInstallationStateAsync", [typeof(string), typeof(CancellationToken)])!;
        }
        internal async Task<(bool Success, double ElapsedMs)> WaitForHealthAsync()
        {
            var timer = Stopwatch.GetTimestamp();
            try
            {
                await ((Task)_health.Invoke(_client, [_catalog, CancellationToken.None, null, false])!).ConfigureAwait(false);
                return (true, Stopwatch.GetElapsedTime(timer).TotalMilliseconds);
            }
            catch (Exception exception) when (exception.GetType().Name == "InstallerException")
            {
                return (false, Stopwatch.GetElapsedTime(timer).TotalMilliseconds);
            }
        }
        internal Task<string> ReadInstallationStateAsync()
            => (Task<string>)_read.Invoke(_client, [CameraAgentKestrelFixture.InstallationVerificationToken, CancellationToken.None])!;
    }
}
