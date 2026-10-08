using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>
/// W1/W2 full-resolution VirtualSky captures with and without resolved Sun/Moon disks. The same source runs against
/// the #518 baseline and candidate, so it uses only APIs present on both. The empty catalog isolates the disk path.
/// </summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class Issue518ResolvedDiskEvidenceTests
{
    private const int WarmupCount = 5;
    private const int MeasurementCount = 30;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] SunAndMoon = ["Sun", "Moon"];

    // Day: the Sun is 32° up at HVO. Night: the full Moon is near the meridian.
    private static readonly (string Name, DateTimeOffset Utc, bool Disks)[] Scenarios =
    [
        ("day-disks-off", new(2026, 1, 15, 19, 0, 0, TimeSpan.Zero), false),
        ("day-sun-disk", new(2026, 1, 15, 19, 0, 0, TimeSpan.Zero), true),
        ("night-moon-disk", new(2026, 1, 3, 7, 0, 0, TimeSpan.Zero), true)
    ];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task FullResolutionCapturesRecordBoundedDiskCost()
    {
        if (Environment.GetEnvironmentVariable("HVO_ISSUE518_EVIDENCE") != "1")
            Assert.Inconclusive("Requires an explicitly scheduled #518 evidence run.");
        var output = Path.GetFullPath(Environment.GetEnvironmentVariable("HVO_ISSUE518_OUTPUT")
            ?? throw new InvalidOperationException("HVO_ISSUE518_OUTPUT is required."));
        Assert.IsFalse(File.Exists(output), "Evidence is append-only; select a fresh output path.");

        var measurements = new List<Measurement>();
        foreach (var (workload, fileName) in new[] { ("W1", "virtual-asi174.full.json"), ("W2", "virtual-asi178mc.full.json") })
        {
            foreach (var scenario in Scenarios)
                measurements.Add(await MeasureAsync(workload, fileName, scenario).ConfigureAwait(false));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            SchemaVersion = "issue-518-resolved-disk-performance-v1",
            Revision = ReadGit("rev-parse", "HEAD"),
            WorkingTreeStatus = ReadGit("status", "--short"),
            RecordedUtc = DateTimeOffset.UtcNow,
            Environment = new
            {
                Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                Runtime = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                Environment.ProcessorCount,
                Environment.MachineName,
                WarmupCount,
                MeasurementCount
            },
            Measurements = measurements
        }, SerializerOptions)).ConfigureAwait(false);
        TestContext.WriteLine($"Issue #518 evidence: {output}");
    }

    private static async Task<Measurement> MeasureAsync(
        string workload, string fileName, (string Name, DateTimeOffset Utc, bool Disks) scenario)
    {
        var config = await LoadProfileAsync(fileName).ConfigureAwait(false);
        var options = config.ModuleOptions is { } existing
            ? JsonNode.Parse(existing.GetRawText())!.AsObject()
            : [];
        options["renderSolarSystemDisks"] = scenario.Disks;
        options["solarSystemBodies"] = scenario.Disks ? new JsonArray(SunAndMoon.Select(static body => (JsonNode)body).ToArray()) : new JsonArray();
        options["illuminationMode"] = "ControlledNight";
        config = config with { Module = new("VirtualSky", JsonSerializer.SerializeToElement(options)) };
        var module = new VirtualSkyCameraModule(TimeProvider.System, new InMemoryCelestialCatalog([]), new ProjectedSceneStore(),
            planetEphemeris: new AstronomyEnginePlanetEphemeris());
        await using var moduleLifetime = module.ConfigureAwait(false);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var setpoint = new CaptureSetpoint(TimeSpan.FromMilliseconds(1), config.Rig.Pipeline.DayGain, null, null);
        for (var index = 0; index < WarmupCount; index++)
            _ = await CaptureAsync(module, scenario.Utc, setpoint).ConfigureAwait(false);

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuStart = process.TotalProcessorTime;
        var allocatedStart = GC.GetTotalAllocatedBytes(true);
        var workingSetStart = process.WorkingSet64;
        var peakWorkingSet = workingSetStart;
        var durations = new double[MeasurementCount];
        CaptureResult? final = null;
        for (var index = 0; index < durations.Length; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            final = await CaptureAsync(module, scenario.Utc, setpoint).ConfigureAwait(false);
            durations[index] = stopwatch.Elapsed.TotalMilliseconds;
            process.Refresh();
            peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
        }

        process.Refresh();
        var frame = final!.Frame!;
        Array.Sort(durations);
        frame.Metadata.Extra!.TryGetValue("solarDiskAlgorithm", out var diskAlgorithm);
        return new Measurement(workload, fileName, scenario.Name, scenario.Utc, scenario.Disks,
            config.Rig.Sensor.PixelFormat.ToString(), frame.PixelData.Length,
            durations[durations.Length / 2], durations[(int)Math.Ceiling(durations.Length * 0.95) - 1], durations[^1],
            (process.TotalProcessorTime - cpuStart).TotalMilliseconds / MeasurementCount,
            (GC.GetTotalAllocatedBytes(true) - allocatedStart) / (double)MeasurementCount,
            workingSetStart, peakWorkingSet, Convert.ToHexString(SHA256.HashData(frame.PixelData.Span)),
            frame.Metadata.Scene?.ProjectedSceneSchemaVersion, diskAlgorithm);
    }

    private static Task<CaptureResult> CaptureAsync(VirtualSkyCameraModule module, DateTimeOffset utc, CaptureSetpoint setpoint) =>
        module.CaptureAsync(new CaptureRequest(utc, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint), CancellationToken.None);

    private static async Task<CameraModuleConfig> LoadProfileAsync(string fileName)
    {
        var loader = new FileCameraAgentConfigurationLoader(Options.Create(new CameraAgentHostOptions
        {
            ConfigFilePath = Path.Combine(AppContext.BaseDirectory, fileName),
            AgentId = "issue518-evidence",
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled },
            Observatory = new ObservatoryLocation(35.347, -113.878, 1000, "America/Phoenix")
        }), NullLogger<FileCameraAgentConfigurationLoader>.Instance);
        return await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static string ReadGit(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, WorkingDirectory = AppContext.BaseDirectory };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var git = Process.Start(start)!;
        var text = git.StandardOutput.ReadToEnd().Trim();
        git.WaitForExit();
        return text;
    }

    private sealed record Measurement(string Workload, string Profile, string Scenario, DateTimeOffset Utc, bool Disks,
        string PixelFormat, int PixelBytes, double MedianMilliseconds, double P95Milliseconds, double MaximumMilliseconds,
        double CpuMillisecondsPerCapture, double AllocatedBytesPerCapture, long WorkingSetStartBytes,
        long PeakSampledWorkingSetBytes, string PixelSha256, string? ProjectedSceneSchemaVersion, string? SolarDiskAlgorithm);
}
