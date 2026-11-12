using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Tests.TimeLapses;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Storage;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class StorageRetentionRootIntegrationTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task FrozenTimeLapseHoldsSurviveConfiguredAndCanonicalRootSpellings(bool configuredTrailing, bool queryTrailing)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("time-lapse-root-holds");
        try
        {
            var fixture = new CaptureSatelliteFixture();
            var clock = new NightlyClock(fixture.Descriptor.Timing.DurableIngressUtc);
            using var provider = CaptureSatelliteIntegrationTests.CreateProvider(root, fixture, clock);
            var raw = await CaptureSatelliteIntegrationTests.CaptureAsync(provider,
                CaptureSatelliteIntegrationTests.Configuration(root, fixture), fixture).ConfigureAwait(false);
            var options = SqliteTimeLapseStoreTests.Options(Spelling(root, configuredTrailing));
            using var store = new SqliteTimeLapseStore(options, clock);
            using var lease = await store.AcquireWorkerAsync(CancellationToken.None).ConfigureAwait(false);
            var occurrence = SqliteTimeLapseStoreTests.Occurrence(options.Value.TimeLapses,
                LocalAutomationSourceWindowKind.CompletedCivilHour);
            await store.EnqueueAsync(occurrence, options.Value.TimeLapses.Freeze(NightlyProductFixture.Configuration()),
                [], CancellationToken.None).ConfigureAwait(false);
            var job = await store.ClaimAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(job);
            var payload = raw.Manifest.RelativeArtifactPath;
            var sidecar = Path.ChangeExtension(payload, ".json");
            await store.FreezePlanAsync(job.JobId, new([new(raw.Manifest.Descriptor, payload, sidecar)],
                new Dictionary<string, int>()), CancellationToken.None).ConfigureAwait(false);

            var holds = await store.GetRetentionHoldsAsync(Spelling(root, queryTrailing), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(new ProcessingRetentionHold(raw.Manifest.Descriptor.Artifact.ArtifactId, payload, sidecar),
                Assert.ContainsSingle(holds));
            Assert.IsEmpty(await store.GetRetentionHoldsAsync(Path.Combine(root, "other"), CancellationToken.None).ConfigureAwait(false));

            using var reopened = new SqliteTimeLapseStore(options, clock);
            CollectionAssert.AreEqual(holds.ToArray(),
                (await reopened.GetRetentionHoldsAsync(root, CancellationToken.None).ConfigureAwait(false)).ToArray());
            await SweepAsync(provider, CaptureSatelliteIntegrationTests.Configuration(root, fixture),
                options, reopened, clock.UtcNow.AddYears(2)).ConfigureAwait(false);
            Assert.IsTrue(File.Exists(Path.Combine(root, payload)));
            Assert.IsTrue(File.Exists(Path.Combine(root, sidecar)));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task SatelliteHoldsSurviveConfiguredAndCanonicalRootSpellingsWithConfinement(bool configuredTrailing, bool queryTrailing)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("satellite-root-holds");
        try
        {
            var fixture = new CaptureSatelliteFixture();
            var clock = new NightlyClock(fixture.Descriptor.Timing.DurableIngressUtc);
            using var provider = CaptureSatelliteIntegrationTests.CreateProvider(root, fixture, clock);
            var config = CaptureSatelliteIntegrationTests.Configuration(root, fixture);
            var raw = await CaptureSatelliteIntegrationTests.CaptureAsync(provider, config, fixture).ConfigureAwait(false);
            var result = await CaptureSatelliteIntegrationTests.ProcessAsync(provider, config).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
            var expected = await provider.GetRequiredService<CaptureSatelliteWorkStore>()
                .GetRetentionHoldsAsync(root, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(6, expected);
            var work = new CaptureSatelliteWorkStore(Options.Create(new CameraAgentHostOptions
            { RawIngressRoot = Spelling(root, configuredTrailing) }),
                provider.GetRequiredService<SqliteCaptureProcessingStore>(), provider.GetRequiredService<IOrbitalElementsStore>(), clock);

            var holds = await work.GetRetentionHoldsAsync(Spelling(root, queryTrailing), CancellationToken.None).ConfigureAwait(false);
            CollectionAssert.AreEquivalent(expected.ToArray(), holds.ToArray());
            Assert.IsTrue(holds.Any(hold => hold.ArtifactId == raw.Manifest.Descriptor.Artifact.ArtifactId));
            Assert.IsEmpty(await work.GetRetentionHoldsAsync(Path.Combine(root, "other"), CancellationToken.None).ConfigureAwait(false));
            await work.ReconcileAsync(Spelling(root, queryTrailing), CancellationToken.None).ConfigureAwait(false);
            await SweepAsync(provider, config, Options.Create(new CameraAgentHostOptions
            { RawIngressRoot = Spelling(root, configuredTrailing) }), new SatelliteHolds(work),
                clock.UtcNow.AddYears(2)).ConfigureAwait(false);
            Assert.IsTrue(File.Exists(raw.StoredFrame.AbsolutePath));
            foreach (var hold in holds)
            {
                Assert.IsTrue(File.Exists(Path.Combine(root, hold.PayloadRelativePath)));
                Assert.IsTrue(File.Exists(Path.Combine(root, hold.SidecarRelativePath)));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Spelling(string root, bool trailing) => trailing ? root + Path.DirectorySeparatorChar : root;

    private static async Task SweepAsync(ServiceProvider provider, CameraModuleConfig config,
        IOptions<CameraAgentHostOptions> options, IProcessingRetentionHolds holds, DateTimeOffset now)
    {
        using var outbox = new SqliteArtifactOutbox();
        using var retention = new RetentionBackgroundService(provider.GetRequiredService<ICameraAgentConfigurationAccessor>(),
            options, new NightlyClock(now), outbox, new Capacity(), new StoragePressureState(),
            NullLogger<RetentionBackgroundService>.Instance, processingHolds: holds);
        await retention.ApplyRetentionAsync(config, CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class Capacity : IStorageCapacityProvider
    {
        public StorageCapacity GetCapacity(string storageRoot) => new(100, 100);
    }

    private sealed class SatelliteHolds(CaptureSatelliteWorkStore work) : IProcessingRetentionHolds
    {
        public ValueTask<IReadOnlyList<ProcessingRetentionHold>> GetRetentionHoldsAsync(string storageRoot, CancellationToken cancellationToken)
            => work.GetRetentionHoldsAsync(storageRoot, cancellationToken);
    }
}
