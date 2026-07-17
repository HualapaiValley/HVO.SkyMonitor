using System.Diagnostics;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

public sealed class EnvironmentalObservationPublisher(
    IEnvironmentalObservationTargetResolver targetResolver,
    IEnvironmentalObservationOutbox outbox,
    EnvironmentalObservationDeliveryWakeup wakeup,
    EnvironmentalObservationDeliveryState state,
    EnvironmentalObservationDeliveryTelemetry telemetry,
    IOptions<CameraAgentHostOptions> options,
    TimeProvider timeProvider) : IEnvironmentalObservationPublisher
{
    public async ValueTask<EnvironmentalObservationPublishResult> PublishAsync(
        EnvironmentalObservationFactV1 fact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var target = await targetResolver.ResolveAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Environmental observations require an active device provisioning target.");
        if (target.ObservatoryId == Guid.Empty || target.DevicePublicId == Guid.Empty)
        {
            throw new InvalidOperationException("Environmental observation provisioning target is invalid.");
        }
        var observation = fact.Enrich(new EnvironmentalObservationTarget(
            target.ObservatoryId,
            target.DevicePublicId,
            target.RigId));
        var validation = EnvironmentalObservationJson.Validate(observation);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"Environmental observation fact is invalid ({validation.ReasonCode}:{validation.FieldPath}).",
                nameof(fact));
        }
        var started = timeProvider.GetTimestamp();
        using var activity = EnvironmentalObservationDeliveryTelemetry.ActivitySource.StartActivity("environment.enqueue");
        try
        {
            var disposition = await outbox.EnqueueAsync(
                options.Value.RawIngressRoot,
                observation,
                cancellationToken).ConfigureAwait(false);
            telemetry.RecordEnqueue(disposition, EnvironmentalObservationJson.Serialize(observation).Length, timeProvider.GetElapsedTime(started));
            activity?.SetTag("environment.outcome", disposition.ToString());
            activity?.AddEvent(new ActivityEvent("enqueue"));
            activity?.SetStatus(ActivityStatusCode.Ok);
            wakeup.Signal();
            return new EnvironmentalObservationPublishResult(
                disposition == EnvironmentalObservationEnqueueDisposition.Enqueued
                    ? EnvironmentalObservationPublishDisposition.Enqueued
                    : EnvironmentalObservationPublishDisposition.Duplicate,
                observation);
        }
        catch (Exception exception)
        {
            if (exception is EnvironmentalObservationOutboxCapacityException)
            {
                state.Fail("capacity-exhausted");
            }
            else if (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                state.Fail("durable-outbox-unavailable");
            }
            activity?.AddEvent(new ActivityEvent(
                "exception",
                tags: new ActivityTagsCollection { { "exception.type", exception.GetType().FullName } }));
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            throw;
        }
    }
}
