using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Scheduling;

[TestClass]
[TestCategory("Unit")]
public sealed class SqliteCaptureScheduleStoreTests
{
    [TestMethod]
    public async Task InitializeAsync_LegacyBootstrapAndFileChangePreserveActiveRevision()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            using var store = new SqliteCaptureScheduleStore(
                new JournalInitializer(root), options, TimeProvider.System);
            var legacy = await store.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, legacy.ActiveRevision.RevisionNumber);
            Assert.IsTrue(legacy.ActiveRevision.Definition.LegacyAlwaysOpen);
            Assert.IsNull(legacy.PendingRevision);

            var changed = await store.InitializeAsync(
                Configuration() with { Schedule = Definition("night", 2) },
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(legacy.ActiveRevision.RevisionId, changed.ActiveRevision.RevisionId);
            Assert.IsNotNull(changed.PendingRevision);
            Assert.AreEqual("night", changed.PendingRevision.Definition.SetpointProfiles.Single().Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task StageActivateAndOneShotConsumption_AreDurableAndIdempotent()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var initializer = new JournalInitializer(root);
            CaptureScheduleStoreSnapshot activated;
            using (var store = new SqliteCaptureScheduleStore(initializer, options, TimeProvider.System))
            {
                var initial = await store.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);
                var staged = await store.StageAsync(
                    LocalCaptureProfileDefinition.Create(Configuration(), Definition("night", 2)),
                    "stage-1",
                    initial.Version,
                    "owner",
                    "night operations",
                    CancellationToken.None).ConfigureAwait(false);
                var replay = await store.StageAsync(
                    LocalCaptureProfileDefinition.Create(Configuration(), Definition("night", 2)),
                    "stage-1",
                    initial.Version,
                    "owner",
                    "night operations",
                    CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(staged.Version, replay.Version);

                activated = await store.ActivateAsync(
                    staged.PendingRevision!.RevisionId,
                    "activate-1",
                    staged.Version,
                    "owner",
                    "approved",
                    CancellationToken.None).ConfigureAwait(false);
                var lateReplay = await store.StageAsync(
                    LocalCaptureProfileDefinition.Create(Configuration(), Definition("night", 2)),
                    "stage-1",
                    initial.Version,
                    "owner",
                    "night operations",
                    CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(staged.Version, lateReplay.Version);
                Assert.AreEqual(staged.ActiveRevision.RevisionId, lateReplay.ActiveRevision.RevisionId);
                Assert.AreEqual(staged.PendingRevision!.RevisionId, lateReplay.PendingRevision!.RevisionId);
                var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                var scheduleOverride = new CaptureScheduleOverride(
                    "override-1",
                    activated.ActiveRevision.RevisionId,
                    activated.ActiveRevision.ScheduleSha256,
                    CaptureScheduleOverrideMode.ForceOpen,
                    now.AddMinutes(-1),
                    now.AddMinutes(10),
                    "night",
                    OneShot: true);
                await store.AddOverrideAsync(
                    scheduleOverride,
                    "override-add-1",
                    activated.Version,
                    "owner",
                    "test",
                    CancellationToken.None).ConfigureAwait(false);
                using var competing = new SqliteCaptureScheduleStore(initializer, options, TimeProvider.System);
                var grants = await Task.WhenAll(
                    store.GrantAdmissionAsync(
                        "admission-1", activated.ActiveRevision.RevisionId,
                        scheduleOverride.Id, now, CancellationToken.None),
                    competing.GrantAdmissionAsync(
                        "admission-2", activated.ActiveRevision.RevisionId,
                        scheduleOverride.Id, now, CancellationToken.None)).ConfigureAwait(false);
                Assert.AreEqual(1, grants.Count(static granted => granted));

                var location = new CaptureLocationProvenance(
                    "test-location", 1, "test", null, DateTimeOffset.UnixEpoch, null);
                var preview = CaptureScheduleIntervalExpander.Expand(
                    activated.ActiveRevision.Definition,
                    DateOnly.FromDateTime(now.UtcDateTime),
                    1,
                    TimeZoneInfo.Utc,
                    Configuration().Observatory,
                    new AstronomyEngineSolarEventCalculator());
                await store.PersistPreviewAsync(
                    activated.ActiveRevision, location, preview, CancellationToken.None).ConfigureAwait(false);
                var persistedPreview = await competing.TryReadPreviewAsync(
                    activated.ActiveRevision, location, now, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(persistedPreview);
                Assert.AreEqual(preview.ExpansionSha256, persistedPreview.ExpansionSha256);
            }

            using var restarted = new SqliteCaptureScheduleStore(initializer, options, TimeProvider.System);
            var recovered = await restarted.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);
            var overrides = await restarted.GetActiveOverridesAsync(
                recovered.ActiveRevision.RevisionId,
                DateTimeOffset.UtcNow,
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(activated.ActiveRevision.RevisionId, recovered.ActiveRevision.RevisionId);
            Assert.HasCount(1, overrides);
            Assert.IsNotNull(overrides[0].ConsumedUtc);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-schedule-store", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static CameraModuleConfig Configuration()
        => new(
            new ObservatoryLocation(35, -114, 1000, "UTC"),
            new CameraModuleDescriptor("test"),
            new CameraRigConfig(
                new SensorProfile("test", 2, 2, 5, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("Perspective", 50, 10, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(5),
                    1,
                    10)));

    private static CaptureScheduleDefinition Definition(string profileId, double gain)
        => new(
            "capture-schedule-v1",
            [new CaptureScheduleSetpointProfile(
                profileId,
                TimeSpan.FromSeconds(5),
                gain,
                TimeSpan.FromSeconds(10))],
            [new CaptureWeeklyScheduleWindow(
                "monday",
                DayOfWeek.Monday,
                new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(18, 0)),
                new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(6, 0), DayOffset: 1),
                profileId)]);

    private sealed class JournalInitializer(string root) : IRawCaptureIngress
    {
        public async ValueTask InitializeAsync(CancellationToken cancellationToken)
        {
            var journal = new SqliteRawCaptureJournal(
                Path.Combine(root, "journal", "raw-ingress.db"),
                busyTimeoutSeconds: 5);
            await journal.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
