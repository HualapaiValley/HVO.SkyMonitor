using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Manual")]
public sealed partial class VirtualSkyCloudPerformanceTests
{
    private const int WarmupCount = 5;
    private static readonly DateTimeOffset FixtureUtc = new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions EvidenceSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly Dictionary<(string Workload, string Scenario), OutputChecksums> ExpectedChecksums =
        new Dictionary<(string Workload, string Scenario), OutputChecksums>
        {
            [("W1", "base")] = new(
                "3FBC9C0786E50F5C88468949000D9AAA97C32875B00009E28EBE88C360A291E1",
                "760BE91721EC3AEDA7282251808F5CB450DBF7393C626645774043AF7A4DBAB2"),
            [("W1", "explicit-clear")] = new(
                "FAB9A6BEA6DA154DEF8F23C192300B1222BF37B933EF83695A69C5CB27714EB5",
                "2036FB091BC451579B63A5823452378970BA49A6A13519C479260F053A215539"),
            [("W1", "partial-sustained")] = new(
                "1190FC57EF910CC45EFD1F9B3722E74792D78172514B0C67FDB364F5CE6574F0",
                "EC4244ACC53708D139F2B164F35BC7432CDC8F284DBB1B44700F419054584BF9"),
            [("W1", "transition")] = new(
                "41FB82F653B8A0ED6D91C946F4805AA8AEE21AECFAB17DE508F27720F6C51664",
                "AF20D0DC9A4698DDE9E9E8A2E2E9E7C306D71DC3B430F04A2EC76BE52D0443B3"),
            [("W1", "overcast")] = new(
                "7DEC92F9A133884D236B1A75627C3019A1C9D97E9E82A98083A957A781A058B9",
                "1E5A61480D1BDE4FADE2C19447A6E3AFB7A23E2758FF019214B1812A32FF508F"),
            [("W2", "base")] = new(
                "6CA0EE6A978B2044B96B88C9998A32344D0109E1FE60576DAB56F79F24F12A1B",
                "F41EE465B439395025CFE91A6FD67861F9AB3CAE58DC9D42826A6E203407AF1E"),
            [("W2", "explicit-clear")] = new(
                "8D6BE092D14FB0E5EB9283C7947852CBF39C82E054D76F3488CCEE16CFC5C75C",
                "6D4E916A5DB733AC13834E084A937DCBC3EFC6B3EAE6F94369EBDEC70861F49E"),
            [("W2", "partial-sustained")] = new(
                "83C8076903518D8C7F2E1272CD3FC35FE3F1FB0512924420462FADD2640B48BB",
                "82809739770CD11B670894074BF090174442E70AFA23FD2A1DEEAC60081B828B"),
            [("W2", "transition")] = new(
                "C97C4B8ACD717BEDA61C16F4C6956432D5630BBFF78FE25D07AAD90A9A79046F",
                "3A37D84CFAD9FAAB93F101D70646CCFFB0920D10DD28A61639BDA1EFA0F20E9D"),
            [("W2", "overcast")] = new(
                "05DDD55D8AE3CB982EC7F52EC4885396807CE369E82796CF3DC24D3A6EA6CC92",
                "92E0C264C3BF418686C41E269C78D754E82814D6CFA2C8B93ADB145590CD74BB")
        };

    [TestMethod]
    public async Task W1AndW2CloudRenderEvidence()
    {
        var measurements = new List<CloudRenderMeasurement>();
        foreach (var workload in new[] { Workload.W1, Workload.W2 })
        {
            foreach (var scenario in PerformanceScenario.All)
            {
                measurements.Add(await MeasureAsync(workload, scenario).ConfigureAwait(false));
            }
        }

        foreach (var workload in new[] { "W1", "W2" })
        {
            var workloadMeasurements = measurements.Where(item => item.Workload == workload).ToArray();
            var baseline = workloadMeasurements.Single(item => item.Scenario == "base");
            Assert.IsTrue(workloadMeasurements.All(item => item.OutputBytes == baseline.OutputBytes));
            Assert.IsTrue(workloadMeasurements.All(item => item.PeakWorkingSetBytes > 0));
            Assert.IsTrue(workloadMeasurements.All(item => item.MedianMilliseconds > 0));
        }

        var evidence = new
        {
            SchemaVersion = "issue-104-cloud-render-performance-v2",
            RecordedUtc = DateTimeOffset.UtcNow,
            Environment = new
            {
                Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                Runtime = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                ProcessorCount = Environment.ProcessorCount,
                WarmupCount,
                SequenceCheckpoints = new[] { 10, 30 }
            },
            Correctness = "Each measured frame has the exact workload byte length and a retained final SHA-256.",
            BufferOwnership = new
            {
                W1 = "one double signal plane plus one raw byte buffer; cloud effects evaluated on demand",
                W2Baseline = "three double channel planes plus one raw byte buffer",
                W2Cloud = "three double channel planes plus one temporary two-float cloud-effect map plus one raw byte buffer; no map is persisted"
            },
            Measurements = measurements
        };
        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "TestResults", "issue-104");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "cloud-render-performance.json");
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(evidence, EvidenceSerializerOptions))
            .ConfigureAwait(false);
        TestContext.WriteLine($"Issue #104 performance evidence: {outputPath}");
        foreach (var item in measurements)
        {
            TestContext.WriteLine(
                $"{item.Workload} scenario={item.Scenario}: median={item.MedianMilliseconds:F2}ms, " +
                $"p95={item.P95Milliseconds:F2}ms, fps={item.FramesPerSecond:F3}, " +
                $"alloc/frame={item.AllocatedBytesPerFrame:F0}, peakRSS={item.PeakWorkingSetBytes}");
        }
    }

    [TestMethod]
    public async Task CloudObservationCanonicalizationAndEnqueueEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-cloud-observation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        SqliteEnvironmentalObservationOutbox? outbox = null;
        try
        {
            outbox = new SqliteEnvironmentalObservationOutbox();
            var publisher = new PerformanceObservationPublisher(outbox, root);
            var step = new VirtualSkyCloudObservationProcessingStep(
                new CaptureProcessingStepMetadata(
                    "CloudObservation",
                    typeof(VirtualSkyCloudObservationProcessingStep).FullName!,
                    10),
                new VirtualSkyCloudObservationProcessingStepOptions(),
                publisher);
            var scenario = PerformanceScenario.All.Single(static item => item.Id == "partial-sustained");
            var definition = scenario.Definition! with
            {
                ScenarioId = scenario.Definition!.ComputeCanonicalScenarioId()
            };
            var config = CreateConfig(Workload.W1, scenario);
            for (var index = 0; index < WarmupCount; index++)
            {
                await step.ProcessAsync(
                    CreateObservationContext(config, definition, scenario.Exposure, index),
                    CancellationToken.None).ConfigureAwait(false);
            }

            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var cpuStart = process.TotalProcessorTime;
            var allocatedStart = GC.GetTotalAllocatedBytes(true);
            var workingSetStart = process.WorkingSet64;
            var peakWorkingSet = workingSetStart;
            var gen0Start = GC.CollectionCount(0);
            var gen1Start = GC.CollectionCount(1);
            var gen2Start = GC.CollectionCount(2);
            var durations = new double[30];
            for (var index = 0; index < durations.Length; index++)
            {
                var stopwatch = Stopwatch.StartNew();
                await step.ProcessAsync(
                    CreateObservationContext(config, definition, scenario.Exposure, WarmupCount + index),
                    CancellationToken.None).ConfigureAwait(false);
                stopwatch.Stop();
                durations[index] = stopwatch.Elapsed.TotalMilliseconds;
                process.Refresh();
                peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
            }
            process.Refresh();
            var cpu = process.TotalProcessorTime - cpuStart;
            var allocated = GC.GetTotalAllocatedBytes(false) - allocatedStart;
            var workingSetEnd = process.WorkingSet64;
            var snapshot = await outbox.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(WarmupCount + durations.Length, snapshot.PendingCount);
            Assert.AreEqual(WarmupCount + durations.Length, publisher.EnqueuedCount);
            Array.Sort(durations);
            var p95 = durations[(int)Math.Ceiling(durations.Length * 0.95) - 1];
            Assert.IsTrue(p95 < 25_000, $"Observation enqueue p95 {p95:F3} ms exceeded the 25-second cadence.");
            var evidence = new
            {
                SchemaVersion = "issue-104-cloud-observation-enqueue-performance-v1",
                RecordedUtc = DateTimeOffset.UtcNow,
                WarmupCount,
                MeasurementCount = durations.Length,
                MedianMilliseconds = durations[durations.Length / 2],
                P95Milliseconds = p95,
                OperationsPerSecond = durations.Length / (durations.Sum() / 1000),
                CpuMilliseconds = cpu.TotalMilliseconds,
                AllocatedBytesPerOperation = allocated / (double)durations.Length,
                WorkingSetStartBytes = workingSetStart,
                PeakWorkingSetBytes = peakWorkingSet,
                WorkingSetEndBytes = workingSetEnd,
                Gen0Collections = GC.CollectionCount(0) - gen0Start,
                Gen1Collections = GC.CollectionCount(1) - gen1Start,
                Gen2Collections = GC.CollectionCount(2) - gen2Start,
                PendingCount = snapshot.PendingCount,
                PendingBytes = snapshot.PendingBytes,
                PublisherPayloadBytes = publisher.PayloadBytes,
                SqliteFileBytes = Directory.EnumerateFiles(Path.Combine(root, ".environment"))
                    .Sum(static path => new FileInfo(path).Length)
            };
            var outputDirectory = Path.Combine(AppContext.BaseDirectory, "TestResults", "issue-104");
            Directory.CreateDirectory(outputDirectory);
            var outputPath = Path.Combine(outputDirectory, "cloud-observation-enqueue-performance.json");
            await File.WriteAllTextAsync(
                outputPath,
                JsonSerializer.Serialize(evidence, EvidenceSerializerOptions)).ConfigureAwait(false);
            TestContext.WriteLine($"Issue #104 observation evidence: {outputPath}");
        }
        finally
        {
            outbox?.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task<CloudRenderMeasurement> MeasureAsync(
        Workload workload,
        PerformanceScenario scenario)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var config = CreateConfig(workload, scenario);
        var sceneStore = new ProjectedSceneStore();
        var module = new VirtualSkyCameraModule(
            TimeProvider.System,
            new InMemoryCelestialCatalog([]),
            sceneStore);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var setpoint = new CaptureSetpoint(scenario.Exposure, 0, null, null);
        for (var index = 0; index < WarmupCount; index++)
        {
            _ = await CaptureAsync(module, setpoint, index).ConfigureAwait(false);
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSetStart = process.WorkingSet64;
        var memoryStart = GC.GetGCMemoryInfo();
        var managedHeapStart = memoryStart.HeapSizeBytes;
        var lohStart = memoryStart.GenerationInfo[3];
        var managedLiveStart = GC.GetTotalMemory(forceFullCollection: false);
        var peakWorkingSet = workingSetStart;
        var cpuStart = process.TotalProcessorTime;
        var allocatedStart = GC.GetTotalAllocatedBytes(true);
        var gen0Start = GC.CollectionCount(0);
        var gen1Start = GC.CollectionCount(1);
        var gen2Start = GC.CollectionCount(2);
        var durations = new double[scenario.MeasurementCount];
        CaptureResult? final = null;
        for (var index = 0; index < scenario.MeasurementCount; index++)
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
        var gen0Collections = GC.CollectionCount(0) - gen0Start;
        var gen1Collections = GC.CollectionCount(1) - gen1Start;
        var gen2Collections = GC.CollectionCount(2) - gen2Start;
        var finalSha256 = Convert.ToHexString(SHA256.HashData(final!.Frame!.PixelData.Span));
        var finalSceneId = final.Frame.Metadata.Extra!["sceneId"];
        Assert.IsTrue(sceneStore.TryGet(finalSceneId, out var scene));
        final = null;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        process.Refresh();
        var workingSetEnd = process.WorkingSet64;
        var memoryEnd = GC.GetGCMemoryInfo();
        var managedHeapEnd = memoryEnd.HeapSizeBytes;
        var lohEnd = memoryEnd.GenerationInfo[3];
        var managedLiveEnd = GC.GetTotalMemory(forceFullCollection: false);
        var firstTenDurations = durations[..10].ToArray();
        Array.Sort(firstTenDurations);
        Array.Sort(durations);
        var totalSeconds = durations.Sum() / 1000;
        var renderOnly = MeasureRenderOnly(workload, scenario, scene!);
        var expectedChecksums = ExpectedChecksums[(workload.Id, scenario.Id)];
        Assert.AreEqual(expectedChecksums.Complete, finalSha256);
        Assert.AreEqual(expectedChecksums.RenderOnly, renderOnly.FinalSha256);
        return new CloudRenderMeasurement(
            workload.Id,
            scenario.Id,
            scenario.Definition is not null,
            workload.Width,
            workload.Height,
            workload.PixelFormat.ToString(),
            scenario.Exposure.TotalSeconds,
            scenario.Definition?.Octaves ?? 0,
            scenario.Definition?.TemporalSampleCount ?? 0,
            scenario.MeasurementCount,
            workload.OutputBytes,
            durations[scenario.MeasurementCount / 2],
            durations[(int)Math.Ceiling(scenario.MeasurementCount * 0.95) - 1],
            firstTenDurations[5],
            scenario.MeasurementCount / totalSeconds,
            cpu.TotalMilliseconds,
            allocated / (double)scenario.MeasurementCount,
            workingSetStart,
            peakWorkingSet,
            workingSetEnd,
            workingSetEnd - workingSetStart,
            managedHeapStart,
            managedHeapEnd,
            managedHeapEnd - managedHeapStart,
            managedLiveStart,
            managedLiveEnd,
            managedLiveEnd - managedLiveStart,
            lohStart.SizeAfterBytes,
            lohEnd.SizeAfterBytes,
            lohEnd.SizeAfterBytes - lohStart.SizeAfterBytes,
            lohStart.FragmentationAfterBytes,
            lohEnd.FragmentationAfterBytes,
            gen0Collections,
            gen1Collections,
            gen2Collections,
            finalSha256,
            renderOnly);
    }

    private static RenderOnlyMeasurement MeasureRenderOnly(
        Workload workload,
        PerformanceScenario scenario,
        VisibleScene scene)
    {
        var layout = new ImageLayout(
            workload.Width,
            workload.Height,
            workload.PixelFormat,
            workload.Width * ImageLayout.BytesPerPixel(workload.PixelFormat));
        var cloudField = scenario.Definition is null ? null : new VirtualCloudField(scenario.Definition);
        for (var index = 0; index < WarmupCount; index++)
        {
            _ = Render(workload, scenario, scene, layout, cloudField, index);
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var lohStart = GC.GetGCMemoryInfo().GenerationInfo[3];
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuStart = process.TotalProcessorTime;
        var allocatedStart = GC.GetTotalAllocatedBytes(true);
        var peakWorkingSet = process.WorkingSet64;
        var durations = new double[30];
        SceneRenderResult? final = null;
        for (var index = 0; index < durations.Length; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            final = Render(workload, scenario, scene, layout, cloudField, WarmupCount + index);
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
        final = null;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var lohEnd = GC.GetGCMemoryInfo().GenerationInfo[3];
        Array.Sort(durations);
        return new RenderOnlyMeasurement(
            durations.Length,
            durations[durations.Length / 2],
            durations[(int)Math.Ceiling(durations.Length * 0.95) - 1],
            durations.Length / (durations.Sum() / 1000),
            cpu.TotalMilliseconds,
            allocated / durations.Length,
            peakWorkingSet,
            finalSha256,
            lohStart.SizeAfterBytes,
            lohEnd.SizeAfterBytes,
            lohEnd.SizeAfterBytes - lohStart.SizeAfterBytes,
            lohStart.FragmentationAfterBytes,
            lohEnd.FragmentationAfterBytes);
    }

    private static SceneRenderResult Render(
        Workload workload,
        PerformanceScenario scenario,
        VisibleScene scene,
        ImageLayout layout,
        VirtualCloudField? cloudField,
        int index)
    {
        var cloud = cloudField is null
            ? null
            : new VirtualCloudRenderContext(
                cloudField,
                FixtureUtc.AddSeconds(index * 5),
                scenario.Exposure);
        return workload == Workload.W1
            ? Mono16SceneRenderer.Render(scene, layout, new Mono16SceneRenderOptions
            {
                ExposureSeconds = scenario.Exposure.TotalSeconds,
                MagnitudeZeroElectronsPerSecond = 300,
                BackgroundElectronsPerSecond = 2,
                VignettingStrength = 0.25,
                ShotNoiseEnabled = true,
                Seed = 2025,
                SensorResponse = Asi174MmSensorModel.Resolve(0, 64),
                Cloud = cloud
            })
            : BayerRggb16Renderer.Render(scene, layout, new BayerRggb16RenderOptions
            {
                ExposureSeconds = scenario.Exposure.TotalSeconds,
                MagnitudeZeroElectronsPerSecond = 18_000,
                BackgroundElectronsPerSecond = 2,
                VignettingStrength = 0.25,
                ShotNoiseEnabled = true,
                Seed = 2025,
                SensorResponse = Asi178McSensorModel.Resolve(0, 64),
                Cloud = cloud
            });
    }

    private static Task<CaptureResult> CaptureAsync(
        VirtualSkyCameraModule module,
        CaptureSetpoint setpoint,
        int index)
        => module.CaptureAsync(
            new CaptureRequest(
                FixtureUtc.AddSeconds(index * 5),
                TimeSpan.FromSeconds(5),
                CaptureMode.Still,
                setpoint),
            CancellationToken.None);

    private static CaptureProcessingContext CreateObservationContext(
        CameraModuleConfig config,
        VirtualCloudScenarioDefinition definition,
        TimeSpan exposure,
        int index)
    {
        var startUtc = FixtureUtc.AddSeconds(index * 5);
        var parameters = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(definition));
        var parametersSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(parameters);
        var cloud = new CloudScenarioProvenance(
            definition.SchemaVersion,
            definition.ScenarioId,
            definition.ScenarioVersion,
            VirtualCloudScenarioDefinition.CurrentAlgorithmVersion,
            parametersSha256,
            definition.Seed,
            definition.EpochUtc,
            startUtc,
            startUtc + exposure,
            definition.TemporalSampleCount,
            parameters);
        var scene = new SceneProvenance(
            $"scene-{index}",
            "performance-rig-v1",
            "fixture",
            "1",
            new string('A', 64),
            "EquidistantFisheye",
            "projection-v1",
            "astronomy-v1",
            "sensor-v1",
            CloudScenario: cloud);
        var setpoint = new CaptureSetpoint(exposure, 0, null, null);
        var frame = new CameraFrame(
            startUtc,
            1,
            1,
            CameraPixelFormat.Mono16,
            new byte[2],
            new FrameMetadata(exposure, 0, double.NaN, "VirtualSky", Scene: scene),
            2);
        var request = new CaptureRequest(startUtc, TimeSpan.FromSeconds(5), CaptureMode.Still, setpoint);
        var result = new CaptureResult(frame, setpoint, TimeSpan.Zero, CaptureMode.Still, false);
        return new CaptureProcessingContext(
            config,
            new CaptureLoopSubmission(request, result, startUtc, request.TargetInterval, TimeSpan.Zero));
    }

    private static CameraModuleConfig CreateConfig(Workload workload, PerformanceScenario scenario)
    {
        var options = JsonSerializer.SerializeToElement(new
        {
            seed = 2025,
            maximumResults = 1,
            magnitudeZeroElectronsPerSecond = workload == Workload.W1 ? 300d : 18_000d,
            backgroundElectronsPerSecond = 2d,
            shotNoiseEnabled = false,
            darkCurrentElectronsPerSecond = 0d,
            cloudScenario = scenario.Definition,
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

    private sealed record CloudRenderMeasurement(
        string Workload,
        string Scenario,
        bool CloudEnabled,
        int Width,
        int Height,
        string PixelFormat,
        double ExposureSeconds,
        int Octaves,
        int TemporalSampleCount,
        int MeasurementCount,
        int OutputBytes,
        double MedianMilliseconds,
        double P95Milliseconds,
        double FirstTenMedianMilliseconds,
        double FramesPerSecond,
        double CpuMilliseconds,
        double AllocatedBytesPerFrame,
        long WorkingSetStartBytes,
        long PeakWorkingSetBytes,
        long WorkingSetEndBytes,
        long WorkingSetGrowthBytes,
        long ManagedHeapStartBytes,
        long ManagedHeapEndBytes,
        long ManagedHeapGrowthBytes,
        long ManagedLiveStartBytes,
        long ManagedLiveEndBytes,
        long ManagedLiveGrowthBytes,
        long LohStartBytes,
        long LohEndBytes,
        long LohGrowthBytes,
        long LohFragmentationStartBytes,
        long LohFragmentationEndBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        string FinalSha256,
        RenderOnlyMeasurement RenderOnly);

    private sealed record RenderOnlyMeasurement(
        int MeasurementCount,
        double MedianMilliseconds,
        double P95Milliseconds,
        double FramesPerSecond,
        double CpuMilliseconds,
        double AllocatedBytesPerFrame,
        long PeakWorkingSetBytes,
        string FinalSha256,
        long LohStartBytes,
        long LohEndBytes,
        long LohGrowthBytes,
        long LohFragmentationStartBytes,
        long LohFragmentationEndBytes);

    private sealed record OutputChecksums(string Complete, string RenderOnly);

    private sealed class PerformanceObservationPublisher(
        IEnvironmentalObservationOutbox outbox,
        string root) : IEnvironmentalObservationPublisher
    {
        private static readonly EnvironmentalObservationTarget Target = new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"));

        public int EnqueuedCount { get; private set; }
        public long PayloadBytes { get; private set; }

        public async ValueTask<EnvironmentalObservationPublishResult> PublishAsync(
            EnvironmentalObservationFactV1 fact,
            CancellationToken cancellationToken = default)
        {
            var observation = fact.Enrich(Target);
            var payload = EnvironmentalObservationJson.Serialize(observation);
            var disposition = await outbox.EnqueueAsync(root, observation, cancellationToken).ConfigureAwait(false);
            PayloadBytes += payload.Length;
            EnqueuedCount++;
            return new EnvironmentalObservationPublishResult(
                disposition == EnvironmentalObservationEnqueueDisposition.Enqueued
                    ? EnvironmentalObservationPublishDisposition.Enqueued
                    : EnvironmentalObservationPublishDisposition.Duplicate,
                observation);
        }
    }

    private sealed record PerformanceScenario(
        string Id,
        TimeSpan Exposure,
        int MeasurementCount,
        VirtualCloudScenarioDefinition? Definition)
    {
        public static IReadOnlyList<PerformanceScenario> All { get; } =
        [
            new("base", TimeSpan.FromSeconds(1), 30, null),
            new("explicit-clear", TimeSpan.FromSeconds(0.1), 30, Cloud(
                "scn-104-3a2d5601", 1, 1,
                [new() { Coverage = 0, MaximumOpacity = 0, ScatterFraction = 0 }])),
            new("partial-sustained", TimeSpan.FromSeconds(1), 30, Cloud(
                "scn-104-6e2074ad", 3, 2,
                [new() { Coverage = 0.55, MaximumOpacity = 0.8, ScatterFraction = 0.1 }])),
            new("transition", TimeSpan.FromSeconds(4), 30, Cloud(
                "scn-104-91f743c8", 5, 8,
                [
                    new() { OffsetSeconds = 0, Coverage = 0, MaximumOpacity = 0, ScatterFraction = 0 },
                    new() { OffsetSeconds = 50, Coverage = 0.55, MaximumOpacity = 0.8, ScatterFraction = 0.1 },
                    new() { OffsetSeconds = 100, Coverage = 1, MaximumOpacity = 0.95, ScatterFraction = 0.15 }
                ])),
            new("overcast", TimeSpan.FromSeconds(10), 30, Cloud(
                "scn-104-d5b88a0e", 4, 4,
                [new() { Coverage = 1, MaximumOpacity = 0.95, ScatterFraction = 0.15 }]))
        ];

        private static VirtualCloudScenarioDefinition Cloud(
            string id,
            int octaves,
            int temporalSampleCount,
            IReadOnlyList<VirtualCloudKeyframe> keyframes)
            => new()
            {
                ScenarioId = id,
                ScenarioVersion = "1",
                Seed = 104,
                EpochUtc = FixtureUtc,
                SpatialFrequency = 3,
                DriftEastCellsPerSecond = 0.01,
                DriftNorthCellsPerSecond = -0.005,
                EvolutionCellsPerSecond = 0.001,
                Octaves = octaves,
                EdgeSoftness = 0.5,
                HorizonFadeDegrees = 5,
                TemporalSampleCount = temporalSampleCount,
                Keyframes = keyframes
            };
    }

    private sealed record Workload(
        string Id,
        int Width,
        int Height,
        CameraPixelFormat PixelFormat,
        double ImageCircleRadius,
        double? FocalLengthPixels)
    {
        public static Workload W1 { get; } = new("W1", 1936, 1216, CameraPixelFormat.Mono16, 595.84, null);
        public static Workload W2 { get; } = new("W2", 3096, 2080, CameraPixelFormat.BayerRggb16, 1187.5, 735.553926);
        public int OutputBytes => checked(Width * Height * 2);
    }
}
