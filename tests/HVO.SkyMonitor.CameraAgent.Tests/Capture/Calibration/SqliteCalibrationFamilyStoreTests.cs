using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.CameraAgent.Tests.Silo;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Calibration;

/// <summary>
/// The calibration-family store and activation service (#1475 slice C1): canonical storage by identity, explicit
/// idempotent activation and deactivation, one active family per agent and rig, refusal of a family that does not fit
/// its target, durability across a reopen and a crash, and the retention hold on the active family's masters.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Capture.Calibration")]
[DoNotParallelize]
public sealed class SqliteCalibrationFamilyStoreTests
{
    private const string Agent = "agent-a";
    private const string Rig = "rig-a";
    private static readonly string RigSha256 = Hash("rig");
    private static readonly string SensorSha256 = Hash("sensor");
    private static readonly string MaskSha256 = Hash("mask");
    private static readonly ProfileIdentityDescriptor Model = new(
        "synthetic-calibration-model", SyntheticCalibrationModelV1.CurrentSchemaVersion, Hash("model"));
    private static readonly DateTimeOffset EffectiveFrom = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task StoreAsync_KeepsCanonicalBytesByIdentityAndStoresEachFamilyOnce()
    {
        var root = CreateRoot();
        try
        {
            var family = Family("family-a", Member(10), Member(20));
            var identity = CalibrationFamilyContractJson.ComputeIdentitySha256(family);
            using (var store = CreateStore(root))
            {
                var stored = await store.StoreAsync(family, CancellationToken.None).ConfigureAwait(false);
                // The same family with its members and masters in another order has the same canonical bytes.
                var reordered = family with
                {
                    Members = [.. family.Members.Reverse().Select(static member => member with { Masters = [.. member.Masters.Reverse()] })]
                };
                var again = await store.StoreAsync(reordered, CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual(identity, stored.IdentitySha256);
                Assert.AreEqual(identity, again.IdentitySha256);
                Assert.AreEqual(stored.CreatedUtc, again.CreatedUtc, "storing again returns the record already stored");
                CollectionAssert.AreEqual(
                    CalibrationFamilyContractJson.Serialize(family),
                    CalibrationFamilyContractJson.Serialize((await store.GetAsync(Lowercase(identity), CancellationToken.None).ConfigureAwait(false))!.Family));
                Assert.IsNull(await store.GetAsync(Hash("absent"), CancellationToken.None).ConfigureAwait(false));
                await Assert.ThrowsExactlyAsync<ArgumentException>(
                    async () => await store.StoreAsync(family with { Members = [] }, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            }

            using var connection = Open(root);
            Assert.AreEqual(1L, Scalar(connection, "SELECT count(*) FROM calibration_families;"));
            CollectionAssert.AreEqual(
                CalibrationFamilyContractJson.Serialize(family),
                (byte[])Query(connection, "SELECT canonical_json FROM calibration_families;").Single()!);
            CollectionAssert.AreEqual(
                family.Members.SelectMany(static member => member.Masters).Select(static master => master.ArtifactId.ToString("N")).Order(StringComparer.Ordinal).ToArray(),
                Query(connection, "SELECT artifact_id FROM calibration_family_masters ORDER BY artifact_id;").Cast<string>().ToArray(),
                "the master index lists exactly the family's masters");
            Assert.AreEqual(1L, Scalar(connection, "PRAGMA user_version;"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    [DataRow("UPDATE calibration_families SET canonical_json = CAST(replace(CAST(canonical_json AS TEXT), 'family-a', 'family-b') AS BLOB);")]
    [DataRow("UPDATE calibration_families SET canonical_json = CAST(replace(CAST(canonical_json AS TEXT), '\"gain\":10', '\"gain\":11') AS BLOB);")]
    [DataRow("UPDATE calibration_families SET agent_id = 'agent-b';")]
    public async Task GetAsync_RefusesAStoredFamilyThatNoLongerParsesToItsIdentity(string tamper)
    {
        var root = CreateRoot();
        try
        {
            var family = Family("family-a", Member(10));
            var identity = CalibrationFamilyContractJson.ComputeIdentitySha256(family);
            using (var store = CreateStore(root))
            {
                _ = await store.StoreAsync(family, CancellationToken.None).ConfigureAwait(false);
            }
            Execute(root, "PRAGMA foreign_keys = OFF; " + tamper);

            using var reopened = CreateStore(root);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                async () => await reopened.GetAsync(identity, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ActivateAndDeactivate_AreDurableIdempotentAndRefuseAReusedKeyWithAnotherPayload()
    {
        var root = CreateRoot();
        try
        {
            using var store = CreateStore(root);
            var service = new CalibrationFamilyActivationService(store, NullLogger<CalibrationFamilyActivationService>.Instance);
            var family = await store.StoreAsync(Family("family-a", Member(10)), CancellationToken.None).ConfigureAwait(false);
            var request = Activation("activate-1", family.IdentitySha256, reason: "approved");

            var activated = await service.ActivateAsync(request, CancellationToken.None).ConfigureAwait(false);
            var replayed = await service.ActivateAsync(
                request with { FamilyIdentitySha256 = Lowercase(family.IdentitySha256) }, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(activated.IsAccepted, activated.ReasonCode);
            Assert.IsFalse(activated.Replayed);
            Assert.AreEqual(CalibrationFamilyActivationReasonCodes.Activated, activated.ReasonCode);
            Assert.IsNull(activated.Record!.FromIdentitySha256);
            Assert.AreEqual(family.IdentitySha256, activated.Record.ToIdentitySha256);
            Assert.AreEqual(("operator", "approved"), (activated.Record.Actor, activated.Record.Reason));
            Assert.IsTrue(replayed.Replayed);
            Assert.AreEqual(activated.Record, replayed.Record, "a replay returns the recorded result");
            foreach (var changed in new[]
                     {
                         request with { Reason = "another reason" },
                         request with { Actor = "another-operator" },
                         request with { Reason = null }
                     })
            {
                var conflict = await service.ActivateAsync(changed, CancellationToken.None).ConfigureAwait(false);
                Assert.IsFalse(conflict.IsAccepted);
                Assert.AreEqual(CalibrationFamilyActivationReasonCodes.IdempotencyConflict, conflict.ReasonCode);
            }
            var reusedForDeactivation = await service.DeactivateAsync(
                Deactivation("activate-1"), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CalibrationFamilyActivationReasonCodes.IdempotencyConflict, reusedForDeactivation.ReasonCode);
            Assert.AreEqual(family.IdentitySha256, (await store.GetActiveAsync(Agent, Rig, CancellationToken.None).ConfigureAwait(false))!.Family.IdentitySha256);

            var deactivated = await service.DeactivateAsync(Deactivation("deactivate-1"), CancellationToken.None).ConfigureAwait(false);
            var deactivationReplay = await service.DeactivateAsync(Deactivation("deactivate-1"), CancellationToken.None).ConfigureAwait(false);
            var deactivationConflict = await service.DeactivateAsync(
                Deactivation("deactivate-1") with { Reason = "changed" }, CancellationToken.None).ConfigureAwait(false);
            // An activation replayed after a later deactivation returns its record and does not activate again.
            var historical = await service.ActivateAsync(request, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(deactivated.IsAccepted, deactivated.ReasonCode);
            Assert.AreEqual(CalibrationFamilyActivationReasonCodes.Deactivated, deactivated.ReasonCode);
            Assert.AreEqual((family.IdentitySha256, (string?)null), (deactivated.Record!.FromIdentitySha256, deactivated.Record.ToIdentitySha256));
            Assert.IsTrue(deactivationReplay.Replayed);
            Assert.AreEqual(deactivated.Record, deactivationReplay.Record);
            Assert.AreEqual(CalibrationFamilyActivationReasonCodes.IdempotencyConflict, deactivationConflict.ReasonCode);
            Assert.IsTrue(historical.Replayed);
            Assert.AreEqual(activated.Record, historical.Record);
            Assert.IsNull(await store.GetActiveAsync(Agent, Rig, CancellationToken.None).ConfigureAwait(false));
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await service.ActivateAsync(
                request with { IdempotencyKey = "unsafe\nkey" }, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            using var connection = Open(root);
            CollectionAssert.AreEqual(
                new object?[] { "activate-1", "deactivate-1" },
                Query(connection, "SELECT idempotency_key FROM calibration_family_activations ORDER BY recorded_unix_ms, idempotency_key;"),
                "only the two accepted commands are recorded");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ActivateAsync_KeepsOneActiveFamilyPerAgentAndRig()
    {
        var root = CreateRoot();
        try
        {
            using var store = CreateStore(root);
            var service = new CalibrationFamilyActivationService(store, NullLogger<CalibrationFamilyActivationService>.Instance);
            var first = await store.StoreAsync(Family("family-a", Member(10)), CancellationToken.None).ConfigureAwait(false);
            var second = await store.StoreAsync(Family("family-b", Member(20)), CancellationToken.None).ConfigureAwait(false);
            var otherRig = await store.StoreAsync(Family("family-c", Member(30)) with { RigId = "rig-b" }, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue((await service.ActivateAsync(Activation("a", first.IdentitySha256), CancellationToken.None).ConfigureAwait(false)).IsAccepted);
            var replaced = await service.ActivateAsync(Activation("b", second.IdentitySha256), CancellationToken.None).ConfigureAwait(false);
            var beside = await service.ActivateAsync(
                Activation("c", otherRig.IdentitySha256) with { Target = Target() with { RigId = "rig-b" } }, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual((first.IdentitySha256, second.IdentitySha256), (replaced.Record!.FromIdentitySha256, replaced.Record.ToIdentitySha256));
            Assert.IsTrue(beside.IsAccepted, beside.ReasonCode);
            Assert.IsNull(beside.Record!.FromIdentitySha256);
            Assert.AreEqual(second.IdentitySha256, (await store.GetActiveAsync(Agent, Rig, CancellationToken.None).ConfigureAwait(false))!.Family.IdentitySha256);
            Assert.AreEqual(otherRig.IdentitySha256, (await store.GetActiveAsync(Agent, "rig-b", CancellationToken.None).ConfigureAwait(false))!.Family.IdentitySha256);
            CollectionAssert.AreEquivalent(
                MasterIds(second.Family).Concat(MasterIds(otherRig.Family)).ToArray(),
                (await store.GetActiveMasterArtifactIdsAsync(CancellationToken.None).ConfigureAwait(false)).ToArray(),
                "only the active families' masters are listed");
            using var connection = Open(root);
            Assert.AreEqual(2L, Scalar(connection, "SELECT count(*) FROM calibration_family_active;"));
            // The schema itself keeps one active family per agent and rig, and only a family that names them.
            Assert.ThrowsExactly<SqliteException>(() => Execute(root,
                $"INSERT INTO calibration_family_active VALUES('{Agent}', '{Rig}', '{first.IdentitySha256}', 0);"));
            Assert.ThrowsExactly<SqliteException>(() => Execute(root,
                $"PRAGMA foreign_keys = ON; UPDATE calibration_family_active SET identity_sha256 = '{otherRig.IdentitySha256}' WHERE rig_id = '{Rig}';"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ActivateAsync_RefusesAFamilyThatDoesNotFitItsAgentAndRigAndWritesNothing()
    {
        var root = CreateRoot();
        try
        {
            using var store = CreateStore(root);
            var service = new CalibrationFamilyActivationService(store, NullLogger<CalibrationFamilyActivationService>.Instance);
            var family = await store.StoreAsync(Family("family-a", Member(10)), CancellationToken.None).ConfigureAwait(false);
            var cases = new (CalibrationFamilyActivationTarget Target, string Reason)[]
            {
                (Target() with { AgentId = "agent-b" }, CalibrationFamilyActivationReasonCodes.IdentityMismatch),
                (Target() with { RigId = "rig-b" }, CalibrationFamilyActivationReasonCodes.IdentityMismatch),
                (Target() with { RigProfileSha256 = Hash("other rig") }, CalibrationFamilyActivationReasonCodes.IdentityMismatch),
                (Target() with { SensorProfileSha256 = Hash("other sensor") }, CalibrationFamilyActivationReasonCodes.IdentityMismatch),
                (Target() with { AcquisitionModel = Model with { Name = "virtual-calibration-source-model" } }, CalibrationFamilyActivationReasonCodes.AcquisitionModelMismatch),
                (Target() with { AcquisitionModel = Model with { Version = "other-version" } }, CalibrationFamilyActivationReasonCodes.AcquisitionModelMismatch),
                (Target() with { AcquisitionModel = Model with { Sha256 = Hash("other model") } }, CalibrationFamilyActivationReasonCodes.AcquisitionModelMismatch)
            };
            for (var index = 0; index < cases.Length; index++)
            {
                var refused = await service.ActivateAsync(
                    Activation($"mismatch-{index}", family.IdentitySha256) with { Target = cases[index].Target },
                    CancellationToken.None).ConfigureAwait(false);
                Assert.IsFalse(refused.IsAccepted, $"case {index}");
                Assert.AreEqual(cases[index].Reason, refused.ReasonCode, $"case {index}");
            }
            var unknown = await service.ActivateAsync(Activation("unknown", Hash("not stored")), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CalibrationFamilyActivationReasonCodes.UnknownFamily, unknown.ReasonCode);
            // Case does not matter for a profile or model hash.
            var fitting = await service.ActivateAsync(Activation("fits", family.IdentitySha256) with
            {
                Target = Target() with { RigProfileSha256 = Lowercase(RigSha256), AcquisitionModel = Model with { Sha256 = Lowercase(Model.Sha256) } }
            }, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(fitting.IsAccepted, fitting.ReasonCode);

            using var connection = Open(root);
            CollectionAssert.AreEqual(
                new object?[] { "fits" },
                Query(connection, "SELECT idempotency_key FROM calibration_family_activations;"),
                "a refused command records nothing");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ActivationState_SurvivesAReopenAndACrashAfterCommit()
    {
        var root = CreateRoot();
        var crashed = CreateRoot();
        try
        {
            CalibrationFamilyRecord family;
            CalibrationFamilyActivationResult activated;
            using (var store = CreateStore(root))
            {
                await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                // SQLite checkpoints the WAL into the database when its last connection closes, which the store's
                // unpooled connections otherwise do after every call. Another reader open through the commits and the
                // copy keeps them in the WAL, so the copy is what a power loss right after the commit leaves behind.
                using var reader = Open(root);
                Assert.AreEqual(0, Scalar(reader, "SELECT count(*) FROM calibration_family_activations;"));
                var service = new CalibrationFamilyActivationService(store, NullLogger<CalibrationFamilyActivationService>.Instance);
                family = await store.StoreAsync(Family("family-a", Member(10)), CancellationToken.None).ConfigureAwait(false);
                activated = await service.ActivateAsync(Activation("activate-1", family.IdentitySha256), CancellationToken.None).ConfigureAwait(false);
                using (var second = CreateStore(root))
                {
                    await Assert.ThrowsExactlyAsync<IOException>(
                        async () => await second.InitializeAsync(CancellationToken.None).ConfigureAwait(false),
                        "a second store never shares the root").ConfigureAwait(false);
                }
                // A power loss right after the commit: the database and its uncheckpointed WAL, without the shared-memory
                // index, which is rebuilt from the WAL on reopen.
                var source = Path.Combine(root, SqliteCalibrationFamilyStore.DirectoryName);
                var target = Path.Combine(crashed, SqliteCalibrationFamilyStore.DirectoryName);
                Directory.CreateDirectory(target);
                foreach (var file in Directory.EnumerateFiles(source, SqliteCalibrationFamilyStore.DatabaseFileName + "*")
                             .Where(static file => !file.EndsWith("-shm", StringComparison.Ordinal)))
                {
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
                }
                var database = Path.Combine(target, SqliteCalibrationFamilyStore.DatabaseFileName);
                var wal = new FileInfo(database + "-wal");
                Assert.IsTrue(wal.Exists && wal.Length > 0, "the commits must still be in the WAL when the files are copied");
                Assert.IsFalse(File.Exists(database + "-shm"));

                // The database file alone does not have the activation yet, so recovering it below needs the WAL.
                var withoutWal = Path.Combine(crashed, "database-only.db");
                File.Copy(database, withoutWal);
                using (var databaseOnly = new SqliteConnection($"Data Source={withoutWal};Pooling=False"))
                {
                    await databaseOnly.OpenAsync(CancellationToken.None).ConfigureAwait(false);
                    Assert.AreEqual(0, Scalar(databaseOnly, "SELECT count(*) FROM calibration_family_activations;"));
                }
            }

            foreach (var reopenedRoot in new[] { root, crashed })
            {
                using var reopened = CreateStore(reopenedRoot);
                var service = new CalibrationFamilyActivationService(reopened, NullLogger<CalibrationFamilyActivationService>.Instance);
                var active = await reopened.GetActiveAsync(Agent, Rig, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(family.IdentitySha256, active?.Family.IdentitySha256, reopenedRoot);
                Assert.AreEqual(activated.Record, await reopened.GetActivationAsync("activate-1", CancellationToken.None).ConfigureAwait(false));
                var replay = await service.ActivateAsync(Activation("activate-1", family.IdentitySha256), CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(replay.Replayed, reopenedRoot);
                Assert.AreEqual(activated.Record, replay.Record);
                CollectionAssert.AreEquivalent(MasterIds(family.Family).ToArray(),
                    (await reopened.GetActiveMasterArtifactIdsAsync(CancellationToken.None).ConfigureAwait(false)).ToArray());
            }
        }
        finally
        {
            DeleteRoot(root);
            DeleteRoot(crashed);
        }
    }

    [TestMethod]
    [DataRow("CREATE TABLE added_by_someone_else(id TEXT PRIMARY KEY) STRICT;")]
    [DataRow("PRAGMA user_version = 2;")]
    [DataRow("DROP INDEX calibration_family_activations_target;")]
    [DataRow("DROP INDEX calibration_family_activations_target; CREATE INDEX calibration_family_activations_target ON calibration_family_activations(actor);")]
    public async Task InitializeAsync_FailsClosedOnASchemaItDoesNotWrite(string change)
    {
        var root = CreateRoot();
        try
        {
            using (var store = CreateStore(root))
            {
                await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            }
            Execute(root, change);

            using var reopened = CreateStore(root);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                async () => await reopened.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task RetentionHolds_KeepTheActiveFamilysMastersWhateverTheirBundleHoldUntilDeactivated()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, RawIngressSqliteBusyTimeoutSeconds = 1 });
            var masters = await CreateLibraryMastersAsync(root, options).ConfigureAwait(false);
            // Today every bundle holds its own artifacts. Clear that, so only the family hold can keep the masters.
            ExecuteDatabase(Path.Combine(root, "journal", "raw-ingress.db"), "UPDATE calibration_library_bundles SET retention_hold = 0;");
            using var library = new SqliteCalibrationLibraryStore(new InitializedIngress(), options, TimeProvider.System);
            using var processingStore = new SqliteCaptureProcessingStore(options);
            await processingStore.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using var processingTelemetry = new CaptureProcessingTelemetry();
            var persistence = new CaptureProcessingPersistence(
                options, processingStore, processingTelemetry, NullLogger<CaptureProcessingPersistence>.Instance);
            using var families = new SqliteCalibrationFamilyStore(options, TimeProvider.System, NullLogger<SqliteCalibrationFamilyStore>.Instance);
            var holds = new CompositeProcessingRetentionHolds(
                persistence, new CameraAgentClearReferenceLoader(options), library, calibrationFamilies: families);
            var service = new CalibrationFamilyActivationService(families, NullLogger<CalibrationFamilyActivationService>.Instance);
            var family = await families.StoreAsync(
                Family("family-a", Member(10) with { Masters = [.. masters.Select(static master => new CalibrationFamilyMasterV1(master.Kind, master.ArtifactId, master.PayloadSha256))] }),
                CancellationToken.None).ConfigureAwait(false);
            var expected = masters.Select(static master => new ProcessingRetentionHold(master.ArtifactId)).ToArray();

            Assert.IsFalse((await HeldAsync(holds, root).ConfigureAwait(false)).Intersect(expected).Any(), "a stored family that is not active holds nothing");

            Assert.IsTrue((await service.ActivateAsync(Activation("activate", family.IdentitySha256), CancellationToken.None).ConfigureAwait(false)).IsAccepted);
            CollectionAssert.IsSubsetOf(expected, (await HeldAsync(holds, root).ConfigureAwait(false)).ToArray(), "the active family's masters are held");
            CollectionAssert.IsSubsetOf(expected, (await HeldUnderLifecycleLockAsync(holds, root).ConfigureAwait(false)).ToArray(),
                "the sweep's read under the raw-ingress lifecycle lock holds them too");

            Assert.IsTrue((await service.DeactivateAsync(Deactivation("deactivate"), CancellationToken.None).ConfigureAwait(false)).IsAccepted);
            Assert.IsFalse((await HeldAsync(holds, root).ConfigureAwait(false)).Intersect(expected).Any(), "deactivating drops the hold");
            Assert.IsFalse((await HeldUnderLifecycleLockAsync(holds, root).ConfigureAwait(false)).Intersect(expected).Any());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task SiloBundleOpenRunAndConfiguredClearReferenceHoldAndReleaseExactMaster()
    {
        var token = CancellationToken.None;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var root = silo.RootPath;
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, RawIngressSqliteBusyTimeoutSeconds = 1 });
        var master = (await CreateLibraryMastersAsync(root, options).ConfigureAwait(false))[0];
        var (settings, installed) = await Distribution.SiloRawEvidenceRecoveryTests.CreateRecoverySiloAsync().ConfigureAwait(false);
        using var storage = new SiloInstalledStorage(settings, options, silo.Clock, null, null, installed, null);
        var artifacts = new SiloArtifactStorage(storage, silo.Clock);
        using var library = new SqliteCalibrationLibraryStore(new InitializedIngress(), options, silo.Clock) { Silo = artifacts };
        var loader = new CameraAgentClearReferenceLoader(options, artifacts, library);
        var draft = SiloDrafts.Capture(silo.Payloads, 1, rawRequired: false);
        var id = master.ArtifactId.ToString("D");
        draft = draft with { Members = [draft.Members[0] with { ArtifactId = id, Role = "calibration-frame" }, draft.Members[1]] };
        await silo.PublishAsync(draft).ConfigureAwait(false);
        var locator = silo.MemberLocator(draft.GroupId, id);
        silo.Clock.Advance(TimeSpan.FromDays(2));
        var retention = SiloRetentionTests.OpenForFixture(silo);
        await retention.SetPolicyAsync(new(false, [new("calibration-frame", TimeSpan.Zero, false, true)]), token).ConfigureAwait(false);
        var database = Path.Combine(root, "journal", "raw-ingress.db");

        async ValueTask<string> DecisionAsync(CameraAgentClearReferenceLoader configured)
        {
            var held = (await library.GetRetentionHoldsUnderLifecycleLockAsync(root, token).ConfigureAwait(false))
                .Concat(await configured.GetRetentionHoldsAsync(root, token).ConfigureAwait(false));
            var candidates = await retention.DryRunAsync(silo.Clock.GetUtcNow(), false,
                held.Select(static hold => new SiloRetentionHold(SiloHoldKind.CalibrationMaster, hold.ArtifactId.ToString("D"))).ToArray(),
                token).ConfigureAwait(false);
            return candidates.Single(candidate => candidate.Locator == locator).Decision;
        }

        Assert.AreEqual("held-reference", await DecisionAsync(loader).ConfigureAwait(false));
        ExecuteDatabase(database, """
            UPDATE calibration_library_bundles SET retention_hold = 0;
            INSERT INTO centered_exposure_runs(run_id, agent_id, camera_id, snapshot_json, snapshot_sha256, calibration_bundle_id)
            SELECT 'fixture-run', 'agent', 'camera', x'7b7d', 'fixture', bundle_id FROM calibration_library_bundles;
            """);
        Assert.AreEqual("held-reference", await DecisionAsync(loader).ConfigureAwait(false));
        ExecuteDatabase(database, "UPDATE centered_exposure_runs SET closed_reason = 'complete' WHERE run_id = 'fixture-run';");
        Assert.AreEqual("expire-role", await DecisionAsync(loader).ConfigureAwait(false));

        loader.RegisterRetentionHold(master.ManifestPath);
        Assert.AreEqual("held-reference", await DecisionAsync(loader).ConfigureAwait(false));
        var replacement = new CameraAgentClearReferenceLoader(options, artifacts, library);
        Assert.AreEqual("expire-role", await DecisionAsync(replacement).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ActiveFamilySelector_SelectsTheOneActiveFamilyWhateverTheGainAndRefusesWithStableReasons()
    {
        var root = CreateRoot();
        try
        {
            using var store = CreateStore(root);
            var service = new CalibrationFamilyActivationService(store, NullLogger<CalibrationFamilyActivationService>.Instance);
            var selector = new ActiveCalibrationFamilySelector(store);
            var config = SelectorConfiguration("VirtualSky");
            var low = SelectorLight(10);

            Assert.AreEqual("normalization.plan.no-active-family",
                (await selector.SelectAsync(config, low, CancellationToken.None).ConfigureAwait(false)).ReasonCode);

            var family = await store.StoreAsync(Family("family-a", Member(10), Member(20)), CancellationToken.None).ConfigureAwait(false);
            // A second stored family whose only member is at the higher gain is never chosen by gain.
            _ = await store.StoreAsync(Family("family-b", Member(20)), CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue((await service.ActivateAsync(Activation("activate", family.IdentitySha256), CancellationToken.None).ConfigureAwait(false)).IsAccepted);

            foreach (var gain in new[] { 10d, 20d, 30d })
            {
                var selected = await selector.SelectAsync(config, SelectorLight(gain), CancellationToken.None).ConfigureAwait(false);
                Assert.IsNull(selected.ReasonCode, selected.ReasonCode);
                Assert.AreEqual(family.IdentitySha256, selected.FamilyIdentitySha256);
                CollectionAssert.AreEqual(CalibrationFamilyContractJson.Serialize(family.Family), selected.FamilyJson.ToArray());
                Assert.AreEqual(Asi174MmSensorModel.Version, selected.Response!.Descriptor.ResponseModelVersion);
            }

            Assert.AreEqual(CalibrationFamilyActivationReasonCodes.IdentityMismatch, (await selector.SelectAsync(
                config, low with { Profiles = low.Profiles with { Sensor = low.Profiles.Sensor with { Sha256 = Hash("other") } } },
                CancellationToken.None).ConfigureAwait(false)).ReasonCode);
            Assert.AreEqual(CalibrationFamilyActivationReasonCodes.AcquisitionModelMismatch, (await selector.SelectAsync(
                config, low with { Profiles = low.Profiles with { Calibration = Model with { Version = "other-model" } } },
                CancellationToken.None).ConfigureAwait(false)).ReasonCode);
            Assert.AreEqual(ActiveCalibrationFamilySelector.NoActiveFamily, (await selector.SelectAsync(
                config, low with { Capture = low.Capture with { RigId = "rig-b" } }, CancellationToken.None).ConfigureAwait(false)).ReasonCode);
            Assert.AreEqual(GainNormalizationReasonCodes.PhysicalResponseNotEnabled, (await selector.SelectAsync(
                SelectorConfiguration("Zwo"), low, CancellationToken.None).ConfigureAwait(false)).ReasonCode);

            Assert.IsTrue((await service.DeactivateAsync(Deactivation("deactivate"), CancellationToken.None).ConfigureAwait(false)).IsAccepted);
            Assert.AreEqual(ActiveCalibrationFamilySelector.NoActiveFamily,
                (await selector.SelectAsync(config, low, CancellationToken.None).ConfigureAwait(false)).ReasonCode);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void ActiveFamilySelector_DeclaresTheResponseVirtualSkyRendersWith()
    {
        var virtualSky = SelectorConfiguration("VirtualSky");
        var configured = virtualSky with
        {
            Rig = virtualSky.Rig with
            {
                Sensor = virtualSky.Rig.Sensor with
                {
                    SimulationResponse = new ConfiguredSensorResponseProfile(
                        "selector-test-response-v1", 16, 0, 400, 4.0, 200, 60_000,
                        [new SensorReadNoisePoint(0, 2.0), new SensorReadNoisePoint(400, 1.0)], 0, "ZWO 0.1 dB", "selector test")
                }
            }
        };

        Assert.AreEqual(GainResponseSourceKind.VirtualAsi174MmModel, ActiveCalibrationFamilySelector.ResolveSource(virtualSky));
        Assert.AreEqual(GainResponseSourceKind.VirtualConfiguredResponse, ActiveCalibrationFamilySelector.ResolveSource(configured));
        Assert.AreEqual(GainResponseSourceKind.PhysicalCamera, ActiveCalibrationFamilySelector.ResolveSource(SelectorConfiguration("Zwo")));
    }

    /// <summary>A capture of the fixture's agent and rig under its profiles and acquisition model, at one gain.</summary>
    private static ReconstructionDescriptor SelectorLight(double gain)
    {
        var descriptor = ReconstructableCaptureContractTests.CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]).Descriptor;
        return descriptor with
        {
            Capture = descriptor.Capture with { AgentId = Agent, RigId = Rig },
            Controls = descriptor.Controls with { RequestedGain = gain, EffectiveGain = gain },
            Profiles = descriptor.Profiles with
            {
                Rig = descriptor.Profiles.Rig with { Sha256 = RigSha256 },
                Sensor = descriptor.Profiles.Sensor with { Sha256 = SensorSha256 },
                Calibration = Model
            }
        };
    }

    private static CameraModuleConfig SelectorConfiguration(string moduleType)
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor(moduleType),
            new CameraRigConfig(
                new SensorProfile(
                    "VirtualAsi174Mm", 2, 2, 5.86, SensorColorMode.Mono, CameraPixelFormat.Mono16,
                    SensorResponseMode.Monochrome, 64, SampleByteOrder.LittleEndian, "virtual-asi174mm-electron-domain-v2"),
                new OpticsProfile("EquidistantFisheye", 2.5, 170, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(32), TimeSpan.FromMilliseconds(32), 82, 82)),
            CapturePipelineConfig.Empty,
            AgentId: Agent);

    private static async Task<IReadOnlyList<ProcessingRetentionHold>> HeldAsync(CompositeProcessingRetentionHolds holds, string root)
        => await holds.GetRetentionHoldsAsync(root, CancellationToken.None).ConfigureAwait(false);

    private static async Task<IReadOnlyList<ProcessingRetentionHold>> HeldUnderLifecycleLockAsync(CompositeProcessingRetentionHolds holds, string root)
        => await ((IRawIngressLifecycleProcessingRetentionHolds)holds)
            .GetRetentionHoldsUnderLifecycleLockAsync(root, CancellationToken.None).ConfigureAwait(false);

    /// <summary>A published synthetic library bundle; returns its four masters as the library records them.</summary>
    private static async Task<(string Kind, Guid ArtifactId, string PayloadSha256, string PayloadPath, string ManifestPath)[]> CreateLibraryMastersAsync(
        string root,
        IOptions<CameraAgentHostOptions> options)
    {
        Directory.CreateDirectory(root);
        await new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1)
            .InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var model = new SyntheticCalibrationModelV1 { Gain = 99.5, TemperatureC = -9.5, Defects = [new SyntheticCalibrationDefect(1, 1)] };
        var template = ReconstructableCaptureContractTests.CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
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
            },
            Profiles = template.Descriptor.Profiles with
            {
                Calibration = new ProfileIdentityDescriptor(
                    "synthetic-calibration-model", model.SchemaVersion, SyntheticCalibrationReferenceGenerator.ComputeModelIdentitySha256(model))
            }
        };
        var synthetic = await new SyntheticCalibrationReferenceStore(options, new CameraAgentClearReferenceLoader(options))
            .GetOrCreateAsync(light, model, CancellationToken.None).ConfigureAwait(false);
        using (var library = new SqliteCalibrationLibraryStore(new InitializedIngress(), options, TimeProvider.System))
        {
            _ = await library.AdoptPublishedBundleAsync(synthetic.LibraryBundle, CancellationToken.None).ConfigureAwait(false);
        }
        var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        await using var disposeConnection = connection.ConfigureAwait(false);
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT reference_kind, artifact_id, payload_sha256, payload_relative_path, manifest_relative_path
            FROM calibration_library_artifacts WHERE role = 'master' ORDER BY artifact_id;
            """;
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var masters = new List<(string, Guid, string, string, string)>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            masters.Add((reader.GetString(0), Guid.ParseExact(reader.GetString(1), "N"), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }
        Assert.HasCount(4, masters, "the synthetic bundle has one master of each kind");
        return [.. masters];
    }

    private static CalibrationFamilyActivationRequest Activation(string key, string identity, string? reason = null)
        => new(key, Target(), identity, "operator", reason);

    [TestMethod]
    public async Task SiloExpiryRespectsTheConfiguredMasterAcrossRestartAndAfterDeactivation()
    {
        var token = CancellationToken.None;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var member = Member(10);
        var masterId = member.Masters[0].ArtifactId.ToString("D");
        var draft = SiloDrafts.Capture(silo.Payloads, 1, rawRequired: false);
        draft = draft with { Members = [draft.Members[0] with { ArtifactId = masterId, Role = "calibration-frame" }, draft.Members[1]] };
        await silo.PublishAsync(draft).ConfigureAwait(false);
        silo.Clock.Advance(TimeSpan.FromDays(2));
        var retention = SiloRetentionTests.OpenForFixture(silo);
        await retention.SetPolicyAsync(new(false, [new("calibration-frame", TimeSpan.Zero, false, true)]), token).ConfigureAwait(false);

        static async ValueTask<IReadOnlyList<SiloRetentionHold>> ReadHoldsAsync(SqliteCalibrationFamilyStore store, CancellationToken token) =>
            (await store.GetActiveMasterArtifactIdsAsync(token).ConfigureAwait(false)).Select(static id =>
                new SiloRetentionHold(SiloHoldKind.CalibrationMaster, id.ToString("D"))).ToArray();
        using (var first = CreateStore(silo.RootPath))
        {
            var configured = await first.StoreAsync(Family("retention-family", member), token).ConfigureAwait(false);
            var service = new CalibrationFamilyActivationService(first, NullLogger<CalibrationFamilyActivationService>.Instance);
            Assert.IsTrue((await service.ActivateAsync(Activation("retention-activate", configured.IdentitySha256), token).ConfigureAwait(false)).IsAccepted);
            Assert.AreEqual("no-eligible-versions",
                await retention.SweepAsync(true, ct => ReadHoldsAsync(first, ct), null, token).ConfigureAwait(false));
        }
        await silo.RestartAsync().ConfigureAwait(false);
        using var reopened = CreateStore(silo.RootPath);
        retention = SiloRetentionTests.OpenForFixture(silo);
        var replacement = new CalibrationFamilyActivationService(reopened, NullLogger<CalibrationFamilyActivationService>.Instance);
        var holdsCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deactivated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sweep = retention.SweepAsync(true, async ct =>
        {
            var holds = await ReadHoldsAsync(reopened, ct).ConfigureAwait(false);
            holdsCaptured.SetResult();
            await deactivated.Task.WaitAsync(ct).ConfigureAwait(false);
            return holds;
        }, null, token).AsTask();
        try
        {
            await holdsCaptured.Task.WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            Assert.IsTrue((await replacement.DeactivateAsync(Deactivation("retention-release"), token).ConfigureAwait(false)).IsAccepted);
        }
        finally
        {
            deactivated.SetResult();
        }
        Assert.AreEqual("no-eligible-versions", await sweep.ConfigureAwait(false));
        Assert.AreEqual("deleted", await retention.SweepAsync(true, ct => ReadHoldsAsync(reopened, ct), null, token).ConfigureAwait(false));
    }

    private static CalibrationFamilyDeactivationRequest Deactivation(string key)
        => new(key, Agent, Rig, "operator", null);

    private static CalibrationFamilyActivationTarget Target()
        => new(Agent, Rig, RigSha256, SensorSha256, Model);

    private static IEnumerable<Guid> MasterIds(CalibrationFamilyV1 family)
        => family.Members.SelectMany(static member => member.Masters).Select(static master => master.ArtifactId).Distinct();

    private static CalibrationFamilyV1 Family(string familyId, params CalibrationFamilyMemberV1[] members)
        => new(CalibrationFamilyV1.CurrentSchemaVersion, familyId, Agent, Rig, RigSha256, SensorSha256, Model, MaskSha256, members);

    private static CalibrationFamilyMemberV1 Member(double gain)
        => new(
            FormattableString.Invariant($"gain-{gain}"), gain, 10, Layout(), -5, 5,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), EffectiveFrom, null, MaskSha256,
            [.. CalibrationReferenceKinds.All.Select(kind => new CalibrationFamilyMasterV1(
                kind,
                new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"artifact:{kind}:{gain:R}")))[..16]),
                Hash(FormattableString.Invariant($"payload:{kind}:{gain:R}"))))]);

    private static FrameLayoutDescriptor Layout()
        => new(4, 2, 8, CameraPixelFormat.BayerRggb16, FrameByteOrder.LittleEndian, 12, 16, FrameSamplePacking.ByteAligned,
            ColorFilterArrayPattern.Rggb, 64, 4095, 16)
        {
            StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
            LevelCodeSpace = FrameLevelCodeSpace.NativeSample,
            Readout = new FrameReadoutDescriptor(4, 2, 0, 0, 4, 2, 1, 1, FrameBinningAlgorithm.IdentityV1, 0, 0)
        };

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Lowercase(string value) => new([.. value.Select(char.ToLowerInvariant)]);

    private static SqliteCalibrationFamilyStore CreateStore(string root)
        => new(Path.Combine(root, SqliteCalibrationFamilyStore.DirectoryName), TimeProvider.System, NullLogger<SqliteCalibrationFamilyStore>.Instance);

    private static SqliteConnection Open(string root)
    {
        var connection = new SqliteConnection(
            $"Data Source={Path.Combine(root, SqliteCalibrationFamilyStore.DirectoryName, SqliteCalibrationFamilyStore.DatabaseFileName)};Pooling=False");
        connection.Open();
        return connection;
    }

    private static void Execute(string root, string sql)
        => ExecuteDatabase(Path.Combine(root, SqliteCalibrationFamilyStore.DirectoryName, SqliteCalibrationFamilyStore.DatabaseFileName), sql);

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Tests pass only fixed SQL.")]
    private static void ExecuteDatabase(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Tests pass only fixed SQL.")]
    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Tests pass only fixed SQL.")]
    private static object?[] Query(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<object?>();
        while (reader.Read())
        {
            values.Add(reader.IsDBNull(0) ? null : reader.GetValue(0));
        }
        return [.. values];
    }

    private static string CreateRoot()
        => Path.Combine(Path.GetTempPath(), "hvo-calibration-families", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class InitializedIngress : IRawCaptureIngress
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
