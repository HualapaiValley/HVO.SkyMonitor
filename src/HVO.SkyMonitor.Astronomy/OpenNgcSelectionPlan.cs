using System.Runtime.CompilerServices;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>Bounded query-plan cache scoped to an immutable catalog, with no per-frame catalog scan.</summary>
internal sealed record OpenNgcSelectionPlan(IReadOnlyList<DeepSkyObject> Objects, int MatchCount)
{
    private static readonly ConditionalWeakTable<IDeepSkyCatalog, Plans> Cache = new();

    internal static OpenNgcSelectionPlan For(IDeepSkyCatalog catalog, OpenNgcDeepSkyOptions options)
    {
        var plans = Cache.GetValue(catalog, static _ => new());
        lock (plans)
        {
            if (plans.Values.TryGetValue(options, out var cached)) return cached;
            var matches = catalog.Query(options.ToQuery(int.MaxValue)).Objects
                .Where(static item => !item.IsStellar || item.MessierNumber is not null)
                .OrderBy(static item => item.MessierNumber ?? int.MaxValue)
                .ThenByDescending(static item => ProjectedDeepSkyObject.IsFeatured(item.MessierNumber, item.CaldwellNumber, item.CommonName))
                .ThenByDescending(static item => item.MajorAxisArcminutes ?? 0)
                .ThenBy(static item => item.VMagnitude ?? double.PositiveInfinity)
                .ThenBy(static item => item.Id, StringComparer.Ordinal).ToArray();
            var plan = new OpenNgcSelectionPlan(Array.AsReadOnly(matches.Take(options.MaximumObjects).ToArray()), matches.Length);
            if (plans.Values.Count == 32) plans.Values.Remove(plans.Order.Dequeue());
            plans.Values.Add(options, plan);
            plans.Order.Enqueue(options);
            return plan;
        }
    }

    private sealed class Plans
    {
        internal Dictionary<OpenNgcDeepSkyOptions, OpenNgcSelectionPlan> Values { get; } = [];
        internal Queue<OpenNgcDeepSkyOptions> Order { get; } = new();
    }
}
