using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

// Receipt consumption is independent from the single-view worker, so encoding
// a stack or rehydrating its optional layers cannot block step 50 singles.
internal sealed partial class CenteredViewWindowPublicationWorker(
    RawCaptureIngress ingress, SqliteCaptureProcessingStore store, SqliteCenteredViews views,
    ICenteredViewProducer producer, IExposureProductReader products,
    CenteredViewSourceLayers layers, TimeProvider clock,
    ILogger<CenteredViewWindowPublicationWorker> logger) : BackgroundService
{
    internal const int MaximumMissingCenterRetries = 8;
    internal const string MissingCenterSingleTerminalReason = "centered.window-center-single-unavailable";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);
    private static readonly CenteredViewPreviewOptions PreviewOptions = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval, clock);
        do
        {
            try
            {
                if (ingress.CenteredPublicationReady)
                    await PublishReadyAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SqliteException or IOException)
            {
                WindowReadFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async ValueTask PublishReadyAsync(CancellationToken token)
    {
        foreach (var id in await views.ReadDueWindowPublicationsAsync(8, token).ConfigureAwait(false))
        {
            try
            {
                await PublishAsync(id, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SqliteException or IOException)
            {
                WindowFailed(logger, id, exception);
                await views.CheckpointWindowAsync(id, null, RetryDelay, null, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or InvalidOperationException
                or System.Text.Json.JsonException)
            {
                WindowFailed(logger, id, exception);
                await views.CheckpointWindowAsync(id, null, null, "centered.window-evidence-rejected", token).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask PublishAsync(string id, CancellationToken token)
    {
        var disposition = await store.ReadCenteredWindowDispositionAsync(id, token).ConfigureAwait(false);
        if (disposition is not { IsTerminal: true, Closure.Center.Input: { } center })
            throw new InvalidDataException("The stack publication queue does not match the terminal window.");
        var retained = await ingress.ReadCenteredRawAsync(center.Key, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The window reference admission could not be reopened.");
        if (retained.Manifest.CenteredExposure is not { } binding || binding.Source.RunId != center.Key.RunId)
            throw new InvalidDataException("The window reference does not match the centered run.");

        var stream = new CenteredViewStream(center.Key.Camera, center.Key.RunId, retained.Run.GraphRevisionId);
        CenteredView? stack = null;
        if (disposition.Publication is { } receipt)
        {
            stack = await producer.PublishRegisteredStackAsync(stream, id, receipt.EvidenceArtifactId, products, PreviewOptions, token)
                .ConfigureAwait(false);
        }

        // The first two slot calendars precede full warm-up; preserve any bases
        // for archive without issuing a retroactive startup stack.
        if (binding.Source.Ordinal <= 2 && disposition.Closure.Slots.Any(slot => slot.State == CenteredWindowSlotState.OutsideRun))
        {
            if (stack is not null)
                await layers.RebuildForBaseAsync(stack.Base, token).ConfigureAwait(false);
            await views.CheckpointWindowAsync(id, null, null, null, token).ConfigureAwait(false);
            return;
        }

        var liveBase = stack?.Base.BaseId;
        if (liveBase is null)
        {
            var linear = await products.ReadLinearAsync(center.Key, token).ConfigureAwait(false);
            long? settledLinearRevision = linear is { Disposition: not LinearExposureDisposition.Pending, Revision: > 0 }
                ? linear.Revision
                : null;
            var observedLinearRevision = linear?.Revision ?? 0;
            var resolution = await views.ResolveOrCheckpointMissingCenterAsync(
                id, stream, center.Key.CaptureId, settledLinearRevision, observedLinearRevision,
                RetryDelay, MaximumMissingCenterRetries, MissingCenterSingleTerminalReason, token)
                .ConfigureAwait(false);
            if (resolution.Outcome == SqliteCenteredViews.CenterSingleCheckpointOutcome.SettledWithoutBase)
                CenterSingleUnavailable(logger, id, center.Key.CaptureId);
            else if (resolution.Outcome == SqliteCenteredViews.CenterSingleCheckpointOutcome.ExhaustedRetries)
                CenterSingleRetryExhausted(logger, id, center.Key.CaptureId, MaximumMissingCenterRetries);
            return;
        }
        // Make the ready base eligible before optional reconstruction. A crash or
        // a missing optional input cannot make a usable base disappear.
        await views.CheckpointWindowAsync(id, liveBase, RetryDelay, null, token).ConfigureAwait(false);
        await layers.RebuildForBaseAsync(stack!.Base, token).ConfigureAwait(false);
        await views.CheckpointWindowAsync(id, liveBase, null, null, token).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The centered publication worker could not read closed windows.")]
    private static partial void WindowReadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The centered view for closed window {WindowIdentity} could not publish.")]
    private static partial void WindowFailed(ILogger logger, string windowIdentity, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Closed window {WindowIdentity} for reference capture {CaptureId} ended without a publishable center single.")]
    private static partial void CenterSingleUnavailable(ILogger logger, string windowIdentity, Guid captureId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Closed window {WindowIdentity} for reference capture {CaptureId} exhausted {MaximumAttempts} attempts waiting for its center single.")]
    private static partial void CenterSingleRetryExhausted(ILogger logger, string windowIdentity, Guid captureId, int maximumAttempts);
}
