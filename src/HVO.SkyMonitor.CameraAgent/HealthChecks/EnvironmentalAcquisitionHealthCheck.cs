using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.HealthChecks;

public sealed class EnvironmentalAcquisitionHealthCheck(
    IEnvironmentalAcquisitionStateStore stateStore,
    ILocalEnvironmentalObservationStore observationStore,
    IOptions<CameraAgentHostOptions> options,
    TimeProvider timeProvider) : IHealthCheck
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Health checks return bounded aggregate state and never disclose persistence exceptions.")]
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var configured = options.Value.EnvironmentalAcquisition;
        if (!configured.Enabled)
        {
            return HealthCheckResult.Healthy(
                "Environmental acquisition is disabled.",
                Data("Disabled", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        }
        try
        {
            var root = options.Value.RawIngressRoot;
            var states = await stateStore.ReadSourceStatesAsync(root, cancellationToken).ConfigureAwait(false);
            var journal = await observationStore.GetLocalSnapshotAsync(root, cancellationToken).ConfigureAwait(false);
            var byId = states.ToDictionary(static state => state.SourceId, StringComparer.Ordinal);
            var now = timeProvider.GetUtcNow();
            var required = configured.Sources.Where(static source => source.Required).ToArray();
            var initialized = configured.Sources.Count(source => byId.TryGetValue(source.Id, out var state) &&
                state.LastDisposition.HasValue);
            var fresh = configured.Sources.Count(source => byId.TryGetValue(source.Id, out var state) &&
                state.LastStaleAfterUtc > now);
            var stale = configured.Sources.Count(source => byId.TryGetValue(source.Id, out var state) &&
                state.LastStaleAfterUtc is { } staleAfter && staleAfter <= now);
            var missing = configured.Sources.Count(source => !byId.TryGetValue(source.Id, out var state) ||
                state.LastStaleAfterUtc is null ||
                state.LastDisposition is null or EnvironmentalAcquisitionDisposition.Missing);
            var failing = configured.Sources.Count(source => byId.TryGetValue(source.Id, out var state) &&
                state.LastDisposition is EnvironmentalAcquisitionDisposition.Failed or EnvironmentalAcquisitionDisposition.TimedOut);
            var overdue = configured.Sources.Count(source => byId.TryGetValue(source.Id, out var state) &&
                state.NextPollUtc is { } nextPoll && nextPoll < now);
            var maximumFailures = states.Count == 0 ? 0 : states.Max(static state => state.ConsecutiveFailures);
            var capacityExhausted = journal.StoredCount >= configured.MaximumHistoryCount ||
                journal.StoredBytes >= configured.MaximumHistoryBytes;
            var allRequiredUnable = required.Length > 0 && required.All(source => byId.TryGetValue(source.Id, out var state) &&
                state.LastDisposition is EnvironmentalAcquisitionDisposition.Failed or EnvironmentalAcquisitionDisposition.TimedOut);
            var availability = capacityExhausted || allRequiredUnable ? "Unhealthy" :
                initialized < configured.Sources.Count || stale > 0 || missing > 0 || failing > 0 || overdue > 0
                    ? "Degraded"
                    : "Healthy";
            var data = Data(
                availability,
                configured.Sources.Count,
                required.Length,
                initialized,
                fresh,
                stale,
                missing,
                failing,
                overdue,
                maximumFailures,
                journal.StoredCount,
                journal.StoredBytes);
            return availability switch
            {
                "Healthy" => HealthCheckResult.Healthy("Environmental acquisition is current.", data),
                "Unhealthy" => HealthCheckResult.Unhealthy(
                    "Environmental acquisition requires operator attention.", data: data),
                _ => HealthCheckResult.Degraded("Environmental acquisition has stale, missing, or failed sources.", data: data)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy(
                "Environmental acquisition journal is unavailable.",
                data: Data("Unhealthy", configured.Sources.Count, configured.Sources.Count(static source => source.Required),
                    0, 0, 0, 0, 0, 0, 0, 0, 0));
        }
    }

    private static Dictionary<string, object> Data(
        string availability,
        int configured,
        int required,
        int initialized,
        int fresh,
        int stale,
        int missing,
        int failing,
        int overdue,
        int maximumFailures,
        long storedCount,
        long storedBytes)
        => new()
        {
            ["Availability"] = availability,
            ["ConfiguredSourceCount"] = configured,
            ["RequiredSourceCount"] = required,
            ["InitializedSourceCount"] = initialized,
            ["FreshSourceCount"] = fresh,
            ["StaleSourceCount"] = stale,
            ["MissingSourceCount"] = missing,
            ["FailingSourceCount"] = failing,
            ["OverduePollCount"] = overdue,
            ["MaximumConsecutiveFailures"] = maximumFailures,
            ["StoredCount"] = storedCount,
            ["StoredBytes"] = storedBytes
        };
}
