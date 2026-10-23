using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Configured disposal would hide the strongly typed fixture.")]
public sealed class InstallationVerificationReadTests
{
    private static readonly ActivitySource ReadSource = new("HVO.Issue1559");
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Integration")]
    [SuppressMessage("Reliability", "CA2025:Do not pass IDisposable instances into unawaited tasks", Justification = "The deliberately delayed read is always released and awaited in finally before client/fixture disposal.")]
    public async Task RepeatedDelayedAndConcurrentReadsObserveActualEditsAndRejectInvalidState()
    {
        await using var fixture = await InstallationReadAttributionFixture.CreateAsync(Environment.GetEnvironmentVariable("HVO_INSTALLATION_READ_EVIDENCE_DIR"), solar: false).ConfigureAwait(false);
        _ = await fixture.SeedAsync(2, 2).ConfigureAwait(false);
        using var client = fixture.CreateClient();
        using var stages = new InstallationReadStageCollector();
        using (var deniedTrace = StartTrace("denied")!)
        {
            foreach (var token in new string?[] { null, "incorrect-token" })
            {
                using var deniedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/internal/owner-bootstrap/installation-verification");
                if (token is not null) deniedRequest.Headers.Add("X-HVO-Installation-Token", token);
                deniedRequest.Headers.Add("traceparent", deniedTrace.Id);
                using var denied = await client.SendAsync(deniedRequest).ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            }
            Assert.AreEqual(0, stages.ForTrace(deniedTrace.TraceId).Length, "Unauthenticated requests must not reach the loader.");
        }
        var first = await fixture.ReadAsync(client).ConfigureAwait(false);
        Assert.AreEqual(OwnerBootstrapStates.TemporaryPassword, first.GetProperty("ownerBootstrapState").GetString());
        for (var index = 0; index < 3; index++)
        {
            using var trace = StartTrace("repeat")!;
            var repeated = await fixture.ReadAsync(client).ConfigureAwait(false);
            Assert.AreEqual(first.GetRawText(), repeated.GetRawText());
            Assert.AreEqual(2, stages.ForTrace(trace.TraceId).Count(stage => stage.Name == "configuration.schedule-expand-366-days"));
            Assert.AreEqual(1, stages.ForTrace(trace.TraceId).Count(stage => stage.Name == "deployment-location.validate-retained-journal"));
        }
        fixture.Loader.DelayNextRead();
        var entered = fixture.Loader.Entered;
        var pending = fixture.ReadAsync(client);
        JsonElement changed;
        try
        {
            await entered.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Assert.IsFalse(pending.IsCompleted, "The controlled delayed read must still be waiting.");
            await fixture.EditAsync().ConfigureAwait(false);
        }
        finally
        {
            fixture.Loader.Release();
            changed = await pending.ConfigureAwait(false);
        }
        Assert.AreEqual("issue1559-edited-current-agent", changed.GetProperty("agentId").GetString());
        Assert.AreNotEqual(first.GetProperty("configurationSha256").GetString(), changed.GetProperty("configurationSha256").GetString());
        Assert.AreNotEqual(first.GetProperty("scheduleSha256").GetString(), changed.GetProperty("scheduleSha256").GetString());
        using (var config = JsonDocument.Parse(await File.ReadAllBytesAsync(fixture.ConfigPath).ConfigureAwait(false)))
        {
            Assert.AreEqual(CaptureContractJson.ComputeCanonicalJsonSha256(config.RootElement), changed.GetProperty("configurationSha256").GetString());
        }
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.ReadAsync(client))).ConfigureAwait(false);
        Assert.IsTrue(concurrent.All(value => value.GetRawText() == changed.GetRawText()));

        var manual = fixture.Locations.Manual;
        var locationChange = await fixture.Locations.ApplyManualAsync(new ManualDeploymentLocationRequest(
            35.6, -113.9, 600, "America/Phoenix", manual.KnownVersion, manual.ManualSequence,
            "issue-1559-change", "disposable-test", "Actual retained location edit"), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ManualDeploymentLocationStatus.Applied, locationChange.Status);
        var relocated = await fixture.ReadAsync(client).ConfigureAwait(false);
        Assert.IsGreaterThan(changed.GetProperty("deploymentLocationVersion").GetInt64(), relocated.GetProperty("deploymentLocationVersion").GetInt64());
        Assert.AreNotEqual(changed.GetProperty("deploymentLocationSha256").GetString(), relocated.GetProperty("deploymentLocationSha256").GetString());
        Assert.AreEqual(changed.GetProperty("configurationSha256").GetString(), relocated.GetProperty("configurationSha256").GetString());

        // Both retained-file and journal edits must be inspected again after successful reads.
        var manifestPath = Path.Combine(fixture.IngressRoot, "fixtures", "00001.json");
        var originalManifest = await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false);
        var altered = JsonNode.Parse(originalManifest)!;
        altered["descriptor"]!["location"]!["version"] = 999;
        await File.WriteAllTextAsync(manifestPath, altered.ToJsonString()).ConfigureAwait(false);
        await AssertRejectedAsync(fixture, client).ConfigureAwait(false);
        await File.WriteAllBytesAsync(manifestPath, originalManifest).ConfigureAwait(false);
        _ = await fixture.ReadAsync(client).ConfigureAwait(false);
        using (var connection = new SqliteConnection($"Data Source={fixture.JournalPath};Pooling=False"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT manifest_json FROM raw_captures ORDER BY capture_sequence LIMIT 1;";
            var originalJournalManifest = (byte[])(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
            command.CommandText = "UPDATE raw_captures SET manifest_json=$manifest WHERE capture_sequence=3;";
            command.Parameters.AddWithValue("$manifest", System.Text.Encoding.UTF8.GetBytes(altered.ToJsonString()));
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            await AssertRejectedAsync(fixture, client).ConfigureAwait(false);
            command.Parameters["$manifest"].Value = originalJournalManifest;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        _ = await fixture.ReadAsync(client).ConfigureAwait(false);
        // Unsupported current configuration must also fail before a previously validated identity can escape.
        var configNode = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ConfigPath).ConfigureAwait(false))!;
        configNode["pipeline"]!["schemaVersion"] = "future-unsupported";
        await File.WriteAllTextAsync(fixture.ConfigPath, configNode.ToJsonString()).ConfigureAwait(false);
        await AssertRejectedAsync(fixture, client).ConfigureAwait(false);
        configNode["pipeline"]!["schemaVersion"] = "cameraagent-capture-pipeline-v2";
        await File.WriteAllTextAsync(fixture.ConfigPath, configNode.ToJsonString()).ConfigureAwait(false);
        _ = await fixture.ReadAsync(client).ConfigureAwait(false);
        var statePath = Path.Combine(fixture.IngressRoot, ".location", "deployment-location.v1.protected");
        var protectedHistory = await File.ReadAllBytesAsync(statePath).ConfigureAwait(false);
        var protector = fixture.Host.Services.GetRequiredService<IDeploymentLocationProtector>();
        var history = JsonNode.Parse(protector.Unprotect(protectedHistory))!;
        history["schemaVersion"] = 999;
        await File.WriteAllBytesAsync(statePath, protector.Protect(System.Text.Encoding.UTF8.GetBytes(history.ToJsonString()))).ConfigureAwait(false);
        await AssertRejectedAsync(fixture, client).ConfigureAwait(false);
        await File.WriteAllBytesAsync(statePath, protectedHistory).ConfigureAwait(false);
        _ = await fixture.ReadAsync(client).ConfigureAwait(false);
    }

    [TestMethod]
    [TestCategory("Manual")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each loop iteration owns await using fixture and using client/listener declarations, disposed on every exit.")]
    public async Task ConstrainedDisposableReadsRecordStageAttribution()
    {
        var output = Environment.GetEnvironmentVariable("HVO_INSTALLATION_READ_EVIDENCE_DIR");
        Assert.IsFalse(string.IsNullOrWhiteSpace(output), InstallationReadAttributionFixture.EvidenceRootGuidance);
        InstallationReadAttributionFixture.ValidateEvidenceRoot(output);
        Directory.CreateDirectory(output);
        var workloads = new List<object>();
        foreach (var solar in new[] { false, true })
        {
            await using var fixture = await InstallationReadAttributionFixture.CreateAsync(output, solar).ConfigureAwait(false);
            using var client = fixture.CreateClient();
            var samples = new List<ReadMeasurement>
            {
                await MeasureAsync("cold-host-empty", fixture, client, null).ConfigureAwait(false)
            };
            var identity = await fixture.SeedAsync(5000, 5000).ConfigureAwait(false);
            var retainedBefore = await fixture.HashRetainedInputsAsync().ConfigureAwait(false);
            // First protected HTTP read after fixture setup; startup/seed loading and filesystem cache are already warm.
            samples.Add(await MeasureAsync("first-http-no-listener", fixture, client, null).ConfigureAwait(false));
            for (var index = 0; index < 5; index++)
                samples.Add(await MeasureAsync("repeat-no-listener", fixture, client, null).ConfigureAwait(false));
            using var stages = new InstallationReadStageCollector();
            samples.Add(await MeasureAsync("first-instrumented", fixture, client, stages).ConfigureAwait(false));
            for (var index = 0; index < 5; index++) _ = await fixture.ReadAsync(client).ConfigureAwait(false);
            for (var index = 0; index < 10; index++)
                samples.Add(await MeasureAsync("repeat", fixture, client, stages).ConfigureAwait(false));
            for (var batch = 0; batch < 2; batch++)
            {
                // Entire batch has its own allocation/CPU counters; individual overlapping stage deltas are not additive.
                using var trace = StartTrace("concurrent-batch")!;
                using var process = Process.GetCurrentProcess();
                var allocated = GC.GetTotalAllocatedBytes(true);
                var cpu = process.TotalProcessorTime;
                var io = ReadIo();
                var started = Stopwatch.GetTimestamp();
                var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.ReadAsync(client))).ConfigureAwait(false);
                Assert.IsTrue(responses.All(value => value.GetRawText() == responses[0].GetRawText()));
                samples.Add(new ReadMeasurement("concurrent-4", Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    (process.TotalProcessorTime - cpu).TotalMilliseconds, GC.GetTotalAllocatedBytes(true) - allocated,
                    process.WorkingSet64, process.PeakWorkingSet64, IoDelta(io), responses[0], stages.ForTrace(trace.TraceId)));
            }
            var retainedAfter = await fixture.HashRetainedInputsAsync().ConfigureAwait(false);
            Assert.AreEqual(retainedBefore, retainedAfter, "Repeated and concurrent reads must leave retained inputs unchanged.");
            var before = samples.Single(sample => sample.Kind == "first-instrumented").Identity;
            await fixture.EditAsync().ConfigureAwait(false);
            samples.Add(await MeasureAsync("config-change", fixture, client, stages).ConfigureAwait(false));
            var manual = fixture.Locations.Manual;
            var result = await fixture.Locations.ApplyManualAsync(new ManualDeploymentLocationRequest(
                35.6, -113.9, 600, "America/Phoenix", manual.KnownVersion, manual.ManualSequence,
                "issue-1559-measured", "disposable-measurement", "Measured location change"), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ManualDeploymentLocationStatus.Applied, result.Status);
            samples.Add(await MeasureAsync("location-change", fixture, client, stages).ConfigureAwait(false));
            var after = samples[^1].Identity;
            Assert.AreNotEqual(before.GetProperty("configurationSha256").GetString(), after.GetProperty("configurationSha256").GetString());
            Assert.AreNotEqual(before.GetProperty("deploymentLocationSha256").GetString(), after.GetProperty("deploymentLocationSha256").GetString());
            workloads.Add(new { Schedule = solar ? "solar-astronomical-night" : "fixed-all-day", Fixture = identity, RetainedBeforeSha256 = retainedBefore, RetainedAfterSha256 = retainedAfter, Samples = samples });
        }
        var evidence = new
        {
            Schema = "issue-1559-attribution-v1",
            SourceRevision = Environment.GetEnvironmentVariable("HVO_ATTRIBUTION_REVISION"),
            TimestampUtc = DateTimeOffset.UtcNow,
            RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount,
            HeapHardLimit = Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit"),
            HeapAvailableBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            ServerGC = System.Runtime.GCSettings.IsServerGC,
            CpuAffinity = ReadCpuAffinity(),
            AllocationSemantics = "Process-wide inclusive deltas; stage nesting and concurrent intervals overlap. No additive per-request allocation claim.",
            ColdDefinition = "cold-host-empty: first protected request on each fresh host before loader/retained fixture setup. first-http-no-listener: first request with 10,000 retained manifests after setup; filesystem caches and loader JIT are warm. Neither drops OS caches.",
            Workloads = workloads
        };
        var path = Path.Combine(output, "installation-read-attribution.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence, InstallationReadAttributionFixture.EvidenceJson)).ConfigureAwait(false);
        TestContext.AddResultFile(path);
    }

    private static Activity? StartTrace(string name) => ReadSource.StartActivity(name, ActivityKind.Internal,
        new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded));

    private static async Task<ReadMeasurement> MeasureAsync(string kind, InstallationReadAttributionFixture fixture,
        HttpClient client, InstallationReadStageCollector? stages)
    {
        using var trace = StartTrace("installation-read");
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var allocated = GC.GetTotalAllocatedBytes(true);
        var io = ReadIo();
        var started = Stopwatch.GetTimestamp();
        var identity = await fixture.ReadAsync(client).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return new ReadMeasurement(kind, elapsed, (process.TotalProcessorTime - cpu).TotalMilliseconds,
            GC.GetTotalAllocatedBytes(true) - allocated, process.WorkingSet64, process.PeakWorkingSet64,
            IoDelta(io), identity, trace is null || stages is null ? [] : stages.ForTrace(trace.TraceId));
    }

    private static string ReadCpuAffinity() => OperatingSystem.IsLinux()
        ? File.ReadLines("/proc/self/status").Single(line => line.StartsWith("Cpus_allowed_list:", StringComparison.Ordinal))
        : "unavailable";

    private static Dictionary<string, long> ReadIo()
        => OperatingSystem.IsLinux()
            ? File.ReadLines("/proc/self/io").Select(line => line.Split(':', 2, StringSplitOptions.TrimEntries))
                .ToDictionary(parts => parts[0], parts => long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture))
            : [];

    private static Dictionary<string, long> IoDelta(Dictionary<string, long> before)
        => ReadIo().ToDictionary(pair => pair.Key, pair => pair.Value - before[pair.Key]);

    private static async Task AssertRejectedAsync(InstallationReadAttributionFixture fixture, HttpClient client)
    {
        Assert.AreEqual(fixture.Host.BaseAddress, client.BaseAddress);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/internal/owner-bootstrap/installation-verification");
        request.Headers.Add("X-HVO-Installation-Token", CameraAgentKestrelFixture.InstallationVerificationToken);
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}

internal sealed record ReadMeasurement(string Kind, double ElapsedMs, double CpuMs, long ProcessAllocatedBytes,
    long WorkingSetBytes, long PeakWorkingSetBytes, Dictionary<string, long> IoDelta, JsonElement Identity, InstallationReadStage[] Stages);
