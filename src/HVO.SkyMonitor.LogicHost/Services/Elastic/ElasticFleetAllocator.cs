using System.Diagnostics;

namespace HVO.SkyMonitor.LogicHost.Services.Elastic;

/// <summary>Allocates queued work to the currently available slots of heterogeneous registered runners.</summary>
internal static class ElasticFleetAllocator
{
    internal const int MaximumJobs = 4096;
    internal const int MaximumSlots = 4096;
    internal const long MaximumCompatibilityChecks = 100_000_000;
    internal static readonly TimeSpan MaximumElapsed = TimeSpan.FromSeconds(5);

    internal sealed record Job(Guid Id, string Recipe, long InputBytes, DateTimeOffset? AvailableSince);

    internal sealed record Registration(
        string Id,
        IReadOnlySet<string> Recipes,
        long MaxTransferBytes,
        int MaxConcurrency,
        int OccupiedSlots);

    internal sealed record Result(
        IReadOnlyList<Guid> MatchedJobIds,
        IReadOnlyList<Guid> UnmatchedJobIds,
        int AvailableSlots,
        long CompatibilityChecks,
        TimeSpan Elapsed);

    internal static Result Allocate(IReadOnlyList<Job> jobs, IReadOnlyList<Registration> registrations)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(registrations);
        if (jobs.Count > MaximumJobs)
        {
            throw new InvalidOperationException($"Elastic allocation candidate limit exceeded: {jobs.Count} jobs exceeds {MaximumJobs}.");
        }
        if (jobs.Any(job => job.Id == Guid.Empty || string.IsNullOrWhiteSpace(job.Recipe) || job.InputBytes < 0)
            || jobs.Select(job => job.Id).Distinct().Count() != jobs.Count)
        {
            throw new ArgumentException("Jobs require unique nonempty IDs, recipes, and non-negative input bytes.", nameof(jobs));
        }
        if (registrations.Any(registration => string.IsNullOrWhiteSpace(registration.Id)
                                               || registration.MaxTransferBytes < 0
                                               || registration.MaxConcurrency < 0
                                               || registration.OccupiedSlots < 0
                                               || registration.Recipes.Any(string.IsNullOrWhiteSpace))
            || registrations.Select(registration => registration.Id).Distinct(StringComparer.Ordinal).Count() != registrations.Count)
        {
            throw new ArgumentException("Registrations require unique nonempty IDs and non-negative capacity values.", nameof(registrations));
        }

        var slotCount = registrations.Sum(registration => Math.Max(0,
            registration.MaxConcurrency - Math.Clamp(registration.OccupiedSlots, 0, registration.MaxConcurrency)));
        if (slotCount > MaximumSlots)
        {
            throw new InvalidOperationException($"Elastic allocation candidate limit exceeded: {slotCount} slots exceeds {MaximumSlots}.");
        }

        var started = Stopwatch.GetTimestamp();
        var slots = registrations.OrderBy(registration => registration.Id, StringComparer.Ordinal)
            .SelectMany(registration => Enumerable.Range(0, Math.Max(0,
                    registration.MaxConcurrency - Math.Clamp(registration.OccupiedSlots, 0, registration.MaxConcurrency)))
                .Select(index => new Slot($"{registration.Id}:{index}", registration.Recipes, registration.MaxTransferBytes)))
            .ToArray();
        var assignedJobBySlot = new int?[slots.Length];
        long checks = 0;

        bool CanClaim(Slot slot, Job job)
        {
            checks++;
            if (checks > MaximumCompatibilityChecks)
            {
                throw new InvalidOperationException($"Elastic allocation compatibility limit exceeded: more than {MaximumCompatibilityChecks} checks.");
            }
            if (Stopwatch.GetElapsedTime(started) > MaximumElapsed)
            {
                throw new TimeoutException($"Elastic allocation elapsed limit exceeded: more than {MaximumElapsed}.");
            }
            return slot.MaxTransferBytes >= job.InputBytes && slot.Recipes.Contains(job.Recipe);
        }

        bool Match(int jobIndex, bool[] visited)
        {
            for (var slotIndex = 0; slotIndex < slots.Length; slotIndex++)
            {
                if (visited[slotIndex] || !CanClaim(slots[slotIndex], jobs[jobIndex]))
                {
                    continue;
                }
                visited[slotIndex] = true;
                if (assignedJobBySlot[slotIndex] is not { } assigned || Match(assigned, visited))
                {
                    assignedJobBySlot[slotIndex] = jobIndex;
                    return true;
                }
            }
            return false;
        }

        // Constrained jobs go first; augmenting paths preserve an earlier job unless it can be reassigned.
        var order = jobs.Select((job, index) => new
        {
            Index = index,
            CompatibleSlots = slots.Count(slot => CanClaim(slot, job)),
            job.AvailableSince,
            job.Id
        })
            .OrderBy(item => item.CompatibleSlots)
            .ThenBy(item => item.AvailableSince ?? DateTimeOffset.MaxValue)
            .ThenBy(item => item.Id)
            .ToArray();
        foreach (var item in order)
        {
            _ = Match(item.Index, new bool[slots.Length]);
        }

        var matchedIndexes = assignedJobBySlot.Where(index => index.HasValue).Select(index => index!.Value).ToHashSet();
        var matched = matchedIndexes.Select(index => jobs[index].Id).Order().ToArray();
        var unmatched = jobs.Where((_, index) => !matchedIndexes.Contains(index))
            .OrderBy(job => job.AvailableSince ?? DateTimeOffset.MaxValue)
            .ThenBy(job => job.Id)
            .Select(job => job.Id)
            .ToArray();
        return new Result(matched, unmatched, slots.Length, checks, Stopwatch.GetElapsedTime(started));
    }

    private sealed record Slot(string Id, IReadOnlySet<string> Recipes, long MaxTransferBytes);
}
