using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Catalog.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>One isolated process per baseline/after workload; raw failure evidence is retained.</summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class VirtualStellarExposureResourceTests
{
    private static readonly string[] Sensors = ["mono", "cfa", "asi676"];
    private static readonly string[] ExposureCases = ["short", "long", "day"];
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ConfiguredStellarExposureResources()
    {
        var revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION");
        Assert.IsTrue(revision is { Length: 40 } && revision.All(Uri.IsHexDigit));
        var binaryRevision = GetType().Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.IsTrue(binaryRevision.Contains(revision, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("0", Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"));
        var label = Environment.GetEnvironmentVariable("HVO_EXPOSURE_LABEL");
        Assert.IsTrue(label is "before" or "after");
        var workload = Environment.GetEnvironmentVariable("HVO_EXPOSURE_WORKLOAD")!;
        Assert.IsTrue(Sensors.SelectMany(sensor =>
            ExposureCases.Select(exposureCase => $"{sensor}-{exposureCase}")).Contains(workload));
        var parts = workload.Split('-');
        var exposure = parts[1] switch { "short" => .1, "long" => 20d, _ => 60d };
        var utc = new DateTimeOffset(2026, 2, 10, parts[1] == "day" ? 20 : 8, 0, 0, TimeSpan.Zero);
        var root = Environment.GetEnvironmentVariable("HVO_ASTROMETRY_CATALOG_ROOT");
        Assert.IsFalse(string.IsNullOrWhiteSpace(root));
        var snapshot = CatalogSnapshotResolver.Resolve(new(root, "hyg-v42-production"));
        Assert.AreEqual(119625L, snapshot.RowCount);
        Assert.AreEqual("B51D18B722199E89AA8FE4622EBE507346C75EFFB375E546881452A263F0B9E2", snapshot.DatabaseSha256);
        var rig = parts[0] == "asi676" ? LoadRig("virtual-asi676mc.full.json") :
            VirtualAstrometryFixture.Profiles(52201).Single(profile =>
                profile.Name == (parts[0] == "mono" ? "mono-native" : "cfa-native")).Config.Rig;
        var options = new VirtualSkyCameraModuleOptions
        {
            Seed = 52201,
            MaximumMagnitude = 6.5,
            MaximumResults = label == "before" ? 300 : 32768,
            MagnitudeZeroElectronsPerSecond = 60000,
            BackgroundElectronsPerSecond = parts[1] == "day" ? 20000 : 2,
            PsfSigmaPixels = 1,
            PsfRadiusPixels = 4,
            VignettingStrength = .25,
            ShotNoiseEnabled = true,
            Asi174Sensor = new() { Enabled = parts[0] == "mono" }
        };
        var config = new CameraModuleConfig(new(35.347, -113.878, 1000, "America/Phoenix"),
            new("VirtualSky", JsonSerializer.SerializeToElement(options, VirtualAstrometryFixture.JsonOptions)), rig,
            CapturePipelineConfig.Empty, "virtual-stellar-exposure-resource");
        var catalog = new MeasuredCatalog(snapshot.Catalog);
        var module = new VirtualSkyCameraModule(TimeProvider.System, catalog, new ProjectedSceneStore());
        await using var lifetime = module.ConfigureAwait(false);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        using var process = Process.GetCurrentProcess();
        var initialProcessPeakBytes = process.PeakWorkingSet64;
        var samples = new List<object>();
        var times = new List<double>();
        var failures = new List<string>();
        var path = Path.Combine(TestContext.TestRunDirectory!, $"stellar-exposure-{label}-{workload}.json");
        try
        {
            for (var index = -5; index < 30; index++)
            {
                catalog.Reset();
                var request = new CaptureRequest(utc.AddSeconds(60 * (index + 5)), TimeSpan.FromSeconds(60),
                    CaptureMode.Still, new(TimeSpan.FromSeconds(exposure), 150, null, null));
                var allocation = GC.GetTotalAllocatedBytes(precise: true);
                var cpu = process.TotalProcessorTime;
                var io = LinuxIo();
                var clock = Stopwatch.StartNew();
                var result = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
                clock.Stop();
                var captureMs = clock.Elapsed.TotalMilliseconds;
                var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocation;
                var afterIo = LinuxIo();
                var frame = result.Frame!;
                Assert.IsNotNull(frame);
                Assert.IsNotNull(frame.Layout);
                Assert.AreEqual(frame.Layout.ByteLength, frame.PixelData.Length);
                process.Refresh();
                if (index < 0) continue;
                times.Add(captureMs);
                samples.Add(new
                {
                    index,
                    request,
                    captureMs,
                    cpuMs,
                    allocatedBytes,
                    catalog.QueryMilliseconds,
                    catalog.CandidateCount,
                    catalog.QueryCount,
                    visibleObjectCount = frame.Metadata.Scene!.Objects!.Count,
                    processWorkingSetBytes = process.WorkingSet64,
                    processPeakWorkingSetBytes = process.PeakWorkingSet64,
                    payloadBytes = frame.PixelData.Length,
                    sourceHash = Convert.ToHexString(SHA256.HashData(frame.PixelData.Span)),
                    scene = frame.Metadata.Scene,
                    metadata = frame.Metadata.Extra,
                    readBytes = io is null || afterIo is null ? (long?)null : afterIo.Value.Read - io.Value.Read,
                    writeBytes = io is null || afterIo is null ? (long?)null : afterIo.Value.Write - io.Value.Write
                });
            }
            var p95 = times.Order().ElementAt(28);
            if (p95 > (parts[0] == "mono" ? 10000 : 40000)) failures.Add($"Capture p95 {p95}ms exceeds predeclared budget.");
            if (process.PeakWorkingSet64 > 2L * 1024 * 1024 * 1024) failures.Add($"Cumulative process peak {process.PeakWorkingSet64} exceeds 2 GiB.");
        }
        finally
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                schema = "virtual-stellar-exposure-resources-v1",
                revision,
                binaryRevision,
                label,
                workload,
                config,
                configHash = CaptureContractJson.ComputeCanonicalJsonSha256(config),
                snapshot.DatabaseSha256,
                warmups = 5,
                measured = 30,
                concurrency = 1,
                initialProcessPeakBytes,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                runtime = Environment.Version.ToString(),
                processors = Environment.ProcessorCount,
                tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
                backlog = "N/A: serial stateless camera capture; no durable queues or outages",
                baselineStageLimit = "Legacy query measured by catalog wrapper; independent projection/render CPU unavailable before instrumentation",
                measurement = "Capture return boundary before hashing; process-wide allocations/CPU; process working set and cumulative peak, no forced GC",
                captureMedianMs = times.Count == 30 ? (times.Order().ElementAt(14) + times.Order().ElementAt(15)) / 2 : (double?)null,
                captureP95Ms = times.Count == 30 ? times.Order().ElementAt(28) : (double?)null,
                operationsPerSecond = times.Count > 0 ? 1000 * times.Count / times.Sum() : (double?)null,
                samples,
                failures
            }, VirtualAstrometryFixture.JsonOptions)).ConfigureAwait(false);
            TestContext.AddResultFile(path);
        }
        Assert.HasCount(30, samples);
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    private static CameraRigConfig LoadRig(string filename)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, filename)));
        return document.RootElement.GetProperty("rig").Deserialize<CameraRigConfig>(VirtualAstrometryFixture.JsonOptions)!;
    }

    private static (long Read, long Write)? LinuxIo()
    {
        if (!OperatingSystem.IsLinux()) return null;
        var values = File.ReadAllLines("/proc/self/io").Select(line => line.Split(':', 2))
            .ToDictionary(parts => parts[0], parts => long.Parse(parts[1].Trim(), CultureInfo.InvariantCulture), StringComparer.Ordinal);
        return (values["read_bytes"], values["write_bytes"]);
    }

    private sealed class MeasuredCatalog(ICelestialCatalog source) : ICelestialCatalog, ICelestialCatalogMetadataSource
    {
        public double QueryMilliseconds { get; private set; }
        public int CandidateCount { get; private set; }
        public int QueryCount { get; private set; }
        public CatalogMetadata Metadata => ((ICelestialCatalogMetadataSource)source).Metadata;
        public string PreprocessingVersion => ((ICelestialCatalogMetadataSource)source).PreprocessingVersion;
        public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query) => source.Query(query);
        public void Reset() { QueryMilliseconds = 0; CandidateCount = 0; QueryCount = 0; }
        public async ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(CatalogCandidateQuery query,
            CancellationToken cancellationToken = default)
        {
            var clock = Stopwatch.StartNew();
            var candidates = await source.QueryCandidatesAsync(query, cancellationToken).ConfigureAwait(false);
            QueryMilliseconds += clock.Elapsed.TotalMilliseconds;
            CandidateCount += candidates.Count;
            QueryCount++;
            return candidates;
        }
    }
}
