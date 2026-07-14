using System;
using HVO.SkyMonitor.AgentCore;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Logging;

internal static partial class CameraAgentLog
{
    [LoggerMessage(EventId = 2000, Level = LogLevel.Error, Message = "Camera agent configuration file not found at {Path}")]
    public static partial void ConfigurationFileMissing(this ILogger logger, string path);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Error, Message = "Camera agent configuration file {Path} could not be deserialized")]
    public static partial void ConfigurationFileInvalid(this ILogger logger, string path);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Information, Message = "Camera agent configuration loaded from {Path}")]
    public static partial void ConfigurationLoaded(this ILogger logger, string path);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Information, Message = "Camera agent configuration initialized for module type {ModuleType}")]
    public static partial void ConfigurationInitialized(this ILogger logger, string moduleType);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Information, Message = "Stored frame at {Path}")]
    public static partial void FrameStored(this ILogger logger, string path);

    [LoggerMessage(EventId = 2026, Level = LogLevel.Warning, Message = "Skipped invalid or incomplete stored-frame entry in {IndexPath}")]
    public static partial void FrameBrowseEntrySkipped(this ILogger logger, string indexPath);

    [LoggerMessage(EventId = 2015, Level = LogLevel.Warning, Message = "Failed to store frame to root {StorageRoot}")]
    public static partial void FrameStorageFailed(this ILogger logger, string storageRoot, Exception exception);

    [LoggerMessage(EventId = 2005, Level = LogLevel.Error, Message = "Retention sweep failed")]
    public static partial void RetentionSweepFailed(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2006, Level = LogLevel.Information, Message = "Deleted frame directory {Directory}")]
    public static partial void FrameDirectoryDeleted(this ILogger logger, string directory);

    [LoggerMessage(EventId = 2007, Level = LogLevel.Information, Message = "Deleted index file {File}")]
    public static partial void IndexFileDeleted(this ILogger logger, string file);

    [LoggerMessage(EventId = 2008, Level = LogLevel.Information, Message = "Deleted derived output {File}")]
    public static partial void DerivedFileDeleted(this ILogger logger, string file);

    [LoggerMessage(EventId = 2025, Level = LogLevel.Information, Message = "Retention sweep completed for {StorageRoot}: deleted {DeletedFileCount} files and protected {ProtectedArtifactCount} pending artifacts")]
    public static partial void RetentionSweepCompleted(
        this ILogger logger,
        string StorageRoot,
        int DeletedFileCount,
        int ProtectedArtifactCount);

    [LoggerMessage(EventId = 2009, Level = LogLevel.Information, Message = "Camera module {Module} initialized")]
    public static partial void CameraModuleInitialized(this ILogger logger, string module);

    [LoggerMessage(EventId = 2010, Level = LogLevel.Error, Message = "Capture loop encountered an error")]
    public static partial void CaptureLoopFailed(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2032, Level = LogLevel.Warning, Message = "Capture failure {ConsecutiveFailures}; retrying after {DelayMilliseconds} ms")]
    public static partial void CaptureFailureBackoff(this ILogger logger, int consecutiveFailures, double delayMilliseconds);

    [LoggerMessage(EventId = 2033, Level = LogLevel.Information, Message = "Capture recovered after {ConsecutiveFailures} consecutive failures")]
    public static partial void CaptureRecovered(this ILogger logger, int consecutiveFailures);

    [LoggerMessage(EventId = 2034, Level = LogLevel.Error, Message = "Camera module returned no capture result on consecutive failure {ConsecutiveFailures}")]
    public static partial void CaptureReturnedNull(this ILogger logger, int consecutiveFailures);

    [LoggerMessage(EventId = 2011, Level = LogLevel.Information, Message = "Created camera module {ModuleType} ({Implementation})")]
    public static partial void CameraModuleCreated(this ILogger logger, string moduleType, string implementation);

    [LoggerMessage(EventId = 2012, Level = LogLevel.Debug, Message = "Capture cycle completed in {ElapsedMilliseconds} ms (Interval {IntervalMilliseconds} ms, Exposure {ExposureMilliseconds} ms, Gain {Gain}, Mode {Mode}, ImmediateUpload {ImmediateUpload})")]
    public static partial void CaptureCycleCompleted(
        this ILogger logger,
        double ElapsedMilliseconds,
        double IntervalMilliseconds,
        double ExposureMilliseconds,
        double Gain,
        CaptureMode Mode,
        bool ImmediateUpload);

    [LoggerMessage(EventId = 2013, Level = LogLevel.Warning, Message = "Capture processing step {Step} failed for frame captured at {TimestampUtc}")]
    public static partial void CaptureProcessingStepFailed(this ILogger logger, string Step, DateTimeOffset TimestampUtc, Exception exception);

    [LoggerMessage(EventId = 2014, Level = LogLevel.Debug, Message = "Built capture processing pipeline with {StepCount} steps")]
    public static partial void CaptureProcessingPipelineBuilt(this ILogger logger, int StepCount);

    [LoggerMessage(EventId = 2027, Level = LogLevel.Information, Message = "Capture processing channel drained")]
    public static partial void CaptureProcessingDrainCompleted(this ILogger logger);

    [LoggerMessage(EventId = 2028, Level = LogLevel.Warning, Message = "Capture processing channel drain aborted by the host shutdown deadline")]
    public static partial void CaptureProcessingDrainAborted(this ILogger logger);

    [LoggerMessage(EventId = 2029, Level = LogLevel.Warning, Message = "Disk pressure entered for {StorageRoot} at {AvailablePercent:F2}% available")]
    public static partial void DiskPressureEntered(this ILogger logger, string storageRoot, double availablePercent);

    [LoggerMessage(EventId = 2030, Level = LogLevel.Information, Message = "Disk pressure recovered for {StorageRoot} at {AvailablePercent:F2}% available")]
    public static partial void DiskPressureRecovered(this ILogger logger, string storageRoot, double availablePercent);

    [LoggerMessage(EventId = 2031, Level = LogLevel.Error, Message = "Storage capacity probe failed for {StorageRoot}")]
    public static partial void StorageCapacityProbeFailed(this ILogger logger, string storageRoot, Exception exception);

    [LoggerMessage(EventId = 2020, Level = LogLevel.Information, Message = "NoOp storage step {Step} skipped because no frame was captured")]
    public static partial void NoOpStorageSkipped(this ILogger logger, string Step);

    [LoggerMessage(EventId = 2021, Level = LogLevel.Information, Message = "NoOp storage step {Step} would store frame captured at {TimestampUtc} to {Root} with retention {RetentionDays} days")]
    public static partial void NoOpStoragePlanned(this ILogger logger, string Step, DateTimeOffset TimestampUtc, string Root, int RetentionDays);

    [LoggerMessage(EventId = 2022, Level = LogLevel.Debug, Message = "NoOp upload step {Step} skipped upload (Immediate={Immediate}, UploadAll={UploadAll})")]
    public static partial void NoOpUploadSkipped(this ILogger logger, string Step, bool Immediate, bool UploadAll);

    [LoggerMessage(EventId = 2023, Level = LogLevel.Information, Message = "NoOp upload step {Step} would upload frame captured at {TimestampUtc} to {Endpoint} with batch size {BatchSize} after warmup {WarmupSeconds}s")]
    public static partial void NoOpUploadPlanned(this ILogger logger, string Step, DateTimeOffset TimestampUtc, string Endpoint, int BatchSize, int WarmupSeconds);

    [LoggerMessage(EventId = 2024, Level = LogLevel.Debug, Message = "Calibration step {Step} applying {Strategy} strategy with {Passes} passes and max {MaxSeconds}s window")]
    public static partial void CalibrationApplying(this ILogger logger, string Step, string Strategy, int Passes, int MaxSeconds);

    [LoggerMessage(EventId = 2040, Level = LogLevel.Information, Message = "Raw ingress initialized at schema {SchemaVersion} with {PendingCount} held captures")]
    public static partial void RawIngressInitialized(this ILogger logger, int schemaVersion, long pendingCount);

    [LoggerMessage(EventId = 2041, Level = LogLevel.Information, Message = "Raw ingress reconciliation inspected {Inspected}, recovered {Recovered}, cleaned {Cleaned}, quarantined {Quarantined}, and found {MissingEvidence} missing committed records")]
    public static partial void RawIngressReconciled(
        this ILogger logger, int inspected, int recovered, int cleaned, int quarantined, int missingEvidence);

    [LoggerMessage(EventId = 2042, Level = LogLevel.Debug, Message = "Raw ingress committed {PayloadBytes} bytes in {DurationMilliseconds} ms")]
    public static partial void RawIngressCommitted(this ILogger logger, long payloadBytes, double durationMilliseconds);

    [LoggerMessage(EventId = 2043, Level = LogLevel.Debug, Message = "Raw ingress accepted an existing idempotent capture in {DurationMilliseconds} ms")]
    public static partial void RawIngressExisting(this ILogger logger, double durationMilliseconds);

    [LoggerMessage(EventId = 2044, Level = LogLevel.Error, Message = "Raw ingress refused capture during {Phase} because {Reason}")]
    public static partial void RawIngressRefused(this ILogger logger, string phase, string reason);

    [LoggerMessage(EventId = 2045, Level = LogLevel.Information, Message = "Raw ingress recovered and is accepting captures")]
    public static partial void RawIngressRecovered(this ILogger logger);

    [LoggerMessage(EventId = 2046, Level = LogLevel.Warning, Message = "Raw ingress reconciliation quarantined {RecordCount} records totaling {PayloadBytes} bytes")]
    public static partial void RawIngressQuarantined(this ILogger logger, int recordCount, long payloadBytes);

    [LoggerMessage(EventId = 2047, Level = LogLevel.Critical, Message = "Raw ingress schema or integrity validation failed because {Reason}")]
    public static partial void RawIngressIntegrityFailed(this ILogger logger, string reason);

    [LoggerMessage(EventId = 2048, Level = LogLevel.Warning, Message = "Raw ingress SQLite operation {Operation} completed with result {Result}")]
    public static partial void RawIngressSqliteResult(this ILogger logger, string operation, string result);

    [LoggerMessage(EventId = 2049, Level = LogLevel.Warning, Message = "Raw ingress compatibility index projection failed and will be repaired at restart")]
    public static partial void RawIngressIndexProjectionFailed(this ILogger logger);

    [LoggerMessage(EventId = 2050, Level = LogLevel.Information, Message = "Capture lanes initialized at schema {SchemaVersion} with {LaneCount} configured lanes")]
    public static partial void CaptureLanesInitialized(this ILogger logger, int schemaVersion, int laneCount);

    [LoggerMessage(EventId = 2051, Level = LogLevel.Information, Message = "Capture lane {Lane} recovered durable work at attempt {Attempt}")]
    public static partial void CaptureLaneRecovered(this ILogger logger, string lane, int attempt);

    [LoggerMessage(EventId = 2052, Level = LogLevel.Debug, Message = "Capture lane {Lane} created {Outcome} work (Required={Required})")]
    public static partial void CaptureLaneWorkCreated(this ILogger logger, string lane, bool required, string outcome);

    [LoggerMessage(EventId = 2053, Level = LogLevel.Debug, Message = "Capture lane {Lane} claimed durable work at attempt {Attempt}")]
    public static partial void CaptureLaneClaimed(this ILogger logger, string lane, int attempt);

    [LoggerMessage(EventId = 2054, Level = LogLevel.Debug, Message = "Capture lane {Lane} completed durable work")]
    public static partial void CaptureLaneCompleted(this ILogger logger, string lane);

    [LoggerMessage(EventId = 2055, Level = LogLevel.Warning, Message = "Capture lane {Lane} scheduled retry attempt {Attempt} because {Reason}")]
    public static partial void CaptureLaneRetryScheduled(this ILogger logger, string lane, int attempt, string reason);

    [LoggerMessage(EventId = 2056, Level = LogLevel.Warning, Message = "Capture lane {Lane} reached terminal outcome {Outcome} because {Reason}")]
    public static partial void CaptureLaneTerminal(this ILogger logger, string lane, string outcome, string reason);

    [LoggerMessage(EventId = 2057, Level = LogLevel.Warning, Message = "Capture lane availability changed to {Availability} because {Reason}")]
    public static partial void CaptureLanePressureChanged(this ILogger logger, string availability, string reason);

    [LoggerMessage(EventId = 2063, Level = LogLevel.Information, Message = "Capture lane pressure recovered and availability is {Availability}")]
    public static partial void CaptureLanePressureRecovered(this ILogger logger, string availability);

    [LoggerMessage(EventId = 2058, Level = LogLevel.Information, Message = "Capture lane workers drained before shutdown")]
    public static partial void CaptureLaneDrainCompleted(this ILogger logger);

    [LoggerMessage(EventId = 2059, Level = LogLevel.Warning, Message = "Capture lane drain reached the shutdown deadline and released recoverable work")]
    public static partial void CaptureLaneDrainAborted(this ILogger logger);

    [LoggerMessage(EventId = 2064, Level = LogLevel.Information, Message = "Validated capture processing graph with {NodeCount} nodes")]
    public static partial void CaptureProcessingGraphValidated(this ILogger logger, int nodeCount);

    [LoggerMessage(EventId = 2065, Level = LogLevel.Debug, Message = "Capture processing node {Step} started at attempt {Attempt}")]
    public static partial void CaptureProcessingNodeStarted(this ILogger logger, string step, int attempt);

    [LoggerMessage(EventId = 2066, Level = LogLevel.Debug, Message = "Capture processing node {Step} completed with {Outcome}")]
    public static partial void CaptureProcessingNodeOutcome(this ILogger logger, string step, string outcome);

    [LoggerMessage(EventId = 2067, Level = LogLevel.Warning, Message = "Capture processing node {Step} requested retry because {Reason}")]
    public static partial void CaptureProcessingNodeRetry(this ILogger logger, string step, string reason);

    [LoggerMessage(EventId = 2068, Level = LogLevel.Error, Message = "Capture processing node {Step} reached terminal outcome because {Reason}")]
    public static partial void CaptureProcessingNodeTerminal(this ILogger logger, string step, string reason);

    [LoggerMessage(EventId = 2069, Level = LogLevel.Debug, Message = "Capture processing node {Step} reused an existing immutable output")]
    public static partial void CaptureProcessingOutputExisting(this ILogger logger, string step);

    [LoggerMessage(EventId = 2070, Level = LogLevel.Debug, Message = "Capture processing node {Step} persisted {OutputCount} outputs totaling {OutputBytes} bytes")]
    public static partial void CaptureProcessingOutputPersisted(this ILogger logger, string step, int outputCount, long outputBytes);

    [LoggerMessage(EventId = 2071, Level = LogLevel.Information, Message = "Capture processing node {Step} recovered {OutputCount} durable outputs")]
    public static partial void CaptureProcessingNodeRecovered(this ILogger logger, string step, int outputCount);

    [LoggerMessage(EventId = 2072, Level = LogLevel.Information, Message = "Capture processing graph completed with {Outcome}")]
    public static partial void CaptureProcessingGraphCompleted(this ILogger logger, string outcome);
}
