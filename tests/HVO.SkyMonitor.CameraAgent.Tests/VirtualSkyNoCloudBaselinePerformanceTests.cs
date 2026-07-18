using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Manual")]
public sealed class VirtualSkyNoCloudBaselinePerformanceTests
{
    private const int WarmupCount = 5;
    private const int MeasurementCount = 30;
    private static readonly DateTimeOffset FixtureUtc = new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [TestMethod]
    public async Task W1AndW2NoCloudEvidence()
    {
        var revision = ReadGit("rev-parse", "HEAD");
        if (Environment.GetEnvironmentVariable("ISSUE104_BASELINE_REVISION") is { } expectedRevision)
        {
            Assert.AreEqual(expectedRevision, revision);
        }
        var measurements = new List<NoCloudMeasurement>();
        foreach (var workload in new[] { Workload.W1, Workload.W2 })
        {
            measurements.Add(await MeasureAsync(workload).ConfigureAwait(false));
        }

        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "TestResults", "issue-104");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "no-cloud-baseline-performance.json");
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
        {
            SchemaVersion = "issue-104-no-cloud-baseline-v1",
            Revision = revision,
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
        TestContext.WriteLine($"Issue #104 no-cloud evidence: {outputPath}");
    }

    public TestContext TestContext { get; set; } = null!;

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

    private static async Task<NoCloudMeasurement> MeasureAsync(Workload workload)
    {
        var sceneStore = new ProjectedSceneStore();
        var module = new VirtualSkyCameraModule(TimeProvider.System, new InMemoryCelestialCatalog([]), sceneStore);
        await module.InitializeAsync(CreateConfig(workload), CancellationToken.None).ConfigureAwait(false);
        var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 0, null, null);
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
        var peakWorkingSet = workingSetStart;
        var durations = new double[MeasurementCount];
        CaptureResult? final = null;
        for (var index = 0; index < durations.Length; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            final = await CaptureAsync(module, setpoint, WarmupCount + index).ConfigureAwait(false);
            stopwatch.Stop();
            durations[index] = stopwatch.Elapsed.TotalMilliseconds;
            Assert.AreEqual(workload.OutputBytes, final.Frame!.PixelData.Length);
            process.Refresh();
            peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
        }
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuStart;
        var allocated = GC.GetTotalAllocatedBytes(false) - allocatedStart;
        var finalSha256 = Convert.ToHexString(SHA256.HashData(final!.Frame!.PixelData.Span));
        Assert.AreEqual(workload.CompleteSha256, finalSha256);
        Assert.IsTrue(sceneStore.TryGet(final.Frame.Metadata.Extra!["sceneId"], out var scene));
        Array.Sort(durations);
        var renderOnly = MeasureRenderOnly(workload, scene!);
        return new NoCloudMeasurement(
            workload.Id,
            workload.Width,
            workload.Height,
            workload.PixelFormat.ToString(),
            workload.OutputBytes,
            durations[durations.Length / 2],
            durations[(int)Math.Ceiling(durations.Length * 0.95) - 1],
            MeasurementCount / (durations.Sum() / 1000),
            cpu.TotalMilliseconds,
            allocated / (double)MeasurementCount,
            workingSetStart,
            peakWorkingSet,
            process.WorkingSet64,
            finalSha256,
            renderOnly);
    }

    private static NoCloudRenderMeasurement MeasureRenderOnly(Workload workload, VisibleScene scene)
    {
        var layout = new ImageLayout(
            workload.Width,
            workload.Height,
            workload.PixelFormat,
            workload.Width * ImageLayout.BytesPerPixel(workload.PixelFormat));
        for (var index = 0; index < WarmupCount; index++)
        {
            _ = Render(workload, scene, layout);
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuStart = process.TotalProcessorTime;
        var allocatedStart = GC.GetTotalAllocatedBytes(true);
        var peakWorkingSet = process.WorkingSet64;
        var durations = new double[MeasurementCount];
        SceneRenderResult? final = null;
        for (var index = 0; index < durations.Length; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            final = Render(workload, scene, layout);
            stopwatch.Stop();
            durations[index] = stopwatch.Elapsed.TotalMilliseconds;
            Assert.AreEqual(workload.OutputBytes, final.Pixels.Length);
            process.Refresh();
            peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
        }
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuStart;
        var allocated = GC.GetTotalAllocatedBytes(false) - allocatedStart;
        var finalSha256 = Convert.ToHexString(SHA256.HashData(final!.Pixels.Span));
        Assert.AreEqual(workload.RenderSha256, finalSha256);
        Array.Sort(durations);
        return new NoCloudRenderMeasurement(
            durations[durations.Length / 2],
            durations[(int)Math.Ceiling(durations.Length * 0.95) - 1],
            MeasurementCount / (durations.Sum() / 1000),
            cpu.TotalMilliseconds,
            allocated / (double)MeasurementCount,
            peakWorkingSet,
            finalSha256);
    }

    private static SceneRenderResult Render(Workload workload, VisibleScene scene, ImageLayout layout)
        => workload == Workload.W1
            ? Mono16SceneRenderer.Render(scene, layout, new Mono16SceneRenderOptions
            {
                ExposureSeconds = 1,
                MagnitudeZeroElectronsPerSecond = 300,
                BackgroundElectronsPerSecond = 2,
                VignettingStrength = 0.25,
                ShotNoiseEnabled = true,
                Seed = 2025,
                SensorResponse = Asi174MmSensorModel.Resolve(0, 64)
            })
            : BayerRggb16Renderer.Render(scene, layout, new BayerRggb16RenderOptions
            {
                ExposureSeconds = 1,
                MagnitudeZeroElectronsPerSecond = 18_000,
                BackgroundElectronsPerSecond = 2,
                VignettingStrength = 0.25,
                ShotNoiseEnabled = true,
                Seed = 2025,
                SensorResponse = Asi178McSensorModel.Resolve(0, 64)
            });

    private static Task<CaptureResult> CaptureAsync(VirtualSkyCameraModule module, CaptureSetpoint setpoint, int index)
        => module.CaptureAsync(
            new CaptureRequest(
                FixtureUtc.AddSeconds(index * 5),
                TimeSpan.FromSeconds(5),
                CaptureMode.Still,
                setpoint),
            CancellationToken.None);

    private static CameraModuleConfig CreateConfig(Workload workload)
    {
        var options = JsonSerializer.SerializeToElement(new
        {
            seed = 2025,
            maximumResults = 1,
            magnitudeZeroElectronsPerSecond = workload == Workload.W1 ? 300d : 18_000d,
            backgroundElectronsPerSecond = 2d,
            shotNoiseEnabled = false,
            darkCurrentElectronsPerSecond = 0d,
            asi174Sensor = new { enabled = workload == Workload.W1, blackLevelAdu = 64d },
            asi178Sensor = new { enabled = workload == Workload.W2, blackLevelContainerAdu = 64d }
        });
        return new CameraModuleConfig(
            new ObservatoryLocation(35.347, -113.878, 1000, "America/Phoenix"),
            new CameraModuleDescriptor("VirtualSky", options),
            new CameraRigConfig(
                new SensorProfile(
                    workload.Id,
                    workload.Width,
                    workload.Height,
                    workload == Workload.W1 ? 5.86 : 2.4,
                    workload == Workload.W1 ? SensorColorMode.Mono : SensorColorMode.Color,
                    workload.PixelFormat,
                    workload == Workload.W1 ? SensorResponseMode.Monochrome : SensorResponseMode.BayerRaw,
                    workload.Width * 2,
                    SensorRecipeVersion: $"{workload.Id}-performance-v1"),
                new OpticsProfile(
                    "EquidistantFisheye",
                    workload == Workload.W1 ? 0 : 1.8,
                    workload == Workload.W1 ? 180 : 185,
                    0,
                    LensKind.Fisheye,
                    workload.Width / 2d,
                    workload.Height / 2d,
                    workload.ImageCircleRadius,
                    workload.FocalLengthPixels,
                    workload.FocalLengthPixels,
                    HorizontalFlip: true,
                    CalibrationVersion: $"{workload.Id}-performance-optics-v1"),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 0, 0)));
    }

    private sealed record NoCloudMeasurement(
        string Workload,
        int Width,
        int Height,
        string PixelFormat,
        int OutputBytes,
        double MedianMilliseconds,
        double P95Milliseconds,
        double FramesPerSecond,
        double CpuMilliseconds,
        double AllocatedBytesPerFrame,
        long WorkingSetStartBytes,
        long PeakWorkingSetBytes,
        long WorkingSetEndBytes,
        string FinalSha256,
        NoCloudRenderMeasurement RenderOnly);

    private sealed record NoCloudRenderMeasurement(
        double MedianMilliseconds,
        double P95Milliseconds,
        double FramesPerSecond,
        double CpuMilliseconds,
        double AllocatedBytesPerFrame,
        long PeakWorkingSetBytes,
        string FinalSha256);

    private sealed record Workload(
        string Id,
        int Width,
        int Height,
        CameraPixelFormat PixelFormat,
        double ImageCircleRadius,
        double? FocalLengthPixels,
        string CompleteSha256,
        string RenderSha256)
    {
        public static Workload W1 { get; } = new(
            "W1", 1936, 1216, CameraPixelFormat.Mono16, 595.84, null,
            "3FBC9C0786E50F5C88468949000D9AAA97C32875B00009E28EBE88C360A291E1",
            "760BE91721EC3AEDA7282251808F5CB450DBF7393C626645774043AF7A4DBAB2");
        public static Workload W2 { get; } = new(
            "W2", 3096, 2080, CameraPixelFormat.BayerRggb16, 1187.5, 735.553926,
            "6CA0EE6A978B2044B96B88C9998A32344D0109E1FE60576DAB56F79F24F12A1B",
            "F41EE465B439395025CFE91A6FD67861F9AB3CAE58DC9D42826A6E203407AF1E");
        public int OutputBytes => checked(Width * Height * 2);
    }
}
