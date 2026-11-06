namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;

/// <summary>The shared snapshot outcome rule used by S2 and verified by S3.</summary>
public static class SwpcSnapshotComparison
{
    /// <summary>
    /// Compares snapshots for the same product: equal hashes take precedence;
    /// only strictly older, non-null source stamps make a changed body stale.
    /// </summary>
    public static SwpcFetchOutcome Classify(SwpcSnapshot? lkg, SwpcSnapshot candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (lkg is null) return SwpcFetchOutcome.Updated;
        if (string.Equals(lkg.Sha256, candidate.Sha256, StringComparison.Ordinal))
            return SwpcFetchOutcome.Unchanged;
        return lkg.InFileStampUtc is { } previous && candidate.InFileStampUtc is { } incoming && incoming < previous
            ? SwpcFetchOutcome.StaleSnapshot
            : SwpcFetchOutcome.Updated;
    }
}
