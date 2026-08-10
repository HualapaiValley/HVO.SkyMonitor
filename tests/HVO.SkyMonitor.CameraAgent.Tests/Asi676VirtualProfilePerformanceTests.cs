using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class Asi676VirtualProfilePerformanceTests
{
    private const int WarmupCount = 5;
    private const int MeasurementCount = 30;
    private static readonly DateTimeOffset FixtureUtc = new(2026, 7, 21, 8, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<string, string> ExpectedChecksums =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["virtual-asi676mm.full.json"] = "3FC06CC08F58E148CA03A14ADDC17FDF28E52FE5ADC14380AC04C09E9FA901E8",
            ["virtual-asi676mc.full.json"] = "B126CB15DA8FCD21D6F0F77EC47BDA0F925D9AFC0F6158055D57F3180F532C69"
        };
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [TestMethod]
    public async Task FullProfilesProduceDeterministicBoundedEvidence()
    {
        var measurements = new List<ProfileMeasurement>();
        foreach (var fileName in new[] { "virtual-asi676mm.full.json", "virtual-asi676mc.full.json" })
        {
            measurements.Add(await MeasureAsync(fileName).ConfigureAwait(false));
        }

        Assert.AreNotEqual(measurements[0].FinalSha256, measurements[1].FinalSha256);
        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "TestResults", "issue-183");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "asi676-profile-performance.json");
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
        {
            SchemaVersion = "issue-183-asi676-profile-performance-v1",
            Revision = ReadGit("rev-parse", "HEAD"),
            WorkingTreeStatus = ReadGit("status", "--short"),
            RecordedUtc = DateTimeOffset.UtcNow,
            Environment = new
            {
                Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                Runtime = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                ProcessorCount = Environment.ProcessorCount,
                WarmupCount,
                MeasurementCount
            },
            Measurements = measurements
        }, SerializerOptions)).ConfigureAwait(false);
        TestContext.WriteLine($"Issue #183 ASI676 evidence: {outputPath}");
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task<ProfileMeasurement> MeasureAsync(string fileName)
    {
        var config = await LoadProfileAsync(fileName).ConfigureAwait(false);
        var module = CreateModule();
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var setpoint = new CaptureSetpoint(
            config.Rig.Pipeline.NightExposure,
            config.Rig.Pipeline.NightGain,
            null,
            null);
        for (var index = 0; index < WarmupCount; index++)
        {
            _ = await CaptureAsync(module, setpoint, index).ConfigureAwait(false);
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuStart = process.TotalProcessorTime;
        var allocatedStart = GC.GetTotalAllocatedBytes(true);
        var workingSetStart = process.WorkingSet64;
        var maximumSampledWorkingSet = workingSetStart;
        var lohStart = GetLohSize();
        var durations = new double[MeasurementCount];
        CaptureResult? final = null;
        for (var index = 0; index < durations.Length; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            final = await CaptureAsync(module, setpoint, WarmupCount + index).ConfigureAwait(false);
            stopwatch.Stop();
            durations[index] = stopwatch.Elapsed.TotalMilliseconds;
            Assert.AreEqual(checked(3552 * 3552 * 2), final.Frame!.PixelData.Length);
            process.Refresh();
            maximumSampledWorkingSet = Math.Max(maximumSampledWorkingSet, process.WorkingSet64);
        }

        process.Refresh();
        var frame = final!.Frame!;
        var checksum = Convert.ToHexString(SHA256.HashData(frame.PixelData.Span));
        Assert.AreEqual(ExpectedChecksums[fileName], checksum);
        Assert.AreEqual(Asi676SensorModel.Version, frame.Metadata.Extra!["sensorModel"]);
        Assert.AreEqual("4095", frame.Metadata.Extra["whiteLevelAdu"]);
        Assert.IsTrue(MaximumSample(frame.PixelData.Span) <= 4095);
        Array.Sort(durations);
        return new ProfileMeasurement(
            fileName,
            config.Rig.Sensor.Name,
            config.Rig.Sensor.PixelFormat.ToString(),
            frame.PixelData.Length,
            durations[durations.Length / 2],
            durations[(int)Math.Ceiling(durations.Length * 0.95) - 1],
            MeasurementCount / (durations.Sum() / 1000),
            (process.TotalProcessorTime - cpuStart).TotalMilliseconds,
            (GC.GetTotalAllocatedBytes(false) - allocatedStart) / (double)MeasurementCount,
            workingSetStart,
            maximumSampledWorkingSet,
            process.WorkingSet64,
            lohStart,
            GetLohSize(),
            checksum);
    }

    private static VirtualSkyCameraModule CreateModule()
        => new(TimeProvider.System, new InMemoryCelestialCatalog([]), new ProjectedSceneStore());

    private static Task<CaptureResult> CaptureAsync(
        VirtualSkyCameraModule module,
        CaptureSetpoint setpoint,
        int index)
        => module.CaptureAsync(
            new CaptureRequest(
                FixtureUtc.AddSeconds(index),
                TimeSpan.FromSeconds(1),
                CaptureMode.Still,
                setpoint),
            CancellationToken.None);

    private static async Task<CameraModuleConfig> LoadProfileAsync(string fileName)
    {
        var loader = new FileCameraAgentConfigurationLoader(Options.Create(new CameraAgentHostOptions
        {
            ConfigFilePath = Path.Combine(AppContext.BaseDirectory, fileName),
            AgentId = "asi676-performance-test",
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled },
            Observatory = new ObservatoryLocation(35.347, -113.878, 1000, "America/Phoenix")
        }), NullLogger<FileCameraAgentConfigurationLoader>.Instance);
        return await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static long GetLohSize()
    {
        var generation = GC.GetGCMemoryInfo().GenerationInfo;
        return generation.Length > 3 ? generation[3].SizeAfterBytes : 0;
    }

    private static ushort MaximumSample(ReadOnlySpan<byte> pixels)
    {
        ushort maximum = 0;
        for (var offset = 0; offset < pixels.Length; offset += 2)
        {
            maximum = Math.Max(maximum, (ushort)(pixels[offset] | pixels[offset + 1] << 8));
        }
        return maximum;
    }

    private static string ReadGit(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start Git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Git failed: {error}");
        }
        return output.Trim();
    }

    private sealed record ProfileMeasurement(
        string FileName,
        string SensorName,
        string PixelFormat,
        int OutputBytes,
        double MedianMilliseconds,
        double P95Milliseconds,
        double FramesPerSecond,
        double CpuMilliseconds,
        double AllocatedBytesPerFrame,
        long WorkingSetStartBytes,
        long MaximumSampledWorkingSetBytes,
        long WorkingSetEndBytes,
        long LohStartBytes,
        long LohEndBytes,
        string FinalSha256);
}
