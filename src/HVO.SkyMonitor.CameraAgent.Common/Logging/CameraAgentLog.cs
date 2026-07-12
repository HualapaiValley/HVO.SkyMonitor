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
}
