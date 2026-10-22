using System.Security.Cryptography;
using System.Text.Json;

namespace HVO.SkyMonitor.Astronomy;

public enum OrbitalPopulationCompleteness { Unknown, Partial, CompleteForRequestedPopulation }

/// <summary>Requested-population completeness never asserts global satellite/streak coverage.</summary>
public sealed record OrbitalPopulationCoverage(string RequestedPopulation, OrbitalPopulationCompleteness Completeness,
    IReadOnlyList<string> KnownExclusions);

public sealed record OrbitalSnapshotSource(string DatasetKey, string Provider, string UnderlyingLineage, Uri SourceUri,
    DateTimeOffset RetrievedUtc, DateTimeOffset? ProviderGeneratedUtc, string? EntityTag, string? LastModified,
    string RawSha256, long RawLengthBytes, OrbitalSoftwareIdentity Parser, OrbitalSoftwareIdentity Normalizer,
    Uri TermsUri, string TermsReference);

public sealed record OrbitalSnapshotManifestV1(string SchemaVersion, string ProfileVersion, string ManifestSha256,
    string NormalizedPayloadSha256, int NormalizedPayloadLengthBytes, int RecordCount, int ObjectCount,
    DateTimeOffset? MinimumEpochUtc, DateTimeOffset? MaximumEpochUtc, OrbitalPopulationCoverage Coverage,
    IReadOnlyList<OrbitalSnapshotSource> Sources);

/// <summary>Validated, materialized immutable memory. Loading/storage pinning is the adapter's responsibility.</summary>
public sealed class OrbitalSnapshot
{
    public OrbitalSnapshotManifestV1 Manifest { get; }
    public IReadOnlyList<OrbitalRecordV1> Records { get; }
    public IReadOnlyList<OrbitalObjectIdentity> ObjectIdentities { get; }
    private readonly Dictionary<OrbitalObjectIdentity, IReadOnlyList<OrbitalRecordV1>> recordsByObject;

    private OrbitalSnapshot(OrbitalSnapshotManifestV1 manifest, OrbitalRecordV1[] records)
    {
        Manifest = manifest with
        {
            Sources = Array.AsReadOnly(manifest.Sources.ToArray()),
            Coverage = manifest.Coverage with { KnownExclusions = Array.AsReadOnly(manifest.Coverage.KnownExclusions.ToArray()) }
        };
        Records = Array.AsReadOnly(records);
        recordsByObject = records.GroupBy(static r => r.ObjectIdentity).ToDictionary(static g => g.Key,
            static g => (IReadOnlyList<OrbitalRecordV1>)Array.AsReadOnly(g.ToArray()));
        ObjectIdentities = Array.AsReadOnly(recordsByObject.Keys.OrderBy(static i => i.CatalogNumber, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Immutable object lookup, prepared once during materialization rather than rescanning the feed per frame.</summary>
    public IReadOnlyList<OrbitalRecordV1> RecordsFor(OrbitalObjectIdentity objectIdentity)
    {
        OrbitalContract.Require(OrbitalContract.IsObjectIdentity(objectIdentity), "object-identity");
        return recordsByObject.TryGetValue(objectIdentity, out var records) ? records : Array.Empty<OrbitalRecordV1>();
    }

    public static OrbitalSnapshot Create(IEnumerable<OrbitalRecordV1> records, IEnumerable<OrbitalSnapshotSource> sources,
        OrbitalPopulationCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(coverage.KnownExclusions);
        var items = records.Take(OrbitalContract.MaximumRecords + 1).ToArray();
        var sourceItems = sources.Take(OrbitalContract.MaximumSources + 1).ToArray();
        var normalizedCoverage = coverage with
        {
            KnownExclusions = Array.AsReadOnly(coverage.KnownExclusions.Take(65).Order(StringComparer.Ordinal).ToArray())
        };
        OrbitalContract.Require(items.Length <= OrbitalContract.MaximumRecords && sourceItems.Length is > 0 and <= OrbitalContract.MaximumSources, "snapshot-bound");
        items = items.Select(OrbitalContract.ValidateAndNormalizeRecord).ToArray();
        OrbitalContract.Require(sourceItems.All(static s => s is not null), "missing-source");
        Array.Sort(items, static (a, b) => StringComparer.Ordinal.Compare(a.RecordSha256, b.RecordSha256));
        Array.Sort(sourceItems, static (a, b) => StringComparer.Ordinal.Compare(a.DatasetKey, b.DatasetKey));
        var payload = OrbitalCanonicalJson.Digest(items);
        var value = new OrbitalSnapshotManifestV1(OrbitalContract.SchemaVersion, OrbitalContract.CelesTrakGpProfile, string.Empty,
            payload.Sha256, payload.Length, items.Length,
            items.Select(static r => r.ObjectIdentity).Distinct().Count(),
            items.Length == 0 ? null : items.Min(static r => r.EpochUtc),
            items.Length == 0 ? null : items.Max(static r => r.EpochUtc), normalizedCoverage, Array.AsReadOnly(sourceItems));
        value = value with { ManifestSha256 = ComputeManifestIdentity(value) };
        return ValidateAndMaterialize(value, items, payload, recordsValidated: true);
    }

    public byte[] SerializePayload() => OrbitalContract.SerializeCanonical(Records);
    public byte[] SerializeManifest() => OrbitalContract.SerializeCanonical(Manifest);

    /// <summary>Only canonical v1 payload/manifest bytes are accepted. This is not a provider-feed parser.</summary>
    public static OrbitalSnapshotParseResult Parse(ReadOnlyMemory<byte> manifestJson, ReadOnlyMemory<byte> payloadJson)
    {
        if (manifestJson.Length is 0 or > OrbitalContract.MaximumManifestBytes ||
            payloadJson.Length is 0 or > OrbitalContract.MaximumPayloadBytes) return new(null, "payload-bound");
        try
        {
            var manifest = JsonSerializer.Deserialize<OrbitalSnapshotManifestV1>(manifestJson.Span, OrbitalContract.JsonOptions);
            var records = JsonSerializer.Deserialize<OrbitalRecordV1[]>(payloadJson.Span, OrbitalContract.JsonOptions);
            OrbitalContract.Require(manifest is not null && records is not null, "missing-document");
            OrbitalContract.Require(manifestJson.Span.SequenceEqual(OrbitalContract.SerializeCanonical(manifest)), "noncanonical-manifest");
            var payload = (Convert.ToHexString(SHA256.HashData(payloadJson.Span)), payloadJson.Length);
            var snapshot = ValidateAndMaterialize(manifest!, records!, payload);
            return new(snapshot, null);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException or OverflowException)
        {
            return new(null, ex is ArgumentException ? ex.Message : "invalid-json");
        }
    }

    public static string ComputeManifestIdentity(OrbitalSnapshotManifestV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return OrbitalContract.Hash(new
        {
            value.SchemaVersion,
            value.ProfileVersion,
            value.NormalizedPayloadSha256,
            value.NormalizedPayloadLengthBytes,
            value.RecordCount,
            value.ObjectCount,
            value.MinimumEpochUtc,
            value.MaximumEpochUtc,
            value.Coverage,
            value.Sources
        });
    }

    private static OrbitalSnapshot ValidateAndMaterialize(OrbitalSnapshotManifestV1 m, OrbitalRecordV1[] records,
        (string Sha256, int Length) payload, bool recordsValidated = false)
    {
        OrbitalContract.Require(m.SchemaVersion == OrbitalContract.SchemaVersion && m.ProfileVersion == OrbitalContract.CelesTrakGpProfile && OrbitalContract.IsSha256(m.ManifestSha256) &&
            OrbitalContract.IsSha256(m.NormalizedPayloadSha256), "manifest-schema-identity");
        OrbitalContract.Require(records.Length <= OrbitalContract.MaximumRecords && m.RecordCount == records.Length &&
            m.Sources is { Count: > 0 and <= OrbitalContract.MaximumSources } && m.Coverage is not null, "snapshot-bound");
        var coverage = m.Coverage!;
        OrbitalContract.Require(OrbitalContract.Text(coverage.RequestedPopulation) && Enum.IsDefined(coverage.Completeness) &&
            coverage.KnownExclusions is { Count: <= 64 } && coverage.KnownExclusions.All(static s => OrbitalContract.Text(s)) &&
            coverage.KnownExclusions.SequenceEqual(coverage.KnownExclusions.Distinct().Order(StringComparer.Ordinal)), "coverage");
        var sources = new Dictionary<string, OrbitalSnapshotSource>(StringComparer.Ordinal);
        foreach (var source in m.Sources!)
        {
            OrbitalContract.Require(source is not null && OrbitalContract.Dataset(source.DatasetKey), "source-dataset");
            var s = source!;
            OrbitalContract.Require(sources.TryAdd(s.DatasetKey, s) && OrbitalContract.Text(s.Provider, 128) &&
                OrbitalContract.Text(s.UnderlyingLineage) && SafeUri(s.SourceUri, allowGpQuery: true) && SafeUri(s.TermsUri, false) &&
                OrbitalContract.Text(s.TermsReference, 2048) && OrbitalContract.IsSha256(s.RawSha256) &&
                s.RawLengthBytes is > 0 and <= 32 * 1024 * 1024 && OrbitalContract.Utc(s.RetrievedUtc) &&
                (s.ProviderGeneratedUtc is null || OrbitalContract.Utc(s.ProviderGeneratedUtc.Value)) &&
                OrbitalContract.OptionalText(s.EntityTag, 1024) && OrbitalContract.OptionalText(s.LastModified, 128) &&
                OrbitalContract.Software(s.Parser) && OrbitalContract.Software(s.Normalizer), "source-provenance");
        }
        OrbitalContract.Require(m.Sources.Select(static s => s.DatasetKey).SequenceEqual(sources.Keys.Order(StringComparer.Ordinal)), "source-order");
        string? previous = null;
        foreach (var record in records)
        {
            if (!recordsValidated) OrbitalContract.ValidateRecord(record);
            OrbitalContract.Require(previous is null || StringComparer.Ordinal.Compare(previous, record.RecordSha256) < 0, "record-order-or-duplicate");
            previous = record.RecordSha256;
            OrbitalContract.Require(sources.TryGetValue(record.SourceDataset, out var source) &&
                record.Model.Parser == source.Parser && record.Model.Normalizer == source.Normalizer, "record-source-binding");
        }
        OrbitalContract.Require((m.MinimumEpochUtc is null || OrbitalContract.Utc(m.MinimumEpochUtc.Value)) &&
            (m.MaximumEpochUtc is null || OrbitalContract.Utc(m.MaximumEpochUtc.Value)) && m.ObjectCount == records.Select(static r => r.ObjectIdentity).Distinct().Count() &&
            m.MinimumEpochUtc == (records.Length == 0 ? null : records.Min(static r => r.EpochUtc)) &&
            m.MaximumEpochUtc == (records.Length == 0 ? null : records.Max(static r => r.EpochUtc)), "snapshot-count-or-epoch");
        OrbitalContract.Require(payload.Length <= OrbitalContract.MaximumPayloadBytes && m.NormalizedPayloadLengthBytes == payload.Length &&
            m.NormalizedPayloadSha256 == payload.Sha256 &&
            // Create already streamed these validated records once. Parse must also prove that the original
            // input hashes to precisely the canonical form, including its byte length (no payload-sized copy).
            (recordsValidated || OrbitalCanonicalJson.Digest(records) == payload), "payload-integrity-or-canonical-form");
        OrbitalContract.Require(m.ManifestSha256 == ComputeManifestIdentity(m) &&
            OrbitalCanonicalJson.Digest(m).Length <= OrbitalContract.MaximumManifestBytes, "manifest-integrity");
        return new(m, records.ToArray());
    }

    private static bool SafeUri(Uri? uri, bool allowGpQuery)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps ||
            uri.OriginalString.Length > 2048 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
        return uri.Query.Length == 0 || allowGpQuery && uri.Query == "?GROUP=active&FORMAT=JSON";
    }
}

public sealed record OrbitalSnapshotParseResult(OrbitalSnapshot? Snapshot, string? Reason)
{
    public bool IsValid => Snapshot is not null;
}

public enum OrbitalSnapshotUnavailableReason
{
    InvalidIdentity, NotConfigured, NoActiveSnapshot, UnknownIdentity, Retired, IntegrityFailure, StorageUnavailable
}

public sealed class OrbitalSnapshotReadResult
{
    public OrbitalSnapshot? Snapshot { get; }
    public OrbitalSnapshotUnavailableReason? UnavailableReason { get; }
    private OrbitalSnapshotReadResult(OrbitalSnapshot? snapshot, OrbitalSnapshotUnavailableReason? reason)
        => (Snapshot, UnavailableReason) = (snapshot, reason);
    public static OrbitalSnapshotReadResult Available(OrbitalSnapshot snapshot)
        => new(snapshot ?? throw new ArgumentNullException(nameof(snapshot)), null);
    public static OrbitalSnapshotReadResult Unavailable(OrbitalSnapshotUnavailableReason reason)
        => Enum.IsDefined(reason) ? new(null, reason) : throw new ArgumentOutOfRangeException(nameof(reason));
}

/// <summary>
/// Exact reads use the uppercase manifest SHA, return InvalidIdentity before I/O for malformed keys and never
/// substitute current. Retired requires a known tombstone. Cancellation throws OperationCanceledException.
/// Halted/disabled refresh does not invalidate retained snapshots. Adapters pin storage while materializing;
/// protect active, previous-active, loading and unretired product/assessment references from collection.
/// </summary>
public interface IOrbitalSnapshotReader
{
    ValueTask<OrbitalSnapshotReadResult> ReadCurrentAsync(CancellationToken cancellationToken);
    ValueTask<OrbitalSnapshotReadResult> ReadExactAsync(string snapshotSha256, CancellationToken cancellationToken);
}
