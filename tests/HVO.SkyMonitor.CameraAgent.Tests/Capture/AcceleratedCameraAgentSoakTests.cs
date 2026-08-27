using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture;

[TestClass]
[TestCategory("Soak")]
public sealed class AcceleratedCameraAgentSoakTests
{
    [TestMethod]
    [Timeout(120_000)]
    public async Task AcceleratedTwentyFourHours_ArtifactsAndBoundedOwnersRemainConsistent()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-soak", Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new InMemoryCelestialCatalog([new CelestialCatalogObject("star", "Star", 2.5, 20, 1)]);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ICelestialCatalog>(catalog);
            services.AddCameraAgentInfrastructure(new ConfigurationBuilder().Build());
            using var provider = services.BuildServiceProvider();
            var sceneStore = (ProjectedSceneStore)provider.GetRequiredService<IProjectedSceneStore>();
            var start = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
            var clock = new AcceleratedTimeProvider(start);
            var module = new VirtualSkyCameraModule(clock, catalog, sceneStore,
                provider.GetRequiredService<IConstellationTopology>());
            var config = CreateConfig(root);
            await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            var steps = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateGraph(config).Nodes
                .Select(static node => node.Step).ToArray();
            const int captures = 289;
            var channel = new FrameProcessingChannel(4);
            using var cancellation = new CancellationTokenSource();
            var hostContext = new CountingHostContext(config, channel, cancellation, captures);
            var worker = new FrameProcessingWorker(channel, steps, NullLogger.Instance).RunAsync(CancellationToken.None);
            await new CameraModuleRunner(module, hostContext, clock, NullLogger.Instance)
                .RunAsync(cancellation.Token).ConfigureAwait(false);
            channel.Complete();
            await worker.ConfigureAwait(false);

            var storage = provider.GetRequiredService<IFrameStorageService>();
            var stored = storage.List(root, DateOnly.FromDateTime(start.UtcDateTime), null, 10_000)
                .Concat(storage.List(root, DateOnly.FromDateTime(start.AddDays(1).UtcDateTime), null, 10_000))
                .DistinctBy(item => item.AbsolutePath).ToArray();
            foreach (var role in new[] { FrameArtifactRole.Raw, FrameArtifactRole.Combined,
                         FrameArtifactRole.Preview, FrameArtifactRole.AnnotatedPreview })
            {
                Assert.AreEqual(captures, stored.Count(item => item.Role == role), role.ToString());
            }
            Assert.IsTrue(stored.All(item => File.Exists(item.AbsolutePath) &&
                File.Exists(Path.ChangeExtension(item.AbsolutePath, ".json"))));
            var payloadCount = Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories).Count();
            var metadataCount = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Count();
            var indexCount = Directory.EnumerateFiles(Path.Combine(root, "index"), "*.jsonl")
                .Sum(path => File.ReadLines(path).Count());
            Assert.AreEqual(captures * 4, payloadCount);
            Assert.AreEqual(payloadCount, metadataCount);
            Assert.AreEqual(payloadCount, indexCount);
            Assert.IsEmpty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
            Assert.AreEqual(captures, channel.AcceptedCount);
            Assert.AreEqual(channel.AcceptedCount, channel.DequeuedCount);
            Assert.AreEqual(0, channel.CurrentDepth);
            Assert.AreEqual(captures, hostContext.PublishedCount);
            Assert.HasCount(captures, hostContext.Submissions);
            var actualStarts = hostContext.Submissions.Select(static submission => submission.CaptureStartedUtc).ToArray();
            var cycleEvidence = hostContext.Submissions.Select(static submission => submission.CycleEvidence).ToArray();
            Assert.IsTrue(cycleEvidence.All(static evidence => evidence is not null));
            Assert.AreEqual(start, actualStarts[0]);
            Assert.AreEqual(start.AddHours(24), actualStarts[^1]);
            Assert.IsTrue(actualStarts.Zip(actualStarts.Skip(1), static (previous, current) => current - previous)
                .All(static gap => gap == TimeSpan.FromMinutes(5)));
            Assert.IsTrue(hostContext.Submissions.All(static submission =>
                submission.CycleEvidence!.ModuleCallStartedUtc == submission.CaptureStartedUtc));
            Assert.IsNull(cycleEvidence[0]!.ObservedInterExposureGap);
            Assert.IsTrue(cycleEvidence.Skip(1).All(static evidence =>
                evidence!.ObservedInterExposureGap == TimeSpan.FromMinutes(5)));
            Assert.IsTrue(cycleEvidence.All(static evidence => evidence!.MonotonicStartJitter >= TimeSpan.Zero));
            Assert.IsLessThanOrEqualTo(32, sceneStore.Count);
            Assert.HasCount(120, provider.GetRequiredService<ICaptureTelemetryProvider>().GetSnapshot().Samples);
            Assert.AreEqual(4, steps.OfType<RollingCombinationCaptureProcessingStep>().Single().BufferedFrameCount);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static CameraModuleConfig CreateConfig(string root)
        => new(
            new ObservatoryLocation(35.347, -113.878, 0, "UTC"),
            new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(
                new VirtualSkyCameraModuleOptions { MaximumResults = 10, ShotNoiseEnabled = false })),
            new CameraRigConfig(
                new SensorProfile("Soak", 64, 48, 5, SensorColorMode.Mono, CameraPixelFormat.Mono16,
                    SensorResponseMode.Monochrome),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye,
                    PrincipalPointX: 32, PrincipalPointY: 24, ImageCircleRadiusPixels: 23),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 10, 10)),
            new CapturePipelineConfig([
                Step("RollingCombination", 25, new RollingCombinationProcessingStepOptions { WindowSize = 4 }),
                Step("Preview", 50, new PreviewProcessingStepOptions()),
                Step("Annotation", 75, new AnnotationProcessingStepOptions
                {
                    DrawLabels = false, DrawConstellationLines = false, DrawImageCircle = true
                }),
                Step("Storage", 100,
                    new NoOpFileStorageProcessingStepOptions
                    {
                        StorageRoot = root, RetentionDays = 1, QueueForUpload = false, UpdateLatestFrame = false
                    }),
                Step("Telemetry", 200,
                    new TelemetryProcessingStepOptions())
            ]), AgentId: "accelerated-soak");

    private static CaptureProcessingStepConfig Step<T>(string type, int order, T options)
        => new(type, type, order, JsonSerializer.SerializeToElement(options));

    private sealed class CountingHostContext(
        CameraModuleConfig configuration,
        FrameProcessingChannel channel,
        CancellationTokenSource cancellation,
        int target) : ICaptureHostContext
    {
        private int _count;
        private readonly List<CaptureLoopSubmission> _submissions = [];
        public CameraModuleConfig Configuration => configuration;
        public int PublishedCount => _count;
        public IReadOnlyList<CaptureLoopSubmission> Submissions => _submissions;
        public async ValueTask PublishAsync(CaptureLoopSubmission submission, CancellationToken cancellationToken)
        {
            _submissions.Add(submission);
            await channel.WriteAsync(new FrameProcessingItem(configuration, submission), cancellationToken).ConfigureAwait(false);
            if (Interlocked.Increment(ref _count) == target)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class AcceleratedTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private long _utcTicks = start.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
        public override long GetTimestamp() => Interlocked.Read(ref _utcTicks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Add(ref _utcTicks, dueTime.Ticks);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new NoOpTimer();
        }

        private sealed class NoOpTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
