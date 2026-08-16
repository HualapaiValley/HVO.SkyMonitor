namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;

public enum CaptureLaneAvailability
{
    Initializing,
    Healthy,
    Degraded,
    Unhealthy
}

public sealed record CaptureLaneBacklogSnapshot(
    string Lane,
    bool Required,
    long PendingCount,
    long PendingBytes,
    DateTimeOffset? OldestPendingUtc,
    long LeasedCount,
    long RetryCount,
    long QuarantineCount,
    int PressureLevel,
    IReadOnlyList<CaptureLanePendingCaptureSnapshot> PendingCaptures);

public sealed record CaptureLanePendingCaptureSnapshot(string AgentId, long CaptureSequence);

public sealed record CaptureLaneSnapshot(
    CaptureLaneAvailability Availability,
    string Reason,
    IReadOnlyList<CaptureLaneBacklogSnapshot> Lanes,
    long PendingCount,
    long PendingBytes,
    long LeasedCount,
    long RetryCount,
    long QuarantineCount,
    DateTimeOffset? OldestPendingUtc,
    DateTimeOffset EvaluatedUtc);

public sealed class CaptureLaneState(
    TimeProvider timeProvider,
    Microsoft.Extensions.Options.IOptions<Options.CameraAgentHostOptions> options)
{
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly Options.CaptureDistributionOptions _options = options.Value.CaptureDistribution;
    private CaptureLaneSnapshot _snapshot = new(
        CaptureLaneAvailability.Initializing,
        "initializing",
        [],
        0,
        0,
        0,
        0,
        0,
        null,
        timeProvider.GetUtcNow());

    public CaptureLaneSnapshot Snapshot => Volatile.Read(ref _snapshot);

    internal void Update(IReadOnlyList<CaptureLaneBacklog> backlogs)
    {
        var now = _timeProvider.GetUtcNow();
        var availability = CaptureLaneAvailability.Healthy;
        var reason = "accepting";
        var lanes = new List<CaptureLaneBacklogSnapshot>(backlogs.Count);
        foreach (var backlog in backlogs)
        {
            var countMaximum = backlog.Required
                ? _options.RequiredMaximumPendingCount
                : _options.OptionalMaximumPendingCount;
            var bytesMaximum = backlog.Required
                ? _options.RequiredMaximumPendingBytes
                : _options.OptionalMaximumPendingBytes;
            var ageMaximum = TimeSpan.FromMinutes(backlog.Required
                ? _options.RequiredMaximumOldestAgeMinutes
                : _options.OptionalMaximumOldestAgeMinutes);
            var age = backlog.OldestPendingUtc is { } oldest ? now - oldest : TimeSpan.Zero;
            var hard = backlog.PendingCount >= countMaximum ||
                       backlog.PendingBytes >= bytesMaximum ||
                       age >= ageMaximum;
            var warning = hard ||
                          backlog.PendingCount * 100 >= countMaximum * _options.PressureRecoveryPercent ||
                          CaptureLanePressureMath.IsAtOrAbovePercentage(
                              backlog.PendingBytes, bytesMaximum, _options.PressureRecoveryPercent);
            var pressure = Math.Max(backlog.PressureLevel, hard ? 2 : warning ? 1 : 0);
            hard = pressure == 2;
            warning = pressure >= 1;
            if (backlog.Required && (hard || backlog.QuarantineCount > 0))
            {
                availability = CaptureLaneAvailability.Unhealthy;
                reason = backlog.QuarantineCount > 0 ? "required-quarantine" : "required-pressure";
            }
            else if (availability != CaptureLaneAvailability.Unhealthy &&
                     (warning || backlog.QuarantineCount > 0))
            {
                availability = CaptureLaneAvailability.Degraded;
                reason = backlog.Required ? "required-warning" : "optional-pressure";
            }
            lanes.Add(new CaptureLaneBacklogSnapshot(
                backlog.Lane,
                backlog.Required,
                backlog.PendingCount,
                backlog.PendingBytes,
                backlog.OldestPendingUtc,
                backlog.LeasedCount,
                backlog.RetryCount,
                backlog.QuarantineCount,
                pressure,
                backlog.PendingCaptures?.Select(static capture => new CaptureLanePendingCaptureSnapshot(
                    capture.AgentId,
                    capture.CaptureSequence)).ToArray() ?? []));
        }
        Volatile.Write(ref _snapshot, new CaptureLaneSnapshot(
            availability,
            reason,
            lanes,
            lanes.Sum(static lane => lane.PendingCount),
            lanes.Sum(static lane => lane.PendingBytes),
            lanes.Sum(static lane => lane.LeasedCount),
            lanes.Sum(static lane => lane.RetryCount),
            lanes.Sum(static lane => lane.QuarantineCount),
            lanes.Where(static lane => lane.OldestPendingUtc is not null)
                .Select(static lane => lane.OldestPendingUtc)
                .Min(),
            now));
    }

    internal void SetUnhealthy(string reason)
    {
        var snapshot = Snapshot;
        Volatile.Write(ref _snapshot, snapshot with
        {
            Availability = CaptureLaneAvailability.Unhealthy,
            Reason = reason,
            EvaluatedUtc = _timeProvider.GetUtcNow()
        });
    }

    internal DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow();
}
