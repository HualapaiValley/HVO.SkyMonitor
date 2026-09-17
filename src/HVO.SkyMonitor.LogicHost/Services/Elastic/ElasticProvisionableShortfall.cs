namespace HVO.SkyMonitor.LogicHost.Services.Elastic;

/// <summary>Unmatched work that current entitlement headroom permits a newly provisioned runner to claim.</summary>
internal sealed record ElasticProvisionableShortfall(IReadOnlyList<Guid> JobIds, TimeSpan OldestAge)
{
    internal static readonly ElasticProvisionableShortfall Empty = new([], TimeSpan.Zero);

    public int Count => JobIds.Count;
}

internal static class ElasticProvisionableShortfallSelector
{
    internal sealed record Job(Guid Id, Guid ObservatoryId, DateTimeOffset? AvailableSince);

    internal static ElasticProvisionableShortfall Select(
        IReadOnlyList<Job> unmatchedJobs,
        IReadOnlyDictionary<Guid, int>? remainingEntitlement,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(unmatchedJobs);
        if (unmatchedJobs.Any(job => job.Id == Guid.Empty || job.ObservatoryId == Guid.Empty)
            || unmatchedJobs.Select(job => job.Id).Distinct().Count() != unmatchedJobs.Count)
        {
            throw new ArgumentException("Unmatched jobs require unique nonempty job and observatory IDs.", nameof(unmatchedJobs));
        }
        if (remainingEntitlement?.Any(pair => pair.Key == Guid.Empty || pair.Value < 0) == true)
        {
            throw new ArgumentOutOfRangeException(nameof(remainingEntitlement));
        }
        if (remainingEntitlement is not null && unmatchedJobs.Any(job => !remainingEntitlement.ContainsKey(job.ObservatoryId)))
        {
            throw new ArgumentException("Every unmatched observatory requires explicit remaining entitlement.", nameof(remainingEntitlement));
        }

        var remaining = remainingEntitlement?.ToDictionary(pair => pair.Key, pair => pair.Value);
        var provisionable = unmatchedJobs
            .OrderBy(job => job.AvailableSince ?? DateTimeOffset.MaxValue)
            .ThenBy(job => job.Id)
            .Where(job =>
            {
                if (remaining is null)
                {
                    return true;
                }
                var headroom = remaining[job.ObservatoryId];
                if (headroom == 0)
                {
                    return false;
                }
                if (headroom != int.MaxValue)
                {
                    remaining[job.ObservatoryId] = headroom - 1;
                }
                return true;
            })
            .ToArray();
        if (provisionable.Length == 0)
        {
            return ElasticProvisionableShortfall.Empty;
        }
        var oldest = provisionable[0].AvailableSince is { } availableSince && availableSince < now
            ? now - availableSince
            : TimeSpan.Zero;
        return new ElasticProvisionableShortfall(provisionable.Select(job => job.Id).ToArray(), oldest);
    }
}
