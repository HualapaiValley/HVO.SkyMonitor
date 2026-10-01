using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Catalog.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class VirtualAstrometryResourceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ActualConfiguredResolutionResources()
    {
        Assert.AreEqual("0", Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), "The declared measurement disables tiered compilation.");
        var revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION");
        Assert.IsTrue(revision is { Length: 40 } && revision.All(Uri.IsHexDigit));
        var binaryRevision = typeof(VirtualAstrometryResourceTests).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.IsTrue(binaryRevision.Contains(revision, StringComparison.OrdinalIgnoreCase), "Build this exact committed head before resource evidence.");
        var root = Environment.GetEnvironmentVariable("HVO_ASTROMETRY_CATALOG_ROOT");
        Assert.IsFalse(string.IsNullOrWhiteSpace(root), "Approved production snapshot is mandatory.");
        var snapshot = CatalogSnapshotResolver.Resolve(new(root, "hyg-v42-production"));
        Assert.AreEqual(119625L, snapshot.RowCount);
        Assert.AreEqual("B51D18B722199E89AA8FE4622EBE507346C75EFFB375E546881452A263F0B9E2", snapshot.DatabaseSha256);
        using var process = Process.GetCurrentProcess();
        var initialProcessPeakBytes = process.PeakWorkingSet64;
        var reports = new List<object>();
        var rawSamples = new List<object>();
        var failures = new List<string>();
        var path = Path.Combine(TestContext.TestRunDirectory!, "virtual-astrometry-resources.json");
        try
        {
            foreach (var profile in VirtualAstrometryFixture.Profiles(110220).Where(profile => profile.Name is "mono-native" or "cfa-native"))
            {
                var module = new VirtualSkyCameraModule(TimeProvider.System, snapshot.Catalog, new ProjectedSceneStore());
                await using var lifetime = module.ConfigureAwait(false);
                await module.InitializeAsync(profile.Config, CancellationToken.None).ConfigureAwait(false);
                var nominal = VirtualAstrometryFixture.NominalCalibration(profile);
                var utc = new DateTimeOffset(2026, 2, 10, 8, 0, 0, TimeSpan.Zero);
                var captureTimes = new List<double>(); var detectionTimes = new List<double>();
                var samples = new List<object>(); var allocationSamples = new List<long>();
                for (var index = -5; index < 30; index++)
                {
                    var request = new CaptureRequest(utc.AddSeconds(60 * (index + 5)), TimeSpan.FromSeconds(60), CaptureMode.Still,
                        new(TimeSpan.FromSeconds(1), 150, null, null));
                    var allocated = GC.GetTotalAllocatedBytes(precise: true);
                    var cpu = process.TotalProcessorTime; var io = LinuxIo();
                    var clock = Stopwatch.StartNew();
                    var result = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
                    var captureMs = clock.Elapsed.TotalMilliseconds;
                    clock.Restart();
                    var input = VirtualAstrometryPixels.FromCapture(result.Frame!);
                    var measured = VirtualAstrometryFixture.Measure(input, nominal);
                    var detectionMs = clock.Elapsed.TotalMilliseconds;
                    var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                    allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
                    var afterIo = LinuxIo();
                    process.Refresh();
                    if (index < 0) continue;
                    captureTimes.Add(captureMs); detectionTimes.Add(detectionMs); allocationSamples.Add(allocated);
                    var sample = new
                    {
                        profile.Name,
                        index,
                        request,
                        captureMs,
                        detectionMs,
                        cpuMs,
                        allocatedBytes = allocated,
                        processWorkingSetBytes = process.WorkingSet64,
                        processPeakWorkingSetBytes = process.PeakWorkingSet64,
                        payloadBytes = input.Payload.Length,
                        decodedPixels = input.Layout.Width * input.Layout.Height,
                        decodedDoubleBytes = (long)input.Layout.Width * input.Layout.Height * sizeof(double),
                        validityBytes = (long)input.Layout.Width * input.Layout.Height,
                        maximumMedianSampleBytes = (long)input.Layout.Width * input.Layout.Height * sizeof(double),
                        maximumCfaRgbAndLuminanceBytes = input.Layout.PixelFormat == CameraPixelFormat.BayerRggb16
                            ? (long)input.Layout.Width * input.Layout.Height * sizeof(double) * 4 : 0,
                        pixelBufferModel = "Mono: raw copy + double + bool + median sample; CFA additionally RGB + luminance + reconstruction validity",
                        readBytes = io is null || afterIo is null ? (long?)null : afterIo.Value.Read - io.Value.Read,
                        writeBytes = io is null || afterIo is null ? (long?)null : afterIo.Value.Write - io.Value.Write,
                        sourceHash = Convert.ToHexString(SHA256.HashData(input.Payload.Span)),
                        sourceDescriptorHash = VirtualAstrometryFixture.FrameContext(input).SourceDescriptorSha256,
                        detected = measured.Detections.Count,
                        measured.CandidateCount
                    };
                    samples.Add(sample);
                    rawSamples.Add(sample);
                    if (measured.Detections.Count < 12) failures.Add($"{profile.Name}/{index}: fewer than 12 detections");
                }
                var captureP95 = Percentile95(captureTimes); var detectionP95 = Percentile95(detectionTimes);
                var isCfa = profile.Name == "cfa-native";
                reports.Add(new
                {
                    profile.Name,
                    configHash = CaptureContractJson.ComputeCanonicalJsonSha256(profile.Config),
                    nominal.IdentitySha256,
                    captureMedianMs = Median(captureTimes),
                    captureP95Ms = captureP95,
                    detectionMedianMs = Median(detectionTimes),
                    detectionP95Ms = detectionP95,
                    medianAllocatedBytes = allocationSamples.Order().ElementAt(15),
                    operationsPerSecond = 30000 / (captureTimes.Sum() + detectionTimes.Sum()),
                    samples
                });
                if (captureP95 > (isCfa ? 20000d : 10000)) failures.Add($"{profile.Name}: capture p95 {captureP95}ms exceeds budget");
                if (detectionP95 > (isCfa ? 5000d : 2000)) failures.Add($"{profile.Name}: detection p95 {detectionP95}ms exceeds budget");
                if (process.PeakWorkingSet64 > 2L * 1024 * 1024 * 1024)
                    failures.Add($"{profile.Name}: cumulative process peak {process.PeakWorkingSet64} exceeds 2 GiB; run this resource workload in its own test process");
            }
        }
        finally
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                schema = "virtual-astrometry-resources-v2",
                revision,
                binaryRevision,
                route = "existing-production-through-qualified-pixel-harness",
                snapshot.DatabaseSha256,
                warmup = 5,
                measured = 30,
                concurrency = 1,
                tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
                runtime = Environment.Version.ToString(),
                host = Environment.MachineName,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                processors = Environment.ProcessorCount,
                backlog = "N/A: bounded stateless capture and measurement; no queue",
                workingSetMeaning = "Process working set and cumulative process peak, not operation-local retained memory",
                initialProcessPeakBytes,
                reports,
                rawSamples,
                failures
            }, VirtualAstrometryFixture.JsonOptions)).ConfigureAwait(false);
            TestContext.AddResultFile(path);
        }
        Assert.HasCount(2, reports, "Both native camera workloads must complete.");
        Assert.HasCount(60, rawSamples, "Each native workload requires 30 measured samples.");
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToArray(); return (sorted[14] + sorted[15]) / 2;
    }
    private static double Percentile95(List<double> values) => values.Order().ElementAt(28);
    private static (long Read, long Write)? LinuxIo()
    {
        if (!OperatingSystem.IsLinux()) return null;
        var values = File.ReadAllLines("/proc/self/io").Select(line => line.Split(':', 2))
            .ToDictionary(parts => parts[0], parts => long.Parse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
        return (values["read_bytes"], values["write_bytes"]);
    }
}
