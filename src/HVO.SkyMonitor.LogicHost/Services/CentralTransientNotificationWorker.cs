using System.Data;
using System.Data.Common;
using System.Diagnostics;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class CentralTransientNotificationOptions
{
    public const string SectionName = "CentralTransientNotification";
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan FenceTimeout { get; init; } = TimeSpan.FromMinutes(1);
}

internal interface ICentralTransientNotificationProcessor
{
    Task<bool> ProcessNextAsync(CancellationToken cancellationToken);
}

internal sealed class CentralTransientNotificationProcessor(
    ApplicationDbContext dbContext,
    ICentralTransientEventVersionAppender versionAppender,
    IEmailNotificationService emailService,
    TimeProvider timeProvider,
    IOptions<CentralTransientNotificationOptions> options,
    CentralTransientLifecycleTelemetry? telemetry = null) : ICentralTransientNotificationProcessor
{
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        using var activity = telemetry?.Start("notification", ActivityKind.Consumer);
        var abandonedBeforeUtc = timeProvider.GetUtcNow() - options.Value.FenceTimeout;
        var fenced = await dbContext.CentralTransientNotificationDispatches.AsNoTracking()
            .Where(item => item.State == CentralTransientNotificationDispatchState.Fenced &&
                item.FencedUtc <= abandonedBeforeUtc)
            .OrderBy(item => item.FencedUtc).ThenBy(item => item.DispatchId)
            .Select(item => item.DispatchId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (fenced != Guid.Empty)
        {
            await FinalizeAsync(
                fenced,
                TransientNotificationState.Failed,
                "notification.ambiguous-post-fence",
                cancellationToken).ConfigureAwait(false);
            telemetry?.RecordNotification("recovered", timeProvider.GetElapsedTime(started));
            return true;
        }

        CentralTransientNotificationDispatch? dispatch;
        var superseded = false;
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(
                         IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false))
        {
            dispatch = await dbContext.CentralTransientNotificationDispatches
                .FromSqlInterpolated($"""
                    SELECT TOP(1) dispatch.*
                    FROM [CentralTransientNotificationDispatches] AS dispatch
                        WITH (UPDLOCK, READPAST, ROWLOCK)
                    WHERE dispatch.[State] = N'Pending'
                    ORDER BY dispatch.[CreatedUtc], dispatch.[DispatchId]
                    """)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (dispatch is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
            _ = await dbContext.Database.SqlQuery<int>(
                    $"SELECT CAST(1 AS int) AS [Value] FROM [CentralTransientEvents] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {dispatch.CentralTransientEventId}")
                .SingleAsync(cancellationToken).ConfigureAwait(false);
            superseded = !await dbContext.CentralTransientEventCurrent.AsNoTracking().AnyAsync(item =>
                item.CentralTransientEventId == dispatch.CentralTransientEventId &&
                item.ActiveAssessmentId == dispatch.AssessmentId &&
                item.LatestReviewId == dispatch.ReviewId &&
                item.ReviewState != CentralTransientReviewState.NeedsReview, cancellationToken).ConfigureAwait(false);
            var fencedUtc = timeProvider.GetUtcNow();
            if (fencedUtc < dispatch.CreatedUtc)
            {
                fencedUtc = dispatch.CreatedUtc;
            }
            dispatch.State = CentralTransientNotificationDispatchState.Fenced;
            dispatch.FencedUtc = fencedUtc;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (superseded)
        {
            await FinalizeAsync(dispatch.DispatchId, TransientNotificationState.Failed,
                "notification.superseded-before-dispatch", cancellationToken).ConfigureAwait(false);
            telemetry?.RecordNotification("suppressed", timeProvider.GetElapsedTime(started));
            return true;
        }

        try
        {
            await emailService.SendAsync(
                dispatch.Recipient!,
                "SkyMonitor meteor review",
                $"Reviewed meteor event {dispatch.CentralTransientEventId:D} is available in SkyMonitor.",
                cancellationToken).ConfigureAwait(false);
            await FinalizeAsync(dispatch.DispatchId, TransientNotificationState.Sent, null, cancellationToken)
                .ConfigureAwait(false);
            telemetry?.RecordNotification("sent", timeProvider.GetElapsedTime(started));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FinalizeAsync(
                dispatch.DispatchId,
                TransientNotificationState.Failed,
                "notification.ambiguous-post-fence",
                CancellationToken.None).ConfigureAwait(false);
            telemetry?.RecordNotification("failed", timeProvider.GetElapsedTime(started));
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Net.Mail.SmtpException)
        {
            await FinalizeAsync(
                dispatch.DispatchId,
                TransientNotificationState.Failed,
                "notification.dispatch-failed",
                CancellationToken.None).ConfigureAwait(false);
            telemetry?.RecordNotification("failed", timeProvider.GetElapsedTime(started));
        }
        return true;
    }

    private async Task FinalizeAsync(
        Guid dispatchId,
        TransientNotificationState state,
        string? reasonCode,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var dispatch = await dbContext.CentralTransientNotificationDispatches.SingleAsync(
            item => item.DispatchId == dispatchId && item.State == CentralTransientNotificationDispatchState.Fenced,
            cancellationToken).ConfigureAwait(false);
        var notificationId = Guid.NewGuid();
        var appended = await versionAppender.AppendGeneratedAsync(dispatch.CentralTransientEventId, previous =>
        {
            var createdUtc = timeProvider.GetUtcNow();
            var predecessor = previous.Notifications.Single(item => item.NotificationId == dispatch.LatestNotificationId);
            if (createdUtc <= previous.VersionCreatedUtc || createdUtc <= predecessor.CreatedUtc)
            {
                createdUtc = new[] { previous.VersionCreatedUtc, predecessor.CreatedUtc }.Max().AddTicks(1);
            }
            return previous with
            {
                EventVersionId = Guid.NewGuid(),
                Version = previous.Version + 1,
                PreviousEventVersionId = previous.EventVersionId,
                PreviousVersionCreatedUtc = previous.VersionCreatedUtc,
                VersionCreatedUtc = createdUtc,
                Notifications = previous.Notifications.Append(new TransientNotificationV1(
                    notificationId,
                    createdUtc,
                    dispatch.Channel,
                    state,
                    dispatch.AssessmentId,
                    reasonCode,
                    predecessor.NotificationId)).ToArray()
            };
        }, resetReviewState: false, cancellationToken).ConfigureAwait(false);
        dispatch.State = state == TransientNotificationState.Sent
            ? CentralTransientNotificationDispatchState.Sent
            : CentralTransientNotificationDispatchState.Failed;
        dispatch.LatestNotificationId = notificationId;
        dispatch.CompletedUtc = appended.Event.VersionCreatedUtc;
        dispatch.ReasonCode = reasonCode;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class CentralTransientNotificationWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<CentralTransientNotificationOptions> options,
    CentralTransientLifecycleTelemetry telemetry) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processed = await scope.ServiceProvider.GetRequiredService<ICentralTransientNotificationProcessor>()
                    .ProcessNextAsync(stoppingToken).ConfigureAwait(false);
                if (!processed)
                {
                    await Task.Delay(options.Value.PollInterval, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (DbException)
            {
                telemetry.RecordNotification("failed", TimeSpan.Zero);
                await Task.Delay(options.Value.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                telemetry.RecordNotification("failed", TimeSpan.Zero);
                await Task.Delay(options.Value.PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
