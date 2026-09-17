namespace HVO.SkyMonitor.LogicHost.Tests.Elastic;

/// <summary>
/// Test-only decision oracle for #854. It defines the heterogeneous allocation model before the production
/// autoscaler adopts it: maximum matching over queued jobs and currently available registered slots, followed by
/// entitlement and deadline calculations over only the unmatched work.
/// </summary>
internal static class ElasticFleetAllocationOracle
{
    internal sealed record Job(string Id, string Recipe, long InputBytes, TimeSpan Age);

    internal sealed record Registration(
        string Id,
        IReadOnlySet<string> Recipes,
        long MaxTransferBytes,
        int MaxConcurrency,
        int OccupiedSlots);

    internal sealed record Result(
        IReadOnlyList<string> MatchedJobs,
        IReadOnlyList<string> UncoveredJobs,
        IReadOnlyList<string> ProvisionableJobs,
        TimeSpan OldestProvisionableAge,
        int RequiredInstances,
        int CompatibilityChecks);

    internal static Result Allocate(
        IReadOnlyList<Job> jobs,
        IReadOnlyList<Registration> registrations,
        int? remainingEntitlementConcurrency,
        int templateConcurrency)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentOutOfRangeException.ThrowIfLessThan(templateConcurrency, 1);
        if (remainingEntitlementConcurrency is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(remainingEntitlementConcurrency));
        }
        if (jobs.Any(job => string.IsNullOrWhiteSpace(job.Id) || string.IsNullOrWhiteSpace(job.Recipe)
                            || job.InputBytes < 0 || job.Age < TimeSpan.Zero)
            || jobs.Select(job => job.Id).Distinct(StringComparer.Ordinal).Count() != jobs.Count)
        {
            throw new ArgumentException("Jobs require unique nonempty IDs, recipes, non-negative bytes, and non-negative ages.", nameof(jobs));
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

        var slots = registrations
            .SelectMany(registration => Enumerable.Range(
                    0,
                    Math.Max(0, registration.MaxConcurrency - Math.Clamp(
                        registration.OccupiedSlots,
                        0,
                        Math.Max(0, registration.MaxConcurrency))))
                .Select(index => new Slot($"{registration.Id}:{index}", registration.Recipes, registration.MaxTransferBytes)))
            .ToArray();
        var assignedJobBySlot = new int?[slots.Length];
        var checks = 0;

        bool CanClaim(Slot slot, Job job)
        {
            checks++;
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

        // Constrained jobs first reduces augmenting work but correctness comes from reassigning earlier matches.
        var order = jobs
            .Select((job, index) => new
            {
                Index = index,
                CompatibleSlots = slots.Count(slot => slot.MaxTransferBytes >= job.InputBytes && slot.Recipes.Contains(job.Recipe)),
                job.Age,
                job.Id
            })
            .OrderBy(item => item.CompatibleSlots)
            .ThenByDescending(item => item.Age)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        foreach (var item in order)
        {
            _ = Match(item.Index, new bool[slots.Length]);
        }

        var matchedIndexes = assignedJobBySlot.Where(index => index.HasValue).Select(index => index!.Value).ToHashSet();
        var matched = matchedIndexes.Select(index => jobs[index].Id).Order(StringComparer.Ordinal).ToArray();
        var uncovered = jobs.Where((_, index) => !matchedIndexes.Contains(index))
            .OrderByDescending(job => job.Age)
            .ThenBy(job => job.Id, StringComparer.Ordinal)
            .ToArray();
        var headroom = remainingEntitlementConcurrency ?? int.MaxValue;
        var provisionable = uncovered.Take(headroom).ToArray();
        return new Result(
            matched,
            uncovered.Select(job => job.Id).ToArray(),
            provisionable.Select(job => job.Id).ToArray(),
            provisionable.Length == 0 ? TimeSpan.Zero : provisionable.Max(job => job.Age),
            (int)Math.Ceiling(provisionable.Length / (double)templateConcurrency),
            checks);
    }

    private sealed record Slot(string Id, IReadOnlySet<string> Recipes, long MaxTransferBytes);
}
