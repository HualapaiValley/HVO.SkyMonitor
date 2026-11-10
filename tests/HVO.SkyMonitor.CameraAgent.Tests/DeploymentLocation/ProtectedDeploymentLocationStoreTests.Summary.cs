using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using Microsoft.Data.Sqlite;
using HVO.SkyMonitor.CameraAgent.Common.Storage;

namespace HVO.SkyMonitor.CameraAgent.Tests.DeploymentLocation;

public sealed partial class ProtectedDeploymentLocationStoreTests
{
    private string SummaryPath => Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedManifestValidationIndex.FileName);

    [TestMethod]
    public async Task IndexReusesOnlyCurrentContentAcrossRestartAndInspectsReplacementBytes()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (path, json) = await WriteSummaryCaptureAsync(active, journal: true).ConfigureAwait(false);
        Assert.AreEqual((1L, 1L), await ReadWithParseCountsAsync(store, seed).ConfigureAwait(false));
        Assert.AreEqual((0L, 2L), await ReadWithParseCountsAsync(store, seed).ConfigureAwait(false));
        store.Dispose();
        using var restarted = CreateStore();
        Assert.AreEqual((0L, 2L), await ReadWithParseCountsAsync(restarted, seed).ConfigureAwait(false));
        await RetainedFileMutations.WriteAllBytesAsync(path, [.. json, (byte)' ']).ConfigureAwait(false);
        Assert.AreEqual((1L, 1L), await ReadWithParseCountsAsync(restarted, seed).ConfigureAwait(false));
        RetainedFileMutations.Delete(path);
        Assert.AreEqual((0L, 1L), await ReadWithParseCountsAsync(restarted, seed).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("truncated")]
    [DataRow("tampered")]
    [DataRow("schema")]
    [DataRow("identity")]
    [DataRow("kind")]
    [DataRow("oversized")]
    public async Task AdvisoryIndexDamageCannotHideFreshCorruption(string fault)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (path, json) = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        _ = await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false);
        if (fault == "missing") File.Delete(SummaryPath);
        else if (fault == "truncated") await File.WriteAllBytesAsync(SummaryPath, [1, 2, 3]).ConfigureAwait(false);
        else
        {
            using var connection = new SqliteConnection($"Data Source={SummaryPath};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT proof FROM proofs LIMIT 1;";
            var protectedBytes = (byte[])(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
            if (fault == "tampered") protectedBytes[^1] ^= 1;
            else if (fault == "oversized") protectedBytes = new byte[65_536];
            else
            {
                var plaintext = _protector.Unprotect(protectedBytes);
                plaintext[fault == "schema" ? 0 : fault == "identity" ? 8 : 72] ^= 0x40;
                protectedBytes = _protector.Protect(plaintext);
            }
            command.CommandText = "UPDATE proofs SET proof=$bytes;";
            command.Parameters.AddWithValue("$bytes", protectedBytes);
            _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        store.Dispose();
        using var restarted = CreateStore();
        Assert.AreEqual((1L, 0L), await ReadWithParseCountsAsync(restarted, seed).ConfigureAwait(false));
        var corrupted = JsonNode.Parse(json)!;
        corrupted["descriptor"]!["location"]!["version"] = 999;
        await File.WriteAllTextAsync(path, corrupted.ToJsonString()).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await restarted.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false, "schema")]
    [DataRow(false, "json")]
    [DataRow(false, "contract")]
    [DataRow(true, "schema")]
    [DataRow(true, "json")]
    [DataRow(true, "contract")]
    public async Task WarmIndexRejectsInvalidCurrentManifestInEitherSource(bool journal, string fault)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (path, json) = await WriteSummaryCaptureAsync(active, journal).ConfigureAwait(false);
        _ = await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var node = JsonNode.Parse(json)!;
        if (fault == "schema") node["schemaVersion"] = "unsupported";
        if (fault == "contract") node["descriptor"]!["capture"]!["captureSequence"] = -1;
        var bytes = fault == "json" ? Encoding.UTF8.GetBytes("{") : Encoding.UTF8.GetBytes(node.ToJsonString());
        if (journal)
        {
            using var connection = new SqliteConnection($"Data Source={Path.Combine(_options.Value.RawIngressRoot, "journal", "raw-ingress.db")};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE raw_captures SET manifest_json=$bytes;";
            command.Parameters.AddWithValue("$bytes", bytes);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        else await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MidScanCancellationPersistsOnlyCompletedProofsAndStillReadsCurrentBytes(bool journal)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (path, json) = await WriteSummaryCaptureAsync(active, journal).ConfigureAwait(false);
        if (journal)
        {
            RetainedFileMutations.Delete(path);
            using var journalMutation = RetainedIntegrityLedger.BeginJournal(Path.Combine(_options.Value.RawIngressRoot, "journal", "raw-ingress.db"), "2", json);
            using var connection = new SqliteConnection($"Data Source={Path.Combine(_options.Value.RawIngressRoot, "journal", "raw-ingress.db")};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO raw_captures SELECT manifest_json FROM raw_captures;";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        else RetainedFileMutations.Copy(path, path + ".second.json");
        using var cancellation = new CancellationTokenSource();
        var inspected = 0;
        store.BeforeRetainedManifestValidationForTest = () => { if (++inspected == 2) cancellation.Cancel(); };
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await store.FullVerifyAsync(seed, cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
        store.Dispose();
        using var restarted = CreateStore();
        Assert.AreEqual((0L, 2L), await ReadWithParseCountsAsync(restarted, seed).ConfigureAwait(false));
        // Preserved length and timestamp must not make the saved prefix authoritative.
        if (!journal)
        {
            var timestamp = File.GetLastWriteTimeUtc(path);
            var text = Encoding.UTF8.GetString(json);
            var corrupt = text.Replace("\"version\":1", "\"version\":9", StringComparison.Ordinal);
            Assert.AreNotEqual(text, corrupt);
            Assert.AreEqual(text.Length, corrupt.Length);
            await File.WriteAllTextAsync(path, corrupt).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(path, timestamp);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await restarted.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OptionalIndexWriteFailureKeepsDurableAndInMemoryLocationConsistent(bool change)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _ = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        store.BeforeRetainedProofCommitForTest = _ => throw new IOException("Injected cache ENOSPC.", 28);
        _timeProvider.UtcNow = Now.AddMinutes(1);
        var next = change ? seed with { Coordinates = seed.Coordinates with { LatitudeDegrees = 36 } } : seed;
        var loaded = await store.InitializeAsync(next, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(change ? 2L : 1L, loaded.Version);
        Assert.AreEqual(loaded, store.Active);
        store.Dispose();
        using var restarted = CreateStore();
        Assert.AreEqual(loaded, await restarted.InitializeAsync(next, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual((0L, 1L), await ReadWithParseCountsAsync(restarted, next).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("quarantine/torn.json", "{")]
    [DataRow("quarantine/invalid-v2.json", "{\"schemaVersion\":\"v2\"}")]
    [DataRow("frames/2026/11/10/Raw/torn.json", "{")]
    public async Task StartupAdmitsInvalidSidecarsButStrictVerificationRefusesThem(string relative, string json)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var path = Path.Combine(_options.Value.RawIngressRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
        store.Dispose();
        using var restarted = CreateStore();
        Assert.AreEqual(active, await restarted.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await restarted.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(json, await File.ReadAllTextAsync(path).ConfigureAwait(false), "Recovery must not delete evidence.");
    }

    [TestMethod]
    public async Task OrphanCleanupIsBoundedAndNeverDeletesEvidence()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (path, _) = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        var directory = Path.GetDirectoryName(SummaryPath)!;
        for (var i = 0; i < 12; i++) await File.WriteAllTextAsync(Path.Combine(directory, $".retained-validation.v1.protected.{i}.tmp"), "orphan").ConfigureAwait(false);
        _ = await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(4, Directory.EnumerateFiles(directory, ".retained-validation.v1.protected.*.tmp").Count());
        Assert.IsTrue(File.Exists(path));
        _ = await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, Directory.EnumerateFiles(directory, ".retained-validation.v1.protected.*.tmp").Count());
    }

    [TestMethod]
    public async Task ChangedHistoryInvalidatesProofsAndConcurrentReadsKeepCurrentValidation()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _ = await WriteSummaryCaptureAsync(active, journal: true).ConfigureAwait(false);
        _ = await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _timeProvider.UtcNow = Now.AddMinutes(1);
        var changedSeed = seed with { Coordinates = seed.Coordinates with { LatitudeDegrees = 36 } };
        Assert.AreEqual((1L, 3L), await ReadWithParseCountsAsync(store, changedSeed).ConfigureAwait(false));
        var changed = store.Active;
        Assert.AreEqual(2L, changed!.Version);
        var reads = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
            await store.FullVerifyAsync(changedSeed, CancellationToken.None).ConfigureAwait(false))).ConfigureAwait(false);
        Assert.IsTrue(reads.All(value => value == changed));
    }

    [TestMethod]
    [DataRow(".site/site-profile.v1.json", "{\"schemaVersion\":1,\"revision\":1}")]
    [DataRow(".lifecycle-control/state.json", "{\"schemaVersion\":1,\"state\":\"Capturing\"}")]
    [DataRow("journal/scene-retention-cursor.json", "{\"afterRelativePath\":\"fixture\"}")]
    [DataRow("staging/projected-scenes/fixture.json", "{\"schemaVersion\":\"projected-scene-stage-v1\",\"objects\":[]}")]
    [DataRow("journal/capture-satellites/fixture/input.json", "{\"schemaVersion\":\"capture-satellite-input-v1\",\"descriptor\":{}}")]
    [DataRow("derived/fixture/Metadata/product.json", "{\"schemaVersion\":\"metadata-v1\",\"values\":[]}")]
    [DataRow("derived/fixture/Metadata/product.manifest.json", "{\"schemaVersion\":\"hvo-cameraagent-processing-product-v1\",\"capture\":{},\"relativeArtifactPath\":\"fixture\"}")]
    public async Task OtherOwnedJsonIsClassifiedOnceAndNeverSatisfiesAFrameContract(string relative, string json)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (framePath, _) = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        var path = Path.Combine(_options.Value.RawIngressRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await RetainedFileMutations.WriteAllTextAsync(path, json).ConfigureAwait(false);
        Assert.AreEqual((2L, 0L), await ReadWithParseCountsAsync(store, seed).ConfigureAwait(false));
        Assert.AreEqual((0L, 2L), await ReadWithParseCountsAsync(store, seed).ConfigureAwait(false));
        await File.WriteAllTextAsync(framePath, json).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task TornProductionSidecarCanReachTheRealReconcilerWithoutDeletingEvidence()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var path = Path.Combine(_options.Value.RawIngressRoot, "frames", "2026", "11", "10", "Raw", "torn.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await RetainedFileMutations.WriteAllTextAsync(path, "{").ConfigureAwait(false);
        Assert.AreEqual(active, await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false));
        using var hold = new Capture.MaintenanceHoldRoot();
        using var admission = await hold.AdmissionAsync(paused: false).ConfigureAwait(false);
        var journal = new HVO.SkyMonitor.CameraAgent.Common.RawIngress.SqliteRawCaptureJournal(
            Path.Combine(_options.Value.RawIngressRoot, "journal", "raw-ingress.db"), 1);
        await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var reconciler = new HVO.SkyMonitor.CameraAgent.Common.RawIngress.RawIngressReconciler(_options.Value.RawIngressRoot, journal);
        var result = await reconciler.RunAsync(_ => ValueTask.FromResult(hold.Gate.EnterIfInitialized()), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, result.Quarantined);
        Assert.IsFalse(File.Exists(path));
        var retained = Directory.GetFiles(Path.Combine(_options.Value.RawIngressRoot, "quarantine"), "*.json", SearchOption.AllDirectories);
        Assert.HasCount(1, retained);
        Assert.AreEqual("{", await File.ReadAllTextAsync(retained[0]).ConfigureAwait(false));
        Assert.AreEqual(active, await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task AdvisoryIndexWrongLayoutRebuildsWithoutChangingRetainedEvidence()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (path, json) = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        using (var connection = new SqliteConnection($"Data Source={SummaryPath};Pooling=False"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE proofs; CREATE TABLE foreign_state(value TEXT); INSERT INTO foreign_state VALUES('fixture');";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        Assert.AreEqual((1L, 0L), await ReadWithParseCountsAsync(store, seed).ConfigureAwait(false));
        Assert.AreEqual((0L, 1L), await ReadWithParseCountsAsync(store, seed).ConfigureAwait(false));
        CollectionAssert.AreEqual(json, await File.ReadAllBytesAsync(path).ConfigureAwait(false));
        Assert.AreEqual(active, store.Active);
    }

    [TestMethod]
    public async Task AdvisoryIndexSymlinkIsIgnoredWithoutWritingOutsideTheStateRoot()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _ = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        File.Delete(SummaryPath);
        var outside = Path.Combine(_root, "outside-cache-target");
        await File.WriteAllTextAsync(outside, "must stay unchanged").ConfigureAwait(false);
        File.CreateSymbolicLink(SummaryPath, outside);
        Assert.AreEqual((1L, 0L), await ReadWithParseCountsAsync(store, seed).ConfigureAwait(false));
        Assert.AreEqual("must stay unchanged", await File.ReadAllTextAsync(outside).ConfigureAwait(false));
        Assert.AreEqual(active, store.Active);
    }

    private async Task<(string Path, byte[] Json)> WriteSummaryCaptureAsync(DeploymentLocationSnapshot location, bool journal = false)
    {
        var original = ReconstructableCaptureContractTests.CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
        var manifest = original with
        {
            Descriptor = original.Descriptor with
            {
                Location = location.ToProvenance(),
                Timing = new CaptureTimingDescriptor(Now, Now.AddSeconds(1), Now.AddSeconds(2), Now.AddSeconds(3), Now.AddSeconds(4)),
                Artifact = original.Descriptor.Artifact with { CreatedUtc = Now.AddSeconds(4) }
            }
        };
        var json = CaptureContractJson.Serialize(manifest);
        var parsed = CaptureContractJson.ParseManifest(json);
        Assert.IsTrue(parsed.IsValid, $"{parsed.Validation.ReasonCode}: {parsed.Validation.FieldPath}");
        var directoryPath = Path.Combine(_options.Value.RawIngressRoot, "frames", "2026", "11", "10", "Raw");
        Directory.CreateDirectory(directoryPath);
        var path = Path.Combine(directoryPath, "capture.json");
        await HVO.SkyMonitor.CameraAgent.Common.Storage.RetainedFileMutations.WriteAllBytesAsync(path, json).ConfigureAwait(false);
        if (journal)
        {
            var directory = Path.Combine(_options.Value.RawIngressRoot, "journal");
            Directory.CreateDirectory(directory);
            using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "raw-ingress.db")};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            using var integrity = RetainedIntegrityLedger.BeginJournal(Path.Combine(directory, "raw-ingress.db"), "1", json);
            command.CommandText = "CREATE TABLE raw_captures(manifest_json BLOB NOT NULL); INSERT INTO raw_captures VALUES($bytes);";
            command.Parameters.AddWithValue("$bytes", json);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        return (path, json);
    }

    private static async Task<(long Parses, long Hits)> ReadWithParseCountsAsync(
        ProtectedDeploymentLocationStore store, DeploymentLocationSeed seed)
    {
        long parses = 0, hits = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DeploymentLocationTelemetry.AttributionActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                parses += activity.GetTagItem("attribution.parse.count") as long? ?? 0;
                hits += activity.GetTagItem("attribution.cache_hits") as long? ?? 0;
            }
        };
        ActivitySource.AddActivityListener(listener);
        _ = await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false);
        return (parses, hits);
    }
}
