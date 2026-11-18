using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.DeploymentLocation;

public sealed partial class ProtectedDeploymentLocationStoreTests
{
    [TestMethod]
    public async Task SuccessiveCleanRestartsReadZeroOlderFiles()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        var paths = SeedOlderJson(12);
        using (var initial = CreateStore())
            _ = await initial.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        for (var restart = 0; restart < 2; restart++)
        {
            using var store = CreateStore();
            var reads = 0;
            store.BeforeRetainedManifestValidationForTest = () => reads++;
            _ = await store.LoadCurrentAsync(seed, false, () => true, CancellationToken.None).ConfigureAwait(false);
            _ = await store.ScrubCurrentAsync(seed, () => true, CancellationToken.None).ConfigureAwait(false);
            _ = await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(0, reads, $"Unchanged restart {restart + 1} must not read the {paths.Length} older files.");
        }
    }

    [TestMethod]
    public async Task ManagedNewEvidenceReadsOnlyTheNewFileOnNextCleanRestart()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        _ = SeedOlderJson(12);
        using (var initial = CreateStore())
        {
            _ = await initial.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            await RetainedFileMutations.WriteAllBytesAsync(Path.Combine(_options.Value.RawIngressRoot, "new.json"), "{}"u8.ToArray())
                .ConfigureAwait(false);
        }
        for (var restart = 0; restart < 2; restart++)
        {
            using var store = CreateStore();
            var reads = 0;
            store.BeforeRetainedManifestValidationForTest = () => reads++;
            _ = await store.LoadCurrentAsync(seed, false, () => true, CancellationToken.None).ConfigureAwait(false);
            _ = await store.ScrubCurrentAsync(seed, () => true, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(restart == 0 ? 1 : 0, reads);
            _ = await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ChangedOlderFileRefusesRestartAndFullScrub(bool preserveMetadata)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        var path = SeedOlderJson(1)[0];
        using (var initial = CreateStore())
            _ = await initial.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var oldTime = File.GetLastWriteTimeUtc(path);
        await File.WriteAllTextAsync(path, "{\"sample\":9}").ConfigureAwait(false);
        File.SetLastWriteTimeUtc(path, preserveMetadata ? oldTime : oldTime.AddSeconds(1));
        using var store = CreateStore();
        _ = await store.LoadCurrentAsync(seed, false, () => true, CancellationToken.None).ConfigureAwait(false);
        _ = await store.ScrubCurrentAsync(seed, () => true, CancellationToken.None).ConfigureAwait(false);
        if (!preserveMetadata)
        {
            Assert.IsTrue(store.RequiresRetainedIntegrityScrub);
            await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
                _ = await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
        // Option A: even a metadata-preserving change is caught by actual-byte full scrubbing.
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            _ = await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("expired")]
    [DataRow("dirty")]
    [DataRow("unclean")]
    [DataRow("cache-missing")]
    [DataRow("cache-auth")]
    public async Task UntrustedRestartFallsBackToReadingActualOlderBytes(string fault)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        _ = SeedOlderJson(3);
        using (var initial = CreateStore())
            _ = await initial.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        if (fault == "expired") _timeProvider.UtcNow = Now.AddHours(25);
        else if (fault == "cache-missing") File.Delete(Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedManifestValidationIndex.FileName));
        else if (fault == "cache-auth")
        {
            using var cache = new SqliteConnection($"Data Source={Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedManifestValidationIndex.FileName)};Pooling=False");
            await cache.OpenAsync().ConfigureAwait(false);
            using var command = cache.CreateCommand();
            command.CommandText = "UPDATE proofs SET proof=CAST(substr(proof,1,8)||zeroblob(32)||substr(proof,41) AS BLOB) WHERE substr(proof,41,8)=X'48564F52534D3031';";
            _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        else
        {
            var checkpoint = Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedIntegrityLedger.CheckpointName);
            var json = System.Text.Json.Nodes.JsonNode.Parse(_protector.Unprotect(await File.ReadAllBytesAsync(checkpoint).ConfigureAwait(false)))!;
            if (fault == "unclean") json["CleanShutdown"] = false;
            else json["Qualified"] = false;
            await File.WriteAllBytesAsync(checkpoint, _protector.Protect(Encoding.UTF8.GetBytes(json.ToJsonString()))).ConfigureAwait(false);
        }
        using var store = CreateStore();
        var reads = 0;
        store.BeforeRetainedManifestValidationForTest = () => reads++;
        _ = await store.LoadCurrentAsync(seed, false, () => true, CancellationToken.None).ConfigureAwait(false);
        _ = await store.ScrubCurrentAsync(seed, () => true, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(3, reads);
        _ = await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task IncrementalRestartCannotExtendTheOriginalFullScrubDeadline()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        _ = SeedOlderJson(1);
        using (var initial = CreateStore())
            _ = await initial.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _timeProvider.UtcNow = Now.AddHours(23);
        using var store = CreateStore();
        _ = await store.LoadCurrentAsync(seed, false, () => true, CancellationToken.None).ConfigureAwait(false);
        _ = await store.ScrubCurrentAsync(seed, () => true, CancellationToken.None).ConfigureAwait(false);
        _ = await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(Now.AddHours(23), store.RetainedIntegrityRefreshUtc);
        _timeProvider.UtcNow = Now.AddHours(24);
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            _ = await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("ledger")]
    [DataRow("optional-hints")]
    public async Task HotRollbackJournalRecoversBeforeLedgerLayoutValidation(string target)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        _ = SeedOlderJson(12);
        using (var initial = CreateStore())
            _ = await initial.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var path = Path.Combine(_options.Value.RawIngressRoot, ".location",
            target == "ledger" ? RetainedIntegrityLedger.DatabaseName : RetainedManifestValidationIndex.FileName);
        var copied = Path.Combine(_root, "interrupted.db");
        using (var writer = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await writer.OpenAsync().ConfigureAwait(false);
            using var command = writer.CreateCommand();
            if (target == "ledger")
                command.CommandText = "PRAGMA cache_size=1; PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; BEGIN IMMEDIATE; UPDATE entries SET auth=randomblob(2000);";
            else
                command.CommandText = "PRAGMA cache_size=1; PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; BEGIN IMMEDIATE; UPDATE proofs SET proof=randomblob(2000);";
            _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            // Preserve the spilled DB and rollback journal before orderly disposal
            // rolls back the source. The restored pair has no surviving process lock.
            File.Copy(path, copied);
            File.Copy(path + "-journal", copied + "-journal");
            command.CommandText = "ROLLBACK;";
            _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        File.Copy(copied, path, overwrite: true);
        File.Copy(copied + "-journal", path + "-journal", overwrite: true);
        using (var readOnly = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"))
        {
            var failure = Assert.ThrowsExactly<SqliteException>(() =>
            {
                readOnly.Open();
                using var command = readOnly.CreateCommand();
                if (target == "ledger") command.CommandText = "SELECT COUNT(*) FROM entries;";
                else command.CommandText = "SELECT COUNT(*) FROM proofs;";
                _ = command.ExecuteScalar();
            });
            Assert.AreEqual(8, failure.SqliteErrorCode, "The fixture must require writable rollback recovery.");
        }
        using var store = CreateStore();
        var actualReads = 0;
        store.BeforeRetainedManifestValidationForTest = () => actualReads++;
        _ = await store.LoadCurrentAsync(seed, false, () => true, CancellationToken.None).ConfigureAwait(false);
        _ = await store.ScrubCurrentAsync(seed, () => true, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(12, actualReads, "Recovered hot-journal startup must perform a full byte pass.");
        _ = await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _ = await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(File.Exists(path + "-journal"));
    }

    [TestMethod]
    public async Task CleanRestartsSkipUnchangedProductionJournalManifestBytes()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using (var initial = CreateStore())
        {
            var active = await initial.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            var (path, bytes) = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
            var manifest = CaptureContractJson.ParseManifest(bytes).Document!.Manifest;
            var descriptor = manifest.Descriptor;
            var journal = new SqliteRawCaptureJournal(Path.Combine(_options.Value.RawIngressRoot, "journal", "raw-ingress.db"), 5);
            await journal.InitializeAsync([], CancellationToken.None).ConfigureAwait(false);
            var identity = await journal.ReserveIdentityAsync(descriptor.Capture.AgentId, descriptor.Capture.CaptureId,
                descriptor.Artifact.ArtifactId, CancellationToken.None).ConfigureAwait(false);
            descriptor = descriptor with { Capture = descriptor.Capture with { CaptureSequence = identity.CaptureSequence } };
            manifest = manifest with { Descriptor = descriptor };
            bytes = CaptureContractJson.Serialize(manifest);
            await RetainedFileMutations.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
            _ = await journal.CommitAsync(new RawIngressJournalEntry(descriptor.Capture.AgentId, descriptor.Capture.CaptureSequence,
                descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId, CaptureContractJson.ComputeDescriptorSha256(descriptor),
                CaptureContractJson.ComputeManifestSha256(bytes), descriptor.Artifact.ChecksumSha256, descriptor.Layout.ByteLength,
                manifest.RelativeArtifactPath, Path.GetRelativePath(_options.Value.RawIngressRoot, path), bytes,
                descriptor.Timing.ExposureStartedUtc, descriptor.Timing.DurableIngressUtc), null, null, [], CancellationToken.None).ConfigureAwait(false);
            _ = await initial.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false);
        }
        for (var restart = 0; restart < 2; restart++)
        {
            using var store = CreateStore();
            var reads = 0;
            store.BeforeRetainedManifestValidationForTest = () => reads++;
            _ = await store.LoadCurrentAsync(seed, false, () => true, CancellationToken.None).ConfigureAwait(false);
            _ = await store.ScrubCurrentAsync(seed, () => true, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(0, reads, "Neither older sidecar nor journal BLOB should be fetched on a clean unchanged restart.");
            _ = await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task RealRecoveryLatchSurvivesOngoingManagedCaptureWrites()
    {
        _options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = _options.Value.RawIngressRoot,
            ConfigFilePath = Path.Combine(AppContext.BaseDirectory, "cameraagent.sample.json"),
            AgentId = "synthetic",
            Observatory = new ObservatoryLocation(35, -113, 0, "America/Phoenix"),
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
        });
        var path = SeedOlderJson(1)[0];
        using var store = CreateStore();
        var initial = new FileCameraAgentConfigurationLoader(_options, NullLogger<FileCameraAgentConfigurationLoader>.Instance,
            store, timeProvider: _timeProvider);
        _ = await initial.LoadForRetainedIntegrityScrubAsync(CancellationToken.None).ConfigureAwait(false);
        await File.WriteAllTextAsync(path, "{\"unexpected\":true}").ConfigureAwait(false);
        var reads = 0;
        store.BeforeRetainedManifestValidationForTest = () => reads++;
        var loader = new FileCameraAgentConfigurationLoader(_options, NullLogger<FileCameraAgentConfigurationLoader>.Instance,
            store, hostLifetime: new IncrementalStartedLifetime(), timeProvider: _timeProvider);
        await loader.RequestRetainedIntegrityScrubAsync(true, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(loader.RetainedIntegrityRecoveryRequired);
        var retryKey = store.RetainedIntegrityRetryKey;
        var before = store.RetainedIntegrityGeneration;
        var completedReads = reads;
        for (var write = 0; write < 3; write++)
        {
            await RetainedFileMutations.WriteAllBytesAsync(Path.Combine(_options.Value.RawIngressRoot, $"live-{write}.json"),
                "{}"u8.ToArray()).ConfigureAwait(false);
            _timeProvider.UtcNow = _timeProvider.UtcNow.AddMinutes(5);
            Assert.AreEqual(retryKey, store.RetainedIntegrityRetryKey);
            Assert.IsTrue(loader.RetainedIntegrityRecoveryRequired);
            _ = await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            await loader.DrainRetainedIntegrityScrubAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(completedReads, reads, "Ordinary polling must not rewalk an unchanged recovery-required fault.");
        }
        Assert.IsGreaterThan(before, store.RetainedIntegrityGeneration, "Managed writes must really advance the ledger generation.");
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(() => loader.LoadInstallationSnapshotAsync(CancellationToken.None))
            .ConfigureAwait(false);
        Assert.IsTrue(loader.RetainedIntegrityRecoveryRequired);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RecentAdmissionChecksAnAuthenticatedInterruptedPublicationIntent(bool differentBytes)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        var database = Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedIntegrityLedger.DatabaseName);
        var checkpoint = Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedIntegrityLedger.CheckpointName);
        var saved = Path.Combine(_root, "pending-ledger.db");
        var savedCheckpoint = Path.Combine(_root, "pending-checkpoint");
        using (var initial = CreateStore())
        {
            _ = await initial.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            var directory = Directory.CreateDirectory(Path.Combine(_options.Value.RawIngressRoot, "derived", Now.ToString("yyyy/MM/dd",
                System.Globalization.CultureInfo.InvariantCulture), "Metadata"));
            var path = Path.Combine(directory.FullName, "pending.json");
            using (RetainedIntegrityLedger.BeginWrite(path, "{}"u8.ToArray()))
            {
                await File.WriteAllBytesAsync(path, differentBytes ? "{\"unexpected\":true}"u8.ToArray() : "{}"u8.ToArray()).ConfigureAwait(false);
                File.Copy(database, saved);
                File.Copy(checkpoint, savedCheckpoint);
            }
        }
        File.Copy(saved, database, overwrite: true);
        File.Copy(savedCheckpoint, checkpoint, overwrite: true);
        using var restarted = CreateStore();
        _ = await restarted.LoadCurrentAsync(seed, false, () => true, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(!differentBytes, restarted.RecentRetainedEvidenceReady);
        Assert.IsTrue(restarted.RequiresRetainedIntegrityScrub, "A matching pending intent is not whole-corpus qualification.");
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            _ = await restarted.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        if (!differentBytes) _ = await restarted.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class IncrementalStartedLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => new(canceled: true);
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private string[] SeedOlderJson(int count)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_options.Value.RawIngressRoot, "older"));
        return Enumerable.Range(0, count).Select(index =>
        {
            var path = Path.Combine(directory.FullName, $"sample-{index}.json");
            File.WriteAllText(path, "{\"sample\":0}");
            return path;
        }).ToArray();
    }
}
