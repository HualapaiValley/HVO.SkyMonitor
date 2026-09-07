using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

public enum EnvironmentalAcquisitionDisposition
{
    Produced,
    Duplicate,
    Missing,
    Failed,
    TimedOut,
    Coalesced
}

public sealed record EnvironmentalAcquisitionReceipt(
    string SourceId,
    EnvironmentalAcquisitionTrigger Trigger,
    EnvironmentalAcquisitionDisposition Disposition,
    string Reason,
    Guid? ObservationId,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    DateTimeOffset? ObservedAtUtc = null,
    DateTimeOffset? StaleAfterUtc = null);

public sealed partial class EnvironmentalAcquisitionCoordinator : IDisposable
{
    private readonly EnvironmentalAcquisitionOptions _options;
    private readonly IEnvironmentalObservationPublisher _publisher;
    private readonly IDeploymentLocationStore _deploymentLocation;
    private readonly IEnvironmentalAcquisitionStateStore _stateStore;
    private readonly string _root;
    private readonly TimeProvider _timeProvider;
    private readonly EnvironmentalAcquisitionTelemetry? _telemetry;
    private readonly ILogger<EnvironmentalAcquisitionCoordinator>? _logger;
    private readonly IReadOnlyDictionary<string, IEnvironmentalSource> _sources;
    private readonly IReadOnlyDictionary<string, SemaphoreSlim> _sourceGates;
    private readonly Dictionary<string, PendingAcquisition?> _pending;
    private readonly object _pendingGate = new();
    private readonly SemaphoreSlim _globalGate;

    public EnvironmentalAcquisitionCoordinator(
        EnvironmentalSourceFactory factory,
        IEnvironmentalObservationPublisher publisher,
        IDeploymentLocationStore deploymentLocation,
        IOptions<CameraAgentHostOptions> options,
        TimeProvider timeProvider,
        IEnvironmentalAcquisitionStateStore? stateStore = null,
        EnvironmentalAcquisitionTelemetry? telemetry = null,
        ILogger<EnvironmentalAcquisitionCoordinator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(deploymentLocation);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _publisher = publisher;
        _deploymentLocation = deploymentLocation;
        _stateStore = stateStore ?? NullEnvironmentalAcquisitionStateStore.Instance;
        _timeProvider = timeProvider;
        _telemetry = telemetry;
        _logger = logger;
        _options = options.Value.EnvironmentalAcquisition;
        _root = options.Value.RawIngressRoot;
        var sources = factory.Create(_options);
        _sources = sources.ToDictionary(static source => source.Descriptor.Id, StringComparer.Ordinal);
        _sourceGates = sources.ToDictionary(
            static source => source.Descriptor.Id,
            static _ => new SemaphoreSlim(1, 1),
            StringComparer.Ordinal);
        _pending = sources.ToDictionary(
            static source => source.Descriptor.Id,
            static _ => (PendingAcquisition?)null,
            StringComparer.Ordinal);
        _globalGate = new SemaphoreSlim(_options.MaximumConcurrency, _options.MaximumConcurrency);
        _telemetry?.SetSourceCount(sources.Count);
    }

    public IReadOnlyList<EnvironmentalSourceDescriptor> Sources
        => _sources.Values.Select(static source => source.Descriptor).OrderBy(static source => source.Id, StringComparer.Ordinal).ToArray();

    public async ValueTask<EnvironmentalAcquisitionReceipt> AcquireSourceAsync(
        string sourceId,
        EnvironmentalAcquisitionTrigger trigger,
        DateTimeOffset observedAtUtc,
        long? captureSequence = null,
        Guid? captureId = null,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("Environmental acquisition is disabled.");
        }
        if (!_sources.TryGetValue(sourceId, out var source))
        {
            throw new KeyNotFoundException("The environmental source is not configured.");
        }
        if (!source.Descriptor.Triggers.Contains(trigger))
        {
            throw new InvalidOperationException("The environmental source does not support the requested trigger.");
        }
        if (observedAtUtc == default || observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The environmental observation time must be UTC.", nameof(observedAtUtc));
        }
        var started = _timeProvider.GetUtcNow();
        _telemetry?.RecordTrigger(trigger);
        var sourceGate = _sourceGates[sourceId];
        var coalesced = false;
        lock (_pendingGate)
        {
            if (!sourceGate.Wait(0, CancellationToken.None))
            {
                if (trigger != EnvironmentalAcquisitionTrigger.OnDemand)
                {
                    _pending[sourceId] ??= new PendingAcquisition(
                        trigger, observedAtUtc, captureSequence, captureId);
                }
                coalesced = true;
            }
        }
        if (coalesced)
        {
            var receipt = Receipt(
                sourceId, trigger, EnvironmentalAcquisitionDisposition.Coalesced, "trigger-coalesced", null, started);
            LogReceipt(source.Descriptor, receipt);
            await RecordAttemptAsync(source.Descriptor, receipt, captureSequence, captureId, cancellationToken)
                .ConfigureAwait(false);
            _telemetry?.AcquisitionCompleted(source.Descriptor, receipt, wasInFlight: false);
            return receipt;
        }
        var ownsSourceGate = true;
        try
        {
            var first = await AcquireExclusiveAsync(
                source, trigger, observedAtUtc, captureSequence, captureId, started, cancellationToken).ConfigureAwait(false);
            LogReceipt(source.Descriptor, first);
            await RecordAttemptAsync(source.Descriptor, first, captureSequence, captureId, cancellationToken)
                .ConfigureAwait(false);
            while (true)
            {
                PendingAcquisition? pending;
                lock (_pendingGate)
                {
                    pending = _pending[sourceId];
                    _pending[sourceId] = null;
                    if (pending is null)
                    {
                        sourceGate.Release();
                        ownsSourceGate = false;
                    }
                }
                if (pending is null)
                {
                    return first;
                }
                var pendingReceipt = await AcquireExclusiveAsync(
                    source,
                    pending.Trigger,
                    pending.ObservedAtUtc,
                    pending.CaptureSequence,
                    pending.CaptureId,
                    _timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
                LogReceipt(source.Descriptor, pendingReceipt);
                await RecordAttemptAsync(
                    source.Descriptor,
                    pendingReceipt,
                    pending.CaptureSequence,
                    pending.CaptureId,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (ownsSourceGate)
            {
                lock (_pendingGate)
                {
                    sourceGate.Release();
                }
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The catch balances in-flight telemetry before preserving the original exception.")]
    private async ValueTask<EnvironmentalAcquisitionReceipt> AcquireExclusiveAsync(
        IEnvironmentalSource source,
        EnvironmentalAcquisitionTrigger trigger,
        DateTimeOffset observedAtUtc,
        long? captureSequence,
        Guid? captureId,
        DateTimeOffset started,
        CancellationToken cancellationToken)
    {
        using var activity = EnvironmentalAcquisitionTelemetry.ActivitySource.StartActivity("environment.acquire");
        activity?.SetTag("environment.trigger", trigger.ToString());
        activity?.SetTag("environment.observation_kind", source.Descriptor.Kind.ToString());
        await _globalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _telemetry?.AcquisitionStarted();
        try
        {
            var receipt = await AcquireSourceCoreAsync(
                source, trigger, observedAtUtc, captureSequence, captureId, started, cancellationToken).ConfigureAwait(false);
            _telemetry?.AcquisitionCompleted(source.Descriptor, receipt);
            return receipt;
        }
        catch
        {
            _telemetry?.AcquisitionAborted();
            throw;
        }
        finally
        {
            _globalGate.Release();
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Configured source failures are explicit acquisition outcomes and must not terminate independent sources.")]
    private async ValueTask<EnvironmentalAcquisitionReceipt> AcquireSourceCoreAsync(
        IEnvironmentalSource source,
        EnvironmentalAcquisitionTrigger trigger,
        DateTimeOffset observedAtUtc,
        long? captureSequence,
        Guid? captureId,
        DateTimeOffset started,
        CancellationToken cancellationToken)
    {
        var location = _deploymentLocation.Active
            ?? throw new InvalidOperationException("Deployment location is not initialized.");
        if (!location.IsEffectiveAt(observedAtUtc))
        {
            throw new InvalidOperationException("Deployment location is not effective for the observation time.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(_options.SourceTimeoutMilliseconds));
        EnvironmentalSourceAcquisitionResult result;
        try
        {
            result = await source.AcquireAsync(
                new EnvironmentalSourceAcquisitionContext(
                    trigger,
                    observedAtUtc,
                    location,
                    captureSequence,
                    captureId),
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Receipt(source.Descriptor.Id, trigger, EnvironmentalAcquisitionDisposition.TimedOut, "source-timeout", null, started);
        }
        catch (Exception)
        {
            return Receipt(source.Descriptor.Id, trigger, EnvironmentalAcquisitionDisposition.Failed, "source-failure", null, started);
        }
        if (result.Outcome == EnvironmentalSourceAcquisitionOutcome.Missing)
        {
            return Receipt(source.Descriptor.Id, trigger, EnvironmentalAcquisitionDisposition.Missing, result.Reason, null, started);
        }
        if (result.Outcome == EnvironmentalSourceAcquisitionOutcome.Failed || result.Fact is null)
        {
            return Receipt(source.Descriptor.Id, trigger, EnvironmentalAcquisitionDisposition.Failed, result.Reason, null, started);
        }
        try
        {
            var published = await _publisher.PublishAsync(result.Fact, cancellationToken).ConfigureAwait(false);
            return Receipt(
                source.Descriptor.Id,
                trigger,
                published.Disposition == EnvironmentalObservationPublishDisposition.Duplicate
                    ? EnvironmentalAcquisitionDisposition.Duplicate
                    : EnvironmentalAcquisitionDisposition.Produced,
                published.Disposition == EnvironmentalObservationPublishDisposition.Duplicate ? "duplicate" : "produced",
                result.Fact.ObservationId,
                started,
                result.Fact.ObservedAtUtc,
                result.Fact.StaleAfterUtc);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException or
            LocalEnvironmentalObservationCapacityException or EnvironmentalObservationIdentityConflictException)
        {
            return Receipt(source.Descriptor.Id, trigger, EnvironmentalAcquisitionDisposition.Failed, "journal-unavailable", null, started);
        }
    }

    public async Task<IReadOnlyList<EnvironmentalAcquisitionReceipt>> AcquireTriggerAsync(
        EnvironmentalAcquisitionTrigger trigger,
        DateTimeOffset observedAtUtc,
        long? captureSequence = null,
        Guid? captureId = null,
        CancellationToken cancellationToken = default)
    {
        var selected = _sources.Values
            .Where(source => source.Descriptor.Triggers.Contains(trigger) &&
                (trigger != EnvironmentalAcquisitionTrigger.EveryNthCapture ||
                    captureSequence is { } sequence && sequence % source.Descriptor.EveryNthCapture == 0))
            .OrderBy(static source => source.Descriptor.Id, StringComparer.Ordinal)
            .ToArray();
        return await Task.WhenAll(selected.Select(source => AcquireSourceAsync(
            source.Descriptor.Id,
            trigger,
            observedAtUtc,
            captureSequence,
            captureId,
            cancellationToken).AsTask())).ConfigureAwait(false);
    }

    private EnvironmentalAcquisitionReceipt Receipt(
        string sourceId,
        EnvironmentalAcquisitionTrigger trigger,
        EnvironmentalAcquisitionDisposition disposition,
        string reason,
        Guid? observationId,
        DateTimeOffset started,
        DateTimeOffset? observedAtUtc = null,
        DateTimeOffset? staleAfterUtc = null)
        => new(
            sourceId,
            trigger,
            disposition,
            reason,
            observationId,
            started,
            _timeProvider.GetUtcNow(),
            observedAtUtc,
            staleAfterUtc);

    private ValueTask RecordAttemptAsync(
        EnvironmentalSourceDescriptor source,
        EnvironmentalAcquisitionReceipt receipt,
        long? captureSequence,
        Guid? captureId,
        CancellationToken cancellationToken)
        => _stateStore.RecordAttemptAsync(
            _root, source, receipt, captureSequence, captureId, cancellationToken);

    private void LogReceipt(EnvironmentalSourceDescriptor source, EnvironmentalAcquisitionReceipt receipt)
    {
        if (_logger is null)
        {
            return;
        }
        switch (receipt.Disposition)
        {
            case EnvironmentalAcquisitionDisposition.Failed:
                AcquisitionFailed(
                    _logger, source.Kind.ToString(), receipt.Trigger.ToString(), receipt.Disposition.ToString(), receipt.Reason);
                break;
            case EnvironmentalAcquisitionDisposition.TimedOut:
                AcquisitionTimedOut(
                    _logger, source.Kind.ToString(), receipt.Trigger.ToString(), receipt.Disposition.ToString(), receipt.Reason);
                break;
            case EnvironmentalAcquisitionDisposition.Coalesced:
                AcquisitionCoalesced(
                    _logger, source.Kind.ToString(), receipt.Trigger.ToString(), receipt.Disposition.ToString(), receipt.Reason);
                break;
            default:
                AcquisitionCompleted(
                    _logger, source.Kind.ToString(), receipt.Trigger.ToString(), receipt.Disposition.ToString(), receipt.Reason);
                break;
        }
    }

    [LoggerMessage(2521, LogLevel.Information,
        "Environmental acquisition completed for kind {Kind}, trigger {Trigger}, outcome {Outcome}, and reason {Reason}.")]
    private static partial void AcquisitionCompleted(
        ILogger logger, string kind, string trigger, string outcome, string reason);

    [LoggerMessage(2552, LogLevel.Error,
        "Environmental acquisition failed for kind {Kind}, trigger {Trigger}, outcome {Outcome}, and reason {Reason}.")]
    private static partial void AcquisitionFailed(
        ILogger logger, string kind, string trigger, string outcome, string reason);

    [LoggerMessage(2523, LogLevel.Warning,
        "Environmental acquisition timed out for kind {Kind}, trigger {Trigger}, outcome {Outcome}, and reason {Reason}.")]
    private static partial void AcquisitionTimedOut(
        ILogger logger, string kind, string trigger, string outcome, string reason);

    [LoggerMessage(2524, LogLevel.Information,
        "Environmental acquisition coalesced for kind {Kind}, trigger {Trigger}, outcome {Outcome}, and reason {Reason}.")]
    private static partial void AcquisitionCoalesced(
        ILogger logger, string kind, string trigger, string outcome, string reason);

    public void Dispose()
    {
        _globalGate.Dispose();
        foreach (var gate in _sourceGates.Values)
        {
            gate.Dispose();
        }
    }

    private sealed record PendingAcquisition(
        EnvironmentalAcquisitionTrigger Trigger,
        DateTimeOffset ObservedAtUtc,
        long? CaptureSequence,
        Guid? CaptureId);

    private sealed class NullEnvironmentalAcquisitionStateStore : IEnvironmentalAcquisitionStateStore
    {
        public static NullEnvironmentalAcquisitionStateStore Instance { get; } = new();

        public ValueTask RecordAttemptAsync(
            string root,
            EnvironmentalSourceDescriptor source,
            EnvironmentalAcquisitionReceipt receipt,
            long? captureSequence,
            Guid? captureId,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask<bool> RecordCaptureRegimeAsync(
            string root,
            long captureSequence,
            Guid captureId,
            CaptureSolarRegime regime,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(false);

        public ValueTask UpdateSourceScheduleAsync(
            string root,
            EnvironmentalSourceDescriptor source,
            DateTimeOffset nextPollUtc,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<EnvironmentalSourceRuntimeState>> ReadSourceStatesAsync(
            string root,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<EnvironmentalSourceRuntimeState>>([]);

        public ValueTask<IReadOnlyList<EnvironmentalAcquisitionAttemptRecord>> ReadAttemptsAsync(
            string root,
            int maximumResults,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<EnvironmentalAcquisitionAttemptRecord>>([]);
    }
}
