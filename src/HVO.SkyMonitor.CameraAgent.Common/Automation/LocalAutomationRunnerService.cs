using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Automation;

/// <summary>
/// Evaluates registered local automation definitions on its own timer and runs each due occurrence
/// through the task's existing coordinator.
/// <para>
/// The runner never admits an exposure, never changes acquisition cadence, and never occupies the
/// live processing slot: it only issues the same on-demand command an operator can issue by hand,
/// and that command already owns its durable claim, coalescing, and receipt.
/// </para>
/// </summary>
public sealed partial class LocalAutomationRunnerService(
    ILocalAutomationStore store,
    ILocalAutomationTaskRegistry registry,
    ILocalAutomationCaptureSequenceSource captureSequenceSource,
    IOptions<CameraAgentHostOptions> options,
    TimeProvider timeProvider,
    ILogger<LocalAutomationRunnerService> logger,
    LocalAutomationTelemetry? telemetry = null) : BackgroundService
{
    private readonly LocalAutomationOptions _options = options.Value.Automation;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await store.InitializeAsync(stoppingToken).ConfigureAwait(false);
        if (!_options.Enabled)
        {
            RunnerDisabled(logger);
            return;
        }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.PollIntervalSeconds), timeProvider);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await SweepAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Evaluates every enabled definition once. Public for deterministic focused tests.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A failing definition is recorded and must never stop the runner loop.")]
    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<LocalAutomationRunnerEntry> entries;
        try
        {
            entries = await store.GetRunnerViewAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            SweepFailed(logger, exception);
            return;
        }
        if (entries.Count == 0)
        {
            return;
        }
        long? captureSequence = null;
        if (entries.Any(static entry => entry.Definition.TriggerKind == LocalAutomationTriggerKind.CaptureRelative))
        {
            try
            {
                captureSequence = await captureSequenceSource.GetCaptureSequenceAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                CaptureSequenceUnavailable(logger, exception);
            }
        }
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        foreach (var entry in entries)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            try
            {
                await EvaluateAsync(entry, now, captureSequence, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                DefinitionFailed(logger, entry.Definition.DefinitionId, exception);
            }
        }
    }

    private async Task EvaluateAsync(
        LocalAutomationRunnerEntry entry,
        DateTimeOffset now,
        long? captureSequence,
        CancellationToken cancellationToken)
    {
        if (entry.Definition.TriggerKind == LocalAutomationTriggerKind.Periodic)
        {
            var resolved = LocalAutomationSchedule.ResolvePeriodic(
                entry.Definition.TriggerEpochUtc,
                entry.Definition.TriggerInterval,
                entry.LastOccurrenceUtc,
                now);
            if (resolved is not { } occurrence)
            {
                return;
            }
            var index = LocalAutomationSchedule.OccurrenceIndex(
                entry.Definition.TriggerEpochUtc, entry.Definition.TriggerInterval, occurrence.OccurrenceUtc);
            if (occurrence.MissedOccurrences > 0)
            {
                // Skipped occurrences are recorded once, explicitly, instead of being replayed as a
                // burst of catch-up commands the observatory never asked for.
                await store.RecordTerminalRunAsync(
                    entry,
                    RunKey(entry, 'm', index),
                    occurrence.OccurrenceUtc,
                    LocalAutomationRunOutcome.Missed,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{occurrence.MissedOccurrences} occurrence(s) elapsed while this CameraAgent was not running; they are not replayed."),
                    null,
                    cancellationToken).ConfigureAwait(false);
            }
            await RunAsync(entry, RunKey(entry, 'p', index), occurrence.OccurrenceUtc, null, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (captureSequence is not { } current)
        {
            return;
        }
        if (entry.LastCaptureSequence is not { } last)
        {
            // A newly enabled capture-relative definition is baselined, never fired for captures that
            // predate it.
            await store.SetCaptureBaselineAsync(entry.Definition.DefinitionId, current, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (current - last < entry.Definition.TriggerInterval)
        {
            return;
        }
        await RunAsync(entry, RunKey(entry, 'c', current), now, current, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAsync(
        LocalAutomationRunnerEntry entry,
        string runKey,
        DateTimeOffset scheduledForUtc,
        long? observedCaptureSequence,
        CancellationToken cancellationToken)
    {
        var claimed = await store
            .TryBeginRunAsync(entry, runKey, scheduledForUtc, observedCaptureSequence, cancellationToken)
            .ConfigureAwait(false);
        if (!claimed)
        {
            return;
        }
        using var activity = LocalAutomationTelemetry.ActivitySource.StartActivity("automation.run");
        activity?.SetTag("trigger", entry.Definition.TriggerKind.ToString());
        var started = Stopwatch.GetTimestamp();
        LocalAutomationExecution execution;
        try
        {
            execution = await registry.ExecuteAsync(entry.Definition, runKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The claimed run stays claimed and restart recovery settles it as interrupted.
            throw;
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        activity?.SetTag("outcome", execution.Outcome.ToString());
        await store.CompleteRunAsync(runKey, execution.Outcome, execution.Detail, cancellationToken)
            .ConfigureAwait(false);
        telemetry?.RecordRun(entry.Definition.TriggerKind, execution.Outcome, elapsed);
        RunSettled(
            logger,
            entry.Definition.DefinitionId,
            entry.Definition.TriggerKind.ToString(),
            execution.Outcome.ToString());
    }

    /// <summary>
    /// The run key is the occurrence identity and the command identity at once, so a retry of the
    /// same occurrence replays instead of acquiring twice. The revision prefix keeps a redefined
    /// automation from colliding with the occurrences of its previous revision.
    /// </summary>
    private static string RunKey(LocalAutomationRunnerEntry entry, char discriminator, long occurrence)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{entry.Definition.DefinitionId}|{entry.RevisionSha256[..16]}|{discriminator}{occurrence}");

    [LoggerMessage(7403, LogLevel.Information, "Local automation runs are disabled by configuration.")]
    private static partial void RunnerDisabled(ILogger logger);

    [LoggerMessage(7404, LogLevel.Warning, "The local automation sweep could not read durable state.")]
    private static partial void SweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(7405, LogLevel.Warning,
        "The durable capture sequence is unavailable; capture-relative automations stay pending.")]
    private static partial void CaptureSequenceUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(7406, LogLevel.Warning, "Local automation {DefinitionId} failed to evaluate.")]
    private static partial void DefinitionFailed(ILogger logger, string definitionId, Exception exception);

    [LoggerMessage(7407, LogLevel.Information,
        "Local automation {DefinitionId} on trigger {Trigger} settled with outcome {Outcome}.")]
    private static partial void RunSettled(ILogger logger, string definitionId, string trigger, string outcome);
}
