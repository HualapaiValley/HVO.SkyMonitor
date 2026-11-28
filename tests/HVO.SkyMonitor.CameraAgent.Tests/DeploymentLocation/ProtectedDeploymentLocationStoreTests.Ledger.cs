using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.CameraAgent.Tests.Orbital;

namespace HVO.SkyMonitor.CameraAgent.Tests.DeploymentLocation;

public sealed partial class ProtectedDeploymentLocationStoreTests
{
    [TestMethod]
    public async Task QuarantinedOrbitalCsvKeepsInstallationVerifiedAndLegacyJsonPrunes()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        var token = CancellationToken.None;
        using var locations = CreateStore();
        var active = await locations.InitializeAsync(seed, token).ConfigureAwait(false);
        var orbitalRoot = Path.Combine(_options.Value.RawIngressRoot, ".orbital-elements");
        var clock = new OrbitalElementsStoreTestData.Clock(Now);
        using var orbital = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
            OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 1));
        var quarantine = Path.Combine(orbitalRoot, "quarantine");

        var csv = CelesTrakGpTestData.Payload(CelesTrakGpTestData.Record(("MEAN_MOTION", "NaN")));
        var csvSha = CelesTrakGpTestData.Sha256(csv);
        var attempt = await OrbitalElementsStoreTestData.AdmitAsync(orbital).ConfigureAwait(false);
        await orbital.RecordQuarantineAsync(attempt, csv, csvSha, "orbital.invalid-number", "synthetic CSV", token)
            .ConfigureAwait(false);

        Assert.AreEqual(active, await locations.VerifyInstallationAsync(seed, token).ConfigureAwait(false));
        Assert.AreEqual(active, await locations.FullVerifyAsync(seed, token).ConfigureAwait(false));
        Assert.IsFalse(locations.RequiresRetainedIntegrityScrub);
        var csvPath = Path.Combine(quarantine, csvSha + ".raw");
        CollectionAssert.AreEqual(csv, await File.ReadAllBytesAsync(csvPath, token).ConfigureAwait(false));

        clock.Advance(OrbitalElementsStoreTestData.Interval);
        var legacy = CelesTrakGpTestData.ReadFixture(CelesTrakGpTestData.LegacyJsonFixture);
        var legacySha = CelesTrakGpTestData.Sha256(legacy);
        var legacyAttempt = await OrbitalElementsStoreTestData.AdmitAsync(orbital).ConfigureAwait(false);
        await orbital.RecordQuarantineAsync(legacyAttempt, legacy, legacySha, "orbital.candidate-invalid", "synthetic JSON", token)
            .ConfigureAwait(false);
        Assert.IsFalse(File.Exists(csvPath));
        // Recreate the previous filename for evidence still referenced by the retained attempt.
        var legacyPath = Path.Combine(quarantine, legacySha + ".json");
        RetainedFileMutations.Move(Path.Combine(quarantine, legacySha + ".raw"), legacyPath);
        CollectionAssert.AreEqual(legacy, await File.ReadAllBytesAsync(legacyPath, token).ConfigureAwait(false));
        Assert.AreEqual(active, await locations.FullVerifyAsync(seed, token).ConfigureAwait(false));

        clock.Advance(OrbitalElementsStoreTestData.Interval);
        var laterAttempt = await OrbitalElementsStoreTestData.AdmitAsync(orbital).ConfigureAwait(false);
        await orbital.RecordQuarantineAsync(laterAttempt, csv, csvSha, "orbital.invalid-number", "synthetic CSV", token)
            .ConfigureAwait(false);
        Assert.IsFalse(File.Exists(legacyPath));
        CollectionAssert.AreEqual(csv, await File.ReadAllBytesAsync(csvPath, token).ConfigureAwait(false));
        Assert.AreEqual(active, await locations.VerifyInstallationAsync(seed, token).ConfigureAwait(false));
        Assert.AreEqual(active, await locations.FullVerifyAsync(seed, token).ConfigureAwait(false));
        Assert.IsFalse(locations.RequiresRetainedIntegrityScrub);
    }

    [TestMethod]
    public async Task StartupMigrationMovesMalformedLegacyOrbitalQuarantineJsonAndRestoresVerification()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        var token = CancellationToken.None;
        var orbitalRoot = Path.Combine(_options.Value.RawIngressRoot, ".orbital-elements");
        var quarantine = Path.Combine(orbitalRoot, "quarantine");
        var clock = new OrbitalElementsStoreTestData.Clock(Now);
        var malformedJson = "{\"OBJECT_NAME\":\"TRUNCATED"u8.ToArray();
        var malformedSha = CelesTrakGpTestData.Sha256(malformedJson);
        var legacyJsonPath = Path.Combine(quarantine, malformedSha + ".json");
        var migratedRawPath = Path.Combine(quarantine, malformedSha + ".raw");
        DeploymentLocationSnapshot active;

        using (var locations = CreateStore())
        {
            active = await locations.InitializeAsync(seed, token).ConfigureAwait(false);
            using (var orbital = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
                OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 4)))
            {
                var attempt = await OrbitalElementsStoreTestData.AdmitAsync(orbital).ConfigureAwait(false);
                await orbital.RecordQuarantineAsync(attempt, malformedJson, malformedSha,
                    "orbital.json-syntax", "legacy malformed JSON body", token).ConfigureAwait(false);
                // Recreate the v0.9.1-rc.2 quarantine/*.json filename recorded through the integrity ledger.
                RetainedFileMutations.Move(migratedRawPath, legacyJsonPath);
            }
            Assert.IsTrue(File.Exists(legacyJsonPath));
            Assert.IsFalse(File.Exists(migratedRawPath));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await locations.FullVerifyAsync(seed, token).ConfigureAwait(false)).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
                await locations.VerifyInstallationAsync(seed, token).ConfigureAwait(false)).ConfigureAwait(false);
        }

        // Simulate restart on upgraded CameraAgent: deployment-location store registers the ledger first,
        // then orbital startup reconciliation migrates retained quarantine/*.json to .raw before scrub/verify.
        using var restartedLocations = CreateStore();
        Assert.AreEqual(active, await restartedLocations.InitializeAsync(seed, token).ConfigureAwait(false));
        using (var restartedOrbital = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
            OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 4)))
        {
            await restartedOrbital.InitializeAsync(token).ConfigureAwait(false);
            Assert.IsFalse(File.Exists(legacyJsonPath));
            Assert.IsTrue(File.Exists(migratedRawPath));
            CollectionAssert.AreEqual(malformedJson, await File.ReadAllBytesAsync(migratedRawPath, token).ConfigureAwait(false));

            // A healthy CSV refresh succeeds while the migrated body remains retained among MaximumQuarantinedPayloads.
            clock.Advance(OrbitalElementsStoreTestData.Interval);
            var csv = CelesTrakGpTestData.Payload(CelesTrakGpTestData.Record(
                ("EPOCH", clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture))));
            var normalized = CelesTrakGpNormalizer.CreateCandidate(CelesTrakGpTestData.Retrieved(csv, clock.GetUtcNow()));
            Assert.IsNotNull(normalized.Snapshot, normalized.Detail);
            await OrbitalElementsStoreTestData.PublishAsync(restartedOrbital, clock, new(normalized.Snapshot, csv))
                .ConfigureAwait(false);
            Assert.IsFalse(File.Exists(legacyJsonPath));
            CollectionAssert.AreEqual(malformedJson, await File.ReadAllBytesAsync(migratedRawPath, token).ConfigureAwait(false));
        }

        Assert.AreEqual(active, await restartedLocations.FullVerifyAsync(seed, token).ConfigureAwait(false));
        Assert.AreEqual(active, await restartedLocations.VerifyInstallationAsync(seed, token).ConfigureAwait(false));
        Assert.IsFalse(restartedLocations.RequiresRetainedIntegrityScrub);

        // Subsequent restart is idempotent and preserves installation verification.
        using (var secondRestart = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
            OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 4)))
        {
            await secondRestart.InitializeAsync(token).ConfigureAwait(false);
        }
        Assert.IsFalse(File.Exists(legacyJsonPath));
        CollectionAssert.AreEqual(malformedJson, await File.ReadAllBytesAsync(migratedRawPath, token).ConfigureAwait(false));
        Assert.AreEqual(active, await restartedLocations.VerifyInstallationAsync(seed, token).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StartupMigrationPreservesLegacyQuarantineJsonWhileLedgerIsUnavailable(
        bool matchingDuplicateRawExists)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix file permissions are required to make the retained-integrity database temporarily unreadable.");
            return;
        }

        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        var token = CancellationToken.None;
        var orbitalRoot = Path.Combine(_options.Value.RawIngressRoot, ".orbital-elements");
        var quarantine = Path.Combine(orbitalRoot, "quarantine");
        var clock = new OrbitalElementsStoreTestData.Clock(Now);
        var malformedJson = "{\"OBJECT_NAME\":\"TRUNCATED"u8.ToArray();
        var malformedSha = CelesTrakGpTestData.Sha256(malformedJson);
        var legacyJsonPath = Path.Combine(quarantine, malformedSha + ".json");
        var migratedRawPath = Path.Combine(quarantine, malformedSha + ".raw");
        var ledgerPath = Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedIntegrityLedger.DatabaseName);
        DeploymentLocationSnapshot active;

        using (var locations = CreateStore())
        {
            active = await locations.InitializeAsync(seed, token).ConfigureAwait(false);
            using var orbital = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
                OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 4));
            var attempt = await OrbitalElementsStoreTestData.AdmitAsync(orbital).ConfigureAwait(false);
            await orbital.RecordQuarantineAsync(attempt, malformedJson, malformedSha,
                "orbital.json-syntax", "legacy malformed JSON body", token).ConfigureAwait(false);
            RetainedFileMutations.Move(migratedRawPath, legacyJsonPath);
            if (matchingDuplicateRawExists)
            {
                await File.WriteAllBytesAsync(migratedRawPath, malformedJson, token).ConfigureAwait(false);
            }
        }

        // Make the retained-integrity database temporarily unreadable during recovery startup.
        var originalMode = File.GetUnixFileMode(ledgerPath);
        File.SetUnixFileMode(ledgerPath, UnixFileMode.None);
        try
        {
            using var unavailableLocations = CreateStore();
            Assert.AreEqual(active, await unavailableLocations.InitializeAsync(seed, token).ConfigureAwait(false));
            await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
                await unavailableLocations.VerifyInstallationAsync(seed, token).ConfigureAwait(false)).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await unavailableLocations.FullVerifyAsync(seed, token).ConfigureAwait(false)).ConfigureAwait(false);

            using (var unavailableOrbital = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
                OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 4)))
            {
                await unavailableOrbital.InitializeAsync(token).ConfigureAwait(false);
            }

            Assert.IsTrue(File.Exists(legacyJsonPath),
                "Legacy quarantine .json must be preserved untouched while the retained-integrity ledger is unavailable.");
            CollectionAssert.AreEqual(malformedJson, await File.ReadAllBytesAsync(legacyJsonPath, token).ConfigureAwait(false));
            Assert.AreEqual(matchingDuplicateRawExists, File.Exists(migratedRawPath));
            if (matchingDuplicateRawExists)
            {
                CollectionAssert.AreEqual(malformedJson, await File.ReadAllBytesAsync(migratedRawPath, token).ConfigureAwait(false));
            }
        }
        finally
        {
            File.SetUnixFileMode(ledgerPath, originalMode);
        }

        // Restoring permissions on the same ledger lets restart migration complete and qualify cleanly.
        using var recoveredLocations = CreateStore();
        Assert.AreEqual(active, await recoveredLocations.InitializeAsync(seed, token).ConfigureAwait(false));
        using (var recoveredOrbital = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
            OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 4)))
        {
            await recoveredOrbital.InitializeAsync(token).ConfigureAwait(false);
            Assert.IsFalse(File.Exists(legacyJsonPath));
            Assert.IsTrue(File.Exists(migratedRawPath));
            CollectionAssert.AreEqual(malformedJson, await File.ReadAllBytesAsync(migratedRawPath, token).ConfigureAwait(false));
        }

        Assert.AreEqual(active, await recoveredLocations.FullVerifyAsync(seed, token).ConfigureAwait(false));
        Assert.AreEqual(active, await recoveredLocations.VerifyInstallationAsync(seed, token).ConfigureAwait(false));
        Assert.IsFalse(recoveredLocations.RequiresRetainedIntegrityScrub);
    }

    [TestMethod]
    [DataRow(false, UnixFileMode.UserRead)]
    [DataRow(true, UnixFileMode.UserRead)]
    [DataRow(false, UnixFileMode.UserRead | UnixFileMode.UserWrite)]
    [DataRow(true, UnixFileMode.UserRead | UnixFileMode.UserWrite)]
    public async Task StartupMigrationPreservesLegacyQuarantineJsonWhileLocationDirectoryIsNonSearchable(
        bool matchingDuplicateRawExists, UnixFileMode restrictedLocationMode)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix file permissions are required to make the .location directory non-searchable.");
            return;
        }

        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        var token = CancellationToken.None;
        var orbitalRoot = Path.Combine(_options.Value.RawIngressRoot, ".orbital-elements");
        var quarantine = Path.Combine(orbitalRoot, "quarantine");
        var clock = new OrbitalElementsStoreTestData.Clock(Now);
        var malformedJson = "{\"OBJECT_NAME\":\"TRUNCATED"u8.ToArray();
        var malformedSha = CelesTrakGpTestData.Sha256(malformedJson);
        var legacyJsonPath = Path.Combine(quarantine, malformedSha + ".json");
        var migratedRawPath = Path.Combine(quarantine, malformedSha + ".raw");
        var locationDirectory = Path.Combine(_options.Value.RawIngressRoot, ".location");
        DeploymentLocationSnapshot active;

        using (var locations = CreateStore())
        {
            active = await locations.InitializeAsync(seed, token).ConfigureAwait(false);
            using var orbital = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
                OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 4));
            var attempt = await OrbitalElementsStoreTestData.AdmitAsync(orbital).ConfigureAwait(false);
            await orbital.RecordQuarantineAsync(attempt, malformedJson, malformedSha,
                "orbital.json-syntax", "legacy malformed JSON body", token).ConfigureAwait(false);
            RetainedFileMutations.Move(migratedRawPath, legacyJsonPath);
            if (matchingDuplicateRawExists)
            {
                await File.WriteAllBytesAsync(migratedRawPath, malformedJson, token).ConfigureAwait(false);
            }
        }

        // Mode 0400/0600 permits listing .location entries while failing stat/exists lookups with EACCES.
        var originalMode = File.GetUnixFileMode(locationDirectory);
        File.SetUnixFileMode(locationDirectory, restrictedLocationMode);
        try
        {
            using (var unavailableOrbital = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
                OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 4)))
            {
                await unavailableOrbital.InitializeAsync(token).ConfigureAwait(false);
            }

            Assert.IsTrue(File.Exists(legacyJsonPath),
                "Legacy quarantine .json must be preserved untouched while .location is non-searchable.");
            CollectionAssert.AreEqual(malformedJson, await File.ReadAllBytesAsync(legacyJsonPath, token).ConfigureAwait(false));
            Assert.AreEqual(matchingDuplicateRawExists, File.Exists(migratedRawPath));
            if (matchingDuplicateRawExists)
            {
                CollectionAssert.AreEqual(malformedJson, await File.ReadAllBytesAsync(migratedRawPath, token).ConfigureAwait(false));
            }
        }
        finally
        {
            File.SetUnixFileMode(locationDirectory, originalMode);
        }

        // Restoring .location permissions lets restart migration complete and qualify cleanly.
        using var recoveredLocations = CreateStore();
        Assert.AreEqual(active, await recoveredLocations.InitializeAsync(seed, token).ConfigureAwait(false));
        using (var recoveredOrbital = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
            OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 4)))
        {
            await recoveredOrbital.InitializeAsync(token).ConfigureAwait(false);
            Assert.IsFalse(File.Exists(legacyJsonPath));
            Assert.IsTrue(File.Exists(migratedRawPath));
            CollectionAssert.AreEqual(malformedJson, await File.ReadAllBytesAsync(migratedRawPath, token).ConfigureAwait(false));
        }

        Assert.AreEqual(active, await recoveredLocations.FullVerifyAsync(seed, token).ConfigureAwait(false));
        Assert.AreEqual(active, await recoveredLocations.VerifyInstallationAsync(seed, token).ConfigureAwait(false));
        Assert.IsFalse(recoveredLocations.RequiresRetainedIntegrityScrub);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StartupMigrationPreservesLegacyQuarantineJsonWhileLocationDirectoryIsOffline(
        bool matchingDuplicateRawExists)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        var token = CancellationToken.None;
        var orbitalRoot = Path.Combine(_options.Value.RawIngressRoot, ".orbital-elements");
        var quarantine = Path.Combine(orbitalRoot, "quarantine");
        var clock = new OrbitalElementsStoreTestData.Clock(Now);
        var malformedJson = "{\"OBJECT_NAME\":\"TRUNCATED"u8.ToArray();
        var malformedSha = CelesTrakGpTestData.Sha256(malformedJson);
        var legacyJsonPath = Path.Combine(quarantine, malformedSha + ".json");
        var migratedRawPath = Path.Combine(quarantine, malformedSha + ".raw");
        var locationDirectory = Path.Combine(_options.Value.RawIngressRoot, RetainedIntegrityLedger.LocationDirectoryName);
        var offlineLocationDirectory = Path.Combine(_root, "offline-location-backup");
        var identityMarkerPath = Path.Combine(_options.Value.RawIngressRoot, RetainedIntegrityLedger.IdentityMarkerName);
        DeploymentLocationSnapshot active;

        using (var locations = CreateStore())
        {
            active = await locations.InitializeAsync(seed, token).ConfigureAwait(false);
            using var orbital = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
                OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 4));
            var attempt = await OrbitalElementsStoreTestData.AdmitAsync(orbital).ConfigureAwait(false);
            await orbital.RecordQuarantineAsync(attempt, malformedJson, malformedSha,
                "orbital.json-syntax", "legacy malformed JSON body", token).ConfigureAwait(false);
            RetainedFileMutations.Move(migratedRawPath, legacyJsonPath);
            if (matchingDuplicateRawExists)
            {
                await File.WriteAllBytesAsync(migratedRawPath, malformedJson, token).ConfigureAwait(false);
            }
        }

        Assert.IsTrue(File.Exists(identityMarkerPath),
            "The durable root identity marker must exist at the data root before taking .location offline.");

        // Take the original .location directory offline while keeping .deployment-location.v1.identity at the data root.
        Directory.Move(locationDirectory, offlineLocationDirectory);
        Assert.IsFalse(Directory.Exists(locationDirectory));
        Assert.IsTrue(File.Exists(identityMarkerPath));

        // 1. Orbital reconciliation while .location is completely absent must defer migration.
        using (var absentLocationOrbital = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
            OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 4)))
        {
            await absentLocationOrbital.InitializeAsync(token).ConfigureAwait(false);
        }

        Assert.IsTrue(File.Exists(legacyJsonPath),
            "Legacy quarantine .json must be preserved untouched while .location is absent and root identity marker exists.");
        CollectionAssert.AreEqual(malformedJson, await File.ReadAllBytesAsync(legacyJsonPath, token).ConfigureAwait(false));
        Assert.AreEqual(matchingDuplicateRawExists, File.Exists(migratedRawPath));
        if (matchingDuplicateRawExists)
        {
            CollectionAssert.AreEqual(malformedJson, await File.ReadAllBytesAsync(migratedRawPath, token).ConfigureAwait(false));
        }

        // 2. CameraAgent recovery load (which recreates an empty .location directory without opening a ledger)
        // followed by orbital reconciliation must still defer BOTH rename and duplicate-delete mutations.
        using (var recoveryLocations = CreateStore())
        {
            _ = await recoveryLocations.LoadCurrentAsync(seed, verification: false, () => true, token).ConfigureAwait(false);
            Assert.IsFalse(recoveryLocations.RecentRetainedEvidenceReady);
            Assert.IsTrue(recoveryLocations.RequiresDeploymentLocationReconciliation);
            await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
                await recoveryLocations.VerifyInstallationAsync(seed, token).ConfigureAwait(false)).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await recoveryLocations.FullVerifyAsync(seed, token).ConfigureAwait(false)).ConfigureAwait(false);

            using (var recoveryOrbital = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
                OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 4)))
            {
                await recoveryOrbital.InitializeAsync(token).ConfigureAwait(false);
            }

            Assert.IsTrue(File.Exists(legacyJsonPath),
                "Legacy quarantine .json must be preserved untouched after recovery load while .location is offline.");
            CollectionAssert.AreEqual(malformedJson, await File.ReadAllBytesAsync(legacyJsonPath, token).ConfigureAwait(false));
            Assert.AreEqual(matchingDuplicateRawExists, File.Exists(migratedRawPath));
            if (matchingDuplicateRawExists)
            {
                CollectionAssert.AreEqual(malformedJson, await File.ReadAllBytesAsync(migratedRawPath, token).ConfigureAwait(false));
            }
        }

        // Restore the original .location directory (replacing any empty .location created by recovery load).
        if (Directory.Exists(locationDirectory))
        {
            Directory.Delete(locationDirectory, recursive: true);
        }
        Directory.Move(offlineLocationDirectory, locationDirectory);

        // Restarting with the restored .location lets migration complete through the ledger and qualify with zero drift.
        using var recoveredLocations = CreateStore();
        Assert.AreEqual(active, await recoveredLocations.InitializeAsync(seed, token).ConfigureAwait(false));
        using (var recoveredOrbital = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
            OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 4)))
        {
            await recoveredOrbital.InitializeAsync(token).ConfigureAwait(false);
            Assert.IsFalse(File.Exists(legacyJsonPath));
            Assert.IsTrue(File.Exists(migratedRawPath));
            CollectionAssert.AreEqual(malformedJson, await File.ReadAllBytesAsync(migratedRawPath, token).ConfigureAwait(false));
        }

        Assert.AreEqual(active, await recoveredLocations.FullVerifyAsync(seed, token).ConfigureAwait(false));
        Assert.AreEqual(active, await recoveredLocations.VerifyInstallationAsync(seed, token).ConfigureAwait(false));
        Assert.IsFalse(recoveredLocations.RequiresRetainedIntegrityScrub);
    }

    [TestMethod]
    [DataRow(false, UnixFileMode.None)]
    [DataRow(true, UnixFileMode.None)]
    [DataRow(false, UnixFileMode.UserExecute)]
    [DataRow(true, UnixFileMode.UserExecute)]
    [DataRow(false, UnixFileMode.UserWrite)]
    [DataRow(true, UnixFileMode.UserWrite)]
    [DataRow(false, UnixFileMode.UserRead)]
    [DataRow(true, UnixFileMode.UserRead)]
    [DataRow(false, UnixFileMode.UserRead | UnixFileMode.UserWrite)]
    [DataRow(true, UnixFileMode.UserRead | UnixFileMode.UserWrite)]
    public async Task StartupMigrationPreservesLegacyQuarantineJsonWhileLocationDirectoryIsInaccessibleWithoutIdentityMarker(
        bool matchingDuplicateRawExists, UnixFileMode restrictedLocationMode)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix file permissions are required to restrict the .location directory.");
            return;
        }

        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        var token = CancellationToken.None;
        var orbitalRoot = Path.Combine(_options.Value.RawIngressRoot, ".orbital-elements");
        var quarantine = Path.Combine(orbitalRoot, "quarantine");
        var clock = new OrbitalElementsStoreTestData.Clock(Now);
        var malformedJson = "{\"OBJECT_NAME\":\"TRUNCATED"u8.ToArray();
        var malformedSha = CelesTrakGpTestData.Sha256(malformedJson);
        var legacyJsonPath = Path.Combine(quarantine, malformedSha + ".json");
        var migratedRawPath = Path.Combine(quarantine, malformedSha + ".raw");
        var locationDirectory = Path.Combine(_options.Value.RawIngressRoot, RetainedIntegrityLedger.LocationDirectoryName);
        var identityMarkerPath = Path.Combine(_options.Value.RawIngressRoot, RetainedIntegrityLedger.IdentityMarkerName);
        DeploymentLocationSnapshot active;

        using (var locations = CreateStore())
        {
            active = await locations.InitializeAsync(seed, token).ConfigureAwait(false);
            using var orbital = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
                OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 4));
            var attempt = await OrbitalElementsStoreTestData.AdmitAsync(orbital).ConfigureAwait(false);
            await orbital.RecordQuarantineAsync(attempt, malformedJson, malformedSha,
                "orbital.json-syntax", "legacy malformed JSON body", token).ConfigureAwait(false);
            RetainedFileMutations.Move(migratedRawPath, legacyJsonPath);
            if (matchingDuplicateRawExists)
            {
                await File.WriteAllBytesAsync(migratedRawPath, malformedJson, token).ConfigureAwait(false);
            }
        }

        var identityMarkerBytes = await File.ReadAllBytesAsync(identityMarkerPath, token).ConfigureAwait(false);
        var originalMode = File.GetUnixFileMode(locationDirectory);
        File.Delete(identityMarkerPath);
        File.SetUnixFileMode(locationDirectory, restrictedLocationMode);
        try
        {
            using (var unavailableOrbital = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
                OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 4)))
            {
                await unavailableOrbital.InitializeAsync(token).ConfigureAwait(false);
            }

            Assert.IsTrue(File.Exists(legacyJsonPath),
                "Legacy quarantine .json must be preserved untouched while .location is inaccessible even without the root identity marker.");
            CollectionAssert.AreEqual(malformedJson, await File.ReadAllBytesAsync(legacyJsonPath, token).ConfigureAwait(false));
            Assert.AreEqual(matchingDuplicateRawExists, File.Exists(migratedRawPath));
            if (matchingDuplicateRawExists)
            {
                CollectionAssert.AreEqual(malformedJson, await File.ReadAllBytesAsync(migratedRawPath, token).ConfigureAwait(false));
            }
        }
        finally
        {
            File.SetUnixFileMode(locationDirectory, originalMode);
            await File.WriteAllBytesAsync(identityMarkerPath, identityMarkerBytes, token).ConfigureAwait(false);
        }

        using var recoveredLocations = CreateStore();
        Assert.AreEqual(active, await recoveredLocations.InitializeAsync(seed, token).ConfigureAwait(false));
        using (var recoveredOrbital = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
            OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 4)))
        {
            await recoveredOrbital.InitializeAsync(token).ConfigureAwait(false);
            Assert.IsFalse(File.Exists(legacyJsonPath));
            Assert.IsTrue(File.Exists(migratedRawPath));
            CollectionAssert.AreEqual(malformedJson, await File.ReadAllBytesAsync(migratedRawPath, token).ConfigureAwait(false));
        }

        Assert.AreEqual(active, await recoveredLocations.FullVerifyAsync(seed, token).ConfigureAwait(false));
        Assert.AreEqual(active, await recoveredLocations.VerifyInstallationAsync(seed, token).ConfigureAwait(false));
        Assert.IsFalse(recoveredLocations.RequiresRetainedIntegrityScrub);
    }

    [TestMethod]
    public async Task AuthorityScanInLocationDirectoryIsBoundedFailClosedAndCancellable()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        var root = _options.Value.RawIngressRoot;
        var locationDirectory = Path.Combine(root, RetainedIntegrityLedger.LocationDirectoryName);
        Directory.CreateDirectory(locationDirectory);

        // 1. Clean .location with <= 64 unrelated files and no authority markers returns false and allows clean startup.
        for (var i = 0; i < RetainedIntegrityLedger.BoundedAuthorityEntryLimit; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(locationDirectory, $"unrelated-{i:D4}.tmp"), [], CancellationToken.None)
                .ConfigureAwait(false);
        }

        var inspected = 0;
        RetainedIntegrityLedger.BeforeAuthorityScanEntryForTest = _ => inspected++;
        try
        {
            Assert.IsFalse(RetainedIntegrityLedger.HasEstablishedAuthorityAtRoot(root, CancellationToken.None),
                "At most 64 unrelated entries in .location with no authority markers must not report established authority.");
            Assert.AreEqual(RetainedIntegrityLedger.BoundedAuthorityEntryLimit, inspected);

            // 2. Exceeding 64 unrelated entries (4,096 files) fails closed at the 64-entry cap without scanning the rest.
            for (var i = RetainedIntegrityLedger.BoundedAuthorityEntryLimit; i < 4_096; i++)
            {
                await File.WriteAllBytesAsync(Path.Combine(locationDirectory, $"unrelated-{i:D4}.tmp"), [], CancellationToken.None)
                    .ConfigureAwait(false);
            }

            inspected = 0;
            Assert.IsTrue(RetainedIntegrityLedger.HasEstablishedAuthorityAtRoot(root, CancellationToken.None),
                "Exceeding the bounded .location entry cap must fail closed as possibly established.");
            Assert.AreEqual(RetainedIntegrityLedger.BoundedAuthorityEntryLimit, inspected,
                "Authority scan must stop once the 64-entry cap is exceeded instead of enumerating all 4,096 files.");

            // 3. Startup LoadCurrentAsync threads cancellation and stays bounded/fail-closed through HasAnyRetainedStateBounded.
            inspected = 0;
            using (var store = CreateStore())
            {
                _ = await store.LoadCurrentAsync(seed, verification: false, () => true, CancellationToken.None)
                    .ConfigureAwait(false);
                Assert.AreEqual(RetainedIntegrityLedger.BoundedAuthorityEntryLimit, inspected,
                    "Startup LoadCurrentAsync must stop .location authority scan at the 64-entry cap.");
                Assert.IsFalse(store.RecentRetainedEvidenceReady);
                Assert.IsTrue(store.RequiresDeploymentLocationReconciliation);
                Assert.IsFalse(File.Exists(Path.Combine(locationDirectory, RetainedIntegrityLedger.HistoryFileName)),
                    "Fail-closed bounded startup must not persist provisional history over an unbounded .location directory.");
            }

            // 4. Pre-cancelled token is honored before scanning.
            using var preCancelled = new CancellationTokenSource();
            await preCancelled.CancelAsync().ConfigureAwait(false);
            inspected = 0;
            Assert.ThrowsExactly<OperationCanceledException>(() =>
                RetainedIntegrityLedger.HasEstablishedAuthorityAtRoot(root, preCancelled.Token));
            Assert.AreEqual(0, inspected);

            using (var store = CreateStore())
            {
                await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                    _ = await store.LoadCurrentAsync(seed, verification: false, () => true, preCancelled.Token)
                        .ConfigureAwait(false)).ConfigureAwait(false);
                Assert.AreEqual(0, inspected);
            }

            // 5. Cancellation during enumeration stops immediately both directly and through LoadCurrentAsync.
            using var midScanCancel = new CancellationTokenSource();
            inspected = 0;
            RetainedIntegrityLedger.BeforeAuthorityScanEntryForTest = _ =>
            {
                inspected++;
                if (inspected == 5)
                {
                    midScanCancel.Cancel();
                }
            };
            Assert.ThrowsExactly<OperationCanceledException>(() =>
                RetainedIntegrityLedger.HasEstablishedAuthorityAtRoot(root, midScanCancel.Token));
            Assert.AreEqual(5, inspected);

            using var midScanLoadCancel = new CancellationTokenSource();
            inspected = 0;
            RetainedIntegrityLedger.BeforeAuthorityScanEntryForTest = _ =>
            {
                inspected++;
                if (inspected == 5)
                {
                    midScanLoadCancel.Cancel();
                }
            };
            using (var store = CreateStore())
            {
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                    _ = await store.LoadCurrentAsync(seed, verification: false, () => true, midScanLoadCancel.Token)
                        .ConfigureAwait(false)).ConfigureAwait(false);
                Assert.AreEqual(5, inspected);
            }
        }
        finally
        {
            RetainedIntegrityLedger.BeforeAuthorityScanEntryForTest = null;
        }

        // 6. Verify clean startup succeeds end-to-end when .location contains <= 64 unrelated files and no markers.
        var cleanRoot = Path.Combine(_root, "clean-bounded-root");
        var cleanLocation = Path.Combine(cleanRoot, RetainedIntegrityLedger.LocationDirectoryName);
        Directory.CreateDirectory(cleanLocation);
        for (var i = 0; i < RetainedIntegrityLedger.BoundedAuthorityEntryLimit; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(cleanLocation, $"unrelated-{i:D4}.tmp"), [], CancellationToken.None)
                .ConfigureAwait(false);
        }

        _options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = cleanRoot
        });
        using var cleanStore = CreateStore();
        var active = await cleanStore.LoadCurrentAsync(seed, verification: false, () => true, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(1L, active.Version);
        Assert.IsTrue(cleanStore.RecentRetainedEvidenceReady);
        Assert.IsFalse(cleanStore.RequiresDeploymentLocationReconciliation);
    }

    [TestMethod]
    [DataRow(false, true)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(true, false)]
    public async Task StartupMigrationInterruptionBeforeQuarantineDirectorySyncKeepsIntentRecoverable(
        bool matchingDuplicateRawExists, bool directoryRevertsOnPowerLoss)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        var token = CancellationToken.None;
        var orbitalRoot = Path.Combine(_options.Value.RawIngressRoot, ".orbital-elements");
        var quarantine = Path.Combine(orbitalRoot, "quarantine");
        var clock = new OrbitalElementsStoreTestData.Clock(Now);
        var malformedJson = "{\"OBJECT_NAME\":\"TRUNCATED"u8.ToArray();
        var malformedSha = CelesTrakGpTestData.Sha256(malformedJson);
        var legacyJsonPath = Path.Combine(quarantine, malformedSha + ".json");
        var migratedRawPath = Path.Combine(quarantine, malformedSha + ".raw");
        var expectedKey = RetainedIntegrityLedger.FileKey(_options.Value.RawIngressRoot, legacyJsonPath);
        var ledgerPath = Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedIntegrityLedger.DatabaseName);
        DeploymentLocationSnapshot active;

        using (var locations = CreateStore())
        {
            active = await locations.InitializeAsync(seed, token).ConfigureAwait(false);
            using (var orbital = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
                OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 4)))
            {
                var attempt = await OrbitalElementsStoreTestData.AdmitAsync(orbital).ConfigureAwait(false);
                await orbital.RecordQuarantineAsync(attempt, malformedJson, malformedSha,
                    "orbital.json-syntax", "legacy malformed JSON body", token).ConfigureAwait(false);
                RetainedFileMutations.Move(migratedRawPath, legacyJsonPath);
                if (matchingDuplicateRawExists)
                {
                    await File.WriteAllBytesAsync(migratedRawPath, malformedJson, token).ConfigureAwait(false);
                }
            }
        }

        // Interrupt startup migration after the filesystem rename/delete and before the quarantine directory fsync.
        using (var interruptedLocations = CreateStore())
        {
            Assert.AreEqual(active, await interruptedLocations.InitializeAsync(seed, token).ConfigureAwait(false));
            var faults = new OrbitalElementsStoreTestData.Faults
            {
                ArmedPoint = OrbitalStorageFaultPoint.BeforeQuarantineDirectorySync,
                Throw = static () => new InvalidOperationException("simulated power loss before quarantine directory sync")
            };
            using var interruptedOrbital = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
                OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 4), faults);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                interruptedOrbital.InitializeAsync(token).AsTask()).ConfigureAwait(false);
        }

        // The pending intent must remain open and the legacy entry must not have been tombstoned before directory sync.
        using (var connection = new SqliteConnection($"Data Source={ledgerPath};Pooling=False"))
        {
            await connection.OpenAsync(token).ConfigureAwait(false);
            using var pendingCommand = connection.CreateCommand();
            pendingCommand.CommandText = "SELECT COUNT(*) FROM pending WHERE key = $key AND planned IS NULL AND denied = 0;";
            pendingCommand.Parameters.AddWithValue("$key", expectedKey);
            Assert.AreEqual(1L, (long)(await pendingCommand.ExecuteScalarAsync(token).ConfigureAwait(false))!,
                "Pending delete/publication intent must remain in the ledger until quarantine directory sync succeeds.");

            using var entryCommand = connection.CreateCommand();
            entryCommand.CommandText = "SELECT kind FROM entries WHERE key = $key;";
            entryCommand.Parameters.AddWithValue("$key", expectedKey);
            Assert.AreNotEqual(0L, (long)(await entryCommand.ExecuteScalarAsync(token).ConfigureAwait(false))!,
                "Legacy quarantine JSON entry must not be tombstoned before quarantine directory sync succeeds.");
        }

        if (directoryRevertsOnPowerLoss)
        {
            // Simulate the directory entry reverting on power loss so the intact legacy .json reappears.
            if (matchingDuplicateRawExists)
            {
                await File.WriteAllBytesAsync(legacyJsonPath, malformedJson, token).ConfigureAwait(false);
            }
            else
            {
                File.Move(migratedRawPath, legacyJsonPath, overwrite: false);
            }
            Assert.IsTrue(File.Exists(legacyJsonPath));
        }
        else
        {
            Assert.IsFalse(File.Exists(legacyJsonPath));
            Assert.IsTrue(File.Exists(migratedRawPath));
        }

        // Restarting after power loss must recover cleanly and qualify installation verification.
        using var recoveredLocations = CreateStore();
        Assert.AreEqual(active, await recoveredLocations.InitializeAsync(seed, token).ConfigureAwait(false));
        using (var recoveredOrbital = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
            OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 4)))
        {
            await recoveredOrbital.InitializeAsync(token).ConfigureAwait(false);
            Assert.IsFalse(File.Exists(legacyJsonPath));
            Assert.IsTrue(File.Exists(migratedRawPath));
            CollectionAssert.AreEqual(malformedJson, await File.ReadAllBytesAsync(migratedRawPath, token).ConfigureAwait(false));
        }

        Assert.AreEqual(active, await recoveredLocations.FullVerifyAsync(seed, token).ConfigureAwait(false));
        Assert.AreEqual(active, await recoveredLocations.VerifyInstallationAsync(seed, token).ConfigureAwait(false));
        Assert.IsFalse(recoveredLocations.RequiresRetainedIntegrityScrub);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ScrubServiceReconciliationSyncsQuarantineDirectoryBeforeClearingPendingIntent(
        bool matchingDuplicateRawExists)
    {
        var token = CancellationToken.None;
        _options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = _options.Value.RawIngressRoot,
            ConfigFilePath = Path.Combine(AppContext.BaseDirectory, "cameraagent.sample.json"),
            AgentId = "synthetic",
            Observatory = new ObservatoryLocation(35, -113, 0, "America/Phoenix")
        });
        var seed = new DeploymentLocationSeed(
            _options.Value.DeploymentLocation.LocationId,
            _options.Value.DeploymentLocation.Source,
            _options.Value.DeploymentLocation.HorizontalAccuracyMeters,
            _options.Value.DeploymentLocation.EffectiveFromUtc,
            _options.Value.DeploymentLocation.EffectiveUntilUtc,
            _options.Value.Observatory,
            _options.Value.DeploymentLocation.SourceKind);
        var orbitalRoot = Path.Combine(_options.Value.RawIngressRoot, ".orbital-elements");
        var quarantine = Path.Combine(orbitalRoot, "quarantine");
        var clock = new OrbitalElementsStoreTestData.Clock(Now);
        var malformedJson = "{\"OBJECT_NAME\":\"TRUNCATED"u8.ToArray();
        var malformedSha = CelesTrakGpTestData.Sha256(malformedJson);
        var legacyJsonPath = Path.Combine(quarantine, malformedSha + ".json");
        var migratedRawPath = Path.Combine(quarantine, malformedSha + ".raw");
        var expectedKey = RetainedIntegrityLedger.FileKey(_options.Value.RawIngressRoot, legacyJsonPath);
        var ledgerPath = Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedIntegrityLedger.DatabaseName);
        DeploymentLocationSnapshot active;

        using (var locations = CreateStore())
        {
            active = await locations.InitializeAsync(seed, token).ConfigureAwait(false);
            using var orbital = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
                OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 4));
            var attempt = await OrbitalElementsStoreTestData.AdmitAsync(orbital).ConfigureAwait(false);
            await orbital.RecordQuarantineAsync(attempt, malformedJson, malformedSha,
                "orbital.json-syntax", "legacy malformed JSON body", token).ConfigureAwait(false);
            RetainedFileMutations.Move(migratedRawPath, legacyJsonPath);
            if (matchingDuplicateRawExists)
            {
                await File.WriteAllBytesAsync(migratedRawPath, malformedJson, token).ConfigureAwait(false);
            }
        }

        // Interrupt startup migration just before quarantine directory fsync so the pending row stays open.
        using (var interruptedLocations = CreateStore())
        {
            Assert.AreEqual(active, await interruptedLocations.InitializeAsync(seed, token).ConfigureAwait(false));
            var faults = new OrbitalElementsStoreTestData.Faults
            {
                ArmedPoint = OrbitalStorageFaultPoint.BeforeQuarantineDirectorySync,
                Throw = static () => new InvalidOperationException("simulated power loss before quarantine directory sync")
            };
            using var interruptedOrbital = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
                OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 4), faults);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                interruptedOrbital.InitializeAsync(token).AsTask()).ConfigureAwait(false);
        }

        Assert.AreEqual(1L, await QueryPendingCountAsync(ledgerPath, expectedKey, token).ConfigureAwait(false));

        // Run RetainedIntegrityScrubService on a restarted host BEFORE the orbital store re-initializes.
        // The ledger must durably sync the quarantine directory before committing and clearing the pending row.
        var syncedDirectories = new List<string>();
        long pendingCountAtQuarantineSync = -1;
        var quarantineSynced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RetainedIntegrityLedger.BeforePendingDirectorySyncForTest = directory =>
        {
            syncedDirectories.Add(directory);
            if (string.Equals(directory, quarantine, StringComparison.Ordinal))
            {
                pendingCountAtQuarantineSync = QueryPendingCount(ledgerPath, expectedKey);
                quarantineSynced.TrySetResult();
            }
        };
        try
        {
            using var restartedLocations = CreateStore();
            var lifetime = new IncrementalStartedLifetime();
            var loader = new FileCameraAgentConfigurationLoader(
                _options, NullLogger<FileCameraAgentConfigurationLoader>.Instance, restartedLocations,
                hostLifetime: lifetime, timeProvider: _timeProvider);
            _ = await loader.LoadAsync(token).ConfigureAwait(false);
            using var scrubService = new RetainedIntegrityScrubService(
                loader, NullLogger<RetainedIntegrityScrubService>.Instance, _timeProvider,
                locations: restartedLocations, hostLifetime: lifetime);
            await scrubService.StartAsync(token).ConfigureAwait(false);
            try
            {
                await quarantineSynced.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                await loader.DrainRetainedIntegrityScrubAsync(token).ConfigureAwait(false);
            }
            finally
            {
                await scrubService.StopAsync(token).ConfigureAwait(false);
            }

            CollectionAssert.Contains(syncedDirectories, quarantine,
                "Pending-intent reconciliation must durably sync the quarantine directory before clearing the pending row.");
            Assert.AreEqual(1L, pendingCountAtQuarantineSync,
                "The pending intent row must still be open when DurableSync.Directory(quarantine) executes.");
            Assert.AreEqual(0L, await QueryPendingCountAsync(ledgerPath, expectedKey, token).ConfigureAwait(false),
                "Once directory sync and scrub publication succeed, the pending intent row must be cleared.");
            Assert.IsFalse(restartedLocations.RequiresRetainedIntegrityScrub);
            Assert.AreEqual(active, await restartedLocations.VerifyInstallationAsync(seed, token).ConfigureAwait(false));
            Assert.IsFalse(File.Exists(legacyJsonPath));
            Assert.IsTrue(File.Exists(migratedRawPath));

            // Subsequent orbital store initialization remains idempotent and preserves verification.
            using var restartedOrbital = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
                OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 4));
            await restartedOrbital.InitializeAsync(token).ConfigureAwait(false);
            Assert.AreEqual(active, await restartedLocations.VerifyInstallationAsync(seed, token).ConfigureAwait(false));
        }
        finally
        {
            RetainedIntegrityLedger.BeforePendingDirectorySyncForTest = null;
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ScrubPendingDirectorySyncFailurePreservesPendingIntentAndRecoversRevertedQuarantineJson(
        bool matchingDuplicateRawExists)
    {
        var token = CancellationToken.None;
        _options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = _options.Value.RawIngressRoot,
            ConfigFilePath = Path.Combine(AppContext.BaseDirectory, "cameraagent.sample.json"),
            AgentId = "synthetic",
            Observatory = new ObservatoryLocation(35, -113, 0, "America/Phoenix")
        });
        var seed = new DeploymentLocationSeed(
            _options.Value.DeploymentLocation.LocationId,
            _options.Value.DeploymentLocation.Source,
            _options.Value.DeploymentLocation.HorizontalAccuracyMeters,
            _options.Value.DeploymentLocation.EffectiveFromUtc,
            _options.Value.DeploymentLocation.EffectiveUntilUtc,
            _options.Value.Observatory,
            _options.Value.DeploymentLocation.SourceKind);
        var orbitalRoot = Path.Combine(_options.Value.RawIngressRoot, ".orbital-elements");
        var quarantine = Path.Combine(orbitalRoot, "quarantine");
        var clock = new OrbitalElementsStoreTestData.Clock(Now);
        var malformedJson = "{\"OBJECT_NAME\":\"TRUNCATED"u8.ToArray();
        var malformedSha = CelesTrakGpTestData.Sha256(malformedJson);
        var legacyJsonPath = Path.Combine(quarantine, malformedSha + ".json");
        var migratedRawPath = Path.Combine(quarantine, malformedSha + ".raw");
        var expectedKey = RetainedIntegrityLedger.FileKey(_options.Value.RawIngressRoot, legacyJsonPath);
        var ledgerPath = Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedIntegrityLedger.DatabaseName);
        DeploymentLocationSnapshot active;

        using (var locations = CreateStore())
        {
            active = await locations.InitializeAsync(seed, token).ConfigureAwait(false);
            using var orbital = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
                OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 4));
            var attempt = await OrbitalElementsStoreTestData.AdmitAsync(orbital).ConfigureAwait(false);
            await orbital.RecordQuarantineAsync(attempt, malformedJson, malformedSha,
                "orbital.json-syntax", "legacy malformed JSON body", token).ConfigureAwait(false);
            RetainedFileMutations.Move(migratedRawPath, legacyJsonPath);
            if (matchingDuplicateRawExists)
            {
                await File.WriteAllBytesAsync(migratedRawPath, malformedJson, token).ConfigureAwait(false);
            }
        }

        // Interrupt startup migration before quarantine directory sync.
        using (var interruptedLocations = CreateStore())
        {
            Assert.AreEqual(active, await interruptedLocations.InitializeAsync(seed, token).ConfigureAwait(false));
            var faults = new OrbitalElementsStoreTestData.Faults
            {
                ArmedPoint = OrbitalStorageFaultPoint.BeforeQuarantineDirectorySync,
                Throw = static () => new InvalidOperationException("simulated power loss before quarantine directory sync")
            };
            using var interruptedOrbital = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
                OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 4), faults);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                interruptedOrbital.InitializeAsync(token).AsTask()).ConfigureAwait(false);
        }

        // Inject a directory sync failure when RetainedIntegrityScrubService and FullVerifyAsync attempt
        // to reconcile the pending intent before orbital store re-initialization.
        var syncAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var syncAttempts = 0;
        RetainedIntegrityLedger.BeforePendingDirectorySyncForTest = directory =>
        {
            if (string.Equals(directory, quarantine, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref syncAttempts);
                syncAttempted.TrySetResult();
                throw new IOException("simulated quarantine directory fsync failure during scrub", 5);
            }
        };
        try
        {
            using var failedScrubLocations = CreateStore();
            var lifetime = new IncrementalStartedLifetime();
            var loader = new FileCameraAgentConfigurationLoader(
                _options, NullLogger<FileCameraAgentConfigurationLoader>.Instance, failedScrubLocations,
                hostLifetime: lifetime, timeProvider: _timeProvider);
            _ = await loader.LoadAsync(token).ConfigureAwait(false);
            using var scrubService = new RetainedIntegrityScrubService(
                loader, NullLogger<RetainedIntegrityScrubService>.Instance, _timeProvider,
                locations: failedScrubLocations, hostLifetime: lifetime);
            await scrubService.StartAsync(token).ConfigureAwait(false);
            try
            {
                await syncAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                await loader.DrainRetainedIntegrityScrubAsync(token).ConfigureAwait(false);
            }
            finally
            {
                await scrubService.StopAsync(token).ConfigureAwait(false);
            }

            Assert.IsGreaterThan(0, Volatile.Read(ref syncAttempts));
            Assert.IsTrue(failedScrubLocations.RequiresRetainedIntegrityScrub);
            await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
                await failedScrubLocations.VerifyInstallationAsync(seed, token).ConfigureAwait(false)).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await failedScrubLocations.FullVerifyAsync(seed, token).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            RetainedIntegrityLedger.BeforePendingDirectorySyncForTest = null;
        }

        // The pending intent must remain open and the legacy .json entry must remain intact in entries.
        Assert.AreEqual(1L, await QueryPendingCountAsync(ledgerPath, expectedKey, token).ConfigureAwait(false),
            "Failed pending-intent directory sync must keep the pending intent open.");
        using (var connection = new SqliteConnection($"Data Source={ledgerPath};Pooling=False"))
        {
            await connection.OpenAsync(token).ConfigureAwait(false);
            using var entryCommand = connection.CreateCommand();
            entryCommand.CommandText = "SELECT kind FROM entries WHERE key = $key;";
            entryCommand.Parameters.AddWithValue("$key", expectedKey);
            Assert.AreNotEqual(0L, (long)(await entryCommand.ExecuteScalarAsync(token).ConfigureAwait(false))!,
                "Legacy quarantine JSON entry must not be tombstoned when directory sync fails during scrub.");
        }

        // Simulate power loss reverting the unsynced quarantine directory entry so the intact legacy .json reappears.
        if (matchingDuplicateRawExists)
        {
            await File.WriteAllBytesAsync(legacyJsonPath, malformedJson, token).ConfigureAwait(false);
        }
        else
        {
            File.Move(migratedRawPath, legacyJsonPath, overwrite: false);
        }
        Assert.IsTrue(File.Exists(legacyJsonPath));

        // Restarting after power loss must recover cleanly without rejecting the reverted .json as drift.
        using var recoveredLocations = CreateStore();
        Assert.AreEqual(active, await recoveredLocations.InitializeAsync(seed, token).ConfigureAwait(false));
        using (var recoveredOrbital = OrbitalElementsStoreTestData.Open(orbitalRoot, clock,
            OrbitalElementsStoreTestData.Settings(orbitalRoot, maximumQuarantined: 4)))
        {
            await recoveredOrbital.InitializeAsync(token).ConfigureAwait(false);
            Assert.IsFalse(File.Exists(legacyJsonPath));
            Assert.IsTrue(File.Exists(migratedRawPath));
            CollectionAssert.AreEqual(malformedJson, await File.ReadAllBytesAsync(migratedRawPath, token).ConfigureAwait(false));
        }

        Assert.AreEqual(active, await recoveredLocations.FullVerifyAsync(seed, token).ConfigureAwait(false));
        Assert.AreEqual(active, await recoveredLocations.VerifyInstallationAsync(seed, token).ConfigureAwait(false));
        Assert.IsFalse(recoveredLocations.RequiresRetainedIntegrityScrub);
        Assert.AreEqual(0L, await QueryPendingCountAsync(ledgerPath, expectedKey, token).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ScrubPendingIntentReconciliationHandlesMissingParentDirectoryGracefully()
    {
        var token = CancellationToken.None;
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        var root = _options.Value.RawIngressRoot;
        var nestedDirectory = Path.Combine(root, "ephemeral", "sub");
        var nestedPath = Path.Combine(nestedDirectory, "orphan.json");
        var expectedKey = RetainedIntegrityLedger.FileKey(root, nestedPath);
        var ledgerPath = Path.Combine(root, ".location", RetainedIntegrityLedger.DatabaseName);
        DeploymentLocationSnapshot active;

        using (var locations = CreateStore())
        {
            active = await locations.InitializeAsync(seed, token).ConfigureAwait(false);
            Directory.CreateDirectory(nestedDirectory);
            await RetainedFileMutations.WriteAllBytesAsync(nestedPath, "{}"u8.ToArray(), token).ConfigureAwait(false);
            using (RetainedIntegrityLedger.BeginDelete(nestedPath))
            {
                Directory.Delete(Path.Combine(root, "ephemeral"), recursive: true);
                RetainedIntegrityLedger.AbandonCurrentMutation();
            }
        }

        Assert.IsFalse(Directory.Exists(nestedDirectory));
        Assert.AreEqual(1L, await QueryPendingCountAsync(ledgerPath, expectedKey, token).ConfigureAwait(false));

        var syncedDirectories = new List<string>();
        RetainedIntegrityLedger.BeforePendingDirectorySyncForTest = syncedDirectories.Add;
        try
        {
            using var restarted = CreateStore();
            Assert.AreEqual(active, await restarted.FullVerifyAsync(seed, token).ConfigureAwait(false));
            Assert.AreEqual(active, await restarted.VerifyInstallationAsync(seed, token).ConfigureAwait(false));
            CollectionAssert.Contains(syncedDirectories, Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
                "When the pending intent's parent directory no longer exists, reconciliation must sync its nearest existing ancestor.");
            Assert.AreEqual(0L, await QueryPendingCountAsync(ledgerPath, expectedKey, token).ConfigureAwait(false));
        }
        finally
        {
            RetainedIntegrityLedger.BeforePendingDirectorySyncForTest = null;
        }
    }

    [TestMethod]
    public async Task ScrubPendingIntentReconciliationSyncsSiblingDirectoriesDeepestFirstBeforeRoot()
    {
        var token = CancellationToken.None;
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_options.Value.RawIngressRoot));
        var alpha = Path.Combine(root, "alpha");
        var alphaNested = Path.Combine(alpha, "nested");
        var beta = Path.Combine(root, "beta");
        var firstPath = Path.Combine(alphaNested, "first.json");
        var secondPath = Path.Combine(beta, "second.json");
        var firstKey = RetainedIntegrityLedger.FileKey(root, firstPath);
        var secondKey = RetainedIntegrityLedger.FileKey(root, secondPath);
        var ledgerPath = Path.Combine(root, ".location", RetainedIntegrityLedger.DatabaseName);
        DeploymentLocationSnapshot active;

        using (var locations = CreateStore())
        {
            active = await locations.InitializeAsync(seed, token).ConfigureAwait(false);
            Directory.CreateDirectory(alphaNested);
            Directory.CreateDirectory(beta);
            await RetainedFileMutations.WriteAllBytesAsync(firstPath, "{}"u8.ToArray(), token).ConfigureAwait(false);
            await RetainedFileMutations.WriteAllBytesAsync(secondPath, "{}"u8.ToArray(), token).ConfigureAwait(false);
            using (RetainedIntegrityLedger.BeginDelete(firstPath))
            {
                File.Delete(firstPath);
                RetainedIntegrityLedger.AbandonCurrentMutation();
            }
            using (RetainedIntegrityLedger.BeginDelete(secondPath))
            {
                File.Delete(secondPath);
                RetainedIntegrityLedger.AbandonCurrentMutation();
            }
        }

        Assert.AreEqual(1L, await QueryPendingCountAsync(ledgerPath, firstKey, token).ConfigureAwait(false));
        Assert.AreEqual(1L, await QueryPendingCountAsync(ledgerPath, secondKey, token).ConfigureAwait(false));

        var syncedDirectories = new List<string>();
        RetainedIntegrityLedger.BeforePendingDirectorySyncForTest = syncedDirectories.Add;
        try
        {
            using var restarted = CreateStore();
            Assert.AreEqual(active, await restarted.FullVerifyAsync(seed, token).ConfigureAwait(false));
            Assert.AreEqual(active, await restarted.VerifyInstallationAsync(seed, token).ConfigureAwait(false));
            CollectionAssert.AreEqual(new[] { alphaNested, alpha, beta, root }, syncedDirectories,
                "Pending-intent directory sync must order directories globally deepest-first (with ordinal tie-breaking) so all sibling directories sync before root.");
            Assert.AreEqual(0L, await QueryPendingCountAsync(ledgerPath, firstKey, token).ConfigureAwait(false));
            Assert.AreEqual(0L, await QueryPendingCountAsync(ledgerPath, secondKey, token).ConfigureAwait(false));
        }
        finally
        {
            RetainedIntegrityLedger.BeforePendingDirectorySyncForTest = null;
        }
    }

    private static long QueryPendingCount(string ledgerPath, string key)
    {
        using var connection = new SqliteConnection($"Data Source={ledgerPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pending WHERE key = $key AND planned IS NULL AND denied = 0;";
        command.Parameters.AddWithValue("$key", key);
        return (long)command.ExecuteScalar()!;
    }

    private static async Task<long> QueryPendingCountAsync(string ledgerPath, string key, CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection($"Data Source={ledgerPath};Pooling=False");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pending WHERE key = $key AND planned IS NULL AND denied = 0;";
        command.Parameters.AddWithValue("$key", key);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    [TestMethod]
    public async Task QualifiedProtectedReadNeverTraversesRetainedEvidenceAndConcurrentReadsAgree()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _ = await WriteSummaryCaptureAsync(active, journal: true).ConfigureAwait(false);
        store.BeforeRetainedManifestValidationForTest = () => Assert.Fail("Protected reads must not scan.");
        var reads = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false))).ConfigureAwait(false);
        Assert.IsTrue(reads.All(result => result == active));
        var changed = seed with { Coordinates = seed.Coordinates with { LatitudeDegrees = 36 } };
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            await store.VerifyInstallationAsync(changed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(active, store.Active, "Fast reads must not commit configuration edits.");
    }

    [TestMethod]
    [DataRow("replace")]
    [DataRow("delete")]
    [DataRow("add")]
    public async Task ApprovedOutOfBandBoundaryDetectsDriftAtFullScrub(string mutation)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (path, bytes) = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        // Valid JSON/contract changes are drift too; not only parser errors.
        if (mutation == "replace") await File.WriteAllBytesAsync(path, [.. bytes, (byte)' ']).ConfigureAwait(false);
        else if (mutation == "delete") File.Delete(path);
        else await File.WriteAllBytesAsync(path + ".extra.json", bytes).ConfigureAwait(false);
        Assert.AreEqual(active, await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false),
            "Roy approved delayed out-of-band detection, explicitly exercised here.");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(active, await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false),
            "Recovery/startup must remain available without replacing expected evidence.");
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ManagedPublishCopyMoveRetentionAndTreeMoveMaintainTheCheckpoint()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (path, bytes) = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        var copy = path + ".copy.json";
        RetainedFileMutations.Copy(path, copy);
        await RetainedFileMutations.WriteAllBytesAsync(copy, [.. bytes, (byte)' ']).ConfigureAwait(false);
        var moved = path + ".moved.json";
        RetainedFileMutations.Move(copy, moved);
        RetainedFileMutations.Delete(moved);
        var sourceDirectory = Path.GetDirectoryName(path)!;
        var destination = Path.Combine(_options.Value.RawIngressRoot, "quarantine", "managed");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        RetainedFileMutations.MoveDirectory(sourceDirectory, destination);
        Assert.AreEqual(active, await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(active, await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));
        RetainedFileMutations.DeleteDirectory(destination, recursive: true);
        Assert.AreEqual(active, await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(active, await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("entry-delete")]
    [DataRow("entry-replay")]
    [DataRow("checkpoint")]
    public async Task LedgerTamperCannotCertifyAFullScrub(string fault)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (path, bytes) = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        var state = Path.Combine(_options.Value.RawIngressRoot, ".location");
        if (fault == "checkpoint")
        {
            var checkpoint = Path.Combine(state, RetainedIntegrityLedger.CheckpointName);
            var protectedBytes = await File.ReadAllBytesAsync(checkpoint).ConfigureAwait(false);
            protectedBytes[^1] ^= 1;
            await File.WriteAllBytesAsync(checkpoint, protectedBytes).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
                await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            _ = await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(active, await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
            return;
        }
        using var connection = new SqliteConnection($"Data Source={Path.Combine(state, RetainedIntegrityLedger.DatabaseName)};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE replay AS SELECT * FROM entries;";
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        await RetainedFileMutations.WriteAllBytesAsync(path, [.. bytes, (byte)' ']).ConfigureAwait(false);
        if (fault == "entry-delete") command.CommandText = "DELETE FROM entries;";
        else command.CommandText = "DELETE FROM entries; INSERT INTO entries SELECT * FROM replay;";
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("restart")]
    [DataRow("restore")]
    [DataRow("rollback")]
    public async Task NoNewProcessInheritsCheckpointTrust(string scenario)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var first = CreateStore();
        var active = await first.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _ = await WriteSummaryCaptureAsync(active, journal: true).ConfigureAwait(false);
        first.Dispose();
        var snapshot = Directory.GetFiles(_options.Value.RawIngressRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(_options.Value.RawIngressRoot, path), File.ReadAllBytes, StringComparer.Ordinal);
        first.Dispose();
        if (scenario == "restore")
        {
            Directory.Delete(_options.Value.RawIngressRoot, recursive: true);
            foreach (var (relative, bytes) in snapshot)
            {
                var path = Path.Combine(_options.Value.RawIngressRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
            }
        }
        if (scenario == "rollback")
        {
            // Simulate an old validator's last checkpoint. A new epoch must scrub regardless of it.
            var checkpoint = Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedIntegrityLedger.CheckpointName);
            var json = System.Text.Json.Nodes.JsonNode.Parse(_protector.Unprotect(await File.ReadAllBytesAsync(checkpoint).ConfigureAwait(false)))!;
            json["Binding"] = Convert.ToHexString(SHA256.HashData("old-validator"u8));
            await File.WriteAllBytesAsync(checkpoint, _protector.Protect(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(json))).ConfigureAwait(false);
        }
        using var restarted = CreateStore();
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            await restarted.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        var scanned = 0;
        restarted.BeforeRetainedManifestValidationForTest = () => scanned++;
        Assert.AreEqual(active, await restarted.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(2, scanned);
        Assert.AreEqual(active, await restarted.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025:Do not pass IDisposable instances into unawaited tasks", Justification = "The scrub is released in finally and all tasks finish before store/fixture disposal.")]
    public async Task ScrubMergesConcurrentManagedChangesAndProtectedWaitIsFinite()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (path, bytes) = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        store.BeforeRetainedManifestValidationForTest = () =>
        {
            if (Interlocked.Increment(ref first) != 1) return;
            entered.TrySetResult();
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(15)));
        };
        var scrub = Task.Run(async () => await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));
        Task? writer = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
                await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            writer = Task.Run(async () =>
                await RetainedFileMutations.WriteAllBytesAsync(path, [.. bytes, (byte)' ']).ConfigureAwait(false));
        }
        finally { release.Set(); }
        _ = await scrub.ConfigureAwait(false);
        if (writer is not null) await writer.ConfigureAwait(false);
        store.BeforeRetainedManifestValidationForTest = null;
        Assert.AreEqual(active, await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(active, await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task DailyScrubDeadlineCannotBeExtendedByManagedWrites()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _timeProvider.UtcNow = Now.AddHours(23);
        _ = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        Assert.AreEqual(active, await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
        _timeProvider.UtcNow = Now.AddHours(24);
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        _ = await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(active, await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task IncompleteRestoreAndUnmanagedDeletionCannotBeHiddenByManagedWriters()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (path, _) = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        File.Delete(path);
        Assert.ThrowsExactly<InvalidDataException>(() => RetainedFileMutations.Delete(path));
        store.Dispose();
        using var restarted = CreateStore();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await restarted.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(active, await restarted.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            await restarted.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("truncated")]
    [DataRow("metadata")]
    [DataRow("pending")]
    public async Task DamagedRequiredLedgerLeavesRecoveryAvailableButCannotQualify(string fault)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _ = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        store.Dispose();
        var path = Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedIntegrityLedger.DatabaseName);
        if (fault == "missing") File.Delete(path);
        else if (fault == "truncated") await File.WriteAllBytesAsync(path, [1, 2, 3]).ConfigureAwait(false);
        else
        {
            using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            if (fault == "metadata") command.CommandText = "UPDATE metadata SET payload=zeroblob(64);";
            else command.CommandText = "INSERT INTO pending VALUES('F|forged.json',0,1,NULL,NULL,0,zeroblob(64));";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        using var restarted = CreateStore();
        Assert.AreEqual(active, await restarted.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            await restarted.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await restarted.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ReplacedLedgerPathCannotBeHiddenByAnAlreadyOpenSqliteHandle()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (manifestPath, bytes) = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        var path = Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedIntegrityLedger.DatabaseName);
        var replacement = path + ".replacement";
        File.Copy(path, replacement);
        using (var connection = new SqliteConnection($"Data Source={replacement};Pooling=False"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE metadata SET payload=zeroblob(64);";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        File.Move(replacement, path, overwrite: true);
        var refused = false;
        try { _ = await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) when (exception is InvalidDataException or CryptographicException or IOException or SqliteException) { refused = true; }
        Assert.IsTrue(refused, "A full scrub must inspect the durable path, not trust an old open inode.");
        Assert.AreEqual(active, await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025:Do not pass IDisposable instances into unawaited tasks", Justification = "The pre-registration write lease is released in finally and initialization is awaited before fixture disposal.")]
    public async Task InitialQualificationIncludesManagedWritesAlreadyInFlightBeforeRegistration(bool capture)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        var root = _options.Value.RawIngressRoot;
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "owner-state.json");
        var bytes = "{}"u8.ToArray();
        if (capture)
        {
            var location = DeploymentLocationSnapshot.Create(seed.LocationId, 1, seed.Source, null, Now, null,
                35, -113, 0, "America/Phoenix");
            var original = ReconstructableCaptureContractTests.CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
            var stage = RawIngressFileStore.GetPaths(root, Now, original.Descriptor.Artifact.ArtifactId);
            path = stage.SidecarAbsolutePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            bytes = CaptureContractJson.Serialize(original with
            {
                RelativeArtifactPath = stage.PayloadRelativePath,
                Descriptor = original.Descriptor with
                {
                    Location = location.ToProvenance(),
                    Timing = new CaptureTimingDescriptor(Now, Now.AddSeconds(1), Now.AddSeconds(2), Now.AddSeconds(3), Now.AddSeconds(4)),
                    Artifact = original.Descriptor.Artifact with { CreatedUtc = Now.AddSeconds(4) }
                }
            });
            Assert.IsTrue(CaptureContractJson.ParseManifest(bytes).IsValid);
        }
        var mutation = RetainedIntegrityLedger.BeginWrite(path, bytes);
        using var store = CreateStore();
        Task<DeploymentLocationSnapshot> initialize;
        using (ExecutionContext.SuppressFlow())
            initialize = Task.Run(async () => await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false));
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(40)).ConfigureAwait(false);
            Assert.IsFalse(initialize.IsCompleted, "Root registration must wait for an earlier untracked write.");
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
        }
        finally { mutation.Dispose(); }
        if (capture)
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await initialize.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.IsNull(store.Active);
            Assert.IsFalse(File.Exists(Path.Combine(root, ".location", "deployment-location.v1.protected")),
                "Configuration cannot become first immutable identity for previously published capture evidence.");
            return;
        }
        var active = await initialize.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        Assert.AreEqual(active, await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
        await File.WriteAllTextAsync(path, "{\"changed\":true}").ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task StaleBackgroundConfigurationCannotCommitNewLocationAuthority()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _timeProvider.UtcNow = Now.AddMinutes(1);
        var changed = seed with { Coordinates = seed.Coordinates with { LatitudeDegrees = 36 } };
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            await store.LoadCurrentAsync(changed, false, () => false, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(active, store.Active);
        store.Dispose();
        using var restarted = CreateStore();
        Assert.AreEqual(active, await restarted.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));
    }
}
