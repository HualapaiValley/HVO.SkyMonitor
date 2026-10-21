using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Policy;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;

/// <summary>One atomic configured-product loop, composing the frozen policy and storage seams.</summary>
public sealed class SpaceWeatherRefreshJob
{
    private readonly ISwpcStore store;
    private readonly SwpcAttemptRunner runner;
    private readonly SpaceWeatherAvailability availability;
    private readonly SpaceWeatherWriteGate gate;
    private readonly IOptions<CameraAgentHostOptions> options;
    private readonly TimeProvider clock;
    private readonly SpaceWeatherTelemetry telemetry;
    private readonly ILogger<SpaceWeatherRefreshJob> logger;

    internal SpaceWeatherRefreshJob(ISwpcStore store, SwpcAttemptRunner runner, SpaceWeatherAvailability availability,
        SpaceWeatherWriteGate gate, IOptions<CameraAgentHostOptions> options, TimeProvider clock,
        SpaceWeatherTelemetry telemetry, ILogger<SpaceWeatherRefreshJob> logger)
    {
        this.store = store; this.runner = runner; this.availability = availability; this.gate = gate;
        this.options = options; this.clock = clock; this.telemetry = telemetry; this.logger = logger;
    }

    /// <summary>
    /// Holds the host command gate through retention, startup observation, every attempt and publication.
    /// No new product starts after 30 seconds of injected-clock time. Cancellation always propagates.
    /// </summary>
    public async ValueTask<LocalAutomationExecution> ExecuteAsync(string runKey, SwpcAttemptTrigger trigger,
        DateTimeOffset scheduledForUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runKey);
        await gate.EnterAsync(ct).ConfigureAwait(false);
        try
        {
            var execution = await ExecuteCoreAsync(runKey, trigger, ct).ConfigureAwait(false);
            await PublishStateAsync(ct).ConfigureAwait(false);
            telemetry.RecordRun(execution.Outcome);
            var reason = execution.Detail.Split(':', 2)[0];
            SpaceWeatherLog.Settled(logger, execution.Outcome.ToString(), reason, execution.Detail);
            return execution;
        }
        finally { gate.Exit(); }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Optional storage and runner failures settle the automation with redacted reason codes; cancellation propagates.")]
    private async ValueTask<LocalAutomationExecution> ExecuteCoreAsync(string runKey, SwpcAttemptTrigger trigger, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!options.Value.SpaceWeather.Enabled) return Result(LocalAutomationRunOutcome.Skipped, "space-weather.disabled");
        if (!await availability.EnsureAvailableAsync(ct).ConfigureAwait(false))
            return Result(LocalAutomationRunOutcome.Skipped, SpaceWeatherAvailability.StoreUnavailableReasonCode);
        var began = clock.GetUtcNow();
        var products = Enum.GetValues<SwpcProduct>().Where(options.Value.SpaceWeather.Products.Contains).ToArray();
        SpaceWeatherLog.Admitted(logger, trigger.ToString(), products.Length);
        try
        {
            telemetry.RecordRetention(await store.ApplyRetentionAsync(Now(), ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            SpaceWeatherLog.Storage(logger, exception.GetType().Name);
            return Result(LocalAutomationRunOutcome.Failed, "space-weather.retention-failed");
        }
        var detail = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var results = new List<SwpcRunResult>();
        try
        {
            await gate.ObserveStartupAsync(store, clock, logger, ct).ConfigureAwait(false);
            for (var index = 0; index < products.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                if (clock.GetUtcNow() - began >= TimeSpan.FromSeconds(30))
                {
                    foreach (var remaining in products[index..]) detail[remaining.Token()] = "budget";
                    SpaceWeatherLog.Budget(logger, string.Join(" ", products[index..].Select(static p => p.Token())));
                    break;
                }
                var result = await runner.RunAsync(products[index], trigger, runKey, ct).ConfigureAwait(false);
                results.Add(result);
                detail[result.Product.Token()] = result.Outcome is { } outcome ? SwpcPolicy.OutcomeToken(outcome) : result.ReasonCode;
                telemetry.RecordAttempt(result);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            availability.RecordFailure(exception);
            return Result(LocalAutomationRunOutcome.Failed, "space-weather.storage-failed", detail);
        }
        if (results.Any(static r => r.Outcome is SwpcFetchOutcome.TransientFailure or SwpcFetchOutcome.Rejected or
            SwpcFetchOutcome.RateLimited or SwpcFetchOutcome.Halted ||
            r.AttemptId is not null && (r.Outcome is null || r.WriteStatus is not (SwpcWriteStatus.Applied or SwpcWriteStatus.Replayed))))
            return Result(LocalAutomationRunOutcome.Failed, "space-weather.product-failed", detail);
        if (results.Count == products.Length && results.All(static r => r.Refusal == SwpcRefusal.Halted))
            return Result(LocalAutomationRunOutcome.Skipped, "space-weather.halted", detail);
        if (results.Count == products.Length && results.All(static r => r.Refusal == SwpcRefusal.NotDue))
            return Result(LocalAutomationRunOutcome.Skipped, "space-weather.nothing-due", detail);
        return Result(LocalAutomationRunOutcome.Succeeded, "space-weather.succeeded", detail);
    }

    /// <summary>Publishes a cached gauge snapshot from ungated timed reads; failure is type-only and best effort.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A metric observation cannot change an already settled durable outcome.")]
    public async ValueTask PublishStateAsync(CancellationToken ct)
    {
        if (!availability.IsAvailable) return;
        try
        {
            var now = Now();
            var view = await store.ReadAsync(now, ct).ConfigureAwait(false);
            var health = await store.ReadHealthAsync(now, ct).ConfigureAwait(false);
            telemetry.Observe(view, health, now);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) { SpaceWeatherLog.Storage(logger, exception.GetType().Name); }
    }

    /// <summary>Reason codes plus ordinal product=outcome tokens, never provider or exception content.</summary>
    internal static LocalAutomationExecution Result(LocalAutomationRunOutcome outcome, string reason,
        SortedDictionary<string, string>? products = null)
    {
        var detail = products is { Count: > 0 } ? reason + ": " + string.Join(" ", products.Select(static p => p.Key + "=" + p.Value)) : reason;
        return new(outcome, detail.Length > 512 ? detail[..512] : detail);
    }

    private DateTimeOffset Now() => clock.GetUtcNow().ToUniversalTime();
}
