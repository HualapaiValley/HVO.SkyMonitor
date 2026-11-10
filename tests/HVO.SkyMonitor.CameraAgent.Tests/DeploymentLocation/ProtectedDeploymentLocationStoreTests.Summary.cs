using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.DeploymentLocation;

public sealed partial class ProtectedDeploymentLocationStoreTests
{
    private string SummaryPath => Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedManifestValidationSummary.FileName);

    [TestMethod]
    public async Task SummaryReusesOnlyCurrentContentAcrossRestartAndRetiresDeletedProofs()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (path, json) = await WriteSummaryCaptureAsync(active, journal: true).ConfigureAwait(false);
        Assert.AreEqual((1L, 1L), await ReadWithParseCountsAsync(store, seed).ConfigureAwait(false));
        var summary = await File.ReadAllBytesAsync(SummaryPath).ConfigureAwait(false);
        Assert.AreEqual((0L, 2L), await ReadWithParseCountsAsync(store, seed).ConfigureAwait(false));
        using var restarted = CreateStore();
        Assert.AreEqual((0L, 2L), await ReadWithParseCountsAsync(restarted, seed).ConfigureAwait(false));
        CollectionAssert.AreEqual(summary, await File.ReadAllBytesAsync(SummaryPath).ConfigureAwait(false));
        // Whitespace changes length/content without changing semantics: current bytes require a new proof.
        await File.WriteAllBytesAsync(path, [.. json, (byte)' ']).ConfigureAwait(false);
        Assert.AreEqual((1L, 1L), await ReadWithParseCountsAsync(restarted, seed).ConfigureAwait(false));
        File.Delete(path);
        Assert.AreEqual((0L, 1L), await ReadWithParseCountsAsync(restarted, seed).ConfigureAwait(false));
        var plaintext = _protector.Unprotect(await File.ReadAllBytesAsync(SummaryPath).ConfigureAwait(false));
        Assert.AreEqual(44 + 32, plaintext.Length, "Only the remaining journal proof is retained.");
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("truncated")]
    [DataRow("tampered")]
    [DataRow("schema")]
    [DataRow("identity")]
    [DataRow("oversized")]
    public async Task AdvisorySummaryDamageRebuildsAndCannotHideFreshCorruption(string fault)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (path, json) = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        _ = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var protectedBytes = await File.ReadAllBytesAsync(SummaryPath).ConfigureAwait(false);
        var plaintext = _protector.Unprotect(protectedBytes);
        switch (fault)
        {
            case "missing": File.Delete(SummaryPath); break;
            case "truncated": await File.WriteAllBytesAsync(SummaryPath, protectedBytes[..10]).ConfigureAwait(false); break;
            case "tampered": protectedBytes[^1] ^= 1; await File.WriteAllBytesAsync(SummaryPath, protectedBytes).ConfigureAwait(false); break;
            case "schema": plaintext[0] ^= 1; await File.WriteAllBytesAsync(SummaryPath, _protector.Protect(plaintext)).ConfigureAwait(false); break;
            case "identity": plaintext[8] ^= 1; await File.WriteAllBytesAsync(SummaryPath, _protector.Protect(plaintext)).ConfigureAwait(false); break;
            case "oversized":
                using (var stream = File.OpenWrite(SummaryPath)) stream.SetLength(RetainedManifestValidationSummary.MaximumPlaintextBytes + 4097);
                break;
        }
        using var restarted = CreateStore();
        Assert.AreEqual((1L, 0L), await ReadWithParseCountsAsync(restarted, seed).ConfigureAwait(false));
        var corrupted = JsonNode.Parse(json)!;
        corrupted["descriptor"]!["location"]!["version"] = 999;
        await File.WriteAllTextAsync(path, corrupted.ToJsonString()).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await restarted.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false, "schema")]
    [DataRow(false, "json")]
    [DataRow(false, "contract")]
    [DataRow(true, "schema")]
    [DataRow(true, "json")]
    [DataRow(true, "contract")]
    public async Task WarmSummaryRejectsInvalidCurrentManifestInEitherSource(bool journal, string fault)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (path, json) = await WriteSummaryCaptureAsync(active, journal).ConfigureAwait(false);
        _ = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var before = await File.ReadAllBytesAsync(SummaryPath).ConfigureAwait(false);
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
            await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(SummaryPath).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MidScanCancellationPublishesNoProofAndRestartRevalidates(bool journal)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (path, _) = await WriteSummaryCaptureAsync(active, journal).ConfigureAwait(false);
        if (journal)
        {
            File.Delete(path);
            using var connection = new SqliteConnection($"Data Source={Path.Combine(_options.Value.RawIngressRoot, "journal", "raw-ingress.db")};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO raw_captures SELECT manifest_json FROM raw_captures;";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        else File.Copy(path, path + ".second.json");
        using var cancellation = new CancellationTokenSource();
        var inspected = 0;
        store.BeforeRetainedManifestValidationForTest = () =>
        {
            if (++inspected == 2) cancellation.Cancel();
        };
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await store.InitializeAsync(seed, cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.IsFalse(File.Exists(SummaryPath));
        using var restarted = CreateStore();
        Assert.AreEqual((1L, 1L), await ReadWithParseCountsAsync(restarted, seed).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SummaryPublicationFailureAndAbandonedTemporaryFileRecoverWithoutAuthorityChange(bool cancel)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _ = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        store.BeforeRetainedSummaryPublishForTest = _ =>
        {
            if (cancel) cancellation.Cancel();
            else throw new IOException("Injected pre-publication failure.");
        };
        if (cancel)
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await store.InitializeAsync(seed, cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
        else
            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.IsFalse(File.Exists(SummaryPath));
        Assert.AreEqual(active, store.Active);
        // A process killed before rename can leave a partial temporary file; it is never loaded as a proof.
        var abandoned = SummaryPath + ".interrupted.tmp";
        await File.WriteAllBytesAsync(abandoned, [1, 2, 3]).ConfigureAwait(false);
        using var restarted = CreateStore();
        Assert.AreEqual((1L, 0L), await ReadWithParseCountsAsync(restarted, seed).ConfigureAwait(false));
        Assert.AreEqual(active, restarted.Active);
    }

    [TestMethod]
    public async Task ChangedHistoryInvalidatesProofsAndConcurrentReadsKeepCurrentValidation()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _ = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        _ = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _timeProvider.UtcNow = Now.AddMinutes(1);
        var changedSeed = seed with { Coordinates = seed.Coordinates with { LatitudeDegrees = 36 } };
        Assert.AreEqual((1L, 1L), await ReadWithParseCountsAsync(store, changedSeed).ConfigureAwait(false));
        var changed = store.Active;
        Assert.AreEqual(2L, changed!.Version);
        var reads = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
            await store.InitializeAsync(changedSeed, CancellationToken.None).ConfigureAwait(false))).ConfigureAwait(false);
        Assert.IsTrue(reads.All(value => value == changed));
    }

    [TestMethod]
    public void SummaryCapacityAndFormatAreBoundedAndWrongIdentityNeverLoads()
    {
        var summary = new RetainedManifestValidationSummary(new byte[32]);
        for (var index = 0; index <= RetainedManifestValidationSummary.MaximumEntries; index++)
            summary.Record(new((ulong)index, 0, 0, 0));
        var bytes = summary.Serialize();
        Assert.AreEqual(RetainedManifestValidationSummary.MaximumPlaintextBytes, bytes.Length);
        Assert.IsFalse(summary.Contains(new((ulong)RetainedManifestValidationSummary.MaximumEntries, 0, 0, 0)));
        var restored = new RetainedManifestValidationSummary(new byte[32]);
        Assert.IsTrue(restored.TryLoad(bytes));
        Assert.IsTrue(restored.Contains(new(1, 0, 0, 0)));
        var wrongIdentity = new byte[32];
        wrongIdentity[0] = 1;
        Assert.IsFalse(new RetainedManifestValidationSummary(wrongIdentity).TryLoad(bytes));
        Assert.IsFalse(new RetainedManifestValidationSummary(new byte[32]).TryLoad(bytes.AsSpan(0, bytes.Length - 1)));
    }

    [TestMethod]
    [DataRow(".site/site-profile.v1.json", "{\"schemaVersion\":1,\"revision\":1}")]
    [DataRow(".lifecycle-control/state.json", "{\"schemaVersion\":1,\"state\":\"Capturing\"}")]
    [DataRow("journal/scene-retention-cursor.json", "{\"afterRelativePath\":\"fixture\"}")]
    [DataRow("staging/projected-scenes/fixture.json", "{\"schemaVersion\":\"projected-scene-stage-v1\",\"objects\":[]}")]
    [DataRow("journal/capture-satellites/fixture/input.json", "{\"schemaVersion\":\"capture-satellite-input-v1\",\"descriptor\":{}}")]
    [DataRow("derived/fixture/Metadata/product.json", "{\"schemaVersion\":\"metadata-v1\",\"values\":[]}")]
    [DataRow("derived/fixture/Metadata/product.manifest.json", "{\"schemaVersion\":\"hvo-cameraagent-processing-product-v1\",\"capture\":{},\"relativeArtifactPath\":\"fixture\"}")]
    public async Task OtherOwnedJsonModelsAreInspectedWithoutBecomingArtifactProofs(string relativePath, string json)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _ = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        var path = Path.Combine(_options.Value.RawIngressRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
        Assert.AreEqual((2L, 0L), await ReadWithParseCountsAsync(store, seed).ConfigureAwait(false));
        Assert.AreEqual((1L, 1L), await ReadWithParseCountsAsync(store, seed).ConfigureAwait(false));
        var plaintext = _protector.Unprotect(await File.ReadAllBytesAsync(SummaryPath).ConfigureAwait(false));
        Assert.AreEqual(44 + 32, plaintext.Length, "Only the actual artifact is a reusable proof.");
    }

    [TestMethod]
    public async Task ReplacingFrameManifestWithEmptyObjectCannotEraseItsContract()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (path, _) = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        _ = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        await File.WriteAllTextAsync(path, "{}").ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
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
        var directoryPath = Path.Combine(_options.Value.RawIngressRoot, "frames", "fixture", "Raw");
        Directory.CreateDirectory(directoryPath);
        var path = Path.Combine(directoryPath, "capture.json");
        await File.WriteAllBytesAsync(path, json).ConfigureAwait(false);
        if (journal)
        {
            var directory = Path.Combine(_options.Value.RawIngressRoot, "journal");
            Directory.CreateDirectory(directory);
            using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "raw-ingress.db")};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
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
        _ = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        return (parses, hits);
    }
}
