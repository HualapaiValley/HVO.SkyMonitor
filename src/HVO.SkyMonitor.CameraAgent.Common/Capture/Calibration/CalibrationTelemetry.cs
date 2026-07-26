using System.Diagnostics;
using System.Diagnostics.Metrics;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;

public sealed class CalibrationTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.CameraAgent.Calibration";
    public const string ActivitySourceName = MeterName;

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _acquisitions;
    private readonly Histogram<double> _acquisitionDuration;
    private readonly Histogram<double> _masterBuildDuration;
    private readonly Counter<long> _selections;
    private readonly Histogram<double> _selectionDuration;
    private readonly Counter<long> _failures;
    private readonly ILogger<CalibrationTelemetry> _logger;
    private readonly object _inventoryGate = new();
    private CalibrationTelemetryInventory _inventory = CalibrationTelemetryInventory.Empty;

    public CalibrationTelemetry(ILogger<CalibrationTelemetry> logger)
    {
        _logger = logger;
        _acquisitions = _meter.CreateCounter<long>("camera_agent.calibration.acquisitions", "{acquisition}");
        _acquisitionDuration = _meter.CreateHistogram<double>("camera_agent.calibration.acquisition.duration", "ms");
        _masterBuildDuration = _meter.CreateHistogram<double>("camera_agent.calibration.master_build.duration", "ms");
        _selections = _meter.CreateCounter<long>("camera_agent.calibration.selections", "{selection}");
        _selectionDuration = _meter.CreateHistogram<double>("camera_agent.calibration.selection.duration", "ms");
        _failures = _meter.CreateCounter<long>("camera_agent.calibration.failures", "{failure}");
        _meter.CreateObservableGauge("camera_agent.calibration.bundles", ObserveBundles, "{bundle}");
        _meter.CreateObservableGauge("camera_agent.calibration.reference.bytes", ObserveReferenceBytes, "By");
    }

    internal void ReplaceInventory(CalibrationTelemetryInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        lock (_inventoryGate)
        {
            _inventory = inventory;
        }
    }

    internal void AddPublishedBundle(string source, IReadOnlyList<CalibrationTelemetryArtifact> artifacts, long bytes)
    {
        source = NormalizeSource(source);
        lock (_inventoryGate)
        {
            var bundles = new Dictionary<(string State, string Source), long>(_inventory.Bundles)
            {
                [("published", source)] = _inventory.Bundles.GetValueOrDefault(("published", source)) + 1
            };
            var referenceBytes = new Dictionary<(string Kind, string State), long>(_inventory.ReferenceBytes);
            foreach (var artifact in artifacts)
            {
                var key = (NormalizeKind(artifact.Kind), "published");
                referenceBytes[key] = referenceBytes.GetValueOrDefault(key) + artifact.PayloadBytes;
            }
            _inventory = new CalibrationTelemetryInventory(bundles, referenceBytes);
        }
        CalibrationLog.BundlePublished(_logger, source, artifacts.Count, bytes);
        CalibrationLog.RetentionTransition(_logger, "held", source, artifacts.Count, bytes);
    }

    internal void RecordLibraryOperation(
        string operation,
        string outcome,
        int inspected = 0,
        int adopted = 0,
        int quarantined = 0,
        int failed = 0)
        => CalibrationLog.LibraryOperation(
            _logger, NormalizeOperation(operation), NormalizeOutcome(outcome), inspected, adopted, quarantined, failed);

    internal void RecordAcquisitionTransition(
        string previousState,
        string currentState,
        string phase,
        int attemptCount,
        string? reason)
        => CalibrationLog.AcquisitionTransition(
            _logger,
            NormalizeAcquisitionState(previousState),
            NormalizeAcquisitionState(currentState),
            NormalizePhase(phase),
            attemptCount,
            NormalizeReason(reason));

    internal void RecordAcquisition(string outcome, TimeSpan duration)
    {
        outcome = NormalizeOutcome(outcome);
        var tags = new TagList { { "outcome", outcome } };
        _acquisitions.Add(1, tags);
        _acquisitionDuration.Record(duration.TotalMilliseconds, tags);
    }

    internal void RecordMasterBuild(string kind, string outcome, TimeSpan duration)
    {
        var tags = new TagList
        {
            { "kind", NormalizeKind(kind) },
            { "outcome", NormalizeOutcome(outcome) }
        };
        _masterBuildDuration.Record(duration.TotalMilliseconds, tags);
    }

    internal void RecordActivation(string command, string outcome, long stateVersion)
        => CalibrationLog.Activation(
            _logger, NormalizeActivationCommand(command), NormalizeOutcome(outcome), stateVersion);

    internal void RecordSelection(string reason, TimeSpan duration, long stateVersion, bool log)
    {
        reason = NormalizeReason(reason);
        var outcome = reason == "calibration.library.selected" ? "selected" : "rejected";
        var tags = new TagList { { "outcome", outcome }, { "reason", reason } };
        _selections.Add(1, tags);
        _selectionDuration.Record(duration.TotalMilliseconds, tags);
        if (log)
        {
            CalibrationLog.Selection(_logger, outcome, reason, stateVersion);
        }
    }

    internal void RecordFailure(string boundary, string reason)
    {
        boundary = NormalizeBoundary(boundary);
        reason = NormalizeReason(reason);
        _failures.Add(1, new KeyValuePair<string, object?>("boundary", boundary), new("reason", reason));
    }

    internal void RecordValidationFailure(string boundary, string reason)
    {
        RecordFailure(boundary, reason);
        CalibrationLog.ValidationFailure(_logger, NormalizeBoundary(boundary), NormalizeReason(reason));
    }

    internal void RecordQuarantine(string reason, long bytes)
        => CalibrationLog.Quarantine(_logger, NormalizeReason(reason), bytes);

    public void Dispose() => _meter.Dispose();

    private IEnumerable<Measurement<long>> ObserveBundles()
    {
        CalibrationTelemetryInventory snapshot;
        lock (_inventoryGate)
        {
            snapshot = _inventory;
        }
        return snapshot.Bundles.Select(static item => new Measurement<long>(
            item.Value,
            new KeyValuePair<string, object?>("state", item.Key.State),
            new KeyValuePair<string, object?>("source", item.Key.Source)));
    }

    private IEnumerable<Measurement<long>> ObserveReferenceBytes()
    {
        CalibrationTelemetryInventory snapshot;
        lock (_inventoryGate)
        {
            snapshot = _inventory;
        }
        return snapshot.ReferenceBytes.Select(static item => new Measurement<long>(
            item.Value,
            new KeyValuePair<string, object?>("kind", item.Key.Kind),
            new KeyValuePair<string, object?>("state", item.Key.State)));
    }

    public static string NormalizeReason(string? reason)
        => reason switch
        {
            "calibration.library.selected" or
            "calibration.library.adopted" or
            "calibration.library.reconciled" or
            "calibration.library.reconciliation-failed" or
            "calibration.library.validation-pending" or
            CalibrationLibraryReasonCodes.InvalidJson or
            CalibrationLibraryReasonCodes.PayloadTooLarge or
            CalibrationLibraryReasonCodes.UnsupportedSchema or
            CalibrationLibraryReasonCodes.InvalidBundle or
            CalibrationLibraryReasonCodes.Missing or
            CalibrationLibraryReasonCodes.Stale or
            CalibrationLibraryReasonCodes.Corrupt or
            CalibrationLibraryReasonCodes.Incomplete or
            CalibrationLibraryReasonCodes.Ambiguous or
            CalibrationLibraryReasonCodes.IncompatibleIdentity or
            CalibrationLibraryReasonCodes.IncompatibleReadout or
            CalibrationLibraryReasonCodes.IncompatibleConditions or
            CalibrationLibraryReasonCodes.IncompatibleExposure or
            CalibrationLibraryReasonCodes.IncompatibleCodeSpace or
            CalibrationLibraryReasonCodes.Inactive or
            CalibrationLibraryReasonCodes.PublicationConflict or
            CalibrationLibraryReasonCodes.AcquisitionFailure or
            CalibrationLibraryReasonCodes.MasterBuildFailure => reason,
            null => "none",
            _ => "other"
        };

    private static string NormalizeBoundary(string boundary)
        => boundary is "initialize" or "reconcile" or "acquire" or "master-build" or
            "publish" or "activate" or "select" or "retention" ? boundary : "other";

    private static string NormalizeOperation(string operation)
        => operation is "initialize" or "reconcile" ? operation : "other";

    private static string NormalizeActivationCommand(string command)
        => command is "activate" or "rollback" ? command : "other";

    private static string NormalizeAcquisitionState(string state)
        => state is "none" or CalibrationAcquisitionStates.Planned or CalibrationAcquisitionStates.Acquiring or
            CalibrationAcquisitionStates.Building or CalibrationAcquisitionStates.Publishing or
            CalibrationAcquisitionStates.Published or CalibrationAcquisitionStates.Failed or
            CalibrationAcquisitionStates.Cancelled ? state : "other";

    private static string NormalizePhase(string phase)
        => phase switch
        {
            "planned" or "sources-pending" or "masters-pending" or "masters-built" or
            "profile-published" or "published" or "failed" or "cancelled" => phase,
            _ when phase.StartsWith("source-", StringComparison.Ordinal) => "source",
            _ when phase.StartsWith("master-", StringComparison.Ordinal) => "master",
            _ => "other"
        };

    private static string NormalizeKind(string kind)
        => kind is "bias" or "dark" or "flat" or "defect" ? kind : "other";

    private static string NormalizeSource(string source)
        => source is CalibrationLibraryBundleSources.LegacySyntheticV1 or
            CalibrationLibraryBundleSources.VirtualAcquisitionV1 ? source : "other";

    private static string NormalizeOutcome(string outcome)
        => outcome is "success" or "failure" or "cancelled" or "selected" or "rejected" or
            "published" or "failed" or "reconciled" ? outcome : "other";
}

internal sealed record CalibrationTelemetryArtifact(string Kind, long PayloadBytes);

internal sealed record CalibrationTelemetryInventory(
    IReadOnlyDictionary<(string State, string Source), long> Bundles,
    IReadOnlyDictionary<(string Kind, string State), long> ReferenceBytes)
{
    internal static CalibrationTelemetryInventory Empty { get; } = new(
        new Dictionary<(string State, string Source), long>(),
        new Dictionary<(string Kind, string State), long>());
}

internal static partial class CalibrationLog
{
    [LoggerMessage(EventId = 2610, Level = LogLevel.Information,
        Message = "Calibration library {Operation} completed with {Outcome}: inspected {Inspected}, adopted {Adopted}, quarantined {Quarantined}, failed {Failed}")]
    internal static partial void LibraryOperation(
        ILogger logger, string operation, string outcome, int inspected, int adopted, int quarantined, int failed);

    [LoggerMessage(EventId = 2611, Level = LogLevel.Information,
        Message = "Calibration bundle publication completed for source {Source}: {ArtifactCount} artifacts and {ReferenceBytes} reference bytes")]
    internal static partial void BundlePublished(ILogger logger, string source, int artifactCount, long referenceBytes);

    [LoggerMessage(EventId = 2612, Level = LogLevel.Information,
        Message = "Calibration acquisition transitioned from {PreviousState} to {CurrentState} at phase {Phase}, attempt {Attempt}, reason {Reason}")]
    internal static partial void AcquisitionTransition(
        ILogger logger, string previousState, string currentState, string phase, int attempt, string reason);

    [LoggerMessage(EventId = 2613, Level = LogLevel.Information,
        Message = "Calibration {Command} completed with {Outcome} at state version {StateVersion}")]
    internal static partial void Activation(ILogger logger, string command, string outcome, long stateVersion);

    [LoggerMessage(EventId = 2614, Level = LogLevel.Information,
        Message = "Calibration selection completed with {Outcome}, reason {Reason}, state version {StateVersion}")]
    internal static partial void Selection(ILogger logger, string outcome, string reason, long stateVersion);

    [LoggerMessage(EventId = 2615, Level = LogLevel.Warning,
        Message = "Calibration validation failed at {Boundary} with reason {Reason}")]
    internal static partial void ValidationFailure(ILogger logger, string boundary, string reason);

    [LoggerMessage(EventId = 2616, Level = LogLevel.Warning,
        Message = "Calibration evidence was quarantined with reason {Reason}, totaling {ReferenceBytes} bytes")]
    internal static partial void Quarantine(ILogger logger, string reason, long referenceBytes);

    [LoggerMessage(EventId = 2617, Level = LogLevel.Information,
        Message = "Calibration retention transitioned to {Transition} for source {Source}: {ArtifactCount} artifacts and {ReferenceBytes} reference bytes")]
    internal static partial void RetentionTransition(
        ILogger logger, string transition, string source, int artifactCount, long referenceBytes);
}
