using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

/// <summary>
/// A validated candidate snapshot, or a whole-candidate rejection. <paramref name="RejectionReasonCode"/> is a stable
/// <c>orbital.*</c> code; <paramref name="Detail"/> names record indexes, member names and counts, never record text.
/// </summary>
public sealed record OrbitalCandidateResult(OrbitalSnapshot? Snapshot, string? RejectionReasonCode, string Detail);

/// <summary>
/// Maps a retrieved CelesTrak GP JSON payload to the shared <c>hvo-orbital-elements-v1</c> contract. Any invalid record
/// rejects the whole candidate. Omitted EARTH/TEME/UTC/SGP4 conventions are supplied only under
/// <see cref="OrbitalContract.CelesTrakGpProfile"/>; BSTAR is already in inverse Earth radii and DOT/DDOT retain the
/// GP/TLE field values. Object type is never inferred from names.
/// </summary>
public static class CelesTrakGpNormalizer
{
    internal const string ParserName = "hvo-celestrak-gp-json-parser";
    internal const string NormalizerName = "hvo-celestrak-gp-normalizer";
    internal const string SoftwareVersion = "1";
    internal const string NoradAuthority = "norad";
    private const int MaximumContractReasonLength = 64;

    /// <summary>What the <c>GROUP=active</c> population excludes. Sorted ordinally, as the contract requires.</summary>
    internal static IReadOnlyList<string> KnownExclusions { get; } = Array.AsReadOnly(new[]
    {
        "analyst-objects",
        "debris",
        "inactive-payloads",
        "missing-records",
        "rocket-bodies",
        "supplemental-gp",
        "unpublicized-objects"
    });

    /// <summary>Canonical text of the mapping rules. Changing any rule changes the normalizer's pinned identity.</summary>
    internal static string RuleDescriptor { get; } = BuildRuleDescriptor();

    public static OrbitalSoftwareIdentity Parser { get; } =
        new(ParserName, SoftwareVersion, Sha256Hex(CelesTrakGpParser.RuleDescriptor));

    public static OrbitalSoftwareIdentity Normalizer { get; } =
        new(NormalizerName, SoftwareVersion, Sha256Hex(RuleDescriptor));

    private static readonly OrbitalElementModel Model = new("EARTH", "TEME", "UTC", "SGP4",
        OrbitalContract.GpDerivativeConvention, OrbitalContract.CelesTrakGpProfile, Parser, Normalizer);

    private static readonly OrbitalPopulationCoverage Coverage = new(CelesTrakGpSource.RequestedPopulation,
        OrbitalPopulationCompleteness.CompleteForRequestedPopulation, KnownExclusions);

    /// <summary>Requires <see cref="OrbitalFetchDisposition.Retrieved"/>. Never throws for bad provider content.</summary>
    public static OrbitalCandidateResult CreateCandidate(OrbitalFetchResult retrieved)
    {
        ArgumentNullException.ThrowIfNull(retrieved);
        if (retrieved.Disposition != OrbitalFetchDisposition.Retrieved || retrieved.RawBytes is null)
        {
            throw new ArgumentException("Only a Retrieved fetch result with raw bytes can become a candidate.",
                nameof(retrieved));
        }
        var raw = retrieved.RawBytes;
        if (raw.Length is 0 or > CelesTrakGpSource.MaximumRawBytes)
        {
            return Reject("orbital.raw-bound",
                FormattableString.Invariant($"raw-bytes={raw.Length} limit={CelesTrakGpSource.MaximumRawBytes}"));
        }
        var rawSha256 = Convert.ToHexString(SHA256.HashData(raw));
        if (!string.Equals(rawSha256, retrieved.RawSha256, StringComparison.Ordinal))
        {
            return Reject("orbital.raw-integrity", "raw-sha256-mismatch");
        }

        var retrievedUtc = retrieved.CompletedUtc.ToUniversalTime();
        var parsed = CelesTrakGpParser.Parse(raw, retrievedUtc);
        if (parsed.ReasonCode is not null)
        {
            return Reject(parsed.ReasonCode, parsed.Detail);
        }

        var sets = parsed.ElementSets!;
        var records = new OrbitalRecordV1[sets.Count];
        var firstIndexByHash = new Dictionary<string, int>(sets.Count, StringComparer.Ordinal);
        for (var i = 0; i < sets.Count; i++)
        {
            OrbitalRecordV1 record;
            try
            {
                record = OrbitalContract.CreateRecord(Map(sets[i]));
            }
            catch (ArgumentException exception)
            {
                return Reject("orbital.record-contract",
                    FormattableString.Invariant($"record={i} contract={SanitizeContractReason(exception.Message)}"));
            }
            if (!firstIndexByHash.TryAdd(record.RecordSha256, i))
            {
                return Reject("orbital.duplicate-record",
                    FormattableString.Invariant($"record={i} duplicate-of={firstIndexByHash[record.RecordSha256]}"));
            }
            records[i] = record;
        }

        var source = new OrbitalSnapshotSource(CelesTrakGpSource.DatasetKey, CelesTrakGpSource.ProviderKey,
            CelesTrakGpSource.UnderlyingLineage, CelesTrakGpSource.RequestUri, retrievedUtc, ProviderGeneratedUtc: null,
            CelesTrakGpSource.SanitizeValidator(retrieved.EntityTag, CelesTrakGpSource.MaximumEntityTagLength),
            CelesTrakGpSource.SanitizeValidator(retrieved.LastModified, CelesTrakGpSource.MaximumLastModifiedLength),
            rawSha256, raw.Length, Parser, Normalizer, CelesTrakGpSource.TermsUri, CelesTrakGpSource.TermsReference);
        OrbitalSnapshot snapshot;
        try
        {
            snapshot = OrbitalSnapshot.Create(records, [source], Coverage);
        }
        catch (ArgumentException exception)
        {
            return Reject("orbital.snapshot-contract",
                FormattableString.Invariant($"contract={SanitizeContractReason(exception.Message)}"));
        }
        return new OrbitalCandidateResult(snapshot, null, FormattableString.Invariant(
            $"records={snapshot.Manifest.RecordCount} objects={snapshot.Manifest.ObjectCount}"));
    }

    private static OrbitalRecordV1 Map(CelesTrakGpElementSet set) => new(string.Empty,
        new OrbitalObjectIdentity(NoradAuthority, set.CatalogNumber), CelesTrakGpSource.DatasetKey, SourceRecordId: null,
        set.ObjectName, set.ObjectId, SourceAssertedType: null, SourceAssertedStatus: null, set.EpochUtc,
        ValidFromUtc: null, ValidToUtc: null, Model,
        new OrbitalMeanElements(set.MeanMotion, set.Eccentricity, set.Inclination, set.AscendingNode,
            set.ArgumentOfPericenter, set.MeanAnomaly, set.BStar, set.MeanMotionDot, set.MeanMotionDdot,
            set.EphemerisType, set.Classification, set.ElementSetNumber, set.RevolutionNumber));

    /// <summary>The contract's reasons are short kebab codes; anything else is reported as <c>unrecognized</c>.</summary>
    internal static string SanitizeContractReason(string? reason) =>
        reason is { Length: > 0 and <= MaximumContractReasonLength } &&
        reason.All(static c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
            ? reason
            : "unrecognized";

    private static OrbitalCandidateResult Reject(string reasonCode, string detail) => new(null, reasonCode, detail);

    internal static string Sha256Hex(string descriptor) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor)));

    private static string BuildRuleDescriptor()
    {
        var builder = new StringBuilder();
        builder.Append("normalizer=celestrak-gp;schema=").Append(OrbitalContract.SchemaVersion)
            .Append(";profile=").Append(OrbitalContract.CelesTrakGpProfile).Append('\n');
        builder.Append("raw=1..").Append(CelesTrakGpSource.MaximumRawBytes.ToString(CultureInfo.InvariantCulture))
            .Append(";integrity=sha256-uppercase-hex;rejection=whole-candidate\n");
        builder.Append("identity=").Append(NoradAuthority).Append(":NORAD_CAT_ID:canonical-decimal\n");
        builder.Append("dataset=").Append(CelesTrakGpSource.DatasetKey).Append('\n');
        builder.Append("model=EARTH/TEME/UTC/SGP4;derivatives=").Append(OrbitalContract.GpDerivativeConvention).Append('\n');
        builder.Append("elements=MEAN_MOTION:rev-per-day;ECCENTRICITY;INCLINATION:deg;RA_OF_ASC_NODE:deg;")
            .Append("ARG_OF_PERICENTER:deg;MEAN_ANOMALY:deg;BSTAR:inverse-earth-radii;MEAN_MOTION_DOT:gp-field;")
            .Append("MEAN_MOTION_DDOT:gp-field;EPHEMERIS_TYPE;CLASSIFICATION_TYPE;ELEMENT_SET_NO;REV_AT_EPOCH\n");
        builder.Append("aliases=OBJECT_NAME:ObjectName;OBJECT_ID:InternationalDesignator;source-record-id=null;")
            .Append("asserted-type=null;asserted-status=null;validity=null;type-inference=none\n");
        builder.Append("records=duplicate-hash-reject;distinct-per-object-keep\n");
        builder.Append("source=provider:").Append(CelesTrakGpSource.ProviderKey).Append(";lineage:")
            .Append(CelesTrakGpSource.UnderlyingLineage).Append(";uri:").Append(CelesTrakGpSource.RequestUri.OriginalString)
            .Append(";terms:").Append(CelesTrakGpSource.TermsUri.OriginalString).Append(";terms-reference:")
            .Append(CelesTrakGpSource.TermsReference).Append(";retrieved=completed-utc;provider-generated=null\n");
        builder.Append("validators=etag:printable-ascii-max-")
            .Append(CelesTrakGpSource.MaximumEntityTagLength.ToString(CultureInfo.InvariantCulture))
            .Append(";last-modified:printable-ascii-max-")
            .Append(CelesTrakGpSource.MaximumLastModifiedLength.ToString(CultureInfo.InvariantCulture))
            .Append(";evidence-only\n");
        builder.Append("coverage=").Append(nameof(OrbitalPopulationCompleteness.CompleteForRequestedPopulation))
            .Append(";population=").Append(CelesTrakGpSource.RequestedPopulation).Append(";exclusions=")
            .Append(string.Join(',', KnownExclusions)).Append('\n');
        return builder.ToString();
    }
}
