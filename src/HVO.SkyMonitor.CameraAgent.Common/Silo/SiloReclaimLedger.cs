namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

internal sealed record SiloReclaimEntry(string AttemptId, SiloPhysicalAllocation? Allocation = null,
    long? UpperBoundBytes = null);

/// <summary>Charges accepted absences until an independent observation shows physical progress.</summary>
internal sealed record SiloReclaimLedger(
    IReadOnlyList<SiloReclaimEntry> Entries, SiloPhysicalHeadroom Floor, DateTimeOffset LastProgressUtc,
    long PartialDrainBytes = 0)
{
    private const int MaximumUnknownAllocations = 32;
    private const int MaximumEntries = 256;
    private const int EstimatedBlockBytes = 4096;
    private static readonly TimeSpan StallWindow = TimeSpan.FromMinutes(10);

    // A rounded logical size helps apportion a free-space gain. Only measured allocation counts toward the byte limit.
    internal static long EstimateUpperBound(long length) =>
        length >= long.MaxValue - EstimatedBlockBytes
            ? long.MaxValue : Math.Max(EstimatedBlockBytes, (length + EstimatedBlockBytes - 1) / EstimatedBlockBytes * EstimatedBlockBytes);

    internal static SiloReclaimLedger? Observe(SiloReclaimLedger? ledger, SiloPhysicalHeadroom current, DateTimeOffset now) =>
        Observe(ledger, current, current, now).Ledger;

    private static (SiloReclaimLedger? Ledger, long UnusedFreeBytes) Observe(SiloReclaimLedger? ledger,
        SiloPhysicalHeadroom previous, SiloPhysicalHeadroom current, DateTimeOffset now)
    {
        var floor = ledger?.Floor ?? previous;
        if (floor.TotalBytes != current.TotalBytes) return (ledger, 0);

        // An empty trash sample accounts for all prior entries independently of other writes on the mount.
        if (floor.TrashBytes is > 0 && current.TrashBytes == 0) return (null, 0);
        var gained = floor.TrashBytes is not > 0 && current.TrashBytes is not > 0
            ? Math.Max(0, current.AvailableBytes - floor.AvailableBytes) : 0;
        if (ledger is null) return (null, gained);

        var drained = floor.TrashBytes is { } priorTrash && current.TrashBytes is { } trash ? Math.Max(0, priorTrash - trash) : 0;
        var progress = Math.Max(drained, gained);
        var availableFloor = progress > 0 ? current.AvailableBytes : Math.Min(floor.AvailableBytes, current.AvailableBytes);
        var nextFloor = current with { AvailableBytes = availableFloor, TrashBytes = current.TrashBytes ?? floor.TrashBytes };
        if (progress == 0) return (nextFloor == floor ? ledger : ledger with { Floor = nextFloor }, 0);

        var remainingProgress = (decimal)ledger.PartialDrainBytes + progress;
        var settled = 0;
        var settledUnknown = false;
        foreach (var entry in ledger.Entries)
        {
            var bound = entry.Allocation?.Bytes ?? entry.UpperBoundBytes;
            if (bound is > 0 && bound <= remainingProgress)
            {
                settled++;
                remainingProgress -= bound.Value;
                settledUnknown |= entry.Allocation is null;
                continue;
            }

            // A net gain can be smaller than the rounded logical size because of block layout or other writes.
            // Credit at least one unmeasured entry, or the final near-covered estimate, without spending the gain twice.
            if (entry.Allocation is null && gained > 0 && remainingProgress > 0 &&
                (!settledUnknown || bound is { } estimate && remainingProgress >= estimate / 2m &&
                    estimate - remainingProgress <= EstimatedBlockBytes))
            {
                settled++;
                remainingProgress = 0;
            }
            break;
        }

        return settled == ledger.Entries.Count ? (null, (long)Math.Min(gained, remainingProgress)) :
            (new(ledger.Entries.Skip(settled).ToArray(), nextFloor, now > ledger.LastProgressUtc ? now : ledger.LastProgressUtc,
                (long)Math.Min(long.MaxValue, remainingProgress)), 0);
    }

    internal static bool CanDelete(SiloReclaimLedger? ledger, SiloPhysicalHeadroom physical, long captureHourBytes,
        long? allocation, DateTimeOffset now)
    {
        var limit = Math.Min(physical.TotalBytes / 20, captureHourBytes);
        var bytes = (decimal)(allocation ?? 0);
        if (ledger is null) return bytes <= limit;
        if (ledger.Floor.TotalBytes != physical.TotalBytes || now - ledger.LastProgressUtc >= StallWindow ||
            ledger.Entries.Count >= MaximumEntries ||
            (allocation is null && ledger.Entries.Count(static entry => entry.Allocation is null) >= MaximumUnknownAllocations))
            return false;
        return bytes + ledger.Entries.Sum(static entry => (decimal)(entry.Allocation?.Bytes ?? 0)) <= limit;
    }

    internal static SiloReclaimLedger? Add(SiloReclaimLedger? ledger, SiloRetentionAttempt attempt, SiloPhysicalHeadroom after,
        DateTimeOffset now)
    {
        var before = attempt.Before;
        var observed = attempt.ObservedUtc!.Value;
        var alreadyCharged = ledger?.Entries.Any(entry => entry.AttemptId == attempt.Id ||
            attempt.Allocation is not null && entry.Allocation?.Id == attempt.Allocation.Id) == true;
        ledger = Observe(ledger, before, observed);

        var observedAfterDelete = attempt.AfterDelete is { } sample && sample.TotalBytes == before.TotalBytes ? sample : (SiloPhysicalHeadroom?)null;
        var postDelete = observedAfterDelete ?? before;
        if (after.TotalBytes != before.TotalBytes) after = postDelete;
        var (prior, firstFreeBytes) = Observe(ledger, before, postDelete, observed);
        var (pending, lastFreeBytes) = Observe(prior, postDelete, after, now);
        if (alreadyCharged || observedAfterDelete?.TrashBytes is > 0 && after.TrashBytes == 0)
            return pending;

        // Only the free-space gain left over after settling prior deletes can credit the new entry.
        var freeBytes = after.TrashBytes is > 0 ? 0 : (long)Math.Min(long.MaxValue, (decimal)firstFreeBytes + lastFreeBytes);
        if (pending is null && freeBytes >= (attempt.Allocation?.Bytes ?? 1))
            return null;

        after = after with { TrashBytes = after.TrashBytes ?? postDelete.TrashBytes ?? pending?.Floor.TrashBytes };
        return new([.. pending?.Entries ?? [], new(attempt.Id, attempt.Allocation, attempt.ReclaimUpperBoundBytes)], after,
            pending?.LastProgressUtc ?? (prior is not null || lastFreeBytes > 0 ? now : observed), pending?.PartialDrainBytes ?? freeBytes);
    }
}
