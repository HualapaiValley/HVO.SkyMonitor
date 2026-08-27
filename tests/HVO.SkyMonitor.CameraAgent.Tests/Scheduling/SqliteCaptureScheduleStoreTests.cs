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
    public async Task InitializeAsync_PersistedLegacyControlPoliciesNormalizeDuringHostRestart()
    {
        var cases = new[]
        {
            new LegacyControlCase(
                LocalCaptureProfileDefinition.LegacySchemaVersion,
                new CameraControlPolicy
                {
                    AutoExposure = CameraFeatureDirective.Enabled,
                    AutoGain = CameraFeatureDirective.Disabled
                },
                AutomaticControlOwnership.HostMetered,
                AutomaticControlOwnership.Disabled,
                CapturePipelineSchemaVersions.LegacyV1),
            new LegacyControlCase(
                LocalCaptureProfileDefinition.LegacySchemaVersion,
                null,
                AutomaticControlOwnership.Disabled,
                AutomaticControlOwnership.Disabled,
                CapturePipelineSchemaVersions.LegacyV1),
            new LegacyControlCase(
                LocalCaptureProfileDefinition.CurrentSchemaVersion,
                new CameraControlPolicy
                {
                    AutoExposure = CameraFeatureDirective.Enabled,
                    AutoGain = CameraFeatureDirective.Disabled
                },
                AutomaticControlOwnership.HostMetered,
                AutomaticControlOwnership.Disabled,
                CapturePipelineSchemaVersions.ExplicitV2),
            new LegacyControlCase(
                LocalCaptureProfileDefinition.CurrentSchemaVersion,
                new CameraControlPolicy
                {
                    ExposureControl = AutomaticControlOwnership.CameraNative,
                    GainControl = AutomaticControlOwnership.CameraNative,
                    AutoExposure = CameraFeatureDirective.Enabled,
                    AutoGain = CameraFeatureDirective.Disabled
                },
                AutomaticControlOwnership.CameraNative,
                AutomaticControlOwnership.CameraNative,
                CapturePipelineSchemaVersions.ExplicitV2)
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
                byte[] profileJson;
                using (var store = new SqliteCaptureScheduleStore(
                    journalInitializer, options, TimeProvider.System))
                {
                    var initial = await store.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
                    var legacyConfiguration = configuration with
                    {
                        Rig = configuration.Rig with { ControlPolicy = testCase.Policy }
                    };
                    var profile = testCase.ProfileSchemaVersion == LocalCaptureProfileDefinition.CurrentSchemaVersion
                        ? LocalCaptureProfileDefinition.CreateV2(legacyConfiguration, Definition("legacy", 2))
                        : LocalCaptureProfileDefinition.Create(legacyConfiguration, Definition("legacy", 2));
                    revisionId = initial.ActiveRevision.RevisionId;
                    profileJson = System.Text.Encoding.UTF8.GetBytes(
                        CaptureContractJson.SerializeToElement(profile).GetRawText());
                    using var connection = new SqliteConnection(
                        $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
                    await connection.OpenAsync().ConfigureAwait(false);
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        UPDATE capture_schedule_revisions
                        SET profile_json = $json, profile_sha256 = $profile_sha, schedule_sha256 = $schedule_sha
                        WHERE revision_id = $id;
                        """;
                    command.Parameters.AddWithValue("$json", profileJson);
                    command.Parameters.AddWithValue(
                        "$profile_sha",
                        LocalCaptureProfileContract.ComputePersistedRevisionSha256(profile));
                    command.Parameters.AddWithValue("$schedule_sha", CaptureScheduleContract.ComputeSha256(profile.Schedule));
                    command.Parameters.AddWithValue("$id", revisionId);
                    Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
                }
                using (var document = System.Text.Json.JsonDocument.Parse(profileJson))
                {
                    var serializedPolicy = document.RootElement.GetProperty("rig").GetProperty("controlPolicy");
                    if (testCase.Policy is null)
                    {
                        Assert.AreEqual(System.Text.Json.JsonValueKind.Null, serializedPolicy.ValueKind);
                    }
                    else if (testCase.ProfileSchemaVersion == LocalCaptureProfileDefinition.CurrentSchemaVersion)
                    {
                        Assert.AreEqual(
                            testCase.Policy!.ExposureControl == AutomaticControlOwnership.Unspecified,
                            !serializedPolicy.TryGetProperty("exposureControl", out _));
                        Assert.AreEqual(
                            testCase.Policy.GainControl == AutomaticControlOwnership.Unspecified,
                            !serializedPolicy.TryGetProperty("gainControl", out _));
                        Assert.AreEqual("Enabled", serializedPolicy.GetProperty("autoExposure").GetString());
                        Assert.AreEqual("Disabled", serializedPolicy.GetProperty("autoGain").GetString());
                    }
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
                var recovered = await restarted.GetRevisionAsync(revisionId, CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual(testCase.ExpectedPipelineSchemaVersion, effective.Pipeline.SchemaVersion);
                Assert.AreEqual(
                    testCase.ExpectedExposure,
                    effective.Rig.ControlPolicy!.ExposureControl);
                Assert.AreEqual(
                    testCase.ExpectedGain,
                    effective.Rig.ControlPolicy.GainControl);
                Assert.AreEqual(testCase.ExpectedExposure, recovered.Profile.Rig.ControlPolicy!.ExposureControl);
                Assert.AreEqual(testCase.ExpectedGain, recovered.Profile.Rig.ControlPolicy.GainControl);
                Assert.IsNull(recovered.Profile.Rig.ControlPolicy.AutoExposure);
                Assert.IsNull(recovered.Profile.Rig.ControlPolicy.AutoGain);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed record LegacyControlCase(
        string ProfileSchemaVersion,
        CameraControlPolicy? Policy,
        AutomaticControlOwnership ExpectedExposure,
        AutomaticControlOwnership ExpectedGain,
        string ExpectedPipelineSchemaVersion);

    [TestMethod]
    [DataRow(LocalCaptureProfileDefinition.LegacySchemaVersion, true)]
    [DataRow(LocalCaptureProfileDefinition.LegacySchemaVersion, false)]
    [DataRow(LocalCaptureProfileDefinition.CurrentSchemaVersion, true)]
    [DataRow(LocalCaptureProfileDefinition.CurrentSchemaVersion, false)]
    public async Task InitializeAsync_ChecksumValidUndefinedLegacyDirectiveFailsBeforeNormalization(
        string schemaVersion,
        bool invalidExposure)
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var initializer = new JournalInitializer(root);
            var configuration = HostConfiguration();
            LocalCaptureProfileDefinition malformedProfile;
            string revisionId;
            using (var seedStore = new SqliteCaptureScheduleStore(initializer, options, TimeProvider.System))
            {
                var initial = await seedStore.InitializeAsync(
                    configuration, CancellationToken.None).ConfigureAwait(false);
                var malformedConfiguration = configuration with
                {
                    Rig = configuration.Rig with
                    {
                        ControlPolicy = new CameraControlPolicy
                        {
                            AutoExposure = invalidExposure
                                ? (CameraFeatureDirective)int.MaxValue
                                : CameraFeatureDirective.Enabled,
                            AutoGain = invalidExposure
                                ? CameraFeatureDirective.Disabled
                                : (CameraFeatureDirective)int.MaxValue
                        }
                    }
                };
                malformedProfile = schemaVersion == LocalCaptureProfileDefinition.LegacySchemaVersion
                    ? LocalCaptureProfileDefinition.Create(malformedConfiguration, configuration.Schedule!)
                    : LocalCaptureProfileDefinition.CreateV2(malformedConfiguration, configuration.Schedule!);
                var serializableConfiguration = malformedConfiguration with
                {
                    Rig = malformedConfiguration.Rig with
                    {
                        ControlPolicy = new CameraControlPolicy
                        {
                            AutoExposure = CameraFeatureDirective.Enabled,
                            AutoGain = CameraFeatureDirective.Disabled
                        }
                    }
                };
                var serializableProfile = schemaVersion == LocalCaptureProfileDefinition.LegacySchemaVersion
                    ? LocalCaptureProfileDefinition.Create(serializableConfiguration, configuration.Schedule!)
                    : LocalCaptureProfileDefinition.CreateV2(serializableConfiguration, configuration.Schedule!);
                revisionId = initial.ActiveRevision.RevisionId;
                var profileNode = System.Text.Json.Nodes.JsonNode.Parse(
                    CaptureContractJson.SerializeToElement(serializableProfile).GetRawText())!.AsObject();
                profileNode["rig"]!["controlPolicy"]![invalidExposure ? "autoExposure" : "autoGain"] = int.MaxValue;
                var profileJson = System.Text.Encoding.UTF8.GetBytes(profileNode.ToJsonString());
                using var profileDocument = System.Text.Json.JsonDocument.Parse(profileJson);
                var rawProfileSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(profileDocument.RootElement);
                using var connection = new SqliteConnection(
                    $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE capture_schedule_revisions
                    SET profile_json = $json, profile_sha256 = $profile_sha
                    WHERE revision_id = $revision;
                    """;
                command.Parameters.AddWithValue("$json", profileJson);
                command.Parameters.AddWithValue("$profile_sha", rawProfileSha256);
                command.Parameters.AddWithValue("$revision", revisionId);
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
            }
            var validation = LocalCaptureProfileContract.ValidatePersistedRevision(malformedProfile);
            Assert.IsFalse(validation.IsValid);
            Assert.AreEqual(
                invalidExposure
                    ? "localProfile.rig.controlPolicy.autoExposure"
                    : "localProfile.rig.controlPolicy.autoGain",
                validation.FieldPath);
            using var restarted = new SqliteCaptureScheduleStore(initializer, options, TimeProvider.System);

            _ = await Assert.ThrowsAsync<InvalidDataException>(() => restarted.GetRevisionAsync(
                revisionId, CancellationToken.None)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task StageAsync_RejectsLegacyAndMalformedCurrentProfiles()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            using var store = new SqliteCaptureScheduleStore(
                new JournalInitializer(root), options, TimeProvider.System);
            var configuration = HostConfiguration();
            var initial = await store.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            var legacy = LocalCaptureProfileDefinition.Create(configuration, Definition("legacy", 2));
            var malformedCurrentProfiles = new[]
            {
                LocalCaptureProfileDefinition.CreateV2(
                    configuration with
                    {
                        Rig = configuration.Rig with { ControlPolicy = new CameraControlPolicy() }
                    },
                    Definition("unspecified", 2)),
                LocalCaptureProfileDefinition.CreateV2(
                    configuration with
                    {
                        Rig = configuration.Rig with
                        {
                            ControlPolicy = configuration.Rig.ControlPolicy! with
                            {
                                AutoExposure = CameraFeatureDirective.Enabled,
                                AutoGain = CameraFeatureDirective.Disabled
                            }
                        }
                    },
                    Definition("legacy-directives", 2)),
                LocalCaptureProfileDefinition.CreateV2(
                    configuration,
                    Definition("legacy-schedule", 2) with
                    {
                        WeeklyWindows = [],
                        LegacyAlwaysOpen = true,
                        LegacySetpointProfileId = "legacy-schedule"
                    })
            };

            _ = await Assert.ThrowsAsync<ArgumentException>(() => store.StageAsync(
                legacy, "stage-v1", initial.Version, "test", null, CancellationToken.None)).ConfigureAwait(false);
            foreach (var (malformedCurrent, index) in malformedCurrentProfiles.Select((profile, index) => (profile, index)))
            {
                _ = await Assert.ThrowsAsync<ArgumentException>(() => store.StageAsync(
                    malformedCurrent,
                    $"stage-malformed-v2-{index}",
                    initial.Version,
                    "test",
                    null,
                    CancellationToken.None)).ConfigureAwait(false);
            }

            var unchanged = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(initial.Version, unchanged.Version);
            Assert.IsNull(unchanged.PendingRevision);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(LocalCaptureProfileDefinition.LegacySchemaVersion)]
    [DataRow(LocalCaptureProfileDefinition.CurrentSchemaVersion)]
    public async Task StageAsync_PreUpgradeLegacyCommandReplaysButNewKeyIsRejected(string schemaVersion)
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var initializer = new JournalInitializer(root);
            var configuration = HostConfiguration();
            const string idempotencyKey = "pre-upgrade-stage";
            const string actor = "test";
            const string reason = "pre-upgrade";
            long? expectedVersion;
            CaptureScheduleStoreSnapshot staged;
            LocalCaptureProfileDefinition persistedProfile;
            using (var store = new SqliteCaptureScheduleStore(initializer, options, TimeProvider.System))
            {
                var initial = await store.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
                expectedVersion = initial.Version;
                var schedule = Definition("upgrade", 2);
                staged = await store.StageAsync(
                    LocalCaptureProfileDefinition.CreateV2(configuration, schedule),
                    idempotencyKey,
                    expectedVersion,
                    actor,
                    reason,
                    CancellationToken.None).ConfigureAwait(false);
                var legacyConfiguration = configuration with
                {
                    Rig = configuration.Rig with
                    {
                        ControlPolicy = new CameraControlPolicy
                        {
                            AutoExposure = CameraFeatureDirective.Enabled,
                            AutoGain = CameraFeatureDirective.Disabled
                        }
                    }
                };
                persistedProfile = schemaVersion == LocalCaptureProfileDefinition.LegacySchemaVersion
                    ? LocalCaptureProfileDefinition.Create(legacyConfiguration, schedule)
                    : LocalCaptureProfileDefinition.CreateV2(legacyConfiguration, schedule);
            }
            var profileJson = System.Text.Encoding.UTF8.GetBytes(
                CaptureContractJson.SerializeToElement(persistedProfile).GetRawText());
            var commandSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(new
            {
                CommandKind = "stage",
                Payload = persistedProfile,
                expectedVersion,
                actor,
                reason
            });
            using (var connection = new SqliteConnection(
                $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE capture_schedule_revisions
                    SET profile_json = $json, profile_sha256 = $profile_sha, schedule_sha256 = $schedule_sha
                    WHERE revision_id = $revision;
                    UPDATE capture_schedule_commands
                    SET payload_sha256 = $command_sha
                    WHERE idempotency_key = $operation;
                    """;
                command.Parameters.AddWithValue("$json", profileJson);
                command.Parameters.AddWithValue(
                    "$profile_sha",
                    LocalCaptureProfileContract.ComputePersistedRevisionSha256(persistedProfile));
                command.Parameters.AddWithValue(
                    "$schedule_sha",
                    CaptureScheduleContract.ComputeSha256(persistedProfile.Schedule));
                command.Parameters.AddWithValue("$revision", staged.PendingRevision!.RevisionId);
                command.Parameters.AddWithValue("$command_sha", commandSha256);
                command.Parameters.AddWithValue("$operation", idempotencyKey);
                Assert.AreEqual(2, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
            }

            using var restarted = new SqliteCaptureScheduleStore(initializer, options, TimeProvider.System);
            var replay = await restarted.StageAsync(
                persistedProfile,
                idempotencyKey,
                expectedVersion,
                actor,
                reason,
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(staged.Version, replay.Version);
            Assert.IsNotNull(replay.PendingRevision);
            Assert.AreEqual(schemaVersion, replay.PendingRevision.Profile.SchemaVersion);
            Assert.AreEqual(
                LocalCaptureProfileContract.ComputeEffectiveSha256(replay.PendingRevision.Profile),
                replay.PendingRevision.ProfileSha256);
            _ = await Assert.ThrowsAsync<ArgumentException>(() => restarted.StageAsync(
                persistedProfile,
                $"{idempotencyKey}-new",
                staged.Version,
                actor,
                reason,
                CancellationToken.None)).ConfigureAwait(false);
            _ = await Assert.ThrowsAsync<CaptureScheduleStoreConflictException>(() => restarted.StageAsync(
                persistedProfile with { Schedule = Definition("mismatch", 3) },
                idempotencyKey,
                expectedVersion,
                actor,
                reason,
                CancellationToken.None)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task StageAsync_RecoveredNormalizedActiveProfileIsEffectiveNoOp()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var initializer = new JournalInitializer(root);
            var configuration = HostConfiguration();
            using (var seedStore = new SqliteCaptureScheduleStore(initializer, options, TimeProvider.System))
            {
                var initial = await seedStore.InitializeAsync(
                    configuration, CancellationToken.None).ConfigureAwait(false);
                var persistedConfiguration = configuration with
                {
                    Rig = configuration.Rig with
                    {
                        ControlPolicy = new CameraControlPolicy
                        {
                            AutoExposure = CameraFeatureDirective.Disabled,
                            AutoGain = CameraFeatureDirective.Disabled
                        }
                    }
                };
                var persistedProfile = LocalCaptureProfileDefinition.CreateV2(
                    persistedConfiguration, configuration.Schedule!);
                var profileJson = System.Text.Encoding.UTF8.GetBytes(
                    CaptureContractJson.SerializeToElement(persistedProfile).GetRawText());
                using var connection = new SqliteConnection(
                    $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE capture_schedule_revisions
                    SET profile_json = $json, profile_sha256 = $profile_sha
                    WHERE revision_id = $revision;
                    """;
                command.Parameters.AddWithValue("$json", profileJson);
                command.Parameters.AddWithValue(
                    "$profile_sha",
                    LocalCaptureProfileContract.ComputePersistedRevisionSha256(persistedProfile));
                command.Parameters.AddWithValue("$revision", initial.ActiveRevision.RevisionId);
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
            }

            using var restarted = new SqliteCaptureScheduleStore(initializer, options, TimeProvider.System);
            var recovered = await restarted.InitializeAsync(
                configuration, CancellationToken.None).ConfigureAwait(false);
            using var before = new SqliteConnection(
                $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
            await before.OpenAsync().ConfigureAwait(false);
            var revisionCount = await CountRevisionsAsync(before).ConfigureAwait(false);
            await before.CloseAsync().ConfigureAwait(false);

            var replayed = await restarted.StageAsync(
                recovered.ActiveRevision.Profile,
                "stage-normalized-active",
                recovered.Version,
                "test",
                null,
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(recovered.ActiveRevision.RevisionId, replayed.ActiveRevision.RevisionId);
            Assert.AreEqual(recovered.Version, replayed.Version);
            Assert.IsNull(replayed.PendingRevision);
            using var verification = new SqliteConnection(
                $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
            await verification.OpenAsync().ConfigureAwait(false);
            Assert.AreEqual(
                revisionCount,
                await CountRevisionsAsync(verification).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
                LocalCaptureProfileDefinition.CreateV2(Configuration(), Definition("night", 2)),
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
                var profile = LocalCaptureProfileDefinition.CreateV2(Configuration(), Definition("night", 2));
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
            var profile = LocalCaptureProfileDefinition.CreateV2(Configuration(), Definition("night", 2));
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
                    10),
                new CameraControlPolicy
                {
                    ExposureControl = AutomaticControlOwnership.Disabled,
                    GainControl = AutomaticControlOwnership.Disabled
                }),
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

    private static async Task<long> CountRevisionsAsync(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM capture_schedule_revisions;";
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

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
