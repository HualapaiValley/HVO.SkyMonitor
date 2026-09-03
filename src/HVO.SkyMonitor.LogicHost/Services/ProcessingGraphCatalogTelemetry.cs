using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class ProcessingGraphCatalogTelemetry : IDisposable
{
    internal const string MeterName = "HVO.SkyMonitor.LogicHost.ProcessingGraphs";
    internal const string ActivitySourceName = MeterName;

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");

    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly Counter<long> _operations;
    private readonly Histogram<double> _duration;
    private readonly TimeProvider _timeProvider;
    private long _pendingProposalCount;
    private long _oldestPendingProposalUnixMs = -1;

    public ProcessingGraphCatalogTelemetry(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _operations = _meter.CreateCounter<long>("hvo.processing_graph.operations");
        _duration = _meter.CreateHistogram<double>("hvo.processing_graph.operation.duration", "ms");
        _meter.CreateObservableGauge(
            "hvo.processing_graph.pending_proposals",
            () => Interlocked.Read(ref _pendingProposalCount),
            "proposal");
        _meter.CreateObservableGauge(
            "hvo.processing_graph.oldest_pending_proposal_age",
            ReadOldestPendingAgeSeconds,
            "s");
    }

    internal void Record(string operation, string outcome, TimeSpan duration)
    {
        TagList tags = default;
        tags.Add("operation", operation);
        tags.Add("outcome", outcome);
        _operations.Add(1, tags);
        _duration.Record(duration.TotalMilliseconds, tags);
    }

    internal void RecordBacklog(long count, DateTimeOffset? oldestPendingUtc)
    {
        Interlocked.Exchange(ref _pendingProposalCount, count);
        Interlocked.Exchange(
            ref _oldestPendingProposalUnixMs,
            oldestPendingUtc?.ToUnixTimeMilliseconds() ?? -1);
    }

    private double ReadOldestPendingAgeSeconds()
    {
        var oldestUnixMs = Interlocked.Read(ref _oldestPendingProposalUnixMs);
        return oldestUnixMs < 0
            ? 0
            : Math.Max(0, (_timeProvider.GetUtcNow() -
                DateTimeOffset.FromUnixTimeMilliseconds(oldestUnixMs)).TotalSeconds);
    }

    public void Dispose() => _meter.Dispose();
}

internal sealed class ProcessingGraphBacklogSampler(
    ApplicationDbContext dbContext,
    ProcessingGraphCatalogTelemetry telemetry)
{
    internal async Task SampleAsync(CancellationToken cancellationToken)
    {
        var pending = dbContext.CentralProcessingGraphDeliveryProposals.AsNoTracking()
            .Where(item => !item.Facts.Any(fact =>
                fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Accepted) ||
                fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Rejected) ||
                fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Expired) ||
                fact.Kind == nameof(ProcessingGraphDeliveryFactKind.Superseded)));
        var count = await pending.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var oldest = await pending.OrderBy(item => item.IssuedAtUtc)
            .Select(item => (DateTimeOffset?)item.IssuedAtUtc)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        telemetry.RecordBacklog(count, oldest);
    }
}

[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "A failed SQL sample must not terminate periodic backlog observation.")]
internal sealed partial class ProcessingGraphBacklogWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ProcessingGraphBacklogWorker> logger) : BackgroundService
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ProcessingGraphBacklogSampler>()
                    .SampleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Log.SampleFailed(logger, exception);
            }
            await Task.Delay(SampleInterval, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(2194, LogLevel.Warning, "Processing graph backlog SQL sample failed")]
        internal static partial void SampleFailed(ILogger logger, Exception exception);
    }
}
