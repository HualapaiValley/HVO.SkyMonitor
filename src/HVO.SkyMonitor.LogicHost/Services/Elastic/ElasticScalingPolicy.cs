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
    int InstanceMinutesToday,
    int InFlight = 0,
    int WarmInstances = 0,
    int? Capacity = null,
    int CleanupBacklog = 0,
    int IncompatibleActive = 0,
    int UncoveredBacklog = 0);

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
    /// <summary>Applied by the autoscaler, not the policy: the provider cannot describe an instance, so nothing is provisioned.</summary>
    public const string ReasonInstanceUndescribed = "instance-capabilities-unknown";
    /// <summary>An instance unable to claim the queued recipes is retired at the instance limit to make room for one that can.</summary>
    public const string ReasonIncompatibleReplacement = "incompatible-replacement";
    /// <summary>Queued work no active instance can claim while the instance limit is full of instances that serve other work.</summary>
    public const string ReasonInstanceLimit = "instance-limit";

    public static ElasticScalingDecision Decide(CentralElasticProviderOptions options, ElasticScalingInput input, TimeSpan startupEstimate)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(input);
        var perInstance = Math.Max(1, options.MaxConcurrencyPerInstance);
        var active = input.Running + input.Starting;
        if (options.MaxInstanceMinutesPerDay > 0 && input.InstanceMinutesToday >= options.MaxInstanceMinutesPerDay && active > 0)
        {
            // The daily budget bounds existing capacity too: every instance, registering ones included, drains and
            // stops until the day rolls over.
            return new ElasticScalingDecision(0, active, ReasonDailyLimit);
        }
        // Demand counts work already executing on the instances, so occupied capacity does not mask queued backlog.
        // Existing instances are sized by the concurrency they actually registered (Capacity), not by the configured
        // per-instance value: an adopted single-slot instance never masks demand a four-slot configuration expects.
        // Below capacity the count is a lower bound: the autoscaler applies idle scale-down only to instances whose
        // registered slots the demand does not still need, so heterogeneous adopted instances are never over-retired.
        // Instances registered without the queued recipes (adopted before a placement or capability change) are active
        // for the instance limit and the daily budget but count for nothing toward the demand: only compatible active
        // instances and their registered capacity cover it.
        var incompatible = Math.Clamp(input.IncompatibleActive, 0, active);
        var compatibleActive = active - incompatible;
        var capacity = input.Capacity ?? compatibleActive * perInstance;
        // Demand the existing capacity already covers never asks for more than the compatible active instances,
        // whatever the configured per-instance size now is (an adopted ten-slot instance under a one-slot configuration is enough).
        int InstancesFor(int concurrency) => concurrency > capacity
            ? compatibleActive + (int)Math.Ceiling((concurrency - capacity) / (double)perInstance)
            : Math.Min(compatibleActive, (int)Math.Ceiling(concurrency / (double)perInstance));
        var needed = InstancesFor(input.Backlog + Math.Max(0, input.InFlight));
        var entitlementBound = false;
        if (input.EntitledConcurrency is { } entitled)
        {
            var byEntitlement = InstancesFor(Math.Max(0, entitled));
            if (byEntitlement < needed)
            {
                needed = byEntitlement;
                entitlementBound = true;
            }
        }
        // Expired leases whose attempts are exhausted are terminal cleanup the claim exempts from pool, entitlement,
        // and fairness bounds: they only need one instance to exist, and never trigger the queue-deadline rule.
        if (input.CleanupBacklog > 0 && needed < 1)
        {
            needed = 1;
        }
        // Work no active instance can claim (recipe or input size outside every registration) needs new instances
        // whatever the registered capacity; instances still starting register with the template and will cover it.
        if (input.UncoveredBacklog > 0)
        {
            var uncoveredInstances = Math.Max(0, (int)Math.Ceiling(input.UncoveredBacklog / (double)perInstance) - input.Starting);
            needed = Math.Max(needed, compatibleActive + uncoveredInstances);
        }
        var desired = Math.Clamp(Math.Max(needed, options.MinWarmInstances), 0, options.MaxInstances);
        // The warm minimum is a count of instances that never self-terminate: when fewer than that carry the warm
        // designation (a warm instance was lost, or the minimum was raised), replacements are provisioned even while
        // excess capacity is running, within the instance maximum.
        var warmMissing = Math.Max(0, Math.Min(options.MinWarmInstances, options.MaxInstances) - input.WarmInstances);
        var warmShortfall = Math.Min(warmMissing, Math.Max(0, options.MaxInstances - active));
        if (warmMissing > 0 && warmShortfall == 0 && input.Running - input.WarmInstances > 0)
        {
            // At capacity with too few warm instances: retire one excess (self-terminating) instance so the next sample
            // can provision its warm replacement instead of waiting for the excess instance to exit on its own. The
            // autoscaler applies this only to an excess instance with no work in flight, so busy work is never cut.
            return new ElasticScalingDecision(0, 1, ReasonWarmMinimum);
        }
        // The branch is entered on the unclamped need as well: a full limit with work no active instance can claim
        // still reports the shortfall (or replaces an incompatible instance) instead of reading as satisfied.
        if (desired > compatibleActive || needed > compatibleActive || warmShortfall > 0)
        {
            if (options.MaxInstanceMinutesPerDay > 0 && input.InstanceMinutesToday >= options.MaxInstanceMinutesPerDay)
            {
                return new ElasticScalingDecision(0, 0, ReasonDailyLimit);
            }
            if (input.Backlog > 0 && needed > compatibleActive && startupEstimate + input.OldestBacklogAge > options.QueueDeadline)
            {
                // Provider startup cannot meet the deadline for the oldest work: keep it local, only top up the warm minimum.
                return new ElasticScalingDecision(warmShortfall, 0, ReasonColdStartExceedsDeadline);
            }
            // Room is bounded by every active instance, compatible or not; when incompatible instances fill the limit,
            // one is retired so the next sample can provision an instance able to claim the work.
            var provision = Math.Min(Math.Max(desired - compatibleActive, warmShortfall), Math.Max(0, options.MaxInstances - active));
            if (provision == 0 && desired > compatibleActive && incompatible > 0)
            {
                return new ElasticScalingDecision(0, 1, ReasonIncompatibleReplacement);
            }
            if (provision > 0)
            {
                var reason = needed > compatibleActive ? (entitlementBound ? ReasonEntitlementBound : ReasonBacklog) : ReasonWarmMinimum;
                return new ElasticScalingDecision(provision, 0, reason);
            }
            if (input.UncoveredBacklog > 0 && input.Starting == 0)
            {
                // The limit is full of instances serving other work: the uncovered work is reported, not served by
                // retiring a useful instance.
                return new ElasticScalingDecision(0, 0, ReasonInstanceLimit);
            }
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
