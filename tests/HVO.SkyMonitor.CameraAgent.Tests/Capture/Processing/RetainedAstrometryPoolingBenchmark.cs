using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    private sealed record PoolingBenchmarkIdentity(Guid CaptureId, Guid ArtifactId, string SolutionHash,
        DateTimeOffset Midpoint, Guid SelectedArtifact, Guid SelectedCapture, string SelectedChecksum, int Rejected);

    [TestMethod]
    [TestCategory("Manual")]
    [OSCondition(OperatingSystems.Linux)]
    public async Task Issue2004RetainedScanBenchmark()
    {
        var root = Environment.GetEnvironmentVariable("HVO_RETAINED_SCAN_FIXTURE")
            ?? throw new InvalidOperationException("Set HVO_RETAINED_SCAN_FIXTURE to a scratch archive path.");
        var mode = Environment.GetEnvironmentVariable("HVO_RETAINED_SCAN_MODE");
        var expectedFile = Path.Combine(root, "benchmark-identity.json");
        if (mode == "seed")
        {
            Directory.CreateDirectory(root);
            if (Directory.EnumerateFileSystemEntries(root).Any())
                throw new InvalidOperationException("The fixture directory must be empty before seeding.");
            var countOverride = Environment.GetEnvironmentVariable("HVO_RETAINED_SCAN_REJECTIONS");
            var rejectedCount = countOverride is null ? 6960 : int.Parse(countOverride, CultureInfo.InvariantCulture);
            Assert.IsGreaterThan(0, rejectedCount);
            var reference = await MeasuredAstrometryFixture.CreateAsync().ConfigureAwait(false);
            var database = Path.Combine(root, "journal", "raw-ingress.db");
            var journal = new SqliteRawCaptureJournal(database, 1);
            var seedOptions = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, RawIngressReserveBytes = 0 });
            using var telemetry = new CaptureProcessingTelemetry();
            using var writer = new SqliteCaptureProcessingStore(seedOptions);
            using var gate = new CaptureAstrometryExecutionGate();
            var persistence = CreatePersistence(seedOptions, writer, telemetry);
            var accepted = await CreateAstrometryDurabilityFixtureAsync(root, hostCompatible: true,
                reference: reference, benchmarkJournal: journal).ConfigureAwait(false);
            var output = await ProduceLatestReadFixtureAsync(accepted, writer, persistence, telemetry, gate).ConfigureAwait(false);
            var descriptor = accepted.Fixture.Manifest.Descriptor;

            using var sequencer = new SqliteConnection($"Data Source={database};Pooling=False");
            await sequencer.OpenAsync().ConfigureAwait(false);
            using var setSequence = sequencer.CreateCommand();
            setSequence.CommandText = "UPDATE raw_capture_sequences SET last_sequence = $after_gap WHERE agent_id = $agent;";
            var sequence = setSequence.Parameters.AddWithValue("$after_gap", 0);
            setSequence.Parameters.AddWithValue("$agent", descriptor.Capture.AgentId);
            var seedClock = Stopwatch.StartNew();
            var newest = descriptor;
            for (var index = 1; index <= rejectedCount; index++)
            {
                var previousSequence = index * 60L;
                sequence.Value = previousSequence;
                Assert.AreEqual(1, await setSequence.ExecuteNonQueryAsync().ConfigureAwait(false));
                var raw = reference.Raw with
                {
                    CaptureId = new Guid(index, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]),
                    ArtifactId = new Guid(index, 0, 0, [0, 0, 0, 0, 0, 0, 0, 2])
                };
                var rejected = await CreateAstrometryDurabilityFixtureAsync(root, "rejected", hostCompatible: true,
                    captureSequence: previousSequence + 1, reference: reference with { Raw = raw }, prior: (previousSequence, index),
                    benchmarkJournal: journal).ConfigureAwait(false);
                _ = await ProduceLatestReadFixtureAsync(rejected, writer, persistence, telemetry, gate).ConfigureAwait(false);
                newest = rejected.Fixture.Manifest.Descriptor;
                if (index % 500 == 0)
                    Console.WriteLine(FormattableString.Invariant($"Seeded {index} in {seedClock.Elapsed.TotalSeconds:F2}s"));
            }
            await File.WriteAllTextAsync(expectedFile, JsonSerializer.Serialize(new PoolingBenchmarkIdentity(
                descriptor.Capture.CaptureId, output.ArtifactId, output.ContentIdentitySha256!,
                descriptor.Timing.ExposureStartedUtc.AddMilliseconds(500),
                newest.Artifact.ArtifactId, newest.Capture.CaptureId, newest.Artifact.ChecksumSha256, rejectedCount)))
                .ConfigureAwait(false);
            return;
        }

        var useHistory = mode switch
        {
            "pooled" => true,
            "unpooled" => false,
            _ => throw new InvalidOperationException("Set HVO_RETAINED_SCAN_MODE to seed, pooled or unpooled.")
        };
        var expected = JsonSerializer.Deserialize<PoolingBenchmarkIdentity>(await File.ReadAllTextAsync(expectedFile).ConfigureAwait(false))!;
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, RawIngressReserveBytes = 0 });
        using var store = new SqliteCaptureProcessingStore(options);
        using var artifacts = SiloInfrastructureFixture.OpenArtifactReader(options, store);
        using var reader = CreateReader();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var process = Process.GetCurrentProcess();
        var startup = Stopwatch.StartNew();
        await store.InitializeAsync(deadline.Token).ConfigureAwait(false);
        Console.WriteLine(FormattableString.Invariant($"Startup {startup.Elapsed.TotalMilliseconds:F2}ms"));

        var cacheProbe = new ProcessStartInfo("python3")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        cacheProbe.ArgumentList.Add(Environment.GetEnvironmentVariable("HVO_RETAINED_SCAN_CACHE_PROBE")
            ?? throw new InvalidOperationException("Set HVO_RETAINED_SCAN_CACHE_PROBE to the repository cache probe."));
        using (var eviction = Process.Start(cacheProbe)!)
        {
            Console.WriteLine(await eviction.StandardOutput.ReadToEndAsync(deadline.Token).ConfigureAwait(false));
            await eviction.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            Assert.AreEqual(0, eviction.ExitCode);
        }

        await MeasureAsync("first-scan").ConfigureAwait(false);
        for (var pass = 0; pass < 7; pass++)
            await MeasureAsync("warm-refresh").ConfigureAwait(false);
        for (var pass = 0; pass < 3; pass++)
        {
            using var freshReader = CreateReader();
            await MeasureAsync("warm-full-scan", freshReader).ConfigureAwait(false);
        }

        CameraAgentRetainedAstrometryService CreateReader() => useHistory
            ? new(store, artifacts)
            : CreateUnpooledAstrometryReader(store, artifacts);

        async Task MeasureAsync(string name, CameraAgentRetainedAstrometryService? scanReader = null)
        {
            using var done = new CancellationTokenSource();
            var selectedTimes = new List<double>();
            var initialPayloadReads = artifacts.PayloadValidationReads;
            var allocation = GC.GetTotalAllocatedBytes(precise: true);
            var initialOpens = artifacts.JournalConnectionOpens;
            var io = ReadIo();
            var cpu = process.TotalProcessorTime;
            var timer = Stopwatch.StartNew();
            var selected = Task.Run(async () =>
            {
                do
                {
                    var selectedTimer = Stopwatch.StartNew();
                    var opened = await artifacts.OpenContentAsync(expected.SelectedArtifact, deadline.Token).ConfigureAwait(false);
                    Assert.AreEqual(CameraAgentArtifactReadStatus.Found, opened.Status);
                    var content = opened.Content!;
                    await using (content.ConfigureAwait(false))
                    {
                        Assert.AreEqual(expected.SelectedArtifact, content.ArtifactId);
                        Assert.AreEqual(expected.SelectedCapture, content.CaptureId);
                        Assert.AreEqual(expected.SelectedChecksum, content.ChecksumSha256);
                    }
                    selectedTimes.Add(selectedTimer.Elapsed.TotalMilliseconds);
                    await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token).ConfigureAwait(false);
                } while (!done.IsCancellationRequested);
            }, deadline.Token);
            CameraAgentLatestAstrometry? summary = null;
            var calls = 0;
            TimeSpan elapsed;
            try
            {
                while (summary is null)
                {
                    calls++;
                    var result = await (scanReader ?? reader).ReadLatestAcceptedAsync(deadline.Token).ConfigureAwait(false);
                    summary = result.Summary;
                    if (summary is null)
                        await Task.Delay(TimeSpan.FromMilliseconds(2), deadline.Token).ConfigureAwait(false);
                }
                elapsed = timer.Elapsed;
            }
            finally
            {
                await done.CancelAsync().ConfigureAwait(false);
                await selected.ConfigureAwait(false);
            }
            Assert.AreEqual(expected.CaptureId, summary.CaptureId);
            Assert.AreEqual(expected.ArtifactId, summary.ArtifactId);
            Assert.AreEqual(expected.SolutionHash, summary.SolutionIdentitySha256);
            Assert.AreEqual(expected.Midpoint, summary.MidpointUtc);
            var reads = artifacts.PayloadValidationReads - initialPayloadReads - selectedTimes.Count;
            Assert.AreEqual(name == "warm-refresh" ? 5L : expected.Rejected + 5L, reads);
            selectedTimes.Sort();
            var journalOpens = artifacts.JournalConnectionOpens - initialOpens - selectedTimes.Count;
            Assert.IsTrue(useHistory ? journalOpens > 0 && journalOpens <= calls : journalOpens == reads,
                $"Observed {journalOpens} scan opens across {calls} requests and {reads} artifact checks.");
            var currentIo = ReadIo();
            process.Refresh();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Name = name,
                elapsed.TotalMilliseconds,
                Calls = calls,
                ArtifactChecks = reads,
                JournalOpens = journalOpens,
                summary.CaptureId,
                summary.ArtifactId,
                summary.SolutionIdentitySha256,
                summary.MidpointUtc,
                Selected = selectedTimes.Count,
                SelectedP50 = selectedTimes[selectedTimes.Count / 2],
                SelectedMax = selectedTimes[^1],
                CpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                ManagedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocation,
                process.PeakWorkingSet64,
                BytesFromStorage = currentIo.Bytes - io.Bytes,
                ReadCalls = currentIo.Calls - io.Calls
            }));
        }

        static (long Bytes, long Calls) ReadIo()
        {
            var io = File.ReadLines("/proc/self/io").Select(line => line.Split(": ", 2))
                .ToDictionary(parts => parts[0], parts => long.Parse(parts[1], CultureInfo.InvariantCulture));
            return (io["read_bytes"], io["syscr"]);
        }
    }
}
