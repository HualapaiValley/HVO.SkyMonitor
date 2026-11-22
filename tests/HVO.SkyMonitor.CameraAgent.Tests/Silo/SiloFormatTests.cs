using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

/// <summary>
/// Explicit format identities (#1965 4.7; #1967 acceptance 5). The v1 formats are pinned byte for byte, readers accept
/// additive fields, and every unsupported format is refused read-only with a clear outcome and nothing mutated.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SiloFormatTests
{
    private const string FixtureDirectory = "silo-catalog-v1";
    private const string NewerIndexSql = "CREATE TABLE future(x); PRAGMA application_id = 1213416265; PRAGMA user_version = 2;";
    private const string NewerPublicationSql = "CREATE TABLE future(x); PRAGMA application_id = 1213419603; PRAGMA user_version = 2;";
    private const string ForeignSql = "CREATE TABLE someone_elses(x);";
    private static readonly DateTimeOffset At = new(2026, 11, 22, 4, 5, 6, TimeSpan.Zero);
    private static readonly SiloStorageBinding Binding = new("instance-0001", "local-silo", "instance-bucket");
    private static readonly string Sha = SiloHash.Sha256("fixture"u8);

    private static SiloExactLocator Payload(SiloRetentionClass retentionClass, string sha, string version) =>
        new("local-silo", "instance-bucket", SiloObjectNamespace.PayloadKey(retentionClass, sha), version);

    /// <summary>
    /// A centered view revision under its store-issued IDs: a true typed stack whose second revision adds a Ready layer to
    /// the Waiting one, its preview a member, and its predecessor the genuine first revision.
    /// </summary>
    internal static SiloGroupManifest GoldenManifest()
    {
        var stack = SiloTypedViews.Stack("base-0001");
        var stars = SiloTypedViews.Evidence("0001");
        CenteredViewLayer clouds = new(new("clouds", CenteredViewLayerScope.Stack, null), 0, null, null, CenteredViewReadiness.Waiting, null, null);
        var initial = SiloTypedViews.View(stack.Definition, null, 1, [clouds]);
        var view = SiloTypedViews.View(stack.Definition, initial.Revision.RevisionId, 2,
            [clouds, new(new("stars", CenteredViewLayerScope.Stack, null), 3, stars.Artifact, At.AddSeconds(-5), CenteredViewReadiness.Ready, null, null)]);
        var preview = stack.Preview;
        var previewSha = SiloHash.Sha256(preview.Bytes);
        var members = new HashSet<(string, string)> { (preview.ArtifactId, preview.Artifact.IdentitySha256) };
        return new(SiloFormats.GroupManifest, Binding.InstanceId, Binding.BindingId, 1, SiloJson.CenteredViewGroupKind, view.Base.BaseId,
            view.Revision.RevisionId, At, At.AddSeconds(-100), At,
            [new(preview.ArtifactId, preview.Artifact.IdentitySha256, "preview", SiloRetentionClass.Display, "image/jpeg", preview.Bytes.Length,
                previewSha, true, Payload(SiloRetentionClass.Display, previewSha, "v-0007"), Producer: "centered-preview/1", RecipeIdentitySha256: Sha)],
            [.. SiloCenteredViews.ReopenDependencies(view, scienceDisplayed: true, members), new("raw-0103", "1", Sha, SiloDependencyKind.Compute, true)],
            new([Sha], SiloJson.SerializeToElement(new { captureId = "capture-0103" }), Sha, "rig-3", "site-7"),
            initial.Revision.RevisionId, "camera-1",
            SiloCenteredViews.Section(view, initial.Revision.RevisionId));
    }

    internal static SiloJournalSegment GoldenGenesis() =>
        new(SiloFormats.JournalSegment, Binding.InstanceId, Binding.BindingId, 1, 1, At.AddMinutes(-10),
            [new SiloGenerationOpenRecord(1, "install", 0, new Dictionary<string, long> { ["capture-sequence"] = 0 })]);

    internal static SiloJournalSegment GoldenSegment(string genesisSha)
    {
        var locator = Payload(SiloRetentionClass.Full, Sha, "v-0001");
        var archive = new SiloExactLocator("archive-silo", "archive-bucket", locator.Key, "a-0001");
        var identity = new SiloObjectIdentity(SiloIdentityKind.Member, "raw-0103", "1", Sha, 7);
        var qualification = new SiloQualification(At, SiloFormats.ExactGetSha256, true);
        var manifest = new SiloExactLocator("local-silo", "instance-bucket", SiloObjectNamespace.ManifestKey(At, "capture-0103", "r1", Sha), "v-0002");
        return new(SiloFormats.JournalSegment, Binding.InstanceId, Binding.BindingId, 1, 2, At,
            [
                new SiloPublishRecord("capture", "capture-0103", "r1", new(manifest, 512, Sha, qualification),
                    [new("raw-0103", "1", 7, Sha, Sha, true, locator, qualification)], Capture: new("agent-0001", 103)),
                new SiloLiveIssueRecord("camera-1", "camera-1/run-1/generation-1", "base-0001", "rev-0002", 9, At),
                new SiloCopyRecord(identity, archive, SiloStoreClass.Archive, SiloRetrieval.Online, "replica", At, SiloFormats.ExactGetSha256),
                new SiloReserveSurvivorRecord("hold-1", identity, archive, At, "intent-1"),
                new SiloReleaseSurvivorRecord("hold-1", "settled"),
                new SiloRemapRecord(identity, locator, locator with { VersionId = "v-0009" }, At, SiloFormats.ExactGetSha256, true),
                new SiloPinRecord("pin-1", "group", "base-0001", "rev-0002", true, "operator"),
                new SiloPolicyRecord("display", SiloJson.SerializeToElement(new { keepDays = 30 })),
                new SiloExpiryIntentRecord("intent-1", SiloExpiryScope.Copy, identity, "survivor-hold:hold-1",
                    [new(locator, "local", "allocation-1", 4096)], "headroom", false, new(1, 1)),
                new SiloExpiryDispositionRecord("intent-1", locator, SiloExpiryOutcome.Deleted, At, "NoSuchVersion"),
                new SiloAvailabilityRecord(SiloAvailabilityScope.Copy, identity, SiloAvailabilityState.Evicted, At, "proven absence", locator, "intent-1"),
                new SiloAnchorRecord(new("local-silo", "instance-bucket", "c/g000001/000000000001.json", "v-0003"), 300, Sha, new(1, 1), genesisSha)
            ],
            new(1, 1), genesisSha);
    }

    private static IEnumerable<(string Name, byte[] Bytes)> GoldenObjects()
    {
        var manifest = SiloJson.Serialize(GoldenManifest());
        var genesis = SiloJson.Serialize(GoldenGenesis());
        var genesisSha = SiloHash.Sha256(genesis);
        var segment = SiloJson.Serialize(GoldenSegment(genesisSha));
        var entries = new List<SiloCheckpointEntry>();
        foreach (var (generation, sequence, bytes) in new[] { (1L, 1L, genesis), (1L, 2L, segment) })
        {
            var node = JsonNode.Parse(bytes)!["records"]!.AsArray();
            for (var index = 0; index < node.Count; index++)
            {
                entries.Add(new(generation, sequence, index, System.Text.Json.JsonDocument.Parse(node[index]!.ToJsonString()).RootElement.Clone()));
            }
        }
        var checkpoint = SiloJson.Serialize(new SiloJournalCheckpoint(SiloFormats.JournalCheckpoint, Binding.InstanceId, Binding.BindingId,
            new(1, 2), SiloHash.Sha256(segment), entries));
        return [("group-manifest.json", manifest), ("journal-segment-genesis.json", genesis), ("journal-segment.json", segment),
            ("journal-checkpoint.json", checkpoint)];
    }

    [TestMethod]
    public void TheV1FormatsAreWrittenByteForByteAsTheirGoldenFixturesAndReadBackExactly()
    {
        var write = Environment.GetEnvironmentVariable("HVO_SILO_WRITE_FIXTURES");
        foreach (var (name, bytes) in GoldenObjects())
        {
            // Fixture files end with one LF, as the repository's editor settings require; the object bytes do not.
            byte[] file = [.. bytes, (byte)'\n'];
            if (!string.IsNullOrEmpty(write))
            {
                Directory.CreateDirectory(write);
                File.WriteAllBytes(Path.Combine(write, name), file);
                continue;
            }
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", FixtureDirectory, name))
                .Should().Equal(file, $"{name} pins its v1 format; a change is a new format version");
        }
        var golden = GoldenObjects().ToDictionary(static item => item.Name, static item => item.Bytes);
        SiloJson.Serialize(SiloJson.ReadManifest(golden["group-manifest.json"], "manifest")).Should().Equal(golden["group-manifest.json"]);
        SiloJson.Serialize(SiloJson.ReadSegment(golden["journal-segment-genesis.json"], "genesis")).Should().Equal(golden["journal-segment-genesis.json"]);
        SiloJson.ReadSegment(golden["journal-segment.json"], "segment").Records.Select(SiloJson.RecordType)
            .Should().BeEquivalentTo(SiloRecordTypes.All.Except([SiloRecordTypes.GenerationOpen]));
        SiloJson.ReadCheckpoint(golden["journal-checkpoint.json"], "checkpoint").Entries.Should().HaveCount(13);
        Encoding.UTF8.GetString(golden["group-manifest.json"]).Should().StartWith("{\"format\":\"hvo.cameraagent.silo.group-manifest/1\"");
    }

    [TestMethod]
    [DataRow("hvo.cameraagent.silo.group-manifest/2")]
    [DataRow("hvo.cameraagent.silo.journal-segment/1")]
    [DataRow(null)]
    public void AManifestOfAnUnsupportedFormatIsRefusedWithAClearOutcome(string? format)
    {
        var node = JsonNode.Parse(SiloJson.Serialize(GoldenManifest()))!;
        node["format"] = format;

        var read = () => SiloJson.ReadManifest(Encoding.UTF8.GetBytes(node.ToJsonString()), "manifest base-0001/rev-0002");

        var refusal = read.Should().Throw<SiloFormatRefusedException>().Which;
        refusal.Found.Should().Be(format);
        refusal.Supported.Should().Be(SiloFormats.GroupManifest);
        refusal.Message.Should().Contain("refused read-only and left unchanged");
    }

    [TestMethod]
    public void AdditiveFieldsWithinAFormatVersionAreAcceptedAndAnUnknownViewFormatIsNot()
    {
        var node = JsonNode.Parse(SiloJson.Serialize(GoldenManifest()))!;
        node["futureField"] = new JsonObject { ["anything"] = 1 };
        node["members"]![0]!["futureMemberField"] = "kept by newer writers";

        var manifest = SiloJson.ReadManifest(Encoding.UTF8.GetBytes(node.ToJsonString()), "manifest");

        SiloJson.Serialize(manifest).Should().Equal(SiloJson.Serialize(GoldenManifest()));
        node["view"]!["revisionFormat"] = "cameraagent-centered-view-revision-v2";
        var newer = () => SiloJson.ReadManifest(Encoding.UTF8.GetBytes(node.ToJsonString()), "manifest");
        newer.Should().Throw<SiloFormatRefusedException>().Which.Found.Should().Be("cameraagent-centered-view-revision-v2");
    }

    private static async Task<(string Store, string Index)> FingerprintAsync(SiloHarness silo)
    {
        silo.Close();
        await silo.OpenAsync().ConfigureAwait(false);
        return (silo.Local.Fingerprint(), silo.IndexFileSha256());
    }

    /// <summary>
    /// Rebuild over the live catalog. With the surviving writer's evidence its records certify what it committed; without
    /// it, as after total loss, every segment is still read, so an unsupported object anywhere in the chain is refused.
    /// </summary>
    private static async Task<SiloRebuildResult> RebuildInPlaceAsync(SiloHarness silo, bool withWriterEvidence) =>
        await SiloCatalog.RebuildAsync(silo.StateRoot, silo.Binding, silo.Stores, silo.Authority, silo.Authority, silo.Clock,
            new(silo.Local, Writer: withWriterEvidence ? await silo.WriterEvidenceAsync().ConfigureAwait(false) : null), CancellationToken.None)
            .ConfigureAwait(false);

    [TestMethod]
    public async Task AnUnsupportedManifestRefusesTheRebuildAndChangesNeitherTheBucketNorTheCatalog()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var node = JsonNode.Parse(SiloJson.Serialize(GoldenManifest() with { GroupId = "capture-0777", RevisionId = "r1" }))!;
        node["format"] = "hvo.cameraagent.silo.group-manifest/2";
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
        var key = SiloObjectNamespace.ManifestKey(At, "capture-0777", "r1", SiloHash.Sha256(bytes));
        var version = silo.Local.Plant(key, bytes);
        await silo.Journal.AppendAsync([new SiloPublishRecord(SiloJson.CenteredViewGroupKind, "capture-0777", "r1",
            new(silo.Local.Locator(key, version), bytes.Length, SiloHash.Sha256(bytes), new(At, SiloFormats.ExactGetSha256, true)), [])],
            "publish-unsupported", CancellationToken.None).ConfigureAwait(false);
        var before = await FingerprintAsync(silo).ConfigureAwait(false);

        var rebuild = () => RebuildInPlaceAsync(silo, withWriterEvidence: true);

        (await rebuild.Should().ThrowAsync<SiloFormatRefusedException>().ConfigureAwait(false)).Which.Found
            .Should().Be("hvo.cameraagent.silo.group-manifest/2");
        (await FingerprintAsync(silo).ConfigureAwait(false)).Should().Be(before);
        Directory.EnumerateFiles(silo.RootPath, "*.rebuild-*").Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("record-type")]
    [DataRow("segment-format")]
    [DataRow("foreign-binding")]
    public async Task AnUnsupportedOrForeignJournalSegmentRefusesTheRebuildAndChangesNothing(string fault)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var head = await silo.State.ReadHeadAsync(CancellationToken.None).ConfigureAwait(false);
        var next = new SiloJournalSegment(SiloFormats.JournalSegment, silo.Binding.InstanceId, silo.Binding.BindingId, 1, head.Sequence + 1, At,
            [new SiloPinRecord("pin-1", "group", "capture-0001", "r1", true, "operator")], head.Position, head.Sha256);
        var node = JsonNode.Parse(SiloJson.Serialize(next))!;
        switch (fault)
        {
            case "record-type":
                node["records"]![0]!["type"] = "teleport";
                break;
            case "segment-format":
                node["format"] = "hvo.cameraagent.silo.journal-segment/2";
                break;
            default:
                node["storageBindingId"] = new SiloStorageBinding("instance-other", "local-silo", "instance-bucket").BindingId;
                break;
        }
        silo.Local.Plant(SiloObjectNamespace.SegmentKey(new(1, head.Sequence + 1)), Encoding.UTF8.GetBytes(node.ToJsonString()));
        var before = await FingerprintAsync(silo).ConfigureAwait(false);

        var rebuild = () => RebuildInPlaceAsync(silo, withWriterEvidence: false);

        if (fault == "foreign-binding")
        {
            (await rebuild.Should().ThrowAsync<SiloConflictException>().ConfigureAwait(false)).Which.Code.Should().Be("silo.binding-mismatch");
        }
        else
        {
            await rebuild.Should().ThrowAsync<SiloFormatRefusedException>().ConfigureAwait(false);
        }
        (await FingerprintAsync(silo).ConfigureAwait(false)).Should().Be(before);
    }

    [TestMethod]
    [DataRow("catalog-index", "newer")]
    [DataRow("catalog-index", "foreign")]
    [DataRow("publication-state", "newer")]
    [DataRow("publication-state", "foreign")]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only the three constant fixture statements above are executed.")]
    public async Task ADatabaseOfANewerOrForeignFormatIsRefusedWithoutBeingWritten(string database, string fault)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("silo-format");
        try
        {
            var path = Path.Combine(root, database + ".db");
            var applicationId = database == "catalog-index" ? SiloCatalogIndex.ApplicationId : SiloPublicationState.ApplicationId;
            var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await using (connection.ConfigureAwait(false))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                var command = connection.CreateCommand();
                await using (command.ConfigureAwait(false))
                {
                    command.CommandText = (database, fault) switch
                    {
                        ("catalog-index", "newer") => NewerIndexSql,
                        ("publication-state", "newer") => NewerPublicationSql,
                        _ => ForeignSql
                    };
                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
            var before = SiloHash.Sha256(await File.ReadAllBytesAsync(path).ConfigureAwait(false));
            var clock = new SiloTestClock(At);

            Func<Task> open = database == "catalog-index"
                ? () => new SiloCatalogIndex(path, Binding, clock).InitializeAsync(CancellationToken.None).AsTask()
                : () => new SiloPublicationState(path, Binding, clock).InitializeAsync(CancellationToken.None).AsTask();

            var refusal = (await open.Should().ThrowAsync<SiloFormatRefusedException>().ConfigureAwait(false)).Which;
            refusal.Found.Should().Be(fault == "newer" ? $"application_id 0x{applicationId:X8}, user_version 2" : "unversioned schema objects");
            SiloHash.Sha256(await File.ReadAllBytesAsync(path).ConfigureAwait(false)).Should().Be(before);
            File.Exists(path + "-wal").Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ACatalogOfAnotherStorageBindingIsRefusedAndKeptUnchanged()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("silo-format");
        try
        {
            var path = Path.Combine(root, "catalog-index.db");
            var clock = new SiloTestClock(At);
            using (var index = new SiloCatalogIndex(path, Binding, clock))
            {
                await index.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            }
            var other = new SiloStorageBinding(Binding.InstanceId, Binding.StoreId, "another-bucket");
            using var foreign = new SiloCatalogIndex(path, other, clock);

            var open = () => foreign.InitializeAsync(CancellationToken.None).AsTask();

            (await open.Should().ThrowAsync<SiloConflictException>().ConfigureAwait(false)).Which.Code.Should().Be("silo.binding-mismatch");
            other.BindingId.Should().NotBe(Binding.BindingId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
