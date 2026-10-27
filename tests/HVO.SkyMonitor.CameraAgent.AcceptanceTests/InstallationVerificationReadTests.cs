using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
        var corpus = await fixture.SeedAsync(2, 2).ConfigureAwait(false);
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
            var recorded = stages.ForTrace(trace.TraceId);
            Assert.AreEqual(1, recorded.Count(stage => stage.Name == "configuration.schedule-expand-366-days"));
            AssertRetainedAttribution(recorded, corpus);
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
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => MeasureAsync("concurrent-regression", fixture, client, stages))).ConfigureAwait(false);
        Assert.IsTrue(concurrent.All(value => value.Identity.GetRawText() == changed.GetRawText()));
        foreach (var read in concurrent) AssertRetainedAttribution(read.Stages, corpus);

        var manual = fixture.Locations.Manual;
        var locationChange = await fixture.Locations.ApplyManualAsync(new ManualDeploymentLocationRequest(
            35.6, -113.9, 600, "America/Phoenix", manual.KnownVersion, manual.ManualSequence,
            "issue-1559-change", "disposable-test", "Actual retained location edit"), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ManualDeploymentLocationStatus.Applied, locationChange.Status);
        using var relocatedTrace = StartTrace("manual-location-change")!;
        var relocated = await fixture.ReadAsync(client).ConfigureAwait(false);
        Assert.AreEqual(2, stages.ForTrace(relocatedTrace.TraceId).Count(stage => stage.Name == "configuration.schedule-expand-366-days"));
        AssertProtectedRead(stages.ForTrace(relocatedTrace.TraceId).Single(stage => stage.Name == "deployment-location.read-protected-manual"));
        Assert.IsGreaterThan(changed.GetProperty("deploymentLocationVersion").GetInt64(), relocated.GetProperty("deploymentLocationVersion").GetInt64());
        Assert.AreNotEqual(changed.GetProperty("deploymentLocationSha256").GetString(), relocated.GetProperty("deploymentLocationSha256").GetString());
        Assert.AreEqual(changed.GetProperty("configurationSha256").GetString(), relocated.GetProperty("configurationSha256").GetString());

        // Both retained-file and journal edits must be inspected again after successful reads.
        var manifestPath = Path.Combine(fixture.IngressRoot, "fixtures", "00001.json");
        var originalManifest = await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false);
        var altered = JsonNode.Parse(originalManifest)!;
        altered["descriptor"]!["location"]!["version"] = 999;
        await File.WriteAllTextAsync(manifestPath, altered.ToJsonString()).ConfigureAwait(false);
        var rejectedFile = await AssertRejectedAsync(fixture, client, stages).ConfigureAwait(false);
        Assert.IsTrue(rejectedFile.Any(stage => stage.Name == "deployment-location.validate-retained-files"));
        Assert.IsFalse(rejectedFile.Any(stage => stage.Name == "deployment-location.validate-retained-journal"), "File rejection precedes journal inspection.");
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
            var rejectedJournal = await AssertRejectedAsync(fixture, client, stages).ConfigureAwait(false);
            Assert.IsTrue(rejectedJournal.Any(stage => stage.Name == "deployment-location.validate-retained-journal"));
            command.Parameters["$manifest"].Value = originalJournalManifest;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        _ = await fixture.ReadAsync(client).ConfigureAwait(false);
        // Unsupported current configuration must also fail before a previously validated identity can escape.
        var configNode = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ConfigPath).ConfigureAwait(false))!;
        configNode["pipeline"]!["schemaVersion"] = "future-unsupported";
        await File.WriteAllTextAsync(fixture.ConfigPath, configNode.ToJsonString()).ConfigureAwait(false);
        var rejectedConfig = await AssertRejectedAsync(fixture, client, stages).ConfigureAwait(false);
        Assert.IsFalse(rejectedConfig.Any(stage => stage.Name == "deployment-location.read-protected-history"), "Current configuration validation precedes retained history.");
        configNode["pipeline"]!["schemaVersion"] = "cameraagent-capture-pipeline-v2";
        await File.WriteAllTextAsync(fixture.ConfigPath, configNode.ToJsonString()).ConfigureAwait(false);
        _ = await fixture.ReadAsync(client).ConfigureAwait(false);
        var statePath = Path.Combine(fixture.IngressRoot, ".location", "deployment-location.v1.protected");
        var protectedHistory = await File.ReadAllBytesAsync(statePath).ConfigureAwait(false);
        var protector = fixture.Host.Services.GetRequiredService<IDeploymentLocationProtector>();
        var history = JsonNode.Parse(protector.Unprotect(protectedHistory))!;
        history["schemaVersion"] = 999;
        await File.WriteAllBytesAsync(statePath, protector.Protect(System.Text.Encoding.UTF8.GetBytes(history.ToJsonString()))).ConfigureAwait(false);
        var rejectedHistory = await AssertRejectedAsync(fixture, client, stages).ConfigureAwait(false);
        AssertProtectedRead(rejectedHistory.Single(stage => stage.Name == "deployment-location.read-protected-history"));
        Assert.IsFalse(rejectedHistory.Any(stage => stage.Name == "deployment-location.validate-retained"), "History validation precedes retained evidence and mutation.");
        await File.WriteAllBytesAsync(statePath, [1, 2, 3]).ConfigureAwait(false);
        var rejectedProtection = await AssertRejectedAsync(fixture, client, stages).ConfigureAwait(false);
        var protectionStage = rejectedProtection.Single(stage => stage.Name == "deployment-location.read-protected-history");
        AssertTag(protectionStage, "unprotect.count", 1);
        Assert.IsFalse(protectionStage.NumericTags.ContainsKey("attribution.deserialize.count"), "Unprotect failure must precede deserialization.");
        await File.WriteAllBytesAsync(statePath, protectedHistory).ConfigureAwait(false);
        var repaired = await fixture.ReadAsync(client).ConfigureAwait(false);
        // The same helpers used by the Manual control must also handle absent listeners and batch traces.
        stages.Dispose();
        Assert.IsFalse(DeploymentLocationTelemetry.AttributionActivitySource.HasListeners());
        var control = await MeasureAsync("control-regression", fixture, client, null).ConfigureAwait(false);
        var controlBatch = await MeasureConcurrentAsync(fixture, client, null).ConfigureAwait(false);
        Assert.AreEqual(repaired.GetRawText(), control.Identity.GetRawText());
        Assert.AreEqual(repaired.GetRawText(), controlBatch.Identity.GetRawText());
        Assert.AreEqual(0, control.Stages.Length);
        Assert.AreEqual(0, controlBatch.Stages.Length);
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
        var settlingReadCount = Environment.GetEnvironmentVariable("HVO_INSTALLATION_READ_SETTLING_READS") is { } configuredSettling
            ? int.Parse(configuredSettling, System.Globalization.CultureInfo.InvariantCulture)
            : 40;
        Assert.IsTrue(settlingReadCount is >= 40 and <= 1000, "Settling reads must be between 40 and 1000.");
        var mode = Environment.GetEnvironmentVariable("HVO_INSTALLATION_READ_MODE") ?? "listener";
        Assert.IsTrue(mode is "listener" or "no-listener", "HVO_INSTALLATION_READ_MODE must be listener or no-listener.");
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
            using var stages = mode == "listener" ? new InstallationReadStageCollector() : null;
            if (stages is null)
                Assert.IsFalse(DeploymentLocationTelemetry.AttributionActivitySource.HasListeners(), "Control requires no attribution listener.");
            var firstMeasuredKind = stages is null ? "first-control" : "first-instrumented";
            samples.Add(await MeasureAsync(firstMeasuredKind, fixture, client, stages).ConfigureAwait(false));
            // Retain settling samples separately so tiered-JIT phase changes cannot be credited to reuse (#1591).
            for (var index = 0; index < settlingReadCount; index++)
                samples.Add(await MeasureAsync("settling", fixture, client, stages).ConfigureAwait(false));
            for (var index = 0; index < 30; index++)
                samples.Add(await MeasureAsync("repeat", fixture, client, stages).ConfigureAwait(false));
            for (var batch = 0; batch < 2; batch++)
            {
                samples.Add(await MeasureConcurrentAsync(fixture, client, stages).ConfigureAwait(false));
            }
            var retainedAfter = await fixture.HashRetainedInputsAsync().ConfigureAwait(false);
            Assert.AreEqual(retainedBefore, retainedAfter, "Repeated and concurrent reads must leave retained inputs unchanged.");
            var before = samples.Single(sample => sample.Kind == firstMeasuredKind).Identity;
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
            workloads.Add(new
            {
                Schedule = solar ? "solar-astronomical-night" : "fixed-all-day",
                Fixture = identity,
                RetainedBeforeSha256 = retainedBefore,
                RetainedAfterSha256 = retainedAfter,
                SampleCount = samples.Count,
                StageCounts = samples.SelectMany(sample => sample.Stages).GroupBy(stage => stage.Name)
                    .ToDictionary(group => group.Key, group => group.Count()),
                Samples = samples
            });
        }
        var evidence = new
        {
            Schema = "issue-1559-attribution-v1",
            InstrumentationMode = mode,
            DiagnosticActivitySource = DeploymentLocationTelemetry.AttributionActivitySourceName,
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
            SettlingReadCount = settlingReadCount,
            MeasuredReadCount = 30,
            AllocationSemantics = "ProcessAllocatedBytes are process-wide inclusive deltas; stage nesting and concurrent intervals overlap. NumericTags thread_allocated_bytes are synchronous leaf thread-local deltas, not process-wide. Async history reads have elapsed time/bytes only. Do not add leaves to inclusive spans or sum concurrent spans.",
            CountSemantics = "NumericTags leaf counts include attempts, failed leaves and terminal iterator/reader calls; manifest.count/bytes count materialized inputs before parsing. Enumeration includes enumerator creation. Byte counts are logical materialized payload sizes, not physical storage I/O.",
            ByteSemantics = "read.bytes is materialized file/blob input; parse.bytes and deserialize.bytes are parser input; unprotect.bytes is successfully returned plaintext. Security, enumeration, open, query, provenance and validate bytes are not measured (zero).",
            ColdDefinition = "cold-host-empty: first protected request on each fresh host before loader/retained fixture setup. first-http-no-listener: first request with 10,000 retained manifests after setup; filesystem caches and loader JIT are warm. Neither drops OS caches.",
            // Opt-in probe runs only after every timed phase and both fixtures have been disposed.
            MemoryDiagnostics = ReadMemorySnapshot.CollectAfterMeasurement(),
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
        using var trace = stages is null ? null : StartTrace("installation-read");
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var allocated = GC.GetTotalAllocatedBytes(true);
        var io = ReadIo();
        var started = Stopwatch.GetTimestamp();
        var identity = await fixture.ReadAsync(client).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return new ReadMeasurement(kind, elapsed, (process.TotalProcessorTime - cpu).TotalMilliseconds,
            GC.GetTotalAllocatedBytes(true) - allocated, process.WorkingSet64, process.PeakWorkingSet64,
            IoDelta(io), identity, trace is null || stages is null ? [] : stages.ForTrace(trace.TraceId))
        { Memory = ReadMemorySnapshot.Capture() };
    }

    private static async Task<ReadMeasurement> MeasureConcurrentAsync(InstallationReadAttributionFixture fixture,
        HttpClient client, InstallationReadStageCollector? stages)
    {
        // Entire batch has its own allocation/CPU counters; overlapping stage deltas are not additive.
        using var trace = stages is null ? null : StartTrace("concurrent-batch");
        using var process = Process.GetCurrentProcess();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var cpu = process.TotalProcessorTime;
        var io = ReadIo();
        var started = Stopwatch.GetTimestamp();
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.ReadAsync(client))).ConfigureAwait(false);
        Assert.IsTrue(responses.All(value => value.GetRawText() == responses[0].GetRawText()));
        return new ReadMeasurement("concurrent-4", Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            (process.TotalProcessorTime - cpu).TotalMilliseconds, GC.GetTotalAllocatedBytes(true) - allocated,
            process.WorkingSet64, process.PeakWorkingSet64, IoDelta(io), responses[0],
            trace is null || stages is null ? [] : stages.ForTrace(trace.TraceId))
        { Memory = ReadMemorySnapshot.Capture() };
    }

    private static void AssertRetainedAttribution(InstallationReadStage[] stages, FixtureIdentity corpus)
    {
        var files = stages.Single(stage => stage.Name == "deployment-location.validate-retained-files");
        var journal = stages.Single(stage => stage.Name == "deployment-location.validate-retained-journal");
        AssertTag(files, "manifest.count", 2);
        AssertTag(journal, "manifest.count", 2);
        AssertTag(files, "enumeration.count", 4); // creation, two files, terminal MoveNext
        AssertTag(files, "security.count", 2);
        AssertTag(files, "read.count", 2);
        AssertTag(journal, "security.count", 1);
        AssertTag(journal, "open.count", 1);
        AssertTag(journal, "query.count", 2);
        AssertTag(journal, "read.count", 3); // two rows and terminal Read
        Assert.AreEqual(corpus.ManifestBytes,
            (long)files.NumericTags["attribution.manifest.bytes"] + (long)journal.NumericTags["attribution.manifest.bytes"]);
        foreach (var stage in new[] { files, journal })
        {
            AssertTag(stage, "parse.count", 2);
            AssertTag(stage, "provenance.count", 2);
            Assert.AreEqual(stage.NumericTags["attribution.manifest.bytes"], stage.NumericTags["attribution.read.bytes"]);
            Assert.AreEqual(stage.NumericTags["attribution.manifest.bytes"], stage.NumericTags["attribution.parse.bytes"]);
            foreach (var leaf in stage.NumericTags.Keys.Where(key => key.EndsWith(".count", StringComparison.Ordinal) && key != "attribution.manifest.count"))
            {
                var prefix = leaf[..^".count".Length];
                Assert.IsTrue((double)stage.NumericTags[prefix + ".elapsed_ms"] >= 0);
                Assert.IsTrue((long)stage.NumericTags[prefix + ".thread_allocated_bytes"] >= 0);
            }
        }
        AssertProtectedRead(stages.Single(stage => stage.Name == "deployment-location.read-protected-history"));
        Assert.AreEqual(4, stages.Count(stage => stage.Name.StartsWith("deployment-location.", StringComparison.Ordinal)
            && stage.Name != "deployment-location.initialize"), "Attribution span count must be independent of corpus size.");
    }

    private static void AssertProtectedRead(InstallationReadStage stage)
    {
        foreach (var leaf in new[] { "security", "read", "unprotect", "deserialize", "validate" })
        {
            AssertTag(stage, leaf + ".count", 1);
            Assert.IsTrue((double)stage.NumericTags["attribution." + leaf + ".elapsed_ms"] >= 0);
            if (leaf != "read") Assert.IsTrue((long)stage.NumericTags["attribution." + leaf + ".thread_allocated_bytes"] >= 0);
        }
        Assert.IsTrue((long)stage.NumericTags["attribution.read.bytes"] > 0);
        Assert.IsTrue((long)stage.NumericTags["attribution.unprotect.bytes"] > 0);
        Assert.AreEqual(stage.NumericTags["attribution.unprotect.bytes"], stage.NumericTags["attribution.deserialize.bytes"]);
        Assert.IsFalse(stage.NumericTags.ContainsKey("attribution.read.thread_allocated_bytes"), "Async reads cannot claim thread-local allocation.");
    }

    private static void AssertTag(InstallationReadStage stage, string tag, long expected)
        => Assert.AreEqual(expected, (long)stage.NumericTags["attribution." + tag], stage.Name + ": " + tag);

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

    private static async Task<InstallationReadStage[]> AssertRejectedAsync(InstallationReadAttributionFixture fixture,
        HttpClient client, InstallationReadStageCollector stages)
    {
        Assert.AreEqual(fixture.Host.BaseAddress, client.BaseAddress);
        var retainedBefore = await fixture.HashRetainedInputsAsync().ConfigureAwait(false);
        using var trace = StartTrace("rejected")!;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/internal/owner-bootstrap/installation-verification");
        request.Headers.Add("X-HVO-Installation-Token", CameraAgentKestrelFixture.InstallationVerificationToken);
        request.Headers.Add("traceparent", trace.Id);
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.AreEqual(retainedBefore, await fixture.HashRetainedInputsAsync().ConfigureAwait(false), "Rejected reads must not mutate retained inputs.");
        return stages.ForTrace(trace.TraceId);
    }
}

internal sealed record ReadMeasurement(string Kind, double ElapsedMs, double CpuMs, long ProcessAllocatedBytes,
    long WorkingSetBytes, long PeakWorkingSetBytes, Dictionary<string, long> IoDelta, JsonElement Identity, InstallationReadStage[] Stages)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReadMemorySnapshot? Memory { get; init; }
}

// Supplemental GC diagnostics are outside measured counters/timers. No forced
// collection occurs until all workloads, edits and fixture disposal are complete.
internal sealed record ReadMemorySnapshot(long ManagedAllocatedBytes, long LastGcHeapSizeBytes,
    long ManagedCommittedBytes, long LastGcFragmentedBytes, long WorkingSetBytes, long PeakWorkingSetBytes,
    int Gen0Count, int Gen1Count, int Gen2Count, long LastGcIndex)
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("HVO_INSTALLATION_READ_GC_DIAGNOSTICS") == "1";

    internal static ReadMemorySnapshot? Capture()
    {
        if (!Enabled) return null;
        var memory = GC.GetGCMemoryInfo();
        using var process = Process.GetCurrentProcess();
        return new ReadMemorySnapshot(GC.GetTotalMemory(forceFullCollection: false), memory.HeapSizeBytes,
            memory.TotalCommittedBytes, memory.FragmentedBytes, process.WorkingSet64, process.PeakWorkingSet64,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), memory.Index);
    }

    internal static object? CollectAfterMeasurement()
    {
        if (!Enabled) return null;
        var before = Capture();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return new { Before = before, After = Capture() };
    }
}
