using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;
using HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.TimeLapses;

[TestClass]
[DoNotParallelize]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Focused MSTest methods.")]
[SuppressMessage("Performance", "CA1861:Avoid constant arrays as arguments", Justification = "Focused expected values.")]
public sealed class TimeLapseGenerationTests
{
    [TestMethod]
    [TestCategory("Manual")]
    public async Task ActualIngressToVideo_RetainsStackCadenceChecksumsAndNeverPublishesAnUploadArtifact()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/ffmpeg")) Assert.Inconclusive("Requires qualified Linux FFmpeg.");
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-generate");
        try
        {
            using var provider = Provider(root);
            var (occurrence, captures) = await CaptureAsync(provider);
            var store = provider.GetRequiredService<SqliteTimeLapseStore>();
            using var lease = await store.AcquireWorkerAsync(CancellationToken.None);
            var adapter = provider.GetServices<ILocalAutomationWindowTaskAdapter>().Single(adapter => adapter is TimeLapseAutomationAdapter);
            var queued = await adapter.ExecuteAsync(occurrence, CancellationToken.None);
            Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, queued.Outcome);
            var job = await store.ClaimAsync(CancellationToken.None);
            Assert.IsNotNull(job);
            await provider.GetRequiredService<TimeLapseGenerator>().RunAsync(job, CancellationToken.None);
            var day = await store.GetDayAsync(occurrence.SourceWindow!.ReportingPeriod.ReportDate, CancellationToken.None);
            var product = day.Products.Single();
            Assert.AreEqual(CameraAgentTimeLapseState.Produced, day.Jobs.Single().State);
            Assert.IsTrue(product.HasGaps);
            Assert.IsNotNull(product.Timeline);
            Assert.HasCount(4, product.Timeline.Sources);
            CollectionAssert.AreEqual(captures.Select(capture => capture.Manifest.Descriptor.Artifact.ArtifactId).ToArray(),
                product.Frames.Select(frame => frame.ArtifactId).ToArray());
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 1 }, product.Frames.Select(frame => frame.StackSourceIds.Count).ToArray(),
                "Changing exposure resets the compatibility-bound stack.");
            Assert.AreEqual(TimeSpan.FromSeconds(60), product.Frames[2].TotalIntegration);
            Assert.AreEqual(TimeSpan.FromMilliseconds(1), product.Frames[3].TotalIntegration);
            Assert.AreEqual(20_000_000L, product.Encoding.Media.DurationTicks);
            using var file = await store.OpenVideoAsync(product.ProductId, CancellationToken.None);
            Assert.IsNotNull(file);
            Assert.IsGreaterThan(1000L, file.Length);
            Assert.IsNull(await provider.GetRequiredService<SqliteCaptureProcessingStore>().ReadOutputByArtifactIdAsync(product.ProductId, CancellationToken.None),
                "A local video is not a processing artifact that could enter upload distribution.");
            var restored = await store.GetPlanAsync(job.JobId, CancellationToken.None);
            Assert.IsNotNull(restored);
            Assert.HasCount(4, restored.Sources);
            Assert.IsEmpty(await store.GetRetentionHoldsAsync(root, CancellationToken.None), "Finished videos release their temporary raw holds.");
            await TimeLapseAccessTests.VerifyActualMediaAsync(store, product);
            var policy = occurrence.SourceWindow.Policy with { Kind = LocalAutomationSourceWindowKind.SunriseDay };
            var planner = provider.GetRequiredService<LocalAutomationWindowPlanner>();
            var dailyWindow = planner.ResolveWindows(occurrence.SourceWindow.ReportingPeriod, policy).Single();
            var definition = occurrence.Definition with { DefinitionId = "video-daily-test", SourceWindow = policy };
            var dailyOccurrence = LocalAutomationWindowPlanner.CreateOccurrence(new(definition, 1,
                LocalAutomationContract.ComputeRevisionSha256(definition), null, null), dailyWindow);
            Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, (await adapter.ExecuteAsync(dailyOccurrence, CancellationToken.None)).Outcome);
            var expectedHours = planner.ResolveWindows(dailyWindow.ReportingPeriod, policy with { Kind = LocalAutomationSourceWindowKind.CompletedCivilHour }).Count;
            for (var index = 0; index <= expectedHours; index++)
            {
                var next = await store.ClaimAsync(CancellationToken.None);
                Assert.IsNotNull(next);
                await provider.GetRequiredService<TimeLapseGenerator>().RunAsync(next, CancellationToken.None);
            }
            var completedDay = await store.GetDayAsync(dailyWindow.ReportingPeriod.ReportDate, CancellationToken.None);
            var daily = completedDay.Products.Single(static item => item.IsDaily);
            Assert.HasCount(expectedHours, daily.SegmentProductIds);
            Assert.Contains(product.ProductId, daily.SegmentProductIds, "Daily assembly reuses the existing hourly encode.");
            Assert.IsTrue(daily.HasGaps);
            Assert.AreEqual(CameraAgentTimeLapseState.Produced, completedDay.Jobs.Single(item => item.Occurrence.Definition.DefinitionId == "video-daily-test" && item.ParentJobId is null).State);
            using var dailyBytes = await store.OpenVideoAsync(daily.ProductId, CancellationToken.None);
            Assert.IsNotNull(dailyBytes);
            Assert.AreEqual(daily.Encoding.PayloadBytes, dailyBytes.Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task AllQualityExcludedSourcesRemainExplicitEvenWhenEncoderAndQuotaAreUnavailable()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-quality-empty");
        try
        {
            using var provider = Provider(root, missingEncoder: true, saturationGate: 0, publicationBudget: 1024);
            var (occurrence, _) = await CaptureAsync(provider, saturated: true);
            var options = provider.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value;
            var store = provider.GetRequiredService<SqliteTimeLapseStore>();
            using var lease = await store.AcquireWorkerAsync(CancellationToken.None);
            await store.EnqueueAsync(occurrence, options.TimeLapses.Freeze(Configuration()), [], CancellationToken.None);
            var job = await store.ClaimAsync(CancellationToken.None);
            Assert.IsNotNull(job);
            await provider.GetRequiredService<TimeLapseGenerator>().RunAsync(job, CancellationToken.None);
            var day = await store.GetDayAsync(occurrence.SourceWindow!.ReportingPeriod.ReportDate, CancellationToken.None);
            Assert.AreEqual(CameraAgentTimeLapseState.NoSources, day.Jobs.Single().State);
            Assert.AreEqual("timelapse.quality-excluded-all", day.Jobs.Single().ReasonCode);
            Assert.AreEqual(4, day.Jobs.Single().Exclusions["timelapse.quality-saturation"]);
            Assert.IsEmpty(day.Products);
            Assert.IsEmpty(await store.GetRetentionHoldsAsync(root, CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task MissingEncoder_IsExplicitUnavailableAndLeavesCaptureJournalReadable()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-unavailable");
        try
        {
            using var provider = Provider(root, missingEncoder: true);
            var (occurrence, captures) = await CaptureAsync(provider);
            var options = provider.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value;
            var store = provider.GetRequiredService<SqliteTimeLapseStore>();
            using var lease = await store.AcquireWorkerAsync(CancellationToken.None);
            await store.EnqueueAsync(occurrence, options.TimeLapses.Freeze(Configuration()), [], CancellationToken.None);
            var job = await store.ClaimAsync(CancellationToken.None);
            Assert.IsNotNull(job);
            using var worker = ActivatorUtilities.CreateInstance<TimeLapseWorker>(provider);
            await worker.ExecuteJobAsync(job, CancellationToken.None);
            var day = await store.GetDayAsync(occurrence.SourceWindow!.ReportingPeriod.ReportDate, CancellationToken.None);
            Assert.AreEqual(CameraAgentTimeLapseState.Unavailable, day.Jobs.Single().State);
            Assert.AreEqual("timelapse.encoder-unavailable", day.Jobs.Single().ReasonCode);
            Assert.IsEmpty(day.Products);
            var raw = await provider.GetRequiredService<SqliteCaptureProcessingStore>().ReadTimeLapseRawSourcesAsync(
                "video-fixture", occurrence.SourceWindow.StartUtc, occurrence.SourceWindow.EndUtc, 8192, CancellationToken.None);
            Assert.HasCount(captures.Length, raw);
        }
        finally { Directory.Delete(root, true); }
    }

    private static ServiceProvider Provider(string root, bool missingEncoder = false, int? saturationGate = null, long publicationBudget = 32L * 1024 * 1024 * 1024)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10)));
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CameraAgent:RawIngressRoot"] = root,
            ["CameraAgent:RawIngressReserveBytes"] = "0",
            ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false",
            ["CameraAgent:TimeLapses:Enabled"] = "true",
            ["CameraAgent:TimeLapses:RigProfileSha256"] = RigProjectionContextFactory.CreateProfileHashSha256(Rig()),
            ["CameraAgent:TimeLapses:Frames:MaximumSaturatedMillionths"] = saturationGate?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CameraAgent:TimeLapses:MaximumPublishedBytes"] = publicationBudget.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CameraAgent:TimeLapses:Encoder:ExecutablePath"] = missingEncoder ? Path.Combine(root, "missing-ffmpeg") : "/usr/bin/ffmpeg"
        }).Build());
        services.AddSingleton<IObservingDayCalendarProvider>(new FixedObservingDayCalendarProvider(NightlyProductFixture.Calendar));
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(Configuration());
        return provider;
    }

    private static async Task<(LocalAutomationOccurrence Occurrence, RawCaptureReceipt[] Captures)> CaptureAsync(ServiceProvider provider, bool saturated = false)
    {
        var options = provider.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value.TimeLapses;
        var policy = new LocalAutomationSourceWindowPolicy(LocalAutomationSourceWindowPolicy.CurrentVersion,
            LocalAutomationSourceWindowKind.CompletedCivilHour, LocalAutomationSourceSelection.AllActualSources, TimeSpan.FromMinutes(5));
        var planner = new LocalAutomationWindowPlanner(new FixedObservingDayCalendarProvider(NightlyProductFixture.Calendar));
        var window = planner.ResolveWindows(NightlyProductFixture.ObservingDate, policy).Single(window => window.StartUtc.Hour == 5);
        var definition = new LocalAutomationDefinition("video-test", "Video", true, LocalAutomationTaskKind.TimeLapseGeneration,
            options.Target, LocalAutomationTriggerKind.SourceWindowClosed, 1, window.StartUtc, policy);
        var occurrence = LocalAutomationWindowPlanner.CreateOccurrence(new(definition, 1,
            LocalAutomationContract.ComputeRevisionSha256(definition), null, null), window);
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None);
        await provider.GetRequiredService<ProcessingGraphOperationsCoordinator>().EnsureConfiguredBasicAsync(Configuration(), CancellationToken.None);
        var receipts = new RawCaptureReceipt[4];
        for (var index = 3; index >= 0; index--)
        {
            var utc = window.StartUtc.AddSeconds(index * 20);
            var exposure = index == 3 ? TimeSpan.FromMilliseconds(1) : TimeSpan.FromSeconds(20);
            var setpoint = new CaptureSetpoint(exposure, 1, null, null);
            var pixels = new byte[512 * 512 * 2];
            for (var pixel = 0; pixel < pixels.Length / 2; pixel++)
                BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(pixel * 2), saturated ? ushort.MaxValue : (ushort)(100 + (pixel * 7 + index * 73) % 4000));
            var frame = new CameraFrame(utc, 512, 512, CameraPixelFormat.Mono16, pixels, new FrameMetadata(exposure, 1, 10, "virtual-fixture"));
            receipts[index] = (await ingress.AcceptAsync(Configuration(), new CaptureLoopSubmission(
                new CaptureRequest(utc, exposure, CaptureMode.Still, setpoint),
                new CaptureResult(frame, setpoint, TimeSpan.Zero, CaptureMode.Still, false)
                { AcquisitionTiming = new(utc, utc.Add(exposure), utc.Add(exposure)) }, utc, exposure, exposure), CancellationToken.None))!;
            Assert.IsNotNull(receipts[index]);
        }
        return (occurrence, receipts);
    }

    private static CameraModuleConfig Configuration() => new(NightlyProductFixture.Observatory,
        new CameraModuleDescriptor("VirtualSky"), Rig(), CapturePipelineConfig.Empty, "video-fixture");

    private static CameraRigConfig Rig() => NightlyProductFixture.Rig with
    {
        Sensor = NightlyProductFixture.Rig.Sensor with { WidthPixels = 512, HeightPixels = 512, StrideBytes = 1024 },
        Optics = NightlyProductFixture.Rig.Optics with
        {
            PrincipalPointX = 256, PrincipalPointY = 256, ImageCircleRadiusPixels = 255,
            FocalLengthXPixels = 510 / Math.PI, FocalLengthYPixels = 510 / Math.PI
        }
    };
}
