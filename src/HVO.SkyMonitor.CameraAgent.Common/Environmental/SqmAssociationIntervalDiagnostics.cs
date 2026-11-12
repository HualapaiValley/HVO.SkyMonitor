using System.Diagnostics;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

internal static class SqmAssociationIntervalDiagnostics
{
    // Association records have no free-form diagnostic metadata. Keep this out of persisted identities and provenance.
    internal const string PolicyVersion = "sqm-validity-interval-diagnostics-v1";

    // Windows are source-configurable: one existing validity window preserves the current maximum preceding gap.
    // A universal seconds value would widen or narrow some sources. Fresh/Stale selection remains independent and unchanged.
    internal const int MaximumPreExposureGapInExistingValidityWindows = 1;

    internal static ActivityEvent? Create(Guid captureId, EnvironmentalObservationKind kind,
        LocalEnvironmentalAssociationStatus status, EnvironmentalObservationFactV1? selected,
        DateTimeOffset exposureFromUtc, DateTimeOffset exposureThroughUtc)
    {
        if (kind != EnvironmentalObservationKind.SkyQuality ||
            selected is not null && selected.Source.Provider != "SkyQualityMeter") return null;

        var tags = new ActivityTagsCollection
        {
            ["capture_id"] = captureId.ToString("N"),
            ["status"] = status.ToString(),
            ["relationship"] = "unavailable",
            ["exposure_from_unix_ms"] = exposureFromUtc.ToUnixTimeMilliseconds(),
            ["exposure_through_unix_ms"] = exposureThroughUtc.ToUnixTimeMilliseconds()
        };
        if (selected is not null)
        {
            tags["relationship"] = "interval-unknown";
            tags["validity_covers_exposure"] = selected.ValidFromUtc <= exposureFromUtc && selected.ValidThroughUtc >= exposureThroughUtc;
            tags["freshness_covers_exposure"] = selected.StaleAfterUtc >= exposureThroughUtc;
            if (selected.ObservedFromUtc is { } from && selected.ObservedThroughUtc is { } through)
            {
                tags["sample_from_unix_ms"] = from.ToUnixTimeMilliseconds();
                tags["sample_through_unix_ms"] = through.ToUnixTimeMilliseconds();
                tags["integration_ms"] = (through - from).TotalMilliseconds;
                tags["maximum_pre_exposure_gap_ms"] =
                    ((selected.ValidThroughUtc - through) * MaximumPreExposureGapInExistingValidityWindows).TotalMilliseconds;
                if (through <= exposureFromUtc)
                {
                    // Half-open intervals that only touch have zero gap and do not overlap.
                    tags["relationship"] = "preceding";
                    tags["gap_ms"] = (exposureFromUtc - through).TotalMilliseconds;
                }
                else if (from >= exposureThroughUtc) tags["relationship"] = "following";
                else tags["relationship"] = from == through ? "instantaneous" : "overlapping";
            }
        }
        return new ActivityEvent("environment.association.interval", tags: tags);
    }
}
