# Camera Agent Capture Cadence & Exposure Control

> Draft for review – describes how the standalone camera agent should hand control of exposure cadence to camera modules while keeping the host responsible for orchestration, storage, and telemetry.

## 1. Current State (Problem)
- `CameraCaptureService` (in `HVO.SkyMonitor.CameraAgent.Common`) owns the cadence loop. It calculates a `TimeSpan cadence = config.Agent.CaptureCadenceSeconds` and explicitly delays (`Task.Delay`) between captures.
- `ICameraModule` exposes only `CaptureNextAsync(CancellationToken)`. The module cannot influence exposure duration, gain, or pacing beyond returning `null`.
- Future requirements (adaptive exposure, histogram checks, per-frame gain/exposure adjustments, and video mode) require module-level control of both exposure time and time-to-next-capture.

## 2. Requirements Recap
1. **Adaptive exposure/gain**: modules must start from day/night defaults, clamp within min/max, inspect the frame (histogram/ADU), and pick the next settings.
2. **Interval enforcement**: even if an exposure completes early, we must honor the configured interval (or a module-defined target) by waiting the remaining time before starting the next exposure.
3. **Processing latency awareness**: analysis time counts toward the interval; the wait is `interval - (exposure + analysis)`.
4. **Future video mode**: we need abstractions that allow modules to switch from still-frame cadence to FPS adjustments without redesigning the host.

## 3. Proposed Responsibilities
| Concern | Owner | Notes |
| --- | --- | --- |
| Load static config, host services, retention/background jobs | Host (`CameraCaptureService`) | unchanged |
| Determine exposure/gain/FPS, track last frame state | `ICameraModule` implementation | modules maintain their control loops |
| Schedule next capture start time (relative to now) | `ICameraModule` | host simply respects requested delay |
| Persist frames/metrics/logging | Host | still writes to disk & telemetry |

## 4. Interface Additions
### 4.1 `CaptureRequest` and `CaptureResult`
```csharp
public sealed record CaptureRequest(
    DateTimeOffset RequestedStartUtc,
    TimeSpan TargetInterval,
    CaptureMode Mode);

public enum CaptureMode
{
    Still,
    Video
}

public sealed record CaptureResult(
    CameraFrame? Frame,
    CaptureSetpoint NextSetpoint,
    TimeSpan ProcessingLatency,
    CaptureMode Mode,
    bool RequiresImmediateUpload);

public sealed record CaptureSetpoint(
    TimeSpan Exposure,
    double Gain,
    TimeSpan? NextIntervalOverride,
    double? TargetFps);
```
- `TargetInterval` is the configured interval (e.g., 10 s). Modules can override for special cases via `NextIntervalOverride`.
- `ProcessingLatency` allows the host to compute actual duty cycle and log metrics.
- `TargetFps` remains null for still images; future video modules can set it.
- `RequiresImmediateUpload` lets a module request bypassing buffers (e.g., severe weather alert frames).

### 4.2 `ICameraModule` update
```csharp
public interface ICameraModule : IAsyncDisposable
{
    string Id { get; }
    string DisplayName { get; }
    string ModuleType { get; }
    CameraModuleCapabilities Capabilities { get; }

    Task InitializeAsync(CameraModuleConfig config, CancellationToken ct);
    Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken ct);
}

[Flags]
public enum CameraModuleCapabilities
{
    None = 0,
    StillFrames = 1,
    Video = 2,
    AdaptiveGain = 4,
    AdaptiveExposure = 8
}
```
- `CaptureAsync` replaces `CaptureNextAsync`. Modules receive the previous schedule (`CaptureRequest`) so they can update setpoints.
- Capabilities advertise whether gain/exposure/video functionality is supported.

### 4.3 Host loop changes
Pseudo-code:
```csharp
var nextRequest = new CaptureRequest(
    RequestedStartUtc: timeProvider.GetUtcNow(),
    TargetInterval: TimeSpan.FromSeconds(config.Agent.CaptureCadenceSeconds),
    Mode: CaptureMode.Still);

while (!token.IsCancellationRequested)
{
    var start = timeProvider.GetUtcNow();
    var result = await module.CaptureAsync(nextRequest, token);
    if (result.Frame is { } frame)
    {
        await storage.SaveAsync(config, frame, token);
    }

    var elapsed = timeProvider.GetUtcNow() - start;
    var interval = result.NextSetpoint.NextIntervalOverride ?? nextRequest.TargetInterval;
    var wait = interval - elapsed;
    if (wait > TimeSpan.Zero)
    {
        await Task.Delay(wait, token);
    }

    nextRequest = nextRequest with
    {
        RequestedStartUtc = timeProvider.GetUtcNow() + wait,
        TargetInterval = interval,
        Mode = result.Mode
    };
}
```
- Host never touches exposure/gain; modules supply new setpoints inside `CaptureResult`.
- When we add video support, modules can transition to `CaptureMode.Video` and set `TargetFps`; host can spin up a different processor (or treat each chunk as a “frame sequence”).

## 5. Exposure/Gain Envelope
Define an optional strongly-typed config that modules can read via `CameraModuleConfig` (serialized under `camera.rig.pipeline.exposureEnvelope`):
```csharp
public sealed record ExposureEnvelope(
    TimeSpan MinExposure,
    TimeSpan MaxExposure,
    double MinGain,
    double MaxGain,
    ExposureDefaults DayDefaults,
    ExposureDefaults NightDefaults,
    double TargetAduLevel);

public sealed record ExposureDefaults(TimeSpan Exposure, double Gain);

Example JSON:

```json
"pipeline": {
    "captureInterval": "00:00:10",
    "dayExposure": "00:00:00.1000000",
    "nightExposure": "00:00:05",
    "dayGain": 1.0,
    "nightGain": 100.0,
    "exposureEnvelope": {
        "minExposure": "00:00:00.0500000",
        "maxExposure": "00:00:10",
        "minGain": 1.0,
        "maxGain": 400.0,
        "dayDefaults": { "exposure": "00:00:00.1", "gain": 2.0 },
        "nightDefaults": { "exposure": "00:00:05", "gain": 100.0 },
        "targetAduLevel": 0.65
    }
}
```
```
- `CameraModuleConfig.Camera.Specific` can embed this envelope; common helper methods in `CameraAgent.Common` can deserialize it for modules.
- `TargetAduLevel` is the ADU value we want histograms to converge toward.

## 6. Video Considerations (Forward-Looking)
- `CaptureMode.Video` indicates that frames may be part of a stream; `CameraFrame` might need a `SequenceId`/`FrameIndex` addition later.
- Storage pipeline can route video sequences to a different persistence strategy (e.g., `.mp4` in `derived/video/`).
- `CaptureSetpoint.TargetFps` will let future modules request FPS throttling instead of exposure adjustments.

## 7. Telemetry Surface
- `CameraCaptureService` emits a `CaptureTelemetrySample` for every cycle, capturing interval, exposure, gain, loop duration, and flags such as `RequiresImmediateUpload`.
- Samples land in a bounded in-memory buffer (`CaptureTelemetrySink`) that exposes the latest snapshot plus aggregated metrics (averages, duty cycle, frames per minute).
- Blazor components or diagnostics endpoints can inject `ICaptureTelemetryProvider` (via `CaptureTelemetryDashboardService`) to render live charts/health badges without polling the filesystem. The default dashboard (`/`) now polls the provider every two seconds and visualizes rolling stats plus the last ten captures.
- The buffer retains the most recent 120 samples (~2 minutes at 1 Hz) by default; adjust capacity when wiring production agents.

## 7. Migration Plan
1. Update `ICameraModule` and create new DTOs in `HVO.SkyMonitor.AgentCore`.
2. Refactor `CameraCaptureService` to honor `CaptureResult.NextSetpoint` instead of `captureCadence` and remove hard-coded delay logic.
3. Update `RandomImageCameraModule` to implement `CaptureAsync`, returning deterministic `CaptureResult` objects (e.g., keep `NextIntervalOverride = TargetInterval`).
4. Extend configuration schema with optional exposure envelopes; supply sample JSON showing day/night defaults.
5. Add telemetry fields (e.g., log `elapsed`, `interval`, `exposure`, `gain`) for future metrics dashboards.
6. Surface the telemetry buffer through the camera agent UI (cards showing latest exposure, gain, cadence, etc.) to speed manual validation.

Once approved, we can begin updating the core library and module implementations accordingly.
