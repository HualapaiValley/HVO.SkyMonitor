using System.Diagnostics;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

public sealed partial class ProcessingGraphDeliveryService(
    IProcessingGraphDeliveryTransport transport,
    IProcessingGraphDeliveryInbox inbox,
    IProcessingGraphOperations operations,
    ICameraAgentConfigurationAccessor configurationAccessor,
    ProcessingGraphDeliveryState state,
    ProcessingGraphDeliveryTelemetry telemetry,
    IOptions<CameraAgentHostOptions> options,
    TimeProvider timeProvider,
    ILogger<ProcessingGraphDeliveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.CentralIntegration.Mode == CentralIntegrationMode.Disabled ||
            !options.Value.ProcessingGraphDelivery.Enabled)
        {
            state.Update(ProcessingGraphDeliveryAvailability.Disabled, "disabled", 0);
            return;
        }
        var configuration = await configurationAccessor.WaitForConfigurationAsync(stoppingToken).ConfigureAwait(false);
        var agentId = string.IsNullOrWhiteSpace(configuration.AgentId)
            ? throw new InvalidDataException("Processing graph delivery requires a configured agent identity.")
            : configuration.AgentId;
        var deliveryOptions = options.Value.ProcessingGraphDelivery;
        var pollInterval = StablePollingInterval(
            TimeSpan.FromSeconds(deliveryOptions.PollIntervalSeconds), agentId);
        var nextDelay = pollInterval;
        var consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await inbox.ObserveActiveRevisionAsync(stoppingToken).ConfigureAwait(false);
                var expired = await inbox.ExpirePendingProposalsAsync(stoppingToken).ConfigureAwait(false);
                if (expired > 0)
                {
                    Log.ExpiredPendingProposals(logger, expired);
                }
                await DeliverPendingFactsAsync(deliveryOptions, stoppingToken).ConfigureAwait(false);
                var registry = await operations.GetRegistryAsync(stoppingToken).ConfigureAwait(false);
                var active = registry.Revisions.Single(item => item.RevisionId == registry.ActiveRevisionId);
                var request = new ProcessingGraphProposalPollRequestV1(
                    ProcessingGraphDeliverySchemaVersions.Current,
                    agentId,
                    active.RevisionId,
                    active.DefinitionIdentitySha256,
                    active.SharedPlanIdentitySha256,
                    inbox.Capabilities);
                var started = timeProvider.GetTimestamp();
                ProcessingGraphProposalTransportResult result;
                using (var activity = ProcessingGraphDeliveryTelemetry.ActivitySource.StartActivity("processing-graph.pull"))
                {
                    result = await transport.PullAsync(request, stoppingToken).ConfigureAwait(false);
                    activity?.SetTag("processing_graph.outcome", result.Disposition.ToString());
                    activity?.SetStatus(result.Disposition == ProcessingGraphDeliveryTransportDisposition.Acknowledged
                        ? ActivityStatusCode.Ok
                        : ActivityStatusCode.Error, result.ReasonCode);
                }
                telemetry.Record("pull", result.Disposition.ToString(), timeProvider.GetElapsedTime(started));
                if (result.Disposition == ProcessingGraphDeliveryTransportDisposition.Acknowledged &&
                    result.Response is { } response)
                {
                    consecutiveFailures = 0;
                    if (!string.Equals(response.SchemaVersion, ProcessingGraphDeliverySchemaVersions.Current,
                            StringComparison.Ordinal) ||
                        !Enum.IsDefined(response.Disposition) ||
                        string.IsNullOrWhiteSpace(response.ReasonCode) || response.ReasonCode.Length > 128 ||
                        response.ServerTimeUtc.Offset != TimeSpan.Zero ||
                        response.Disposition == ProcessingGraphProposalPollDisposition.Proposed && response.Proposal is null ||
                        response.Disposition != ProcessingGraphProposalPollDisposition.Proposed && response.Proposal is not null)
                    {
                        throw new InvalidDataException("The central processing graph poll response is invalid.");
                    }
                    if (response.Proposal is { } proposal)
                    {
                        using var stage = ProcessingGraphDeliveryTelemetry.ActivitySource.StartActivity("processing-graph.stage");
                        await inbox.StageAsync(proposal, stoppingToken).ConfigureAwait(false);
                        stage?.SetStatus(ActivityStatusCode.Ok);
                        Log.Staged(logger, proposal.ProposalId, proposal.CatalogRevisionId);
                        await DeliverPendingFactsAsync(deliveryOptions, stoppingToken).ConfigureAwait(false);
                    }
                    var backlog = await ReadBacklogAsync(stoppingToken).ConfigureAwait(false);
                    var degraded = backlog.PendingFactCount > 0 ||
                        response.Disposition == ProcessingGraphProposalPollDisposition.Incompatible;
                    state.Update(
                        degraded ? ProcessingGraphDeliveryAvailability.Degraded : ProcessingGraphDeliveryAvailability.Healthy,
                        backlog.PendingFactCount == 0 ? response.ReasonCode : "acknowledgement-pending",
                        backlog.PendingFactCount,
                        timeProvider.GetUtcNow(),
                        backlog);
                    nextDelay = pollInterval;
                }
                else
                {
                    consecutiveFailures++;
                    var backlog = await ReadBacklogAsync(stoppingToken).ConfigureAwait(false);
                    state.Update(
                        ProcessingGraphDeliveryAvailability.Degraded,
                        result.ReasonCode,
                        backlog.PendingFactCount,
                        backlog: backlog);
                    nextDelay = BoundDelay(
                        result.RetryAfter ?? RetryDelay(consecutiveFailures, deliveryOptions),
                        deliveryOptions);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or
                UnauthorizedAccessException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException)
            {
                consecutiveFailures++;
                ProcessingGraphDeliveryBacklog? backlog = null;
                try
                {
                    backlog = await ReadBacklogAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception countException) when (countException is IOException or InvalidOperationException or
                    Microsoft.Data.Sqlite.SqliteException)
                {
                    Log.Failed(logger, "durable-state-unavailable", countException);
                }
                state.Update(
                    backlog is null
                        ? ProcessingGraphDeliveryAvailability.Unhealthy
                        : ProcessingGraphDeliveryAvailability.Degraded,
                    backlog is null ? "durable-state-unavailable" : "delivery-cycle-failed",
                    backlog?.PendingFactCount ?? 0,
                    backlog: backlog);
                Log.Failed(logger, exception.GetType().Name, exception);
                nextDelay = RetryDelay(consecutiveFailures, deliveryOptions);
            }
            if (nextDelay > TimeSpan.Zero)
            {
                await Task.Delay(nextDelay, timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask DeliverPendingFactsAsync(
        ProcessingGraphDeliveryOptions deliveryOptions,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < deliveryOptions.AcknowledgementBatchSize; index++)
        {
            var outcome = await DeliverOneFactAsync(deliveryOptions, cancellationToken).ConfigureAwait(false);
            if (outcome is not (ProcessingGraphFactDeliveryOutcome.Acknowledged or ProcessingGraphFactDeliveryOutcome.Rejected))
            {
                break;
            }
        }
        _ = await ReadBacklogAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ProcessingGraphFactDeliveryOutcome> DeliverOneFactAsync(
        ProcessingGraphDeliveryOptions deliveryOptions,
        CancellationToken cancellationToken)
    {
        var fact = await inbox.ReadPendingFactAsync(cancellationToken).ConfigureAwait(false);
        if (fact is null)
        {
            return ProcessingGraphFactDeliveryOutcome.None;
        }
        var started = timeProvider.GetTimestamp();
        ProcessingGraphFactTransportResult result;
        using (var activity = ProcessingGraphDeliveryTelemetry.ActivitySource.StartActivity("processing-graph.acknowledge"))
        {
            result = await transport.SendFactAsync(fact, cancellationToken).ConfigureAwait(false);
            activity?.SetTag("processing_graph.fact_kind", fact.Kind.ToString());
            activity?.SetTag("processing_graph.outcome", result.Disposition.ToString());
            activity?.SetStatus(result.Disposition == ProcessingGraphDeliveryTransportDisposition.Acknowledged
                ? ActivityStatusCode.Ok
                : ActivityStatusCode.Error, result.ReasonCode);
        }
        telemetry.Record("acknowledge", result.Disposition.ToString(), timeProvider.GetElapsedTime(started));
        if (result.Disposition == ProcessingGraphDeliveryTransportDisposition.Acknowledged &&
            result.Acknowledgement is { } acknowledgement &&
            acknowledgement.FactId == fact.FactId &&
            Enum.IsDefined(acknowledgement.Disposition) &&
            acknowledgement.AcknowledgedAtUtc.Offset == TimeSpan.Zero &&
            string.Equals(acknowledgement.SchemaVersion, ProcessingGraphDeliverySchemaVersions.Current, StringComparison.Ordinal))
        {
            switch (acknowledgement.Disposition)
            {
                case ProcessingGraphFactAcknowledgementDisposition.Recorded:
                case ProcessingGraphFactAcknowledgementDisposition.Duplicate:
                    await inbox.AcknowledgeFactAsync(fact.FactId, cancellationToken).ConfigureAwait(false);
                    break;
                case ProcessingGraphFactAcknowledgementDisposition.Superseded:
                    await inbox.SupersedeProposalAsync(fact.ProposalId, cancellationToken).ConfigureAwait(false);
                    break;
            }
            Log.FactAcknowledged(logger, fact.Kind.ToString(), fact.FactId);
            return ProcessingGraphFactDeliveryOutcome.Acknowledged;
        }
        if (result.Disposition == ProcessingGraphDeliveryTransportDisposition.Rejected)
        {
            // Central durably refused this immutable fact (invalid payload, unknown or conflicting proposal). Resending
            // can never succeed, so the fact settles as a terminal local rejection instead of growing the backlog.
            await inbox.RejectFactAsync(fact.FactId, result.ReasonCode, cancellationToken).ConfigureAwait(false);
            Log.FactRejected(logger, fact.Kind.ToString(), fact.FactId, result.ReasonCode);
            return ProcessingGraphFactDeliveryOutcome.Rejected;
        }
        var delay = BoundDelay(
            result.RetryAfter ?? TimeSpan.FromSeconds(deliveryOptions.RetryInitialDelaySeconds),
            deliveryOptions);
        await inbox.RetryFactAsync(
            fact.FactId, timeProvider.GetUtcNow() + delay, result.ReasonCode, cancellationToken).ConfigureAwait(false);
        Log.Retrying(logger, result.ReasonCode, (long)delay.TotalMilliseconds);
        return ProcessingGraphFactDeliveryOutcome.Deferred;
    }

    private async ValueTask<ProcessingGraphDeliveryBacklog> ReadBacklogAsync(CancellationToken cancellationToken)
    {
        var backlog = await inbox.ReadBacklogAsync(cancellationToken).ConfigureAwait(false);
        state.UpdateBacklog(backlog);
        return backlog;
    }

    private static TimeSpan BoundDelay(TimeSpan value, ProcessingGraphDeliveryOptions options)
    {
        var minimum = TimeSpan.FromSeconds(options.RetryInitialDelaySeconds);
        var maximum = TimeSpan.FromSeconds(options.RetryMaximumDelaySeconds);
        return TimeSpan.FromTicks(Math.Clamp(value.Ticks, minimum.Ticks, maximum.Ticks));
    }

    private static TimeSpan RetryDelay(int consecutiveFailures, ProcessingGraphDeliveryOptions options)
    {
        var exponent = Math.Min(Math.Max(consecutiveFailures - 1, 0), 20);
        var multiplier = 1L << exponent;
        var initialTicks = TimeSpan.FromSeconds(options.RetryInitialDelaySeconds).Ticks;
        var maximumTicks = TimeSpan.FromSeconds(options.RetryMaximumDelaySeconds).Ticks;
        return TimeSpan.FromTicks(Math.Min(initialTicks * multiplier, maximumTicks));
    }

    private static TimeSpan StablePollingInterval(TimeSpan interval, string identity)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var sample = BinaryPrimitives.ReadUInt32LittleEndian(hash);
        var factor = 0.9 + (sample / (double)uint.MaxValue * 0.2);
        return TimeSpan.FromTicks((long)(interval.Ticks * factor));
    }

    private enum ProcessingGraphFactDeliveryOutcome
    {
        None,
        Acknowledged,
        Rejected,
        Deferred
    }

    private static partial class Log
    {
        [LoggerMessage(2540, LogLevel.Information,
            "Processing graph proposal staged locally: ProposalId={ProposalId}, CatalogRevisionId={CatalogRevisionId}")]
        internal static partial void Staged(ILogger logger, Guid proposalId, Guid catalogRevisionId);

        [LoggerMessage(2541, LogLevel.Debug,
            "Processing graph delivery fact {Kind} acknowledged: FactId={FactId}")]
        internal static partial void FactAcknowledged(ILogger logger, string kind, Guid factId);

        [LoggerMessage(2542, LogLevel.Warning,
            "Processing graph delivery retry scheduled because {ReasonCode} after {DelayMilliseconds} ms")]
        internal static partial void Retrying(ILogger logger, string reasonCode, long delayMilliseconds);

        [LoggerMessage(2543, LogLevel.Error,
            "Processing graph delivery cycle failed because {ReasonCode}")]
        internal static partial void Failed(ILogger logger, string reasonCode, Exception exception);

        [LoggerMessage(2545, LogLevel.Warning,
            "Processing graph delivery expired {Count} locally pending proposal(s) past their deadline")]
        internal static partial void ExpiredPendingProposals(ILogger logger, int count);

        [LoggerMessage(2544, LogLevel.Warning,
            "Processing graph delivery fact {Kind} rejected by central and settled terminally: FactId={FactId}, Reason={ReasonCode}")]
        internal static partial void FactRejected(ILogger logger, string kind, Guid factId, string reasonCode);
    }
}
