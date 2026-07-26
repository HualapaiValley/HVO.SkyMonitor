using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Calibration;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class SqliteCalibrationLibraryStoreTests
{
    [TestMethod]
    public async Task AdoptPublishedBundleAsync_IsRestartStableAndRetainsImmutableEvidence()
    {
        var root = CreateRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            using var store = fixture.Store;

            var adopted = await store.AdoptPublishedBundleAsync(
                fixture.Bundle, CancellationToken.None).ConfigureAwait(false);
            var duplicate = await store.AdoptPublishedBundleAsync(
                fixture.Bundle, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(adopted.BundleIdentitySha256, duplicate.BundleIdentitySha256);
            Assert.AreEqual("published", adopted.PublicationState);
            var holds = await store.GetRetentionHoldsAsync(root, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(5, holds);
            Assert.IsTrue(holds.Any(hold => hold.PayloadRelativePath == fixture.Bundle.ProfileRelativePath &&
                hold.SidecarRelativePath == fixture.Bundle.ProfileRelativePath));
            Assert.IsNull((await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).ActiveBundle);

            using var restarted = CreateStore(root);
            var bundles = await restarted.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, bundles);
            Assert.AreEqual(adopted.BundleIdentitySha256, bundles[0].BundleIdentitySha256);
            using var connection = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarLongAsync(
                connection, "SELECT COUNT(*) FROM calibration_library_bundles;").ConfigureAwait(false));
            Assert.AreEqual(4L, await ScalarLongAsync(
                connection, "SELECT COUNT(*) FROM calibration_library_artifacts;").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ActivateAndSelect_AreVersionedIdempotentAndFailClosedOnMismatch()
    {
        var root = CreateRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            using var store = fixture.Store;
            _ = await store.AdoptPublishedBundleAsync(fixture.Bundle, CancellationToken.None).ConfigureAwait(false);

            var activated = await store.ActivateAsync(
                fixture.Bundle.BundleId, "activate-1", 0, "operator", "initial",
                CancellationToken.None).ConfigureAwait(false);
            var replayed = await store.ActivateAsync(
                fixture.Bundle.BundleId, "activate-1", 0, "operator", "initial",
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1L, activated.Version);
            Assert.AreEqual(activated.Version, replayed.Version);
            Assert.AreEqual(fixture.Bundle.BundleId, activated.ActiveBundle?.Bundle.BundleId);
            var selected = await store.SelectAsync(fixture.Light, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(selected.IsSelected, selected.ReasonCode);
            Assert.AreEqual(1L, selected.StateVersion);

            var incompatible = fixture.Light with
            {
                Controls = fixture.Light.Controls with { EffectiveGain = fixture.Light.Controls.EffectiveGain + 1 }
            };
            var rejected = await store.SelectAsync(incompatible, CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(rejected.IsSelected);
            Assert.AreEqual(CalibrationLibraryReasonCodes.IncompatibleConditions, rejected.ReasonCode);
            var secondActivation = await store.ActivateAsync(
                fixture.Bundle.BundleId, "activate-2", 1, "operator", "confirm",
                CancellationToken.None).ConfigureAwait(false);
            var historicalReplay = await store.ActivateAsync(
                fixture.Bundle.BundleId, "activate-1", 0, "operator", "initial",
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(2L, secondActivation.Version);
            Assert.AreEqual(1L, historicalReplay.Version);
            await Assert.ThrowsExactlyAsync<CalibrationLibraryStoreConflictException>(async () =>
                await store.ActivateAsync(
                    fixture.Bundle.BundleId, "activate-3", 0, "operator", null,
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<CalibrationLibraryStoreConflictException>(async () =>
                await store.ActivateAsync(
                    fixture.Bundle.BundleId, "activate-1", 0, "different-operator", "initial",
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            using var restarted = CreateStore(root);
            var restored = await restarted.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(2L, restored.Version);
            Assert.AreEqual(fixture.Bundle.BundleId, restored.ActiveBundle?.Bundle.BundleId);
            Assert.AreEqual(CalibrationLibraryReasonCodes.IncompatibleConditions, restored.LastSelectionReason);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AdoptPublishedBundleAsync_RejectsMissingOrConflictingEvidenceWithoutDurableRow()
    {
        foreach (var conflict in new[] { false, true })
        {
            var root = CreateRoot();
            try
            {
                var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
                using var store = fixture.Store;
                if (conflict)
                {
                    _ = await store.AdoptPublishedBundleAsync(
                        fixture.Bundle, CancellationToken.None).ConfigureAwait(false);
                    var changed = fixture.Bundle with
                    {
                        AcquisitionModelIdentitySha256 = new string('B', 64)
                    };
                    await Assert.ThrowsExactlyAsync<CalibrationLibraryStoreConflictException>(async () =>
                        await store.AdoptPublishedBundleAsync(changed, CancellationToken.None).ConfigureAwait(false))
                        .ConfigureAwait(false);
                }
                else
                {
                    var manifestPath = Path.Combine(
                        root,
                        fixture.Bundle.Artifacts[0].ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
                    File.Delete(manifestPath);
                    await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                        await store.AdoptPublishedBundleAsync(
                            fixture.Bundle, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                }

                using var connection = await OpenAsync(root).ConfigureAwait(false);
                Assert.AreEqual(conflict ? 1L : 0L, await ScalarLongAsync(
                    connection, "SELECT COUNT(*) FROM calibration_library_bundles;").ConfigureAwait(false));
            }
            finally
            {
                DeleteRoot(root);
            }
        }
    }

    [TestMethod]
    public async Task AdoptPublishedBundleAsync_RejectsEnvelopeFactsThatConflictWithPublishedManifests()
    {
        var root = CreateRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            using var store = fixture.Store;
            var first = fixture.Bundle.Artifacts[0];
            var changed = fixture.Bundle with
            {
                Artifacts = fixture.Bundle.Artifacts.Select(artifact => artifact.ArtifactId == first.ArtifactId
                    ? artifact with { Exposure = artifact.Exposure + TimeSpan.FromMilliseconds(1) }
                    : artifact).ToArray()
            };
            Assert.IsTrue(CalibrationLibraryContract.Validate(changed).IsValid);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await store.AdoptPublishedBundleAsync(changed, CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);

            using var connection = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(0L, await ScalarLongAsync(
                connection, "SELECT COUNT(*) FROM calibration_library_bundles;").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AdoptPublishedBundleAsync_RejectsManifestThatRedirectsToAnotherPayload()
    {
        var root = CreateRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            using var store = fixture.Store;
            var first = fixture.Bundle.Artifacts[0];
            var second = fixture.Bundle.Artifacts[1];
            var manifestPath = Path.Combine(
                root, first.ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var parsed = CaptureContractJson.ParseManifest(
                await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false));
            Assert.IsTrue(parsed.IsValid, parsed.Validation.ReasonCode);
            var redirected = parsed.Document!.Manifest! with
            {
                RelativeArtifactPath = second.ManifestRelativePath.Replace(".json", ".bin", StringComparison.Ordinal)
            };
            await File.WriteAllBytesAsync(manifestPath, CaptureContractJson.Serialize(redirected)).ConfigureAwait(false);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                store.AdoptPublishedBundleAsync(fixture.Bundle, CancellationToken.None)).ConfigureAwait(false);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ReconcileAsync_AdoptsLegacyBundleWithoutChangingEvidenceAndIsRestartStable()
    {
        var root = CreateRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            using var store = fixture.Store;
            var before = Directory.EnumerateFiles(
                    Path.Combine(root, "calibration", "synthetic"), "*", SearchOption.AllDirectories)
                .ToDictionary(
                    path => Path.GetRelativePath(root, path),
                    path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                    StringComparer.Ordinal);
            var reconciler = new CalibrationLibraryReconciler(store, CreateOptions(root));

            var first = await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            var second = await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, first.Adopted);
            Assert.AreEqual(0, first.Failed);
            Assert.AreEqual(1, second.Adopted);
            var after = Directory.EnumerateFiles(
                    Path.Combine(root, "calibration", "synthetic"), "*", SearchOption.AllDirectories)
                .ToDictionary(
                    path => Path.GetRelativePath(root, path),
                    path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                    StringComparer.Ordinal);
            CollectionAssert.AreEquivalent(before.Keys.ToArray(), after.Keys.ToArray());
            foreach (var pair in before)
            {
                Assert.AreEqual(pair.Value, after[pair.Key]);
            }
            Assert.HasCount(1, await store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false));
            using var connection = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarLongAsync(connection, """
                SELECT COUNT(*) FROM calibration_library_reconciliation
                WHERE outcome = 'adopted' AND operation_state = 'completed';
                """).ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ReconcileAsync_DurablyQuarantinesPreMarkerRemnantsAndResumesCleanly()
    {
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(root);
            await new SqliteRawCaptureJournal(
                Path.Combine(root, "journal", "raw-ingress.db"), 1)
                .InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var source = Path.Combine(root, "calibration", "synthetic", new string('A', 64));
            Directory.CreateDirectory(source);
            await File.WriteAllBytesAsync(Path.Combine(source, "bias.bin"), [1, 2, 3, 4]).ConfigureAwait(false);
            using var store = new SqliteCalibrationLibraryStore(
                new InitializedIngress(), CreateOptions(root), TimeProvider.System);
            var reconciler = new CalibrationLibraryReconciler(store, CreateOptions(root));

            var first = await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            var second = await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, first.Quarantined);
            Assert.AreEqual(0, first.Failed);
            Assert.AreEqual(0, second.Inspected);
            Assert.IsFalse(Directory.Exists(source));
            Assert.HasCount(1, Directory.EnumerateFiles(
                Path.Combine(root, "quarantine", "calibration"), "bias.bin", SearchOption.AllDirectories).ToArray());
            using var connection = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarLongAsync(connection, """
                SELECT COUNT(*) FROM calibration_library_reconciliation
                WHERE outcome = 'quarantined' AND operation_state = 'completed';
                """).ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task SelectAsync_RehashesAfterBoundedIntervalAndDetectsTimestampPreservingCorruption()
    {
        var root = CreateRoot();
        try
        {
            var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 1, 2, 4, 0, 0, TimeSpan.Zero));
            var fixture = await CreateFixtureAsync(root, timeProvider).ConfigureAwait(false);
            using var store = fixture.Store;
            _ = await store.AdoptPublishedBundleAsync(fixture.Bundle, CancellationToken.None).ConfigureAwait(false);
            _ = await store.ActivateAsync(
                fixture.Bundle.BundleId, "activate", 0, "operator", null,
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue((await store.SelectAsync(
                fixture.Light, CancellationToken.None).ConfigureAwait(false)).IsSelected);
            var payloadPath = Path.Combine(
                root,
                fixture.Bundle.Artifacts[0].ManifestRelativePath
                    .Replace(".json", ".bin", StringComparison.Ordinal)
                    .Replace('/', Path.DirectorySeparatorChar));
            var originalTimestamp = File.GetLastWriteTimeUtc(payloadPath);
            var bytes = await File.ReadAllBytesAsync(payloadPath).ConfigureAwait(false);
            bytes[0] ^= 0xFF;
            await File.WriteAllBytesAsync(payloadPath, bytes).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(payloadPath, originalTimestamp);
            timeProvider.Advance(TimeSpan.FromMinutes(11));

            var result = await store.SelectAsync(fixture.Light, CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.IsSelected);
            Assert.AreEqual(CalibrationLibraryReasonCodes.Corrupt, result.ReasonCode);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ReconcileAsync_MalformedCommittedBundleRecordsFailureWithoutThrowing()
    {
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(root);
            await new SqliteRawCaptureJournal(
                Path.Combine(root, "journal", "raw-ingress.db"), 1)
                .InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var source = Path.Combine(root, "calibration", "synthetic", new string('A', 64));
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(
                Path.Combine(source, "calibration-profile.json"), "{not-json").ConfigureAwait(false);
            using var store = new SqliteCalibrationLibraryStore(
                new InitializedIngress(), CreateOptions(root), TimeProvider.System);
            var reconciler = new CalibrationLibraryReconciler(store, CreateOptions(root));

            var summary = await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, summary.Failed);
            Assert.IsTrue(Directory.Exists(source));
            using var connection = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarLongAsync(connection, """
                SELECT COUNT(*) FROM calibration_library_reconciliation
                WHERE outcome = 'failed' AND operation_state = 'completed';
                """).ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ReconcileAsync_NullProfileReferencesRecordFailureWithoutAbortingStartup()
    {
        var root = CreateRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            using var store = fixture.Store;
            var profilePath = Path.Combine(
                root,
                fixture.Bundle.ProfileRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var profile = JsonNode.Parse(await File.ReadAllTextAsync(profilePath).ConfigureAwait(false))!.AsObject();
            profile["References"] = null;
            await File.WriteAllTextAsync(profilePath, profile.ToJsonString()).ConfigureAwait(false);
            var reconciler = new CalibrationLibraryReconciler(store, CreateOptions(root));

            var summary = await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, summary.Failed);
            using var connection = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarLongAsync(connection, """
                SELECT COUNT(*) FROM calibration_library_reconciliation
                WHERE outcome = 'failed' AND operation_state = 'completed';
                """).ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task<Fixture> CreateFixtureAsync(
        string root,
        TimeProvider? timeProvider = null)
    {
        Directory.CreateDirectory(root);
        var options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = root,
            RawIngressSqliteBusyTimeoutSeconds = 1
        });
        await new SqliteRawCaptureJournal(
            Path.Combine(root, "journal", "raw-ingress.db"), 1)
            .InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var model = new SyntheticCalibrationModelV1
        {
            Gain = 99.5,
            TemperatureC = -9.5,
            Defects = [new SyntheticCalibrationDefect(1, 1)]
        };
        var payload = new byte[8];
        var template = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 2, 2, 4, payload);
        var light = template.Descriptor with
        {
            Controls = template.Descriptor.Controls with
            {
                RequestedGain = model.Gain,
                EffectiveGain = model.Gain,
                RequestedOffset = null,
                EffectiveOffset = null,
                TemperatureSetpointC = model.TemperatureC,
                EffectiveTemperatureC = model.TemperatureC
            }
        };
        var syntheticStore = new SyntheticCalibrationReferenceStore(
            options, new CameraAgentClearReferenceLoader(options));
        var synthetic = await syntheticStore.GetOrCreateAsync(
            light, model, CancellationToken.None).ConfigureAwait(false);
        var modelIdentity = synthetic.ReferenceManifests.Values.First().RelativeArtifactPath.Split('/')[2];
        var outputLayout = light.Layout with
        {
            SampleDepthBits = 16,
            ContainerDepthBits = 16,
            BlackLevel = 0,
            WhiteLevel = ushort.MaxValue,
            StoredCodeTransform = FrameStoredCodeTransform.IdentityV1,
            LevelCodeSpace = FrameLevelCodeSpace.StoredContainer
        };
        var artifacts = CalibrationReferenceKinds.All.Select(kind =>
        {
            var reference = synthetic.Profile.References.Single(candidate => candidate.Kind == kind);
            var manifestRelativePath = synthetic.EvidenceFiles.Single(file =>
                file.RelativePath.EndsWith($"/{kind}.json", StringComparison.Ordinal)).RelativePath;
            return new CalibrationLibraryArtifactV1(
                kind,
                CalibrationLibraryArtifactRoles.Master,
                reference.ArtifactId,
                manifestRelativePath,
                reference.PayloadSha256,
                reference.Exposure,
                reference.Gain,
                null,
                reference.TemperatureC,
                null,
                [],
                null);
        }).ToArray();
        var profilePath = synthetic.EvidenceFiles.Single(file =>
            file.RelativePath.EndsWith("/calibration-profile.json", StringComparison.Ordinal)).RelativePath;
        var bundle = new CalibrationLibraryBundleV1(
            CalibrationLibraryBundleV1.CurrentSchemaVersion,
            $"legacy-{modelIdentity[..16]}",
            CalibrationLibraryBundleSources.LegacySyntheticV1,
            DateTimeOffset.UnixEpoch,
            profilePath,
            synthetic.ProfileIdentitySha256,
            modelIdentity,
            new CalibrationApplicabilityV1(
                light.Capture.AgentId,
                light.Capture.RigId,
                light.Profiles.Rig.Sha256,
                light.Profiles.Sensor.Sha256,
                light.Layout,
                outputLayout,
                model.Gain,
                model.Gain,
                null,
                null,
                null,
                null,
                model.TemperatureC,
                model.TemperatureC,
                DateTimeOffset.UnixEpoch,
                null),
            artifacts);
        Assert.IsTrue(CalibrationLibraryContract.Validate(bundle).IsValid);
        return new Fixture(new SqliteCalibrationLibraryStore(
            new InitializedIngress(), options, timeProvider ?? TimeProvider.System), bundle, light);
    }

    private static SqliteCalibrationLibraryStore CreateStore(string root)
    {
        var options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = root,
            RawIngressSqliteBusyTimeoutSeconds = 1
        });
        return new SqliteCalibrationLibraryStore(new InitializedIngress(), options, TimeProvider.System);
    }

    private static IOptions<CameraAgentHostOptions> CreateOptions(string root)
        => Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = root,
            RawIngressSqliteBusyTimeoutSeconds = 1
        });

    private static async Task<SqliteConnection> OpenAsync(string root)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Tests pass only fixed SQL assertions.")]
    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string CreateRoot()
        => Path.Combine(Path.GetTempPath(), "hvo-calibration-library", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record Fixture(
        SqliteCalibrationLibraryStore Store,
        CalibrationLibraryBundleV1 Bundle,
        ReconstructionDescriptor Light);

    private sealed class InitializedIngress : IRawCaptureIngress
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
