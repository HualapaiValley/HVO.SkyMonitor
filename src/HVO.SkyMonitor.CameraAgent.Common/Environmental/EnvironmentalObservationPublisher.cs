using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
    TimeProvider timeProvider,
    EnvironmentalAcquisitionTelemetry? acquisitionTelemetry = null) : IEnvironmentalObservationPublisher
{
    public async ValueTask<EnvironmentalObservationPublishResult> PublishAsync(
        EnvironmentalObservationFactV1 fact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (outbox is not ILocalEnvironmentalObservationStore localStore)
        {
            throw new InvalidOperationException("The configured environmental store does not support local history.");
        }
        LocalEnvironmentalObservationCommitResult committed;
        TimeSpan commitDuration;
        var commitStarted = timeProvider.GetTimestamp();
        var commitActivity = EnvironmentalAcquisitionTelemetry.ActivitySource.StartActivity("environment.local.commit");
        try
        {
            committed = await localStore.CommitLocalAsync(
                options.Value.RawIngressRoot,
                fact,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDisposeActivity(commitActivity);
            throw;
        }
        commitDuration = timeProvider.GetElapsedTime(commitStarted);
        CompleteActivity(commitActivity, "environment.outcome", committed.Disposition.ToString(), "commit");

        var projected = await TryProjectAndSignalAsync(
            localStore, fact, committed, cancellationToken).ConfigureAwait(false);

        if (acquisitionTelemetry is not null)
        {
            await TryRecordCommitTelemetryAsync(
                localStore, fact, committed, commitDuration).ConfigureAwait(false);
        }
        var publishDisposition = committed.Disposition == LocalEnvironmentalObservationCommitDisposition.Committed
            ? EnvironmentalObservationPublishDisposition.Enqueued
            : EnvironmentalObservationPublishDisposition.Duplicate;
        return new EnvironmentalObservationPublishResult(publishDisposition, projected.DeliveryObservation);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Optional projection and delivery instrumentation cannot overturn an authoritative local commit.")]
    private async ValueTask<LocalEnvironmentalObservationCommitResult> TryProjectAndSignalAsync(
        ILocalEnvironmentalObservationStore localStore,
        EnvironmentalObservationFactV1 fact,
        LocalEnvironmentalObservationCommitResult committed,
        CancellationToken cancellationToken)
    {
        var projected = committed;
        Activity? enqueueActivity = null;
        try
        {
            var projectionStarted = timeProvider.GetTimestamp();
            if (options.Value.CentralIntegration.Mode != CentralIntegrationMode.Disabled)
            {
                enqueueActivity = EnvironmentalObservationDeliveryTelemetry.ActivitySource.StartActivity(
                    "environment.enqueue");
            }
            projected = await TryProjectAsync(localStore, fact, committed, cancellationToken).ConfigureAwait(false);
            if (projected.ProjectionDisposition == EnvironmentalObservationProjectionDisposition.Waiting)
            {
                state.Fail("capacity-exhausted");
            }
            if (projected.DeliveryObservation is { } observation)
            {
                telemetry.RecordEnqueue(
                    projected.DeliveryDisposition ?? EnvironmentalObservationEnqueueDisposition.Duplicate,
                    EnvironmentalObservationJson.Serialize(observation).Length,
                    timeProvider.GetElapsedTime(projectionStarted));
            }
            if (projected.ProjectionDisposition is EnvironmentalObservationProjectionDisposition.Staged or
                EnvironmentalObservationProjectionDisposition.Waiting)
            {
                wakeup.Signal();
            }
            CompleteActivity(
                enqueueActivity, "environment.outcome", projected.ProjectionDisposition.ToString(), "enqueue");
            enqueueActivity = null;
        }
        catch (Exception)
        {
        }
        finally
        {
            TryDisposeActivity(enqueueActivity);
        }
        return projected;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Optional central projection must never reject an already committed local observation.")]
    private async ValueTask<LocalEnvironmentalObservationCommitResult> TryProjectAsync(
        ILocalEnvironmentalObservationStore localStore,
        EnvironmentalObservationFactV1 fact,
        LocalEnvironmentalObservationCommitResult committed,
        CancellationToken cancellationToken)
    {
        if (options.Value.CentralIntegration.Mode == CentralIntegrationMode.Disabled)
        {
            return committed;
        }
        try
        {
            var target = await targetResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
            if (target is null)
            {
                state.Fail("provisioning-target-unavailable");
                return committed;
            }
            if (target.ObservatoryId == Guid.Empty || target.DevicePublicId == Guid.Empty)
            {
                state.Fail("provisioning-target-invalid");
                return committed;
            }
            return await localStore.CommitLocalAsync(
                options.Value.RawIngressRoot,
                fact,
                target,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            state.Fail("central-projection-unavailable");
            return committed;
        }
        catch (Exception)
        {
            state.Fail("central-projection-unavailable");
            return committed;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Telemetry collection cannot overturn an authoritative local commit.")]
    private async ValueTask TryRecordCommitTelemetryAsync(
        ILocalEnvironmentalObservationStore localStore,
        EnvironmentalObservationFactV1 fact,
        LocalEnvironmentalObservationCommitResult committed,
        TimeSpan commitDuration)
    {
        try
        {
            acquisitionTelemetry!.RecordCommit(
                fact.SchemaVersion,
                committed.Disposition.ToString(),
                EnvironmentalObservationFactJson.Serialize(fact).Length,
                commitDuration,
                await localStore.GetLocalSnapshotAsync(
                    options.Value.RawIngressRoot, CancellationToken.None).ConfigureAwait(false));
        }
        catch (Exception)
        {
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Activity listeners cannot overturn an authoritative local commit.")]
    private static void CompleteActivity(Activity? activity, string tag, string value, string eventName)
    {
        try
        {
            activity?.SetTag(tag, value);
            activity?.AddEvent(new ActivityEvent(eventName));
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception)
        {
        }
        finally
        {
            TryDisposeActivity(activity);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Activity listener disposal cannot overturn persistence outcomes.")]
    private static void TryDisposeActivity(Activity? activity)
    {
        try
        {
            activity?.Dispose();
        }
        catch (Exception)
        {
        }
    }
}
