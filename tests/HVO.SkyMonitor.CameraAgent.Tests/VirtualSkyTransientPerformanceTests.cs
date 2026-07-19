using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class VirtualSkyTransientPerformanceTests
{
    private const int WarmupCount = 5;
    private const int MeasurementCount = 30;
    private const int RuntimePrewarmCount = 64;
    private static readonly DateTimeOffset FixtureUtc = new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions EvidenceOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions StrictOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Dictionary<(string Workload, string Scenario), OutputChecksums> ExpectedChecksums = new()
    {
        [("W1", "none")] = new(
            "3FBC9C0786E50F5C88468949000D9AAA97C32875B00009E28EBE88C360A291E1",
            "760BE91721EC3AEDA7282251808F5CB450DBF7393C626645774043AF7A4DBAB2"),
        [("W2", "none")] = new(
            "6CA0EE6A978B2044B96B88C9998A32344D0109E1FE60576DAB56F79F24F12A1B",
            "F41EE465B439395025CFE91A6FD67861F9AB3CAE58DC9D42826A6E203407AF1E"),
        [("W1", "explicit-empty")] = new(
            "6CFEB7F014457728088B47098C3B394D90C40AF6EFC44B0BA96A6937DA0E4C2C",
            "760BE91721EC3AEDA7282251808F5CB450DBF7393C626645774043AF7A4DBAB2"),
        [("W1", "short-sky-track")] = new(
            "07F5CB01BE47E876D52C0F751BC053BC129489D87CC31D494982CDDA61C94769",
            "9D524D9AF92EFF144E8665B408D01FCCA44A1273DB4225AE0B1016E89459C83B"),
        [("W1", "saturated-fragmented-track")] = new(
            "525F1E343B3F9905598FFD3FFC3E438B63037775699692D304479378F73ACF32",
            "4A7BE062376AFB5683A525B8F597594F2B27FE04BD81D08B3DA40D17081DE909"),
        [("W1", "long-shadow-blink")] = new(
            "0FF696EE19A0EB5F99BBDB4C70BC7BEE2CA212A98D3E8EA086BE6476387F6D92",
            "C1C14FD690984A5E2401241E097797C35FB0DF6AA7FC81BED753DFB805933FFC"),
        [("W1", "sensor-artifacts")] = new(
            "909D38903EFD1D3A16E60BF5BDF711BDEAF3A551CAC0B9CB5D9AEBF35A5D5401",
            "A25C84AE719007B70DEC5409D24A1E832035DDEE88A4184BE1B6E3FE7C38FE85"),
        [("W1", "partial-cloud-sky-track")] = new(
            "9EE4B88918C7134F17EB85FD4F310E24B4621088F10873830805E1386B6872BF",
            "7B18D98A0AB792E2E84A62D5D79FCE6BD1D803982568625B437C1D093BE1336E"),
        [("W2", "explicit-empty")] = new(
            "8C835AABD3F7084F3E6AAAD966AECC54C71034EFA5C615AA63DEAB804F2D89F1",
            "F41EE465B439395025CFE91A6FD67861F9AB3CAE58DC9D42826A6E203407AF1E"),
        [("W2", "short-sky-track")] = new(
            "E61A9B8200DCBB96EAB582A3E4A333C54C2D38BAA96BE27CF3333B045D1B7EA5",
            "9F4ACC3B7F1D8E856CB54D7B2744EA0CB5988198F88538F41FB3C0A823F70811"),
        [("W2", "saturated-fragmented-track")] = new(
            "55EBEF180143126F68BE0B25DD28D977340B3EBFAD4E3D07F33301E57B6C9651",
            "56FFA92159B96DAA3291ED7F0693B39F023169E1B9BE85621F544D42A32FE0F3"),
        [("W2", "long-shadow-blink")] = new(
            "FE446F3361DB792EF8A53C59FF9F4312DC6936275EFABDBA9BB23BE1B0174817",
            "CE8C6A08904B2FC407276821DB0C0CAB7F641A54AEAD6FDE9F44ACD526CB8B8F"),
        [("W2", "sensor-artifacts")] = new(
            "F4DB35F956EEAF69F71BFB4DF9A853F2D58876F977888EDCDE7DA7D20272F888",
            "DD131D39B4F52AE5E3C8D47AD69A4497838449ADC5E61C5AD954172FA46B46BA"),
        [("W2", "partial-cloud-sky-track")] = new(
            "D500491C1DE6816224F26ED3AD62FD247DE94CEF085771B8165F8B88FE64CC14",
            "00A726E2ECA8DAC7BC18AB93DE532BD9B31B851202894EA65DB92010DEC79E3B")
    };
    private static readonly Dictionary<(string Workload, string Scenario), GeometryEvidence> ExpectedGeometry = new()
    {
        [("W1", "explicit-empty")] = new(0, 0),
        [("W1", "short-sky-track")] = new(786, 5452.998590998953),
        [("W1", "saturated-fragmented-track")] = new(2222, 19457093.509935968),
        [("W1", "long-shadow-blink")] = new(1602, 2747.2026949350225),
        [("W1", "sensor-artifacts")] = new(215, 1002250000.0000001),
        [("W1", "partial-cloud-sky-track")] = new(801, 7621.904720979555),
        [("W2", "explicit-empty")] = new(0, 0),
        [("W2", "short-sky-track")] = new(818, 327179.915459937),
        [("W2", "saturated-fragmented-track")] = new(3765, 1167425610.596158),
        [("W2", "long-shadow-blink")] = new(1624, 164832.16169610134),
        [("W2", "sensor-artifacts")] = new(215, 1002250000.0000001),
        [("W2", "partial-cloud-sky-track")] = new(1218, 457314.28325877327)
    };

    [TestMethod]
    public async Task W1AndW2TransientRenderEvidence()
    {
        foreach (var workload in new[] { Workload.W1, Workload.W2 })
        {
            await PrewarmRuntimeAsync(workload).ConfigureAwait(false);
        }
        var measurements = new List<TransientRenderMeasurement>();
        foreach (var workload in new[] { Workload.W1, Workload.W2 })
        {
            foreach (var scenario in PerformanceScenario.All)
            {
                measurements.Add(await MeasureAsync(workload, scenario).ConfigureAwait(false));
            }
        }

        foreach (var measurement in measurements)
        {
            Assert.AreEqual(measurement.ExpectedOutputBytes, measurement.OutputBytes);
            Assert.IsLessThan(25_000, measurement.P95Milliseconds,
                $"{measurement.Workload}/{measurement.Scenario} complete p95 exceeded the configured cadence.");
            Assert.IsLessThan(25_000, measurement.RenderOnly.P95Milliseconds,
                $"{measurement.Workload}/{measurement.Scenario} render p95 exceeded the configured cadence.");
            if (measurement.TransientEnabled && measurement.Scenario != "explicit-empty")
            {
                Assert.IsGreaterThan(0, measurement.ActiveTransientPixels);
                Assert.IsGreaterThan(0d, measurement.DepositedElectrons);
            }
            else
            {
                Assert.AreEqual(0, measurement.ActiveTransientPixels);
            }
        }
        Assert.AreEqual(14, ExpectedChecksums.Count);
        Assert.AreEqual(12, ExpectedGeometry.Count);

        var revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION") ?? "working-tree";
        var outputDirectory = Path.Combine(GetRepositoryRoot(), "TestResults", "issue-61", revision);
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "transient-render-performance.json");
        var evidence = new
        {
            SchemaVersion = "issue-61-transient-render-performance-v1",
            RecordedUtc = DateTimeOffset.UtcNow,
            Revision = ReadGit("rev-parse HEAD"),
            DirtyState = ReadGit("status --short"),
            Environment = new
            {
                Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                Runtime = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                Environment.ProcessorCount,
                RuntimePrewarmCount,
                WarmupCount,
                MeasurementCount
            },
            Correctness = "Every trial checks the exact workload byte length; final raw checksums and sparse signal geometry are retained.",
            BufferOwnership = new
            {
                W1 = "one renderer signal plane plus one raw buffer; transient support is sparse",
                W2 = "three renderer channel planes plus one raw buffer; transient support is sparse",
                CombinedCloud = "the existing two-float cloud effect map is temporary; no transient truth plane is allocated",
                TransientStorage = "Dictionary<int, VirtualTransientPixelSignal>; no dense transient plane or truth mask"
            },
            MemorySampling = "RSS is sampled after each operation; heap and LOH values are post-operation last-GC snapshots, not in-render peaks.",
            BaselineComparison = CreateBaselineComparison(measurements),
            Measurements = measurements
        };
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(evidence, EvidenceOptions)).ConfigureAwait(false);
        TestContext.WriteLine($"Issue #61 performance evidence: {outputPath}");
        foreach (var item in measurements)
        {
            TestContext.WriteLine(
                $"{item.Workload}/{item.Scenario}: complete={item.MedianMilliseconds:F2}/{item.P95Milliseconds:F2}ms " +
                $"sha={item.FinalSha256}; render={item.RenderOnly.MedianMilliseconds:F2}/{item.RenderOnly.P95Milliseconds:F2}ms " +
                $"sha={item.RenderOnly.FinalSha256}; active={item.ActiveTransientPixels}");
        }
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task PrewarmRuntimeAsync(Workload workload)
    {
        foreach (var scenario in PerformanceScenario.All.Where(static item => item.Id is "none" or "short-sky-track"))
        {
            var sceneStore = new ProjectedSceneStore();
            var module = new VirtualSkyCameraModule(TimeProvider.System, new InMemoryCelestialCatalog([]), sceneStore);
            await module.InitializeAsync(CreateConfig(workload, scenario), CancellationToken.None).ConfigureAwait(false);
            var capture = await module.CaptureAsync(
                new CaptureRequest(
                    FixtureUtc,
                    TimeSpan.FromSeconds(25),
                    CaptureMode.Still,
                    new CaptureSetpoint(TimeSpan.FromSeconds(1), 0, null, null)),
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(sceneStore.TryGet(capture.Frame!.Metadata.Extra!["sceneId"], out var scene));
            var layout = Layout(workload);
            var cloudField = scenario.Cloud is null ? null : new VirtualCloudField(scenario.Cloud);
            var transientScenario = scenario.Transient is null ? null : new VirtualTransientScenario(scenario.Transient);
            for (var index = 0; index < RuntimePrewarmCount; index++)
            {
                _ = Render(workload, scenario, scene!, layout, cloudField, transientScenario);
            }
        }
    }

    private static async Task<TransientRenderMeasurement> MeasureAsync(
        Workload workload,
        PerformanceScenario scenario)
    {
        var configMeasurement = MeasureConfiguration(workload, scenario);
        var config = CreateConfig(workload, scenario);
        var sceneStore = new ProjectedSceneStore();
        var module = new VirtualSkyCameraModule(TimeProvider.System, new InMemoryCelestialCatalog([]), sceneStore);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var request = new CaptureRequest(
            FixtureUtc,
            TimeSpan.FromSeconds(25),
            CaptureMode.Still,
            new CaptureSetpoint(TimeSpan.FromSeconds(1), 0, null, null));
        for (var index = 0; index < WarmupCount; index++)
        {
            _ = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSetStart = process.WorkingSet64;
        var memoryStart = GC.GetGCMemoryInfo();
        var managedLiveStart = GC.GetTotalMemory(false);
        var peakWorkingSet = workingSetStart;
        var cpuStart = process.TotalProcessorTime;
        var allocatedStart = GC.GetTotalAllocatedBytes(true);
        var gen0Start = GC.CollectionCount(0);
        var gen1Start = GC.CollectionCount(1);
        var gen2Start = GC.CollectionCount(2);
        var durations = new double[MeasurementCount];
        CaptureResult? final = null;
        for (var index = 0; index < durations.Length; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            final = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
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
        var workingSetEnd = process.WorkingSet64;
        var memoryEnd = GC.GetGCMemoryInfo();
        var managedLiveEnd = GC.GetTotalMemory(false);
        var finalSha256 = Convert.ToHexString(SHA256.HashData(final!.Frame!.PixelData.Span));
        var sceneId = final.Frame.Metadata.Extra!["sceneId"];
        Assert.IsTrue(sceneStore.TryGet(sceneId, out var scene));
        var provenanceBytes = final.Frame.Metadata.Scene?.TransientScenario is null
            ? 0
            : JsonSerializer.SerializeToUtf8Bytes(final.Frame.Metadata.Scene.TransientScenario).Length;
        final = null;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var renderOnly = MeasureRenderOnly(workload, scenario, scene!);
        var geometry = MeasureGeometry(workload, scenario, scene!);
        if (scenario.Transient is not null)
        {
            var expectedGeometry = ExpectedGeometry[(workload.Id, scenario.Id)];
            Assert.AreEqual(expectedGeometry.ActivePixels, geometry.ActivePixels);
            Assert.AreEqual(expectedGeometry.DepositedElectrons, geometry.DepositedElectrons, 1e-6);
        }
        Array.Sort(durations);
        AssertExpectedChecksum(workload.Id, scenario.Id, finalSha256, renderOnly.FinalSha256);
        var totalSeconds = durations.Sum() / 1000;
        return new TransientRenderMeasurement(
            workload.Id,
            scenario.Id,
            scenario.Transient is not null,
            scenario.Cloud is not null,
            workload.Width,
            workload.Height,
            workload.PixelFormat.ToString(),
            workload.OutputBytes,
            workload.OutputBytes,
            MeasurementCount,
            durations[durations.Length / 2],
            durations[(int)Math.Ceiling(durations.Length * 0.95) - 1],
            MeasurementCount / totalSeconds,
            workload.OutputBytes * MeasurementCount / totalSeconds / (1024 * 1024),
            cpu.TotalMilliseconds,
            cpu.TotalMilliseconds / MeasurementCount,
            allocated / (double)MeasurementCount,
            workingSetStart,
            peakWorkingSet,
            workingSetEnd,
            memoryStart.HeapSizeBytes,
            memoryEnd.HeapSizeBytes,
            managedLiveStart,
            managedLiveEnd,
            memoryStart.GenerationInfo[3].SizeAfterBytes,
            memoryEnd.GenerationInfo[3].SizeAfterBytes,
            memoryStart.GenerationInfo[3].FragmentationAfterBytes,
            memoryEnd.GenerationInfo[3].FragmentationAfterBytes,
            gen0Collections,
            gen1Collections,
            gen2Collections,
            config.ModuleOptions?.GetRawText().Length ?? 0,
            configMeasurement.SourceBytes,
            configMeasurement.CanonicalBytes,
            provenanceBytes,
            scenario.Transient?.TemporalSampleCount ?? 0,
            scenario.Transient?.SkyTracks.Count ?? 0,
            scenario.Transient?.SensorTracks.Count ?? 0,
            geometry.ActivePixels,
            geometry.DepositedElectrons,
            scenario.Cloud is not null && workload == Workload.W2
                ? checked((long)workload.Width * workload.Height * 2 * sizeof(float))
                : 0,
            finalSha256,
            configMeasurement,
            renderOnly);
    }

    private static RenderOnlyMeasurement MeasureRenderOnly(
        Workload workload,
        PerformanceScenario scenario,
        VisibleScene scene)
    {
        var layout = Layout(workload);
        var cloudField = scenario.Cloud is null ? null : new VirtualCloudField(scenario.Cloud);
        var transientScenario = scenario.Transient is null ? null : new VirtualTransientScenario(scenario.Transient);
        for (var index = 0; index < WarmupCount; index++)
        {
            _ = Render(workload, scenario, scene, layout, cloudField, transientScenario);
        }
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSetStart = process.WorkingSet64;
        var memoryStart = GC.GetGCMemoryInfo();
        var cpuStart = process.TotalProcessorTime;
        var allocatedStart = GC.GetTotalAllocatedBytes(true);
        var peakWorkingSet = workingSetStart;
        var durations = new double[MeasurementCount];
        SceneRenderResult? final = null;
        for (var index = 0; index < durations.Length; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            final = Render(workload, scenario, scene, layout, cloudField, transientScenario);
            stopwatch.Stop();
            durations[index] = stopwatch.Elapsed.TotalMilliseconds;
            Assert.AreEqual(workload.OutputBytes, final.Pixels.Length);
            process.Refresh();
            peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
        }
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuStart;
        var allocated = GC.GetTotalAllocatedBytes(false) - allocatedStart;
        var workingSetEnd = process.WorkingSet64;
        var memoryEnd = GC.GetGCMemoryInfo();
        var hash = Convert.ToHexString(SHA256.HashData(final!.Pixels.Span));
        final = null;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Array.Sort(durations);
        var totalSeconds = durations.Sum() / 1000;
        return new RenderOnlyMeasurement(
            MeasurementCount,
            durations[durations.Length / 2],
            durations[(int)Math.Ceiling(durations.Length * 0.95) - 1],
            MeasurementCount / totalSeconds,
            workload.OutputBytes * MeasurementCount / totalSeconds / (1024 * 1024),
            cpu.TotalMilliseconds,
            cpu.TotalMilliseconds / MeasurementCount,
            allocated / MeasurementCount,
            workingSetStart,
            peakWorkingSet,
            workingSetEnd,
            memoryStart.GenerationInfo[3].SizeAfterBytes,
            memoryEnd.GenerationInfo[3].SizeAfterBytes,
            memoryStart.GenerationInfo[3].FragmentationAfterBytes,
            memoryEnd.GenerationInfo[3].FragmentationAfterBytes,
            hash);
    }

    private static ConfigurationMeasurement MeasureConfiguration(Workload workload, PerformanceScenario scenario)
    {
        if (scenario.Transient is null)
        {
            return new ConfigurationMeasurement(0, 0, 0, 0, 0, 0);
        }
        var source = JsonSerializer.SerializeToUtf8Bytes(scenario.Transient);
        var durations = new double[MeasurementCount];
        var allocatedStart = GC.GetTotalAllocatedBytes(true);
        for (var index = 0; index < durations.Length; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            var definition = JsonSerializer.Deserialize<VirtualTransientScenarioDefinition>(source, StrictOptions)!;
            definition.ValidateSensorBounds(workload.Width, workload.Height);
            _ = definition.ComputeCanonicalScenarioId();
            _ = definition.ComputeParametersSha256();
            _ = new VirtualTransientScenario(definition);
            stopwatch.Stop();
            durations[index] = stopwatch.Elapsed.TotalMilliseconds;
        }
        var allocated = GC.GetTotalAllocatedBytes(false) - allocatedStart;
        var canonical = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(scenario.Transient));
        Array.Sort(durations);
        return new ConfigurationMeasurement(
            source.Length,
            Encoding.UTF8.GetByteCount(canonical.GetRawText()),
            MeasurementCount,
            durations[durations.Length / 2],
            durations[(int)Math.Ceiling(durations.Length * 0.95) - 1],
            allocated / (double)MeasurementCount);
    }

    private static (int ActivePixels, double DepositedElectrons) MeasureGeometry(
        Workload workload,
        PerformanceScenario scenario,
        VisibleScene scene)
    {
        if (scenario.Transient is null)
        {
            return (0, 0);
        }
        var cloud = scenario.Cloud is null
            ? null
            : new VirtualCloudRenderContext(new VirtualCloudField(scenario.Cloud), FixtureUtc, TimeSpan.FromSeconds(1));
        var signal = VirtualTransientSignalRenderer.Render(
            scene,
            Layout(workload),
            new VirtualTransientRenderContext(new VirtualTransientScenario(scenario.Transient), FixtureUtc, TimeSpan.FromSeconds(1)),
            workload == Workload.W1 ? 300 : 18_000,
            1,
            4,
            cloud);
        return (signal.ActivePixelCount, signal.Geometry.Sum(static item => item.DepositedElectrons));
    }

    private static SceneRenderResult Render(
        Workload workload,
        PerformanceScenario scenario,
        VisibleScene scene,
        ImageLayout layout,
        VirtualCloudField? cloudField,
        VirtualTransientScenario? transientScenario)
    {
        var cloud = cloudField is null
            ? null
            : new VirtualCloudRenderContext(cloudField, FixtureUtc, TimeSpan.FromSeconds(1));
        var transient = transientScenario is null
            ? null
            : new VirtualTransientRenderContext(
                transientScenario, FixtureUtc, TimeSpan.FromSeconds(1));
        return workload == Workload.W1
            ? Mono16SceneRenderer.Render(scene, layout, new Mono16SceneRenderOptions
            {
                ExposureSeconds = 1,
                MagnitudeZeroElectronsPerSecond = 300,
                BackgroundElectronsPerSecond = 2,
                VignettingStrength = 0.25,
                ShotNoiseEnabled = true,
                Seed = 2025,
                SensorResponse = Asi174MmSensorModel.Resolve(0, 64),
                Cloud = cloud,
                Transient = transient
            })
            : BayerRggb16Renderer.Render(scene, layout, new BayerRggb16RenderOptions
            {
                ExposureSeconds = 1,
                MagnitudeZeroElectronsPerSecond = 18_000,
                BackgroundElectronsPerSecond = 2,
                VignettingStrength = 0.25,
                ShotNoiseEnabled = true,
                Seed = 2025,
                SensorResponse = Asi178McSensorModel.Resolve(0, 64),
                Cloud = cloud,
                Transient = transient
            });
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
            cloudScenario = scenario.Cloud,
            transientScenario = scenario.Transient,
            asi174Sensor = new { enabled = workload == Workload.W1, blackLevelAdu = 64d },
            asi178Sensor = new { enabled = workload == Workload.W2, blackLevelContainerAdu = 64d }
        });
        return new CameraModuleConfig(
            new ObservatoryLocation(35.347, -113.878, 1000, "America/Phoenix"),
            new CameraModuleDescriptor("VirtualSky", options),
            new CameraRigConfig(
                new SensorProfile(
                    workload.Id, workload.Width, workload.Height, workload == Workload.W1 ? 5.86 : 2.4,
                    workload == Workload.W1 ? SensorColorMode.Mono : SensorColorMode.Color,
                    workload.PixelFormat,
                    workload == Workload.W1 ? SensorResponseMode.Monochrome : SensorResponseMode.BayerRaw,
                    workload.Width * 2,
                    SensorRecipeVersion: $"{workload.Id}-transient-performance-v1"),
                new OpticsProfile(
                    "EquidistantFisheye", workload == Workload.W1 ? 0 : 1.8, workload == Workload.W1 ? 180 : 185,
                    0, LensKind.Fisheye, workload.Width / 2d, workload.Height / 2d,
                    workload.ImageCircleRadius, workload.FocalLengthPixels, workload.FocalLengthPixels,
                    HorizontalFlip: true, CalibrationVersion: $"{workload.Id}-transient-performance-optics-v1"),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 0, 0)));
    }

    private static ImageLayout Layout(Workload workload)
        => new(workload.Width, workload.Height, workload.PixelFormat,
            workload.Width * ImageLayout.BytesPerPixel(workload.PixelFormat));

    private static void AssertExpectedChecksum(string workload, string scenario, string complete, string renderOnly)
    {
        var expected = ExpectedChecksums[(workload, scenario)];
        Assert.AreEqual(expected.Complete, complete, $"{workload}/{scenario} complete checksum");
        Assert.AreEqual(expected.RenderOnly, renderOnly, $"{workload}/{scenario} render checksum");
    }

    private static object CreateBaselineComparison(IReadOnlyCollection<TransientRenderMeasurement> measurements)
    {
        var w1 = measurements.Single(static item => item.Workload == "W1" && item.Scenario == "none");
        var w2 = measurements.Single(static item => item.Workload == "W2" && item.Scenario == "none");
        Assert.IsLessThanOrEqualTo(5, Math.Abs((w1.MedianMilliseconds / 61.4295 - 1) * 100));
        Assert.IsLessThanOrEqualTo(5, Math.Abs((w1.RenderOnly.MedianMilliseconds / 59.3849 - 1) * 100));
        Assert.IsLessThanOrEqualTo(5, Math.Abs((w2.MedianMilliseconds / 602.9345 - 1) * 100));
        Assert.IsLessThanOrEqualTo(5, Math.Abs((w2.RenderOnly.MedianMilliseconds / 595.034 - 1) * 100));
        return new
        {
            BaselineRevision = "e402bfeb0607101eeec95e292104722d3837c630",
            Source = "clean-parent VirtualSkyNoCloudBaselinePerformanceTests evidence",
            InvestigationThresholdPercent = 5,
            W1 = Delta(w1, 61.4295, 59.3849),
            W2 = Delta(w2, 602.9345, 595.034)
        };

        static object Delta(TransientRenderMeasurement current, double completeBaseline, double renderBaseline) => new
        {
            CompleteBaselineMilliseconds = completeBaseline,
            CompleteCandidateMilliseconds = current.MedianMilliseconds,
            CompleteDeltaPercent = (current.MedianMilliseconds / completeBaseline - 1) * 100,
            RenderBaselineMilliseconds = renderBaseline,
            RenderCandidateMilliseconds = current.RenderOnly.MedianMilliseconds,
            RenderDeltaPercent = (current.RenderOnly.MedianMilliseconds / renderBaseline - 1) * 100
        };
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string ReadGit(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = GetRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("git could not be started.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }

    private sealed record OutputChecksums(string Complete, string RenderOnly);
    private sealed record GeometryEvidence(int ActivePixels, double DepositedElectrons);

    private sealed record ConfigurationMeasurement(
        int SourceBytes,
        int CanonicalBytes,
        int MeasurementCount,
        double MedianMilliseconds,
        double P95Milliseconds,
        double AllocatedBytesPerOperation);

    private sealed record RenderOnlyMeasurement(
        int MeasurementCount,
        double MedianMilliseconds,
        double P95Milliseconds,
        double FramesPerSecond,
        double MebibytesPerSecond,
        double CpuMilliseconds,
        double CpuMillisecondsPerFrame,
        double AllocatedBytesPerFrame,
        long WorkingSetStartBytes,
        long PeakWorkingSetBytes,
        long WorkingSetEndBytes,
        long LohStartBytes,
        long LohEndBytes,
        long LohFragmentationStartBytes,
        long LohFragmentationEndBytes,
        string FinalSha256);

    private sealed record TransientRenderMeasurement(
        string Workload,
        string Scenario,
        bool TransientEnabled,
        bool CloudEnabled,
        int Width,
        int Height,
        string PixelFormat,
        int ExpectedOutputBytes,
        int OutputBytes,
        int MeasurementCount,
        double MedianMilliseconds,
        double P95Milliseconds,
        double FramesPerSecond,
        double MebibytesPerSecond,
        double CpuMilliseconds,
        double CpuMillisecondsPerFrame,
        double AllocatedBytesPerFrame,
        long WorkingSetStartBytes,
        long PeakWorkingSetBytes,
        long WorkingSetEndBytes,
        long ManagedHeapStartBytes,
        long ManagedHeapEndBytes,
        long ManagedLiveStartBytes,
        long ManagedLiveEndBytes,
        long LohStartBytes,
        long LohEndBytes,
        long LohFragmentationStartBytes,
        long LohFragmentationEndBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        int ConfigurationBytes,
        int ScenarioSourceBytes,
        int CanonicalParameterBytes,
        int ProvenanceBytes,
        int TemporalSampleCount,
        int SkyPrimitiveCount,
        int SensorPrimitiveCount,
        int ActiveTransientPixels,
        double DepositedElectrons,
        long TemporaryCloudMapBytes,
        string FinalSha256,
        ConfigurationMeasurement Configuration,
        RenderOnlyMeasurement RenderOnly);

    private sealed record PerformanceScenario(
        string Id,
        VirtualTransientScenarioDefinition? Transient,
        VirtualCloudScenarioDefinition? Cloud)
    {
        public static IReadOnlyList<PerformanceScenario> All { get; } =
        [
            new("none", null, null),
            new("explicit-empty", Definition(6100, [], []), null),
            new("short-sky-track", Definition(6101,
                [Sky("p-001", (0, 75, 260, -4, 0.12), (1, 75, 100, -2, 0.2))], []), null),
            new("saturated-fragmented-track", Definition(6102,
                [
                    Sky("p-001", (0, 72, 250, -12, 0.25), (1, 72, 110, -12, 0.35)),
                    Sky("p-002", (0.25, 71.5, 240, -9, 0.18), (1, 73, 105, -7, 0.22)),
                    Sky("p-003", (0.45, 73, 245, -8, 0.15), (1, 71, 115, -6, 0.2))
                ], []), null),
            new("long-shadow-blink", Definition(6103,
                [
                    Sky("p-001", (0, 62, 270, -2, 0.12), (0.45, 66, 200, 8, 0.12), (1, 70, 130, -2, 0.12)),
                    Sky("p-002", (0, 58, 250, -5, 0.15), (0.2, 61, 220, 8, 0.15),
                        (0.4, 64, 190, -5, 0.15), (0.6, 67, 160, 8, 0.15), (1, 70, 120, -5, 0.15))
                ], []), null),
            new("sensor-artifacts", Definition(6104, [],
                [
                    Sensor("s-001", 2.5, 2.5, 1_000_000_000, 0.25),
                    Sensor("s-002", 100.5, 90.5, 250_000, 2),
                    Sensor("s-003", 300.5, 180.5, 2_000_000, 0.5)
                ]), null),
            new("partial-cloud-sky-track", Definition(6105,
                [Sky("p-001", (0, 68, 260, -5, 0.2), (1, 72, 100, -3, 0.25))], []), PartialCloud())
        ];

        private static VirtualTransientScenarioDefinition Definition(
            int seed,
            IReadOnlyList<VirtualTransientSkyTrack> sky,
            IReadOnlyList<VirtualTransientSensorTrack> sensor)
            => new()
            {
                ScenarioId = "configured-scenario",
                Seed = seed,
                EpochUtc = FixtureUtc,
                TemporalSampleCount = 16,
                SkyTracks = sky,
                SensorTracks = sensor
            };

        private static VirtualTransientSkyTrack Sky(
            string id,
            params (double Offset, double Altitude, double Azimuth, double Magnitude, double Width)[] points)
            => new()
            {
                PrimitiveId = id,
                Keyframes = points.Select(static point => new VirtualTransientSkyKeyframe
                {
                    OffsetSeconds = point.Offset,
                    AltitudeDegrees = point.Altitude,
                    AzimuthDegrees = point.Azimuth,
                    Magnitude = point.Magnitude,
                    AngularWidthDegrees = point.Width
                }).ToArray()
            };

        private static VirtualTransientSensorTrack Sensor(
            string id,
            double x,
            double y,
            double electronsPerSecond,
            double sigma)
            => new()
            {
                PrimitiveId = id,
                Keyframes =
                [
                    new() { OffsetSeconds = 0, PixelX = x, PixelY = y, ElectronsPerSecond = electronsPerSecond, SigmaPixels = sigma },
                    new() { OffsetSeconds = 1, PixelX = x, PixelY = y, ElectronsPerSecond = electronsPerSecond, SigmaPixels = sigma }
                ]
            };

        private static VirtualCloudScenarioDefinition PartialCloud() => new()
        {
            ScenarioId = "configured-cloud",
            Seed = 104,
            EpochUtc = FixtureUtc,
            SpatialFrequency = 3,
            DriftEastCellsPerSecond = 0.01,
            DriftNorthCellsPerSecond = -0.005,
            EvolutionCellsPerSecond = 0.001,
            Octaves = 3,
            EdgeSoftness = 0.5,
            HorizonFadeDegrees = 5,
            TemporalSampleCount = 2,
            Keyframes = [new() { Coverage = 0.55, MaximumOpacity = 0.8, ScatterFraction = 0.1 }]
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
