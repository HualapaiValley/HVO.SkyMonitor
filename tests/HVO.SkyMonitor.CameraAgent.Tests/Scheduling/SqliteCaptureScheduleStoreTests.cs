using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Scheduling;

[TestClass]
[TestCategory("Unit")]
public sealed class SqliteCaptureScheduleStoreTests
{
    [TestMethod]
    public async Task InitializeAsync_ExplicitBootstrapAndFileChangePreserveActiveRevision()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            using var store = new SqliteCaptureScheduleStore(
                new JournalInitializer(root), options, TimeProvider.System);
            var initial = await store.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, initial.ActiveRevision.RevisionNumber);
            Assert.AreEqual("initial", initial.ActiveRevision.Definition.SetpointProfiles.Single().Id);
            Assert.IsNull(initial.PendingRevision);

            var changed = await store.InitializeAsync(
                Configuration() with { Schedule = Definition("night", 2) },
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(initial.ActiveRevision.RevisionId, changed.ActiveRevision.RevisionId);
            Assert.IsNotNull(changed.PendingRevision);
            Assert.AreEqual("night", changed.PendingRevision.Definition.SetpointProfiles.Single().Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_ExplicitPipelinePreservesV2ProfileAndHash()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            using var store = new SqliteCaptureScheduleStore(
                new JournalInitializer(root), options, TimeProvider.System);
            var baseline = Configuration();
            var configuration = baseline with
            {
                Pipeline = new CapturePipelineConfig(
                    [new CaptureProcessingStepConfig("Calibration", "calibration", DependsOn: ["$raw"])],
                    CapturePipelineSchemaVersions.ExplicitV2,
                    CapturePipelineDependencyPolicy.RejectEnabledDependent),
                Schedule = Definition("night", 2)
            };

            var snapshot = await store.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            var effective = snapshot.ActiveRevision.Profile.ApplyTo(configuration);

            Assert.AreEqual(LocalCaptureProfileDefinition.CurrentSchemaVersion, snapshot.ActiveRevision.Profile.SchemaVersion);
            Assert.AreEqual(
                LocalCaptureProfileContract.ComputeSha256(
                    LocalCaptureProfileDefinition.CreateForConfiguration(configuration, configuration.Schedule)),
                snapshot.ActiveRevision.ProfileSha256);
            Assert.AreEqual(CapturePipelineSchemaVersions.ExplicitV2, effective.Pipeline.SchemaVersion);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_PersistedV1ControlPoliciesNormalizeDuringHostRestart()
    {
        var cases = new[]
        {
            new LegacyControlCase(
                new CameraControlPolicy
                {
                    AutoExposure = CameraFeatureDirective.Enabled,
                    AutoGain = CameraFeatureDirective.Disabled
                },
                AutomaticControlOwnership.HostMetered,
                AutomaticControlOwnership.Disabled),
            new LegacyControlCase(
                null,
                AutomaticControlOwnership.Disabled,
                AutomaticControlOwnership.Disabled)
        };
        foreach (var testCase in cases)
        {
            var root = CreateRoot();
            try
            {
                var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
                var journalInitializer = new JournalInitializer(root);
                var configuration = HostConfiguration();
                string revisionId;
                using (var store = new SqliteCaptureScheduleStore(
                    journalInitializer, options, TimeProvider.System))
                {
                    var initial = await store.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
                    var legacyConfiguration = configuration with
                    {
                        Rig = configuration.Rig with { ControlPolicy = testCase.Policy }
                    };
                    var staged = await store.StageAsync(
                        LocalCaptureProfileDefinition.Create(legacyConfiguration, Definition("legacy", 2)),
                        "stage-legacy-policy",
                        initial.Version,
                        "test",
                        null,
                        CancellationToken.None).ConfigureAwait(false);
                    var activated = await store.ActivateAsync(
                        staged.PendingRevision!.RevisionId,
                        "activate-legacy-policy",
                        staged.Version,
                        "test",
                        null,
                        CancellationToken.None).ConfigureAwait(false);
                    revisionId = activated.ActiveRevision.RevisionId;
                }
                if (testCase.Policy is null)
                {
                    using var connection = new SqliteConnection(
                        $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
                    await connection.OpenAsync().ConfigureAwait(false);
                    using var command = connection.CreateCommand();
                    command.CommandText =
                        "SELECT profile_json FROM capture_schedule_revisions WHERE revision_id = $id;";
                    command.Parameters.AddWithValue("$id", revisionId);
                    var profileJson = (byte[])(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
                    using var document = System.Text.Json.JsonDocument.Parse(profileJson);
                    Assert.AreEqual(
                        System.Text.Json.JsonValueKind.Null,
                        document.RootElement.GetProperty("rig").GetProperty("controlPolicy").ValueKind);
                }

                using var restarted = new SqliteCaptureScheduleStore(
                    journalInitializer, options, TimeProvider.System);
                var accessor = new CameraAgentConfigurationAccessor();
                var hostInitializer = new CameraAgentConfigurationInitializer(
                    new StaticConfigurationLoader(configuration),
                    accessor,
                    new EmptyPipelineFactory(),
                    NullLogger<CameraAgentConfigurationInitializer>.Instance,
                    restarted);

                await hostInitializer.StartAsync(CancellationToken.None).ConfigureAwait(false);
                var effective = await accessor.WaitForConfigurationAsync(CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual(CapturePipelineSchemaVersions.LegacyV1, effective.Pipeline.SchemaVersion);
                Assert.AreEqual(
                    testCase.ExpectedExposure,
                    effective.Rig.ControlPolicy!.ExposureControl);
                Assert.AreEqual(
                    testCase.ExpectedGain,
                    effective.Rig.ControlPolicy.GainControl);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed record LegacyControlCase(
        CameraControlPolicy? Policy,
        AutomaticControlOwnership ExpectedExposure,
        AutomaticControlOwnership ExpectedGain);

    private static CameraModuleConfig HostConfiguration()
    {
        var configuration = Configuration();
        return configuration with
        {
            AgentId = "test-agent",
            Rig = configuration.Rig with
            {
                Optics = configuration.Rig.Optics with { ImageCircleRadiusPixels = 1 },
                Pipeline = configuration.Rig.Pipeline with
                {
                    Envelope = new ExposureEnvelope(
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(5),
                        1,
                        10,
                        new ExposureDefaults(TimeSpan.FromSeconds(1), 1),
                        new ExposureDefaults(TimeSpan.FromSeconds(5), 10),
                        0.5)
                },
                ControlPolicy = new CameraControlPolicy
                {
                    ExposureControl = AutomaticControlOwnership.Disabled,
                    GainControl = AutomaticControlOwnership.Disabled
                }
            },
            DeploymentLocation = DeploymentLocationSnapshot.Create(
                "test-location",
                1,
                "test",
                null,
                DateTimeOffset.UnixEpoch,
                null,
                configuration.Observatory.LatitudeDegrees,
                configuration.Observatory.LongitudeDegrees,
                configuration.Observatory.ElevationMeters,
                configuration.Observatory.TimeZoneId)
        };
    }

    private sealed class StaticConfigurationLoader(CameraModuleConfig configuration)
        : ICameraAgentConfigurationLoader
    {
        public Task<CameraModuleConfig> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(configuration);
    }

    private sealed class EmptyPipelineFactory : ICaptureProcessingPipelineFactory
    {
        public CaptureProcessingGraph CreateGraph(CameraModuleConfig config) => new([]);

        public CaptureProcessingPlanPreview PreviewPlan(CameraModuleConfig config)
            => throw new NotSupportedException();
    }

    [TestMethod]
    public async Task Mutations_RequireExpectedDurableVersion()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            using var store = new SqliteCaptureScheduleStore(
                new JournalInitializer(root), options, TimeProvider.System);
            _ = await store.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);

            _ = await Assert.ThrowsAsync<ArgumentException>(() => store.StageAsync(
                LocalCaptureProfileDefinition.Create(Configuration(), Definition("night", 2)),
                "missing-version",
                null,
                "owner",
                null,
                CancellationToken.None)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PersistedPreview_WhenChecksumIsCorrupted_FailsClosed()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            using var store = new SqliteCaptureScheduleStore(
                new JournalInitializer(root), options, TimeProvider.System);
            var state = await store.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);
            var location = new CaptureLocationProvenance(
                "test-location", 1, "test", null, DateTimeOffset.UnixEpoch, null);
            var now = DateTimeOffset.UtcNow;
            var preview = CaptureScheduleIntervalExpander.Expand(
                state.ActiveRevision.Definition,
                DateOnly.FromDateTime(now.UtcDateTime),
                1,
                TimeZoneInfo.Utc,
                Configuration().Observatory,
                new AstronomyEngineSolarEventCalculator());
            await store.PersistPreviewAsync(
                state.ActiveRevision, location, preview, CancellationToken.None).ConfigureAwait(false);
            using (var connection = new SqliteConnection(
                $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE capture_schedule_expansions SET expansion_sha256 = $sha;";
                command.Parameters.AddWithValue("$sha", new string('F', 64));
                _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            _ = await Assert.ThrowsAsync<InvalidDataException>(() => store.TryReadPreviewAsync(
                state.ActiveRevision, location, now, CancellationToken.None)).ConfigureAwait(false);
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
                var profile = LocalCaptureProfileDefinition.Create(Configuration(), Definition("night", 2));
                var staged = (await store.StageWithCurrentFromBasisAsync(
                    profile,
                    initial.ActiveRevision.RevisionId,
                    "stage-1",
                    initial.Version,
                    "owner",
                    "night operations",
                    CancellationToken.None).ConfigureAwait(false)).Receipt;
                var replay = (await store.StageWithCurrentFromBasisAsync(
                    profile,
                    initial.ActiveRevision.RevisionId,
                    "stage-1",
                    initial.Version,
                    "owner",
                    "night operations",
                    CancellationToken.None).ConfigureAwait(false)).Receipt;
                Assert.AreEqual(staged.Version, replay.Version);

                activated = await store.ActivateAsync(
                    staged.PendingRevision!.RevisionId,
                    "activate-1",
                    staged.Version,
                    "owner",
                    "approved",
                    CancellationToken.None).ConfigureAwait(false);
                var lateReplay = (await store.StageWithCurrentFromBasisAsync(
                    profile,
                    initial.ActiveRevision.RevisionId,
                    "stage-1",
                    initial.Version,
                    "owner",
                    "night operations",
                    CancellationToken.None).ConfigureAwait(false)).Receipt;
                Assert.AreEqual(staged.Version, lateReplay.Version);
                Assert.AreEqual(staged.ActiveRevision.RevisionId, lateReplay.ActiveRevision.RevisionId);
                Assert.AreEqual(staged.PendingRevision!.RevisionId, lateReplay.PendingRevision!.RevisionId);
                _ = await Assert.ThrowsAsync<CaptureScheduleStoreConflictException>(() =>
                    store.StageWithCurrentFromBasisAsync(
                        profile,
                        initial.ActiveRevision.RevisionId,
                        "stage-stale-basis",
                        activated.Version,
                        "owner",
                        "stale basis",
                        CancellationToken.None)).ConfigureAwait(false);
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

    [TestMethod]
    public async Task RollbackIsDurableIdempotentAndRejectsNonHistoricalTargets()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var initializer = new JournalInitializer(root);
            using var store = new SqliteCaptureScheduleStore(initializer, options, TimeProvider.System);
            var initial = await store.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);
            var profile = LocalCaptureProfileDefinition.Create(Configuration(), Definition("night", 2));
            var staged = await store.StageAsync(
                profile, "stage-for-rollback", initial.Version, "owner", null, CancellationToken.None)
                .ConfigureAwait(false);
            var activated = await store.ActivateAsync(
                staged.PendingRevision!.RevisionId,
                "activate-before-rollback",
                staged.Version,
                "owner",
                null,
                CancellationToken.None).ConfigureAwait(false);
            var unchanged = await store.StageAsync(
                activated.ActiveRevision.Profile,
                "stage-active-noop",
                activated.Version,
                "owner",
                null,
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(activated.Version, unchanged.Version);
            Assert.IsNull(unchanged.PendingRevision);
            _ = await Assert.ThrowsExactlyAsync<CaptureScheduleStoreConflictException>(() =>
                store.ActivateWithCurrentAsync(
                    initial.ActiveRevision.RevisionId,
                    "activate-backward",
                    activated.Version,
                    "owner",
                    null,
                    CancellationToken.None)).ConfigureAwait(false);
            var historical = await store.StageAsync(
                initial.ActiveRevision.Profile,
                "stage-historical",
                activated.Version,
                "owner",
                null,
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevision.RevisionId, historical.PendingRevision!.RevisionId);
            var cancelled = await store.StageAsync(
                activated.ActiveRevision.Profile,
                "cancel-pending",
                historical.Version,
                "owner",
                null,
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(cancelled.PendingRevision);
            Assert.AreEqual(historical.Version + 1, cancelled.Version);
            historical = await store.StageAsync(
                initial.ActiveRevision.Profile,
                "stage-historical-again",
                cancelled.Version,
                "owner",
                null,
                CancellationToken.None).ConfigureAwait(false);

            var rolledBack = (await store.RollbackWithCurrentAsync(
                initial.ActiveRevision.RevisionId,
                "rollback-1",
                historical.Version,
                "owner",
                "restore previous schedule",
                CancellationToken.None).ConfigureAwait(false)).Receipt;
            var replay = (await store.RollbackWithCurrentAsync(
                initial.ActiveRevision.RevisionId,
                "rollback-1",
                historical.Version,
                "owner",
                "restore previous schedule",
                CancellationToken.None).ConfigureAwait(false)).Receipt;

            Assert.AreEqual(initial.ActiveRevision.RevisionId, rolledBack.ActiveRevision.RevisionId);
            Assert.AreEqual(rolledBack.Version, replay.Version);
            _ = await Assert.ThrowsExactlyAsync<CaptureScheduleStoreConflictException>(() =>
                store.RollbackWithCurrentAsync(
                    activated.ActiveRevision.RevisionId,
                    "rollback-forward",
                    rolledBack.Version,
                    "owner",
                    null,
                    CancellationToken.None)).ConfigureAwait(false);
            _ = await Assert.ThrowsExactlyAsync<CaptureScheduleStoreConflictException>(() =>
                store.ActivateWithCurrentAsync(
                    initial.ActiveRevision.RevisionId,
                    "rollback-1",
                    rolledBack.Version,
                    "owner",
                    "restore previous schedule",
                    CancellationToken.None)).ConfigureAwait(false);

            using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT command_kind FROM capture_schedule_commands WHERE idempotency_key = 'rollback-1';";
            Assert.AreEqual("rollback", await command.ExecuteScalarAsync().ConfigureAwait(false));
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
                    10)),
            CapturePipelineConfig.Empty)
        {
            Schedule = Definition("initial", 1)
        };

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
