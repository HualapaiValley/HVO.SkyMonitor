namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

internal sealed record SiloReclaimEntry(string AttemptId, SiloPhysicalAllocation? Allocation = null);

/// <summary>Charges accepted absences until an independent observation shows physical progress.</summary>
internal sealed record SiloReclaimLedger(
    IReadOnlyList<SiloReclaimEntry> Entries, SiloPhysicalHeadroom Floor, DateTimeOffset LastProgressUtc,
    long PartialDrainBytes = 0)
{
    private const int MaximumUnknownAllocations = 32;
    private const int MaximumEntries = 256;
    private static readonly TimeSpan StallWindow = TimeSpan.FromMinutes(10);

    internal static SiloReclaimLedger? Observe(SiloReclaimLedger? ledger, SiloPhysicalHeadroom current, DateTimeOffset now)
    {
        if (ledger is null || ledger.Floor.TotalBytes != current.TotalBytes) return ledger;

        var floor = ledger.Floor;
        // Silo drains trash independently of capture and journal growth on the same mount.
        if (floor.TrashBytes is > 0 && current.TrashBytes == 0) return null;

        var drained = floor.TrashBytes is { } previous && current.TrashBytes is { } trash ? Math.Max(0, previous - trash) : 0;
        // Free-space evidence is useful when a store has no visible trash.
        var gained = floor.TrashBytes is not > 0 ? Math.Max(0, current.AvailableBytes - floor.AvailableBytes) : 0;
        var progress = Math.Max(drained, gained);
        var availableFloor = progress > 0 ? current.AvailableBytes : Math.Min(floor.AvailableBytes, current.AvailableBytes);
        var nextFloor = current with { AvailableBytes = availableFloor, TrashBytes = current.TrashBytes ?? floor.TrashBytes };
        if (progress == 0) return nextFloor == floor ? ledger : ledger with { Floor = nextFloor };

        var remainingProgress = (decimal)ledger.PartialDrainBytes + progress;
        var settled = 0;
        foreach (var entry in ledger.Entries)
        {
            if (entry.Allocation is not { } allocation)
            {
                // A partial observation cannot assign trash blocks to an unmeasured exact version.
                if (current.TrashBytes is > 0) break;
                settled++;
                remainingProgress = 0;
                break;
            }
            if (allocation.Bytes > remainingProgress) break;
            remainingProgress -= allocation.Bytes;
            settled++;
        }

        return settled == ledger.Entries.Count ? null :
            new(ledger.Entries.Skip(settled).ToArray(), nextFloor, now, (long)Math.Min(long.MaxValue, remainingProgress));
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

    internal static SiloReclaimLedger? Add(SiloReclaimLedger? ledger, SiloRetentionAttempt attempt, SiloPhysicalHeadroom after)
    {
        ledger = Observe(ledger, attempt.Before, attempt.ObservedUtc!.Value);
        if (ledger?.Entries.Any(entry => entry.AttemptId == attempt.Id ||
            attempt.Allocation is not null && entry.Allocation?.Id == attempt.Allocation.Id) == true)
            return ledger;

        if (after.TotalBytes != attempt.Before.TotalBytes) after = attempt.AfterDelete ?? attempt.Before;
        var observedTrash = attempt.AfterDelete is { } observed && observed.TotalBytes == after.TotalBytes
            ? observed.TrashBytes : null;
        if (observedTrash is > 0 && after.TrashBytes == 0) return null;
        after = after with { TrashBytes = after.TrashBytes ?? observedTrash ?? ledger?.Floor.TrashBytes };
        var added = new SiloReclaimLedger([.. ledger?.Entries ?? [], new(attempt.Id, attempt.Allocation)],
            after, ledger?.LastProgressUtc ?? attempt.ObservedUtc.Value, ledger?.PartialDrainBytes ?? 0);
        // Some stores release blocks immediately. Use the post-settlement floor for deferred reclamation.
        return after.TrashBytes is not > 0 && after.AvailableBytes > attempt.Before.AvailableBytes
            ? Observe(added with { Floor = attempt.Before with { TrashBytes = after.TrashBytes } }, after, attempt.ObservedUtc.Value)
            : added;
    }
}
