using System.Threading.Channels;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;

internal sealed class CaptureDistributionService : IHostedService, ICaptureDistributor, IDisposable
{
    private readonly ICameraAgentConfigurationAccessor _configurationAccessor;
    private readonly IRawCaptureIngress _rawIngress;
    private readonly ICaptureLaneStore _store;
    private readonly CaptureLanePolicy _policy;
    private readonly Dictionary<string, ICaptureLaneHandler> _handlers;
    private readonly StandardCaptureLaneHandler _standardHandler;
    private readonly CaptureDistributionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ICaptureLaneFaultInjector _faultInjector;
    private readonly CaptureLaneTelemetry _telemetry;
    private readonly CaptureLaneState _state;
    private readonly ILogger<CaptureDistributionService> _logger;
    private readonly ProcessingGraphOperationsCoordinator? _graphOperations;
    private readonly Dictionary<string, Channel<bool>> _signals = new(StringComparer.Ordinal);
    private readonly Channel<FrameProcessingItem> _ephemeral = Channel.CreateBounded<FrameProcessingItem>(
        new BoundedChannelOptions(4)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    private readonly CancellationTokenSource _abort = new();
    private readonly List<Task> _workers = [];
    private CameraModuleConfig? _configuration;
    private volatile bool _draining;
    private static readonly Action<ILogger, string, Exception?> ClaimFailed = LoggerMessage.Define<string>(
        LogLevel.Error,
        new EventId(2060, "CaptureLaneClaimFailed"),
        "Capture lane {Lane} claim failed.");
    private static readonly Action<ILogger, string, Exception?> HandlerFailed = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(2061, "CaptureLaneHandlerFailed"),
        "Capture lane {Lane} handler failed.");
    private static readonly Action<ILogger, string, Exception?> LeaseLost = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(2062, "CaptureLaneLeaseLost"),
        "Capture lane {Lane} lost its lease.");

    public CaptureDistributionService(
        ICameraAgentConfigurationAccessor configurationAccessor,
        IRawCaptureIngress rawIngress,
        ICaptureLaneStore store,
        CaptureLanePolicy policy,
        IEnumerable<ICaptureLaneHandler> handlers,
        StandardCaptureLaneHandler standardHandler,
        IOptions<CameraAgentHostOptions> options,
        TimeProvider timeProvider,
        ICaptureLaneFaultInjector faultInjector,
        CaptureLaneTelemetry telemetry,
        CaptureLaneState state,
        ILogger<CaptureDistributionService> logger,
        ProcessingGraphOperationsCoordinator? graphOperations = null)
    {
        _configurationAccessor = configurationAccessor;
        _rawIngress = rawIngress;
        _store = store;
        _policy = policy;
        _standardHandler = standardHandler;
        _options = options.Value.CaptureDistribution;
        _timeProvider = timeProvider;
        _faultInjector = faultInjector;
        _telemetry = telemetry;
        _state = state;
        _logger = logger;
        _graphOperations = graphOperations;
        _handlers = handlers.ToDictionary(static handler => handler.Lane, StringComparer.Ordinal);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var activity = CaptureLaneTelemetry.ActivitySource.StartActivity("capture-lanes.initialize");
        _configuration = await _configurationAccessor.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false);
        foreach (var lane in _policy.Definitions.Where(static lane => lane.Enabled))
        {
            if (!_handlers.ContainsKey(lane.Name))
            {
                throw new InvalidOperationException($"Capture lane '{lane.Name}' has no registered handler.");
            }
        }
        await _rawIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (_graphOperations is not null)
        {
            _ = await _graphOperations.EnsureConfiguredBasicAsync(_configuration, cancellationToken)
                .ConfigureAwait(false);
            await _rawIngress.BindRecoveredLiveExecutionsAsync(_configuration, cancellationToken).ConfigureAwait(false);
        }
        await _store.InitializeLanesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var lane in _policy.Definitions.Where(static lane => lane.Enabled))
        {
            var handler = _handlers[lane.Name];
            var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
            _signals.Add(lane.Name, signal);
            _workers.Add(RunLaneAsync(lane, handler, signal.Reader));
        }
        _workers.Add(RunEphemeralAsync());
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.CaptureLanesInitialized(
                SqliteRawCaptureJournal.CurrentSchemaVersion,
                _policy.Definitions.Count(static lane => lane.Enabled));
        }
        activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _draining = true;
        _ephemeral.Writer.TryComplete();
        foreach (var signal in _signals.Values)
        {
            signal.Writer.TryWrite(true);
        }

        var workers = Task.WhenAll(_workers);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.ShutdownDrainSeconds));
        try
        {
            await workers.WaitAsync(timeout.Token).ConfigureAwait(false);
            _logger.CaptureLaneDrainCompleted();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            await _abort.CancelAsync().ConfigureAwait(false);
            await Task.WhenAny(
                workers,
                Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, CancellationToken.None)).ConfigureAwait(false);
            _logger.CaptureLaneDrainAborted();
        }
    }

    public void NotifyCommittedCapture()
    {
        foreach (var lane in _policy.Definitions.Where(static lane => lane.Enabled))
        {
            if (_signals.TryGetValue(lane.Name, out var signal))
            {
                _telemetry.RecordWakeup(lane.Name, lane.Required, signal.Writer.TryWrite(true));
            }
        }
    }

    public ValueTask ProcessEphemeralAsync(
        CameraModuleConfig configuration,
        CaptureLoopSubmission submission,
        CancellationToken cancellationToken)
        => _ephemeral.Writer.WriteAsync(
            new FrameProcessingItem(configuration, submission), cancellationToken);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed durable claim must not terminate independent lane recovery.")]
    private async Task RunLaneAsync(
        CaptureLaneDefinition lane,
        ICaptureLaneHandler handler,
        ChannelReader<bool> signal)
    {
        var owner = $"{Environment.ProcessId}:{lane.Name}:{Guid.NewGuid():N}";
        while (!_abort.IsCancellationRequested)
        {
            CaptureLaneLease? lease;
            try
            {
                lease = await _store.ClaimAsync(
                    lane,
                    owner,
                    _configuration!,
                    _abort.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_abort.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _state.SetUnhealthy("lane-claim-failed");
                ClaimFailed(_logger, lane.Name, exception);
                await DelayAsync(_options.PollIntervalMilliseconds, _abort.Token).ConfigureAwait(false);
                continue;
            }

            if (lease is null)
            {
                if (_draining)
                {
                    return;
                }
                await WaitForSignalAsync(signal, _abort.Token).ConfigureAwait(false);
                continue;
            }

            try
            {
                await ProcessLeaseAsync(handler, lease).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_abort.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _state.SetUnhealthy("lane-worker-failed");
                HandlerFailed(_logger, lane.Name, exception);
                try
                {
                    await _store.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception releaseException)
                {
                    LeaseLost(_logger, lane.Name, releaseException);
                }
                await DelayAsync(_options.PollIntervalMilliseconds, _abort.Token).ConfigureAwait(false);
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Handler exceptions are converted into durable retry state so one lane cannot terminate another.")]
    private async Task ProcessLeaseAsync(ICaptureLaneHandler handler, CaptureLaneLease lease)
    {
        var processingCancellation = CancellationTokenSource.CreateLinkedTokenSource(_abort.Token);
        var renewalCancellation = new CancellationTokenSource();
        var renewal = RenewLeaseAsync(lease, processingCancellation, renewalCancellation.Token);
        using var activity = CaptureLaneTelemetry.ActivitySource.StartActivity("capture-lanes.process");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            _faultInjector.Inject(CaptureLaneFaultPoint.BeforeHandler);
            CaptureLaneHandlerResult result;
            try
            {
                result = await handler.HandleAsync(lease.Context, processingCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (processingCancellation.IsCancellationRequested)
            {
                await _store.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (Exception exception)
            {
                HandlerFailed(_logger, lease.Lane, exception);
                result = CaptureLaneHandlerResult.Retry("handler-exception");
            }
            _faultInjector.Inject(CaptureLaneFaultPoint.AfterHandler);
            _telemetry.RecordProcessing(
                lease.Lane,
                lease.Required,
                result.Outcome,
                System.Diagnostics.Stopwatch.GetElapsedTime(started));
            activity?.SetTag("lane", lease.Lane);
            activity?.SetTag("outcome", result.Outcome.ToString());
            if (result.Outcome == CaptureLaneHandlerOutcome.Completed)
            {
                using var ack = CaptureLaneTelemetry.ActivitySource.StartActivity("capture-lanes.ack");
                await _store.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
                ack?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            }
            else
            {
                using var ack = CaptureLaneTelemetry.ActivitySource.StartActivity("capture-lanes.ack");
                await _store.FailAsync(lease, result, CancellationToken.None).ConfigureAwait(false);
                ack?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            }
            if (string.Equals(lease.Lane, "standard", StringComparison.Ordinal))
            {
                _graphOperations?.NotifyLiveWorkChanged();
            }
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
        }
        catch (CaptureLaneLeaseLostException exception)
        {
            LeaseLost(_logger, lease.Lane, exception);
        }
        finally
        {
            try
            {
                await renewalCancellation.CancelAsync().ConfigureAwait(false);
                try
                {
                    await renewal.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (renewalCancellation.IsCancellationRequested)
                {
                }
            }
            finally
            {
                renewalCancellation.Dispose();
                processingCancellation.Dispose();
            }
        }
    }

    private async Task RenewLeaseAsync(
        CaptureLaneLease lease,
        CancellationTokenSource processingCancellation,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await DelayAsync(_options.LeaseRenewalSeconds * 1_000, cancellationToken).ConfigureAwait(false);
                if (!await _store.RenewAsync(lease, cancellationToken).ConfigureAwait(false))
                {
                    await processingCancellation.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await processingCancellation.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "An ephemeral processing failure must not terminate the only consumer and block acquisition.")]
    private async Task RunEphemeralAsync()
    {
        await foreach (var item in _ephemeral.Reader.ReadAllAsync(_abort.Token).ConfigureAwait(false))
        {
            try
            {
                await _standardHandler.ProcessEphemeralAsync(
                    item.Config,
                    item.Submission,
                    _abort.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_abort.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _state.SetUnhealthy("ephemeral-worker-failed");
                HandlerFailed(_logger, "standard", exception);
            }
        }
    }

    private async Task WaitForSignalAsync(ChannelReader<bool> signal, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(_options.PollIntervalMilliseconds));
        try
        {
            await signal.ReadAsync(timeout.Token).ConfigureAwait(false);
            while (signal.TryRead(out _))
            {
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
        => Task.Delay(TimeSpan.FromMilliseconds(milliseconds), _timeProvider, cancellationToken);

    public void Dispose()
    {
        _abort.Dispose();
    }
}
