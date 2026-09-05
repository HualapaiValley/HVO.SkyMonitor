using HVO.SkyMonitor.LogicHost.Configuration;

namespace HVO.SkyMonitor.LogicHost.Services.Elastic;

internal sealed record ElasticScalingInput(
    int Backlog,
    TimeSpan OldestBacklogAge,
    int Running,
    int Starting,
    int Idle,
    TimeSpan LongestIdle,
    int? EntitledConcurrency,
    int InstanceMinutesToday);

internal sealed record ElasticScalingDecision(int Provision, int Retire, string Reason)
{
    public static readonly ElasticScalingDecision Steady = new(0, 0, "steady");
}

/// <summary>
/// Pure autoscaling decision (#430): desired instances follow provider-eligible backlog and per-instance
/// concurrency, bounded by observatory entitlements, the instance maximum, the warm minimum, and the daily
/// instance-minute limit; a cold start is only worth taking when the oldest backlog can still be served within the
/// queue deadline, otherwise the work is retained locally as backlog; idle instances above the warm minimum are
/// retired after the scale-to-zero delay.
/// </summary>
internal static class ElasticScalingPolicy
{
    public const string ReasonBacklog = "backlog";
    public const string ReasonWarmMinimum = "warm-minimum";
    public const string ReasonIdle = "idle";
    public const string ReasonDailyLimit = "daily-limit";
    public const string ReasonColdStartExceedsDeadline = "cold-start-exceeds-deadline";
    public const string ReasonEntitlementBound = "entitlement-bound";

    public static ElasticScalingDecision Decide(CentralElasticProviderOptions options, ElasticScalingInput input, TimeSpan startupEstimate)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(input);
        var perInstance = Math.Max(1, options.MaxConcurrencyPerInstance);
        var active = input.Running + input.Starting;
        var needed = (int)Math.Ceiling(input.Backlog / (double)perInstance);
        var entitlementBound = false;
        if (input.EntitledConcurrency is { } entitled)
        {
            var byEntitlement = (int)Math.Ceiling(Math.Max(0, entitled) / (double)perInstance);
            if (byEntitlement < needed)
            {
                needed = byEntitlement;
                entitlementBound = true;
            }
        }
        var desired = Math.Clamp(Math.Max(needed, options.MinWarmInstances), 0, options.MaxInstances);
        if (desired > active)
        {
            if (options.MaxInstanceMinutesPerDay > 0 && input.InstanceMinutesToday >= options.MaxInstanceMinutesPerDay)
            {
                return new ElasticScalingDecision(0, 0, ReasonDailyLimit);
            }
            var warmShortfall = Math.Max(0, Math.Min(options.MinWarmInstances, options.MaxInstances) - active);
            if (input.Backlog > 0 && needed > active && startupEstimate + input.OldestBacklogAge > options.QueueDeadline)
            {
                // Provider startup cannot meet the deadline for the oldest work: keep it local, only top up the warm minimum.
                return new ElasticScalingDecision(warmShortfall, 0, ReasonColdStartExceedsDeadline);
            }
            var reason = needed > active ? (entitlementBound ? ReasonEntitlementBound : ReasonBacklog) : ReasonWarmMinimum;
            return new ElasticScalingDecision(desired - active, 0, reason);
        }
        if (desired < active && input.Idle > 0 && input.LongestIdle >= options.ScaleToZeroAfter)
        {
            var retire = Math.Min(input.Idle, active - desired);
            return retire > 0 ? new ElasticScalingDecision(0, retire, ReasonIdle) : ElasticScalingDecision.Steady;
        }
        return entitlementBound && input.Backlog > needed * perInstance
            ? new ElasticScalingDecision(0, 0, ReasonEntitlementBound)
            : ElasticScalingDecision.Steady;
    }
}
