using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

public sealed class EnvironmentalObservationPublisher(
    ILocalEnvironmentalObservationStore localStore,
    IOptions<CameraAgentHostOptions> options,
    TimeProvider timeProvider,
    EnvironmentalAcquisitionTelemetry? acquisitionTelemetry = null)
    : IEnvironmentalObservationPublisher, IEnvironmentalHeldPollPublisher
{
    bool IEnvironmentalHeldPollPublisher.SupportsHeldPollSettlement => localStore is SqliteEnvironmentalObservationStore;

    public async ValueTask<EnvironmentalObservationPublishResult> PublishAsync(
        EnvironmentalObservationFactV1 fact,
        CancellationToken cancellationToken = default)
        => (await PublishCoreAsync(fact, null, cancellationToken).ConfigureAwait(false)).Published;

    async ValueTask<EnvironmentalAcquisitionReceipt> IEnvironmentalHeldPollPublisher.PublishHeldPollAsync(
        EnvironmentalObservationFactV1 fact, EnvironmentalHeldPoll poll, CancellationToken cancellationToken)
        => (await PublishCoreAsync(fact, poll, cancellationToken).ConfigureAwait(false)).HeldReceipt!;

    private async ValueTask<(EnvironmentalObservationPublishResult Published, EnvironmentalAcquisitionReceipt? HeldReceipt)> PublishCoreAsync(
        EnvironmentalObservationFactV1 fact, EnvironmentalHeldPoll? poll, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fact);
        LocalEnvironmentalObservationCommitResult committed;
        TimeSpan commitDuration;
        var commitStarted = timeProvider.GetTimestamp();
        var commitActivity = EnvironmentalAcquisitionTelemetry.ActivitySource.StartActivity("environment.local.commit");
        try
        {
            var root = options.Value.RawIngressRoot;
            committed = poll is { } held
                ? await (localStore as SqliteEnvironmentalObservationStore ?? throw new InvalidOperationException(
                        "The configured environmental store does not support atomic settlement of held environmental polls."))
                    .CommitHeldPollAsync(root, fact, held, cancellationToken).ConfigureAwait(false)
                : await localStore.CommitLocalAsync(root, fact, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDisposeActivity(commitActivity);
            throw;
        }
        commitDuration = timeProvider.GetElapsedTime(commitStarted);
        CompleteActivity(commitActivity, "environment.outcome", committed.Disposition.ToString(), "commit");

        if (acquisitionTelemetry is not null)
        {
            await TryRecordCommitTelemetryAsync(
                localStore, fact, committed, commitDuration).ConfigureAwait(false);
        }
        var publishDisposition = committed.Disposition == LocalEnvironmentalObservationCommitDisposition.Committed
            ? EnvironmentalObservationPublishDisposition.Committed
            : EnvironmentalObservationPublishDisposition.Duplicate;
        return (new EnvironmentalObservationPublishResult(publishDisposition), committed.HeldPollReceipt);
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
