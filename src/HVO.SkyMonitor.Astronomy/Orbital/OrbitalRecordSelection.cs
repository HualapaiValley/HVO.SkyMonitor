namespace HVO.SkyMonitor.Astronomy;

public sealed record OrbitalUtcInterval(DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    public void Validate()
    {
        OrbitalContract.Require(OrbitalContract.Utc(StartUtc) && OrbitalContract.Utc(EndUtc) && StartUtc <= EndUtc, "invalid-utc-interval");
    }
}

public sealed record OrbitalSourcePriority(string SourceDataset, int Priority);

/// <summary>A complete immutable policy. Its age envelope is operational, not an orbit covariance.</summary>
public sealed class OrbitalSelectionPolicy
{
    public const string CurrentVersion = "hvo-gp-selection-v1";
    public string Version { get; } = CurrentVersion;
    public double MinimumEpochOffsetHours { get; } = -24;
    public double StaleAbsoluteAgeHours { get; } = 48;
    public double MaximumEpochOffsetHours { get; } = 168;
    public IReadOnlyList<OrbitalSourcePriority> Sources { get; }
    public bool AllowStaleDiagnostics { get; }
    public string IdentitySha256 { get; }

    public OrbitalSelectionPolicy(IEnumerable<OrbitalSourcePriority> sources, bool allowStaleDiagnostics = false)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var items = sources.Take(OrbitalContract.MaximumSources + 1).ToArray();
        OrbitalContract.Require(items.Length is > 0 and <= OrbitalContract.MaximumSources &&
            items.All(static s => s is not null && OrbitalContract.Dataset(s.SourceDataset) && s.Priority >= 0) &&
            items.Select(static s => s.SourceDataset).Distinct(StringComparer.Ordinal).Count() == items.Length, "selection-source-ranking");
        Sources = Array.AsReadOnly(items.OrderBy(static s => s.SourceDataset, StringComparer.Ordinal).ToArray());
        AllowStaleDiagnostics = allowStaleDiagnostics;
        IdentitySha256 = OrbitalContract.Hash(new { Version, MinimumEpochOffsetHours, StaleAbsoluteAgeHours, MaximumEpochOffsetHours, AllowStaleDiagnostics, Sources });
    }
}

public enum OrbitalSelectionEligibility { Eligible, MissingObject, UnapprovedSource, OutsidePolicyWindow, OutsideSourceValidity, StaleWithheld }
public enum OrbitalElementFreshness { Fresh, Stale, Expired, FutureBeyondPolicy }
public enum OrbitalSourceValidity { Unknown, PartlyKnown, Known }

public sealed record OrbitalSelectionCandidate(string RecordSha256, OrbitalSelectionEligibility Eligibility,
    OrbitalElementFreshness Freshness, double StartEpochAgeHours, double EndEpochAgeHours, OrbitalSourceValidity SourceValidity);

public sealed record OrbitalRecordSelection(string SnapshotManifestSha256, string PolicySha256,
    OrbitalObjectIdentity ObjectIdentity, OrbitalUtcInterval RequestedInterval, OrbitalRecordV1? SelectedRecord,
    OrbitalSelectionEligibility Eligibility, IReadOnlyList<OrbitalSelectionCandidate> Candidates, string SelectionRule);

/// <summary>Deterministic pure selection; a selected record remains fixed for the whole requested interval.</summary>
public static class OrbitalRecordSelector
{
    public const string SelectionRule = "approved-source-priority-midpoint-distance-newest-epoch-ordinal-record-sha256-v1";

    public static OrbitalRecordSelection Select(OrbitalSnapshot snapshot, OrbitalObjectIdentity objectIdentity,
        OrbitalUtcInterval interval, OrbitalSelectionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(interval);
        ArgumentNullException.ThrowIfNull(policy);
        OrbitalContract.Require(OrbitalContract.IsObjectIdentity(objectIdentity), "object-identity");
        interval.Validate();
        var ranks = policy.Sources.ToDictionary(static s => s.SourceDataset, static s => s.Priority, StringComparer.Ordinal);
        var midpointTicks = interval.StartUtc.UtcTicks + (interval.EndUtc.UtcTicks - interval.StartUtc.UtcTicks) / 2;
        var assessed = snapshot.RecordsFor(objectIdentity).Select(record =>
        {
            var start = (interval.StartUtc - record.EpochUtc).TotalHours;
            var end = (interval.EndUtc - record.EpochUtc).TotalHours;
            var freshness = end > policy.MaximumEpochOffsetHours ? OrbitalElementFreshness.Expired :
                start < policy.MinimumEpochOffsetHours ? OrbitalElementFreshness.FutureBeyondPolicy :
                Math.Max(Math.Abs(start), Math.Abs(end)) > policy.StaleAbsoluteAgeHours ? OrbitalElementFreshness.Stale : OrbitalElementFreshness.Fresh;
            var eligibility = !ranks.ContainsKey(record.SourceDataset) ? OrbitalSelectionEligibility.UnapprovedSource :
                start < policy.MinimumEpochOffsetHours || end > policy.MaximumEpochOffsetHours ? OrbitalSelectionEligibility.OutsidePolicyWindow :
                record.ValidFromUtc is { } from && interval.StartUtc < from || record.ValidToUtc is { } to && interval.EndUtc > to
                    ? OrbitalSelectionEligibility.OutsideSourceValidity :
                freshness == OrbitalElementFreshness.Stale && !policy.AllowStaleDiagnostics ? OrbitalSelectionEligibility.StaleWithheld : OrbitalSelectionEligibility.Eligible;
            var validity = record.ValidFromUtc.HasValue && record.ValidToUtc.HasValue ? OrbitalSourceValidity.Known :
                record.ValidFromUtc.HasValue || record.ValidToUtc.HasValue ? OrbitalSourceValidity.PartlyKnown : OrbitalSourceValidity.Unknown;
            return (Record: record, Facts: new OrbitalSelectionCandidate(record.RecordSha256, eligibility, freshness, start, end, validity));
        }).ToArray();
        var selected = assessed.Where(static a => a.Facts.Eligibility == OrbitalSelectionEligibility.Eligible)
            .OrderBy(a => ranks[a.Record.SourceDataset])
            .ThenBy(a => Math.Abs(a.Record.EpochUtc.UtcTicks - midpointTicks))
            .ThenByDescending(static a => a.Record.EpochUtc)
            .ThenBy(static a => a.Record.RecordSha256, StringComparer.Ordinal).FirstOrDefault().Record;
        var outcome = selected is not null ? OrbitalSelectionEligibility.Eligible : assessed.Length == 0
            ? OrbitalSelectionEligibility.MissingObject : assessed.Select(static a => a.Facts.Eligibility).Order().First();
        return new(snapshot.Manifest.ManifestSha256, policy.IdentitySha256, objectIdentity, interval, selected, outcome,
            Array.AsReadOnly(assessed.Select(static a => a.Facts).OrderBy(static a => a.RecordSha256, StringComparer.Ordinal).ToArray()), SelectionRule);
    }
}
