using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;

/// <summary>Bounded actual VirtualSky → raw ingress → durable fixed preview → still producer qualification.</summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Bounded manual qualification.")]
public sealed class FullDayStillProductQualificationTests
{
    private const int Size = 640;
    private const double Latitude = 35.347;
    private const double Longitude = -113.878;
    private static readonly DateOnly Date = new(2026, 10, 12);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly AstronomyEnginePlanetEphemeris Ephemeris = new();
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ActualSunriseDay_ProducesFullAxisDarkTrailDurableLineageAndReproducibleRetry()
    {
        var root = Required("HVO_ISSUE993_FULL_DAY_ROOT");
        Assert.IsFalse(Directory.Exists(root), "Use a fresh evidence root; retained captures and products are immutable.");
        Directory.CreateDirectory(root);
        var snapshot = CatalogSnapshotResolver.Resolve(new(Required("HVO_ISSUE993_CATALOG_ROOT"), "hyg-v42-production")
        { ExpectedPackageVersion = "hyg-v4.2-p3-s2-r1" });
        var site = DeploymentLocationSnapshot.Create("issue-993-qualified-site", 1, "test", null,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, Latitude, Longitude, 0, "America/Phoenix");
        var calendar = ObservingDayCalendar.ForDeployment(site);
        var period = calendar.Resolve(Date).SunrisePeriod!;
        var configuration = Configuration(null);
        var clock = new NightlyClock(period.EndUtc.AddMinutes(10));
        using var provider = Provider(Path.Combine(root, "runtime"), clock);
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None);
        await provider.GetRequiredService<ProcessingGraphOperationsCoordinator>().EnsureConfiguredBasicAsync(configuration, CancellationToken.None);
        var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
        await laneStore.InitializeLanesAsync(CancellationToken.None);
        var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(static lane => lane.Name == "standard");
        var handler = provider.GetServices<ICaptureLaneHandler>().Single(static handler => handler.Lane == "standard");
        var sources = new List<object>();
        var captures = new List<RawCaptureReceipt>();
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var allocated = GC.GetTotalAllocatedBytes();
        var timer = Stopwatch.StartNew();
        var stride = int.Parse(Environment.GetEnvironmentVariable("HVO_ISSUE993_SOURCE_STRIDE_MINUTES") ?? "1",
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.IsTrue(stride is 1 or 60, "Only the full minute sequence or explicit sparse smoke recipe is supported.");
        var slot = 0;
        // Canonical capture contracts retain millisecond clocks; place each actual capture just inside its bin.
        var gridStart = DateTimeOffset.FromUnixTimeMilliseconds(period.StartUtc.ToUnixTimeMilliseconds() + 1);
        for (var utc = gridStart; utc < period.EndUtc; utc = utc.AddMinutes(1), slot++)
        {
            // Deliberate leading/trailing ten-minute holes and one exactly 30-minute internal hole.
            if (slot % stride != 0 || slot < 10 || slot is >= 600 and < 630 || utc >= period.EndUtc.AddMinutes(-10)) continue;
            var projection = RigProjectionContextFactory.Create(configuration.Rig);
            var altitude = SolarAltitudeClassifier.DirectionAt(Ephemeris, utc, Latitude, Longitude).AltitudeDegrees;
            var night = SkyBrightnessModel.PhotometricBackgroundElectronsPerSecond(3, 1000,
                projection.FocalLengthXPixels, projection.FocalLengthYPixels);
            var rate = StellarSkyBackgroundModel.Resolve(night, altitude);
            var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(Math.Clamp(2500 / rate, .000001, 20)), 0, null, null);
            var rendered = Configuration(utc);
            await using var module = new VirtualSkyCameraModule(new NightlyClock(utc), snapshot.Catalog, new ProjectedSceneStore());
            await module.InitializeAsync(rendered, CancellationToken.None);
            var frame = (await module.CaptureAsync(new(utc, TimeSpan.FromMinutes(1), CaptureMode.Still, setpoint), CancellationToken.None)).Frame!;
            var receipt = await ingress.AcceptAsync(rendered, Submission(utc, setpoint, frame), CancellationToken.None);
            Assert.IsNotNull(receipt);
            captures.Add(receipt);
            var lease = await laneStore.ClaimAsync(standard, "issue-993-qualification", rendered, CancellationToken.None);
            Assert.IsNotNull(lease);
            var completed = await handler.HandleAsync(lease.Context, CancellationToken.None);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, completed.Outcome, completed.Reason);
            await laneStore.CompleteAsync(lease, CancellationToken.None);
            var statistics = NativeStatistics(frame, projection);
            sources.Add(new { slot, utc, setpoint, altitude, receipt.Manifest.Descriptor.Capture.CaptureId,
                rawSha256 = Convert.ToHexString(SHA256.HashData(frame.PixelData.Span)), frame.Layout, frame.Metadata.Scene,
                frame.Metadata.Extra, statistics });
            if (slot % 60 == 0)
            {
                TestContext.WriteLine($"Produced actual source slot{slot} at{utc:O}.");
                await File.WriteAllTextAsync(Path.Combine(root, "progress.json"), JsonSerializer.Serialize(new
                { actualMeasuredUtc = DateTimeOffset.UtcNow, sourceCount = captures.Count, slot, sourceUtc = utc }));
            }
        }
        var reader = provider.GetRequiredService<INightlyProductSourceReader>();
        var candidates = await reader.ReadCandidatesAsync(NightlyProductFixture.NodeId, period.StartUtc, period.EndUtc,
            NightlyProductContract.MaximumWindowCandidates, static _ => Assert.Fail("An actual fixed preview was unsupported."), CancellationToken.None);
        Assert.HasCount(captures.Count, candidates);
        Assert.HasCount(1, candidates.Select(static candidate => candidate.RecipeIdentitySha256).Distinct());
        Assert.IsTrue(candidates.All(static candidate => candidate.UsesFixedDisplayTransfer));
        var options = new HVO.SkyMonitor.CameraAgent.Common.Options.NightlyProductOptions
        {
            Enabled = true, SourceNodeId = NightlyProductFixture.NodeId,
            SourceRecipeIdentitySha256 = candidates[0].RecipeIdentitySha256,
            RigProfileSha256 = RigProjectionContextFactory.CreateProfileHashSha256(configuration.Rig), MaximumSegmentsPerRun = 128
        };
        var host = NightlyProductFixture.HostOptions(Path.Combine(root, "runtime"), options);
        using var store = new SqliteNightlyProductStore(host, clock);
        using var generator = new NightlyProductGenerator(host, new FixedConfigurationAccessor(configuration), reader,
            store, Ephemeris, static () => null, clock);
        var planner = new LocalAutomationWindowPlanner(new FixedObservingDayCalendarProvider(calendar));
        var registry = new LocalAutomationTaskRegistry(provider.GetRequiredService<EnvironmentalLocalAutomationTaskRegistry>(),
            [new NightlyProductAutomationTaskRegistry(generator, host)], clock);
        var captureSequence = provider.GetRequiredService<ILocalAutomationCaptureSequenceSource>();
        using var telemetry = new LocalAutomationTelemetry();
        using var automation = new SqliteLocalAutomationStore(registry, captureSequence, host, clock,
            NullLogger<SqliteLocalAutomationStore>.Instance, telemetry, planner);
        clock.UtcNow = period.StartUtc;
        await automation.InitializeAsync(CancellationToken.None);
        foreach (var kind in Enum.GetValues<NightlyProductKind>())
        {
            var policy = new LocalAutomationSourceWindowPolicy(LocalAutomationSourceWindowPolicy.CurrentVersion,
                LocalAutomationSourceWindowKind.SunriseDay, kind == NightlyProductKind.Keogram
                    ? LocalAutomationSourceSelection.AllActualSources : LocalAutomationSourceSelection.DarkNightActualSources,
                TimeSpan.FromMinutes(5));
            var id = $"qualified-{NightlyProductContract.TargetFor(kind)}";
            var saved = await automation.SaveAsync(new(id, $"Qualified {kind}", true, LocalAutomationTaskKind.StillImageGeneration,
                NightlyProductPreset.Target(kind, options), LocalAutomationTriggerKind.SourceWindowClosed, 1, 0, id,
                "qualification-owner", "Actual full-day still qualification", policy), CancellationToken.None);
            Assert.AreEqual(LocalAutomationCommandStatus.Applied, saved.Status);
        }
        clock.UtcNow = period.EndUtc.AddMinutes(10);
        using var runner = new LocalAutomationRunnerService(automation, registry, captureSequence, host, clock,
            NullLogger<LocalAutomationRunnerService>.Instance, telemetry, planner);
        var generationStarted = Stopwatch.GetTimestamp();
        var generationCpu = process.TotalProcessorTime;
        var generationAllocated = GC.GetTotalAllocatedBytes();
        await runner.SweepAsync(CancellationToken.None);
        var scheduledGenerationMilliseconds = Stopwatch.GetElapsedTime(generationStarted).TotalMilliseconds;
        var scheduledGenerationCpuSeconds = (process.TotalProcessorTime - generationCpu).TotalSeconds;
        var scheduledGenerationAllocatedBytes = GC.GetTotalAllocatedBytes() - generationAllocated;
        var automationState = await automation.GetStateAsync(CancellationToken.None);
        Assert.HasCount(2, automationState.Runs.Where(static run => run.Outcome == LocalAutomationRunOutcome.Succeeded).ToArray());
        Assert.AreEqual(0, automationState.RunningRunCount);
        Assert.AreEqual(0, automationState.QueuedRunCount);
        Assert.IsTrue(automationState.Runs.All(static run => run.Outcome is LocalAutomationRunOutcome.Succeeded or LocalAutomationRunOutcome.Missed));
        var products = new List<object>();
        foreach (var kind in Enum.GetValues<NightlyProductKind>())
        {
            var run = automationState.Runs.Single(item => item.Outcome == LocalAutomationRunOutcome.Succeeded && item.DefinitionId == $"qualified-{NightlyProductContract.TargetFor(kind)}");
            var occurrence = run.Occurrence!;
            var window = occurrence.SourceWindow!;
            var started = Stopwatch.GetTimestamp();
            var beforeCpu = process.TotalProcessorTime;
            var beforeAlloc = GC.GetTotalAllocatedBytes();
            var state = await store.ReadWindowAsync(kind, NightlyProductScope.Final, period.StartUtc, CancellationToken.None, occurrence.IdentitySha256);
            Assert.IsNotNull(state);
            var finals = new List<NightlyProductDetail>();
            foreach (var id in state.ProductIds) finals.Add((await store.GetAsync(id, CancellationToken.None))!);
            var final = finals.Single(static item => item.Summary.Scope == NightlyProductScope.Final);
            var rendition = await store.OpenRenditionAsync(final.Summary.ProductId, CancellationToken.None);
            await File.WriteAllBytesAsync(Path.Combine(root, $"{NightlyProductContract.TargetFor(kind)}.jpg"), rendition!.Content.ToArray());
            var expected = candidates.Where(candidate => window.AcceptsSource(candidate.ExposureStartedUtc, Ephemeris)).ToArray();
            var lineage = await Leaves(store, final);
            CollectionAssert.AreEqual(expected.Select(static candidate => candidate.ArtifactId).ToArray(), lineage.ToArray());
            var unchanged = await generator.RunAsync(occurrence, CancellationToken.None);
            Assert.AreEqual(0, unchanged.ProductsPublished);
            Assert.AreEqual(0, unchanged.FailedWindows);
            products.Add(new { kind, occurrence, scheduledRun = run, unchanged, final, actualLeafCount = lineage.Count,
                verificationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                cpuSeconds = (process.TotalProcessorTime - beforeCpu).TotalSeconds,
                allocatedBytes = GC.GetTotalAllocatedBytes() - beforeAlloc });
        }
        // Representative source files are checksum-verified restores from the actual durable journal.
        var solarSamples = candidates.Select((candidate, index) => new
        {
            index, utc = candidate.ExposureStartedUtc,
            altitude = SolarAltitudeClassifier.DirectionAt(Ephemeris, candidate.ExposureStartedUtc, Latitude, Longitude).AltitudeDegrees
        }).ToArray();
        var nadir = solarSamples.MinBy(static sample => sample.altitude)!;
        int Nearest(double altitude, bool rising) => solarSamples
            .Where(sample => rising ? sample.utc >= nadir.utc : sample.utc < nadir.utc)
            .MinBy(sample => Math.Abs(sample.altitude - altitude))!.index;
        var sampleIndexes = new[]
        {
            (Label: "sunrise", Index: 0), (Label: "day", Index: solarSamples.MaxBy(static sample => sample.altitude)!.index),
            (Label: "sunset", Index: Nearest(0, false)), (Label: "civil-twilight", Index: Nearest(-6, false)),
            (Label: "late-twilight", Index: Nearest(-15, false)), (Label: "night", Index: nadir.index),
            (Label: "dawn-twilight", Index: Nearest(-12, true)), (Label: "dawn", Index: candidates.Count - 1)
        };
        var samples = new List<object>();
        foreach (var sample in sampleIndexes)
        {
            var index = sample.Index;
            var candidate = candidates[index];
            var restored = (await reader.RestoreAsync([candidate], CancellationToken.None)).Single();
            var layout = restored.Layout!;
            var jpeg = JpegImageCodec.EncodeToJpeg(new(layout.Width, layout.Height, layout.PixelFormat, layout.StrideBytes), restored.Payload, 95);
            var name = $"source-{sample.Label}-{index:D4}.jpg";
            await File.WriteAllBytesAsync(Path.Combine(root, name), jpeg);
            samples.Add(new { label = sample.Label, name, candidate, sha256 = ProcessingIdentity.ComputePayloadSha256(jpeg) });
        }
        process.Refresh();
        await File.WriteAllTextAsync(Path.Combine(root, "evidence.json"), JsonSerializer.Serialize(new
        {
            schema = "issue-993-full-day-production-still-qualification-v1", period, configuration, options,
            sourceHead = Environment.GetEnvironmentVariable("HVO_ISSUE993_SOURCE_HEAD") ?? "uncommitted-smoke-worktree",
            catalog = new { snapshot.DatabaseSha256, snapshot.SnapshotVersion, snapshot.RowCount },
            sources, samples, products, automationState, scheduledGenerationMilliseconds, scheduledGenerationCpuSeconds,
            scheduledGenerationAllocatedBytes, sourceStrideMinutes = stride, elapsedSeconds = timer.Elapsed.TotalSeconds,
            cpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds, allocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
            process.PeakWorkingSet64, process.WorkingSet64,
            assemblies = new[] { typeof(NightlyProductGenerator).Assembly, typeof(FixedDisplayTransfer).Assembly,
                typeof(ProcessingRecipeExecutor).Assembly, typeof(VirtualSkyCameraModule).Assembly }.Distinct()
                .Select(assembly => new { assembly.FullName, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) }),
            exposurePolicy = "Bounded qualification open-loop clamp(2500 / modeled scalar sky rate, 1us,20s); gain0. Cadence60s.",
            gapPolicy = "Ten-minute leading/trailing omissions; thirty-minute internal omission. No interpolated sky pixels.",
            projection = MeridianSamplePath.Create(RigProjectionContextFactory.Create(configuration.Rig), Size)
        }, Json));
        TestContext.WriteLine($"Qualified{sources.Count} actual captures and{products.Count} final products at{root}.");
    }

    [TestMethod]
    public async Task RetainedQualification_ExportsVerifiedSourceStripAndMatchingFinalColumn()
    {
        var root = Required("HVO_ISSUE993_RETAINED_FULL_DAY_ROOT");
        using var evidence = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "evidence.json")));
        var document = evidence.RootElement;
        var options = document.GetProperty("options").Deserialize<HVO.SkyMonitor.CameraAgent.Common.Options.NightlyProductOptions>(Json)!;
        var configuration = Configuration(null);
        Assert.AreEqual(options.RigProfileSha256, RigProjectionContextFactory.CreateProfileHashSha256(configuration.Rig));
        var productId = document.GetProperty("products").EnumerateArray().Single(static item => item.GetProperty("kind").GetString() == "Keogram")
            .GetProperty("final").GetProperty("summary").GetProperty("productId").GetGuid();
        var candidate = document.GetProperty("samples").EnumerateArray().Single(static item => item.GetProperty("label").GetString() == "civil-twilight")
            .GetProperty("candidate").Deserialize<NightlyProductCandidate>(Json)!;
        var clock = new NightlyClock(document.GetProperty("period").GetProperty("endUtc").GetDateTimeOffset().AddMinutes(10));
        using var provider = Provider(Path.Combine(root, "runtime"), clock);
        var reader = provider.GetRequiredService<INightlyProductSourceReader>();
        var source = (await reader.RestoreAsync([candidate], CancellationToken.None)).Single();
        using var store = new SqliteNightlyProductStore(NightlyProductFixture.HostOptions(Path.Combine(root, "runtime"), options), clock);
        var final = await store.GetAsync(productId, CancellationToken.None);
        Assert.IsNotNull(final);
        var packed = await store.ReadStoredProductAsync(productId, CancellationToken.None);
        var geometry = MeridianSamplePath.Create(RigProjectionContextFactory.Create(configuration.Rig), packed.Layout.Height);
        var strip = KeogramComposer.Compose([new(source.Layout!.Width, source.Layout.Height, source.Layout.StrideBytes,
            source.Layout.PixelFormat, source.Payload, candidate.ExposureStartedUtc)], new(geometry.Select(static point => point.Pixel).ToArray()));
        var column = (int)((candidate.ExposureStartedUtc - final.Occurrence.SourceWindow!.StartUtc).Ticks /
            TimeSpan.FromSeconds(options.KeogramColumnSeconds).Ticks);
        var matching = new byte[strip.PixelData.Length];
        for (var row = 0; row < packed.Layout.Height; row++)
            packed.Payload.Span.Slice(row * packed.Layout.StrideBytes + column * 3, 3).CopyTo(matching.AsSpan(row * 3, 3));
        CollectionAssert.AreEqual(strip.PixelData.ToArray(), matching, "Every source strip pixel must equal its retained final column.");
        var directory = Path.Combine(root, "strip-proof");
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "source.rgb"), source.Payload.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(directory, "strip.rgb"), strip.PixelData.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(directory, "column.rgb"), matching);
        await File.WriteAllTextAsync(Path.Combine(directory, "evidence.json"), JsonSerializer.Serialize(new
        {
            schema = "issue-993-shared-meridian-strip-proof-v1", candidate, sourceLayout = source.Layout,
            productId, column, rows = packed.Layout.Height, geometry,
            sourceSha256 = ProcessingIdentity.ComputePayloadSha256(source.Payload.Span),
            stripSha256 = ProcessingIdentity.ComputePayloadSha256(strip.PixelData.Span),
            columnSha256 = ProcessingIdentity.ComputePayloadSha256(matching), pixelEquality = true
        }, Json));
    }

    private static CameraModuleConfig Configuration(DateTimeOffset? celestialUtc) => new(
        new ObservatoryLocation(Latitude, Longitude, 0, "America/Phoenix"),
        new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(new
        { illuminationMode = "SolarDriven", fixedSceneUtc = celestialUtc, renderSolarSystemDisks = true, seed = 1131,
            maximumMagnitude = 6.5, maximumResults = 32768, magnitudeZeroElectronsPerSecond = 1000, bortleClass = 3,
            asi676Sensor = new { enabled = true, blackLevelAdu = 64 }, shotNoiseEnabled = true,
            vignettingStrength = .15, psfSigmaPixels = .85, psfRadiusPixels = 3.5 })),
        new CameraRigConfig(new SensorProfile("Qualified reduced all-sky", Size, Size, 2, SensorColorMode.Color,
                CameraPixelFormat.BayerRggb16, SensorResponseMode.BayerRaw, SensorRecipeVersion: "issue993-native12-v1"),
            new OpticsProfile("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye, Size / 2d, Size / 2d, Size * .49,
                HorizontalFlip: true, CalibrationVersion: "issue993-640-meridian-v1"),
            new RigOrientation(90, 0, 0), new PipelineExposureProfile(TimeSpan.FromMinutes(1),
                TimeSpan.FromMicroseconds(1), TimeSpan.FromSeconds(20), 0, 0)),
        new CapturePipelineConfig([new CaptureProcessingStepConfig("Preview", NightlyProductFixture.NodeId,
                Options: JsonSerializer.SerializeToElement(new { fixedTransfer = new FixedDisplayTransferOptions(), recipeVersion = "fixed-native-gamma-v1" }), DependsOn: ["$raw"])],
            CapturePipelineSchemaVersions.ExplicitV2, CapturePipelineDependencyPolicy.RejectEnabledDependent), "issue993-full-day-qualification");

    private static CaptureLoopSubmission Submission(DateTimeOffset utc, CaptureSetpoint setpoint, CameraFrame frame) =>
        new(new CaptureRequest(utc, TimeSpan.FromMinutes(1), CaptureMode.Still, setpoint),
            new CaptureResult(frame, setpoint, TimeSpan.Zero, CaptureMode.Still, false)
            { AcquisitionTiming = new(utc, utc + setpoint.Exposure, utc + setpoint.Exposure + TimeSpan.FromMilliseconds(1)) },
            utc, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

    private static ServiceProvider Provider(string root, TimeProvider clock)
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["CameraAgent:RawIngressRoot"] = root, ["CameraAgent:RawIngressReserveBytes"] = "0",
            ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false" }).Build());
        services.AddSingleton<IDeploymentLocationProtector, DataProtectionDeploymentLocationProtector>();
        services.AddSingleton(clock);
        return services.BuildServiceProvider();
    }

    private static object NativeStatistics(CameraFrame frame, ProjectionContext projection)
    {
        var histogram = new int[4096]; var clipped = 0; var count = 0; long total = 0;
        for (var y = 0; y < frame.Height; y++) for (var x = 0; x < frame.Width; x++)
        {
            if (!projection.ContainsSample(x + .5, y + .5)) continue;
            var offset = y * frame.Layout!.StrideBytes + x * 2;
            var value = frame.PixelData.Span[offset] | frame.PixelData.Span[offset + 1] << 8;
            count++; total += value; histogram[Math.Min(value, 4095)]++; if (value >= 4095) clipped++;
        }
        var accumulated = 0; var p50 = 0; var p99 = 0;
        for (var value = 0; value < histogram.Length; value++)
        {
            accumulated += histogram[value];
            if (accumulated <= count / 2) p50 = value + 1;
            if (accumulated <= (int)(count * .99)) p99 = value + 1;
        }
        return new { mean = total / (double)count, p50, p99, saturationFraction = clipped / (double)count, activeSamples = count };
    }

    private static async Task<List<Guid>> Leaves(SqliteNightlyProductStore store, NightlyProductDetail product)
    {
        var result = new List<Guid>();
        foreach (var source in product.Sources)
            if (source.SourceKind == NightlyProductSourceKind.PreviewFrame) result.Add(source.ArtifactId);
            else result.AddRange(await Leaves(store, (await store.GetAsync(source.ArtifactId, CancellationToken.None))!));
        return result;
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set{name} for this bounded manual qualification.");
}
