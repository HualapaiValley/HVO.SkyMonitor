using System.Collections.Concurrent;
using FluentAssertions;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Silo;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

/// <summary>
/// A generated local versioned bucket behind the narrow store seam. It keeps every version's exact bytes and stored
/// metadata separately, so a test can delete a version, damage bytes behind intact metadata, lose an acknowledged
/// version, or make a key time out, exactly as the fault matrix requires.
/// </summary>
internal sealed class SiloTestStore(string storeId, string bucket, SiloStoreClass storeClass = SiloStoreClass.Local, bool qualified = true)
    : ISiloObjectStore
{
    private readonly Lock _gate = new();
    private readonly SortedDictionary<string, List<StoredVersion>> _objects = new(StringComparer.Ordinal);
    private int _counter;

    private sealed record StoredVersion(string VersionId, byte[] Bytes, IReadOnlyDictionary<string, string> Metadata);

    public SiloStoreDescriptor Descriptor { get; } = new(storeId, bucket, storeClass, SiloRetrieval.Online, qualified);

    public bool Unreachable { get; set; }

    /// <summary>The PUT never lands and the client sees a timeout.</summary>
    public Func<string, bool>? FailBeforeLanding { get; set; }

    /// <summary>The PUT lands and the client sees a timeout: an uncertain write.</summary>
    public Func<string, bool>? FailAfterLanding { get; set; }

    /// <summary>The PUT is acknowledged but the version is gone before readback, as an unqualified power cut would do.</summary>
    public Func<string, bool>? LoseAfterLanding { get; set; }

    /// <summary>A GET of the key times out.</summary>
    public Func<string, bool>? ReadUnavailable { get; set; }

    public int Puts { get; private set; }

    public async ValueTask<SiloPutResult> PutCreateOnlyAsync(SiloPutRequest request, CancellationToken cancellationToken)
    {
        ThrowIfUnreachable("put");
        if (FailBeforeLanding?.Invoke(request.Key) == true)
        {
            throw new SiloStoreUnavailableException(Descriptor.StoreId, "put " + request.Key, writeMayHaveLanded: false);
        }
        var stream = await request.OpenContent(cancellationToken).ConfigureAwait(false);
        byte[] bytes;
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }
        if (bytes.Length != request.Length || !string.Equals(SiloHash.Sha256(bytes), request.Sha256, StringComparison.Ordinal))
        {
            throw new SiloChecksumRejectedException(request.Key);
        }
        string version;
        lock (_gate)
        {
            Puts++;
            if (_objects.TryGetValue(request.Key, out var existing) && existing.Count > 0)
            {
                return new(false, null);
            }
            version = NextVersionLocked();
            Versions(request.Key).Add(new(version, bytes, new Dictionary<string, string>(request.Metadata)));
            if (LoseAfterLanding?.Invoke(request.Key) == true)
            {
                Versions(request.Key).RemoveAll(stored => stored.VersionId == version);
            }
        }
        if (FailAfterLanding?.Invoke(request.Key) == true)
        {
            throw new SiloStoreUnavailableException(Descriptor.StoreId, "put " + request.Key, writeMayHaveLanded: true);
        }
        return new(true, version);
    }

    public ValueTask<SiloOpenResult> OpenVersionAsync(string key, string versionId, CancellationToken cancellationToken)
    {
        ThrowIfUnreachable("get");
        if (ReadUnavailable?.Invoke(key) == true)
        {
            throw new SiloStoreUnavailableException(Descriptor.StoreId, "get " + key, writeMayHaveLanded: false);
        }
        lock (_gate)
        {
            if (!_objects.TryGetValue(key, out var versions) || versions.Count == 0)
            {
                return ValueTask.FromResult(new SiloOpenResult(SiloOpenStatus.NoSuchKey, null));
            }
            var found = versions.FirstOrDefault(stored => stored.VersionId == versionId);
            return ValueTask.FromResult(found is null
                ? new SiloOpenResult(SiloOpenStatus.NoSuchVersion, null)
                : new SiloOpenResult(SiloOpenStatus.Found, new MemoryStream(found.Bytes.ToArray(), writable: false)));
        }
    }

    public ValueTask<string?> LocateCurrentVersionAsync(string key, CancellationToken cancellationToken)
    {
        ThrowIfUnreachable("head");
        lock (_gate)
        {
            return ValueTask.FromResult(_objects.TryGetValue(key, out var versions) && versions.Count > 0 ? versions[^1].VersionId : null);
        }
    }

    public async IAsyncEnumerable<string> ListKeysAsync(
        string prefix, string? startAfter, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfUnreachable("list");
        string[] keys;
        lock (_gate)
        {
            keys = _objects.Where(pair => pair.Value.Count > 0 && pair.Key.StartsWith(prefix, StringComparison.Ordinal) &&
                    (startAfter is null || string.CompareOrdinal(pair.Key, startAfter) > 0))
                .Select(static pair => pair.Key).ToArray();
        }
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return key;
        }
    }

    // ----- Fixture controls ------------------------------------------------------------------------------------

    /// <summary>Create a version directly, bypassing create-only: a replica, an import, or a foreign writer.</summary>
    public string Plant(string key, byte[] bytes, IReadOnlyDictionary<string, string>? metadata = null)
    {
        lock (_gate)
        {
            var version = NextVersionLocked();
            Versions(key).Add(new(version, bytes.ToArray(), metadata ?? new Dictionary<string, string>
            {
                [SiloObjectHeaders.Sha256] = SiloHash.Sha256(bytes)
            }));
            return version;
        }
    }

    public SiloExactLocator Locator(string key, string version) => new(Descriptor.StoreId, Descriptor.Bucket, key, version);

    public string? Current(string key)
    {
        lock (_gate)
        {
            return _objects.TryGetValue(key, out var versions) && versions.Count > 0 ? versions[^1].VersionId : null;
        }
    }

    public IReadOnlyList<string> VersionsOf(string key)
    {
        lock (_gate)
        {
            return _objects.TryGetValue(key, out var versions) ? versions.Select(static stored => stored.VersionId).ToArray() : [];
        }
    }

    public byte[] Read(string key, string version)
    {
        lock (_gate)
        {
            return _objects[key].Single(stored => stored.VersionId == version).Bytes.ToArray();
        }
    }

    public IReadOnlyDictionary<string, string> Metadata(string key, string version)
    {
        lock (_gate)
        {
            return _objects[key].Single(stored => stored.VersionId == version).Metadata;
        }
    }

    /// <summary>A permanent, version-ID delete: the version reads back as NoSuchVersion.</summary>
    public void Delete(string key, string version)
    {
        lock (_gate)
        {
            if (Versions(key).RemoveAll(stored => stored.VersionId == version) != 1)
            {
                throw new InvalidOperationException($"No version {version} at {key}.");
            }
        }
    }

    public void Delete(SiloExactLocator locator) => Delete(locator.Key, locator.VersionId);

    /// <summary>Flip one byte of the stored bytes while the stored checksum metadata stays intact.</summary>
    public void Damage(string key, string version)
    {
        lock (_gate)
        {
            var versions = Versions(key);
            var index = versions.FindIndex(stored => stored.VersionId == version);
            var bytes = versions[index].Bytes.ToArray();
            bytes[bytes.Length / 2] ^= 0x5A;
            versions[index] = versions[index] with { Bytes = bytes };
        }
    }

    public void Damage(SiloExactLocator locator) => Damage(locator.Key, locator.VersionId);

    public IReadOnlyList<string> Keys(string prefix)
    {
        lock (_gate)
        {
            return _objects.Where(pair => pair.Value.Count > 0 && pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(static pair => pair.Key).ToArray();
        }
    }

    /// <summary>A digest over every key, version, byte and header, to prove an operation mutated nothing.</summary>
    public string Fingerprint()
    {
        lock (_gate)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var (key, versions) in _objects)
            {
                foreach (var stored in versions)
                {
                    hash.AppendData(Encoding.UTF8.GetBytes(key + "\n" + stored.VersionId + "\n"));
                    hash.AppendData(stored.Bytes);
                    foreach (var (name, value) in stored.Metadata.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                    {
                        hash.AppendData(Encoding.UTF8.GetBytes(name + "=" + value + "\n"));
                    }
                }
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
    }

    private List<StoredVersion> Versions(string key)
    {
        if (!_objects.TryGetValue(key, out var versions))
        {
            versions = [];
            _objects[key] = versions;
        }
        return versions;
    }

    private string NextVersionLocked() => $"{Descriptor.StoreId}-v{++_counter:D5}";

    private void ThrowIfUnreachable(string operation)
    {
        if (Unreachable)
        {
            throw new SiloStoreUnavailableException(Descriptor.StoreId, operation, writeMayHaveLanded: false);
        }
    }
}

/// <summary>The payload spool seam (#1968 owns the real spool): exact staged bytes by token, and release tracking.</summary>
internal sealed class SiloTestPayloads : ISiloPayloadSpool
{
    private readonly ConcurrentDictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, int> Released { get; } = new(StringComparer.Ordinal);

    public string Add(byte[] bytes)
    {
        var token = "spool-" + SiloHash.Sha256(bytes)[..24];
        _bytes[token] = bytes.ToArray();
        return token;
    }

    public void Corrupt(string token)
    {
        var bytes = _bytes[token].ToArray();
        bytes[0] ^= 0xFF;
        _bytes[token] = bytes;
    }

    public ValueTask<Stream> OpenAsync(string sourceToken, CancellationToken cancellationToken) =>
        ValueTask.FromResult<Stream>(new MemoryStream(_bytes[sourceToken].ToArray(), writable: false));

    public ValueTask ReleaseAsync(string sourceToken, CancellationToken cancellationToken)
    {
        Released.AddOrUpdate(sourceToken, 1, static (_, count) => count + 1);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Throws once at the armed boundary, as a process crash there would stop the work.</summary>
internal sealed class SiloTestFaults : ISiloFaults
{
    public SiloFaultPoint? Armed { get; set; }

    public void Hit(SiloFaultPoint point)
    {
        if (Armed == point)
        {
            Armed = null;
            throw new InvalidOperationException("simulated crash at " + point);
        }
    }
}

/// <summary>Generation witnesses and anchor seal evidence, which #1972 supplies in production.</summary>
internal sealed class SiloTestAuthority : ISiloGenerationWitness, ISiloAnchorTrust
{
    /// <summary>Witnessed openings, bound to the generation and the exact prefix end each one continues.</summary>
    public HashSet<(long Generation, string PrefixEndSha256)> WitnessedOpenings { get; } = [];

    public bool TrustAnchors { get; set; }

    public HashSet<SiloJournalPosition> UntrustedCovers { get; } = [];

    public bool IsWitnessed(SiloGenerationOpenRecord open, SiloJournalPosition position) =>
        open.PrefixEndSha256 is { } prefixEnd && WitnessedOpenings.Contains((open.Generation, prefixEnd));

    public bool IsTrusted(SiloAnchorRecord anchor, SiloJournalPosition position) =>
        TrustAnchors && !UntrustedCovers.Contains(anchor.Covers);
}

internal sealed class SiloTestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}

/// <summary>
/// One CameraAgent State root, its local Silo bucket and an archive. <see cref="RestartAsync"/> drops every component
/// and reopens it on the same files and buckets, as a process restart does.
/// </summary>
internal sealed class SiloHarness : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 11, 22, 3, 0, 0, TimeSpan.Zero);

    private SiloHarness(string rootPath)
    {
        RootPath = rootPath;
        Local = new SiloTestStore("local-silo", "instance-bucket");
        Archive = new SiloTestStore("archive-silo", "archive-bucket", SiloStoreClass.Archive);
        Binding = new SiloStorageBinding("instance-0001", Local.Descriptor.StoreId, Local.Descriptor.Bucket);
    }

    public string RootPath { get; }
    public SiloStorageBinding Binding { get; }
    public SiloTestStore Local { get; private set; }
    public SiloTestStore Archive { get; }
    public SiloTestPayloads Payloads { get; } = new();
    public SiloTestFaults Faults { get; } = new();
    public SiloTestAuthority Authority { get; } = new();
    public SiloTestClock Clock { get; } = new(Start);
    public SiloStateRoot StateRoot { get; private set; } = null!;
    public SiloPublicationState State { get; private set; } = null!;
    public SiloCatalogIndex Index { get; private set; } = null!;
    public SiloStoreSet Stores { get; private set; } = null!;
    public SiloCatalog Catalog { get; private set; } = null!;
    public SiloJournalWriter Journal { get; private set; } = null!;
    public SiloPublisher Publisher { get; private set; } = null!;

    public static async Task<SiloHarness> CreateAsync(bool install = true)
    {
        var harness = new SiloHarness(FileSystemTestPaths.CreatePhysicalTemporaryDirectory("silo-catalog"));
        await harness.OpenAsync().ConfigureAwait(false);
        if (install)
        {
            await harness.Journal.OpenFirstGenerationAsync(new Dictionary<string, long> { ["capture-sequence"] = 0 }, CancellationToken.None)
                .ConfigureAwait(false);
            await harness.CatchUpAsync().ConfigureAwait(false);
        }
        return harness;
    }

    /// <summary>Replace the local store, for a store whose profile is not persistence-qualified.</summary>
    public async Task ReplaceLocalAsync(SiloTestStore store)
    {
        Close();
        Local = store;
        await OpenAsync().ConfigureAwait(false);
    }

    public async Task OpenAsync()
    {
        StateRoot = SiloStateRoot.Acquire(RootPath);
        State = new SiloPublicationState(StateRoot.PublicationDatabasePath, Binding, Clock);
        await State.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Index = new SiloCatalogIndex(StateRoot.IndexDatabasePath, Binding, Clock);
        await Index.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Stores = new SiloStoreSet(Local, Archive);
        Catalog = new SiloCatalog(Index, Stores, Authority);
        Journal = new SiloJournalWriter(State, StateRoot.Spool, Local, Binding, Clock, Faults);
        Publisher = new SiloPublisher(State, StateRoot.Spool, Journal, Catalog, Payloads, Binding, Clock, Faults);
    }

    public async Task RestartAsync()
    {
        Close();
        await OpenAsync().ConfigureAwait(false);
    }

    public void Close()
    {
        Publisher?.Dispose();
        Journal?.Dispose();
        Index?.Dispose();
        StateRoot?.Dispose();
    }

    public async Task<SiloPublicationOutcome> PublishAsync(SiloGroupDraft draft)
    {
        await Publisher.StageAsync(draft, CancellationToken.None).ConfigureAwait(false);
        return await Publisher.PublishAsync(draft.GroupId, draft.RevisionId, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Catch the catalog up to the writer's committed head, as startup does.</summary>
    public async Task<SiloWalkEnd> CatchUpAsync() =>
        await Catalog.CatchUpAsync(await State.ReadWriterEvidenceAsync(CancellationToken.None).ConfigureAwait(false), null,
            CancellationToken.None).ConfigureAwait(false);

    public Task<SiloWriterEvidence> WriterEvidenceAsync() => State.ReadWriterEvidenceAsync(CancellationToken.None).AsTask();

    /// <summary>Append records as one operation with a fresh key, then catch the catalog up.</summary>
    public async Task<SiloCommittedSegment> AppendAsync(params SiloJournalRecord[] records)
    {
        var committed = await Journal.AppendAsync(records, "test-" + Guid.NewGuid().ToString("N"), CancellationToken.None).ConfigureAwait(false);
        await CatchUpAsync().ConfigureAwait(false);
        return committed;
    }

    /// <summary>
    /// Delete the catalog index (SQLite loss with Silo and publication state intact), then rebuild it as a host would:
    /// from the local journal, with the surviving writer evidence, unless a test names another source.
    /// </summary>
    public async Task<SiloRebuildResult> DeleteCatalogAndRebuildAsync(SiloRebuildOptions? options = null, ISiloFaults? faults = null)
    {
        Close();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            File.Delete(Path.Combine(RootPath, SiloStateRoot.IndexDatabaseFileName + suffix));
        }
        await OpenAsync().ConfigureAwait(false);
        return await RebuildAsync(options, faults).ConfigureAwait(false);
    }

    public async Task<SiloRebuildResult> RebuildAsync(SiloRebuildOptions? options = null, ISiloFaults? faults = null) =>
        await SiloCatalog.RebuildAsync(StateRoot, Binding, Stores, Authority, Authority, Clock,
            options ?? new(Local, Writer: await WriterEvidenceAsync().ConfigureAwait(false)), CancellationToken.None, faults).ConfigureAwait(false);

    /// <summary>Every committed record in the local journal, in order, read straight from the bucket.</summary>
    public IReadOnlyList<SiloJournalRecord> JournalRecords()
    {
        var records = new List<SiloJournalRecord>();
        foreach (var key in Local.Keys(SiloObjectNamespace.JournalPrefix))
        {
            SiloObjectNamespace.TryParseSegmentKey(key, out var position).Should().BeTrue();
            records.AddRange(SiloJson.ReadSegment(Local.Read(key, Local.Current(key)!), key).Records);
        }
        return records;
    }

    public SiloJournalPosition PublishPosition(string groupId, string revisionId = "r1")
    {
        foreach (var key in Local.Keys(SiloObjectNamespace.JournalPrefix))
        {
            SiloObjectNamespace.TryParseSegmentKey(key, out var position).Should().BeTrue();
            if (SiloJson.ReadSegment(Local.Read(key, Local.Current(key)!), key).Records.OfType<SiloPublishRecord>()
                .Any(record => record.GroupId == groupId && record.RevisionId == revisionId))
            {
                return position;
            }
        }
        throw new InvalidOperationException($"No publish record for {groupId}/{revisionId}.");
    }

    public SiloPublishRecord PublishRecord(string groupId, string revisionId = "r1") =>
        JournalRecords().OfType<SiloPublishRecord>().Single(record => record.GroupId == groupId && record.RevisionId == revisionId);

    public SiloExactLocator MemberLocator(string groupId, string artifactId, string revisionId = "r1") =>
        PublishRecord(groupId, revisionId).Members.Single(member => member.ArtifactId == artifactId).Locator;

    public SiloExactLocator ManifestLocator(string groupId, string revisionId = "r1") =>
        PublishRecord(groupId, revisionId).Manifest.Locator;

    public static SiloObjectIdentity Identity(SiloPublishRecord publish, string artifactId)
    {
        var member = publish.Members.Single(member => member.ArtifactId == artifactId);
        return new(SiloIdentityKind.Member, member.ArtifactId, member.ArtifactRevision, member.Sha256, member.Length);
    }

    public static SiloObjectIdentity ManifestIdentity(SiloPublishRecord publish) =>
        new(SiloIdentityKind.Manifest, publish.GroupId, publish.RevisionId, publish.Manifest.Sha256, publish.Manifest.Length);

    /// <summary>Replicate one exact version to the archive and return the copy record that makes it eligible.</summary>
    public SiloCopyRecord ReplicateToArchive(SiloObjectIdentity identity, SiloExactLocator source)
    {
        var version = Archive.Plant(source.Key, Local.Read(source.Key, source.VersionId));
        return new SiloCopyRecord(identity, Archive.Locator(source.Key, version), SiloStoreClass.Archive, SiloRetrieval.Online, "replica",
            Clock.GetUtcNow(), SiloFormats.ExactGetSha256);
    }

    public string IndexFileSha256() => SiloHash.Sha256(File.ReadAllBytes(Path.Combine(RootPath, SiloStateRoot.IndexDatabaseFileName)));

    public void Dispose()
    {
        Close();
        try
        {
            Directory.Delete(RootPath, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Generated, deterministic group revisions. Nothing here reads a large original image or an installed fixture.</summary>
internal static class SiloDrafts
{
    public static byte[] Bytes(string seed, int length)
    {
        var bytes = new byte[length];
        var block = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        for (var offset = 0; offset < length; offset += block.Length)
        {
            block.AsSpan(0, Math.Min(block.Length, length - offset)).CopyTo(bytes.AsSpan(offset));
            block = SHA256.HashData(block);
        }
        return bytes;
    }

    public static SiloDraftMember Member(
        SiloTestPayloads payloads, string artifactId, string role, SiloRetentionClass retentionClass, string mediaType, byte[] bytes,
        bool required = true, string revision = "1") =>
        new(artifactId, revision, role, retentionClass, mediaType, bytes.Length, SiloHash.Sha256(bytes), required, payloads.Add(bytes),
            Producer: "fixture-producer/1", RecipeIdentitySha256: SiloHash.Sha256(Encoding.UTF8.GetBytes("recipe-" + role)));

    public static string CaptureGroup(int number) => $"capture-{number:D4}";
    public static string Raw(int number) => $"raw-{number:D4}";
    public static string Preview(int number) => $"preview-{number:D4}";

    public static SiloGroupDraft Capture(SiloTestPayloads payloads, int number, bool rawRequired = true)
    {
        var from = SiloHarness.Start.AddSeconds(20 * number);
        return new SiloGroupDraft("capture", CaptureGroup(number), "r1", from.AddSeconds(16), from, from.AddSeconds(15),
            [
                Member(payloads, Raw(number), "raw", SiloRetentionClass.Full, "application/octet-stream", Bytes("raw-" + number, 3000 + number), rawRequired),
                Member(payloads, Preview(number), "preview", SiloRetentionClass.Display, "image/jpeg", Bytes("preview-" + number, 400 + number))
            ],
            [],
            new SiloManifestProvenance([SiloHash.Sha256(Encoding.UTF8.GetBytes("graph-input-" + number))],
                Capture: SiloJson.SerializeToElement(new { captureId = CaptureGroup(number), exposure = number }),
                GraphIdentitySha256: SiloHash.Sha256("graph"u8), RigRevision: "rig-3", SiteRevision: "site-7"),
            CameraId: "camera-1", Capture: new("agent-0001", number));
    }

    public static string ReferenceCapture(string baseId) => SiloTypedViews.ReferenceCapture(baseId).ToString("D");

    public static SiloViewEvidence PreviewBinding(string baseId) => SiloTypedViews.Stack(baseId).Preview.Binding;

    /// <summary>
    /// A centered view revision of a true typed stack base, seeded by <paramref name="baseId"/>. Its revision ID is
    /// derived from that base, the predecessor and the layers. The base's preview, science and closed window are members of
    /// the first revision and dependencies afterwards. Its section and reopen dependencies come from the typed view, exactly
    /// as manifest validation derives them again; callers add only compute dependencies.
    /// </summary>
    public static SiloGroupDraft View(
        SiloTestPayloads payloads, string baseId, string revisionId, string? previousRevisionId, long order,
        IReadOnlyList<SiloViewLayerBinding> layers, IReadOnlyList<SiloManifestDependency> computeDependencies, bool publishPreview)
    {
        var stack = SiloTypedViews.Stack(baseId);
        var view = SiloTypedViews.View(stack.Definition, previousRevisionId, order, [.. layers.Select(SiloTypedViews.Layer)]);
        var members = publishPreview ? stack.Products.Select(product => product.Member(payloads)).ToArray() : [];
        return SiloTypedViews.Draft(baseId, revisionId, previousRevisionId, view, members, computeDependencies, order);
    }

    /// <summary>An evidence product published as its own group; layers bind to it by identity and digest.</summary>
    public static SiloGroupDraft Evidence(SiloTestPayloads payloads, string name)
    {
        var anchor = SiloHarness.Start.AddMinutes(10);
        return new SiloGroupDraft("product", "product-" + name, "r1", anchor, anchor, anchor.AddSeconds(1),
            [SiloTypedViews.Evidence(name).Member(payloads)], [], new SiloManifestProvenance([]), CameraId: "camera-1");
    }

    public static SiloViewLayerBinding Layer(string kind, string readiness, string? evidenceName, long order) =>
        new(kind, "Stack", order, readiness,
            Evidence: evidenceName is null ? null : SiloTypedViews.Evidence(evidenceName).Binding,
            ObservedUtc: evidenceName is null ? null : SiloHarness.Start.AddMinutes(11), Reason: readiness == "Incompatible" ? "geometry" : null);
}
