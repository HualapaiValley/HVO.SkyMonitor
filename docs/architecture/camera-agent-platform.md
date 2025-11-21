# Camera Agent Platform Architecture

> Scope: describes the Phase 01 multi-tenant camera-agent shell that will eventually back every deployed observatory node. This is the authoritative reference for capture cadence, RAW-first ingest, telemetry surfaces, and how the agent integrates with LogicHost. Legacy behaviors from `HVOv9-SkyMonitorv6`/`HVOv9` are called out inline.

## 1. System Snapshot
- **Primary entry point** – `src/HVO.SkyMonitor.CameraAgent/Program.cs` bootstraps ASP.NET Core + Blazor Server, configures telemetry/providers, and wires the capture services found in `HVO.SkyMonitor.CameraAgent.Common`.
- **Capture/processing contracts** – defined in `src/HVO.SkyMonitor.AgentCore/` (`CaptureRequest`, `CaptureResult`, `CameraFrame`, `CaptureProcessingStepMetadata`, etc.). These were lifted from `HVOv9` but now incorporate adaptive cadence data taken from `docs/CameraAgent-CaptureCadence.md`.
- **JSON configuration** – default sample lives at `src/HVO.SkyMonitor.CameraAgent/cameraagent.sample.json` and mirrors the `HVOv9` rig layout (module descriptor, common sensor metadata, rig info, processing pipeline).
- **Processing pipeline** – orchestrated by `CapturePipelineBuilder` + processing step implementations (storage, telemetry, upload stubs). This replaces the bespoke pipeline wiring previously scattered through `HVOv9`’s `FrameProcessor` and `DerivativePlanner` classes.

## 2. Capture Lifecycle (end-to-end)
1. **Configuration load**
   - `CameraAgentServiceCollectionExtensions.AddCameraAgentInfrastructure` registers `FileCameraAgentConfigurationLoader` which watches `CameraAgent:ConfigFilePath`.
   - `CameraAgentConfigurationAccessor` guards first-use access so the capture host never runs on incomplete config.
2. **Module initialization**
   - `ICameraModuleFactory` instantiates the configured module (Phase 01 uses `RandomImageCameraModule`). Legacy note: `HVOv9-SkyMonitorv6` used `CaptureModuleHost` with explicit dependency injection per module; we keep DI but rely on reflection + options binding for now.
3. **Capture loop**
   - `CameraCaptureService` issues `CaptureRequest` objects populated with cadence + mode data.
   - Module returns `CaptureResult`, optionally with `CaptureSetpoint.NextIntervalOverride` and `RequiresImmediateUpload` flags (matching the adaptive logic described in `docs/CameraAgent-CaptureCadence.md`).
4. **Processing pipeline**
   - `CapturePipelineRunner` executes ordered steps from config, such as:
     - `NoOpFileStorageProcessingStep` (placeholder for RAW-first archival).
     - `NoOpUploadProcessingStep` (Phase 01 stub for eventual LogicHost ingest).
     - `TelemetryCaptureProcessingStep` which publishes a `CaptureTelemetrySample` into the shared sink.
   - These steps replace the monolithic `FrameProcessingWorker` used in `HVOv9` but follow the same “ordered step list” concept.
5. **Storage & retention**
   - `FileSystemFrameStorageService` persists frames beneath the configured storage root.
   - `FrameRetentionWorker` sweeps the storage tree based on `retentionDays` per processing step. Legacy parity: `HVOv9`’s `CaptureRetentionService` ran on cron-like timers; our worker runs inside the agent host with the same retention semantics.
6. **Telemetry & UI**
   - `CaptureTelemetryMetricsRecorder` pushes measurements to .NET meters.
   - `CaptureTelemetrySink` retains a rolling buffer for Blazor dashboards (`CaptureTelemetryDashboardService`).
   - `/api/status` + `/latest` endpoints reuse the telemetry stack so LogicHost health monitors can query the agent without scraping files.

Refer to `docs/architecture/diagrams/end-to-end-flow.mmd` for a visual sequence.

## 3. Configuration Surfaces
| Section | Source | Notes |
| --- | --- | --- |
| `CameraAgent` | `appsettings*.json` | Host-level settings (config path, retention sweep interval, observatory metadata). |
| `cameraagent.sample.json` | repo root sample | Mirrors `HVOv9` config structure: `AgentOptions`, `CameraModuleDescriptor`, `Rig`, and `processingSteps` with strongly typed options per step. |
| `CentralIdentity` | `appsettings*.json` | Placeholder for auth (covered in `auth-strategy.md`). |
| Environment overrides | `.env.development`, `.env.template` | Provide container/service addresses identical to `HVOv9` docker stacks so agents can be run locally or remote via `scripts/run-cameraagent-remote.sh`. |

### RAW-first transport preparation
- Even though processing steps produce PNG/JPEG for the in-agent UI, the contracts already carry `CameraFrame.RawImage`. The storage service must persist RAW alongside derived formats before LogicHost ingest is enabled.
- Legacy reference: `HVOv9-SkyMonitorv6`’s `CaptureFrameCacheService` kept both RAW and derivative snapshots in MinIO; our Phase 01 agent will eventually write to MinIO via a dedicated processing step rather than local disk once the ingest API is live.

## 4. Telemetry and Observability
- **Metrics** – look for meter `HVO.SkyMonitor.CameraAgent.Capture` defined by `CaptureTelemetryMetricsRecorder`. Counters/histograms match the old Prometheus gauges from `HVOv9` but use .NET 10’s Meter API so we can export via OpenTelemetry.
- **Logging** – all processing steps accept `ILogger` and emit structured messages. Legacy behavior used Serilog sinks; we stick with default Microsoft.Extensions.Logging but include correlation IDs.
- **Diagnostics endpoints** – Phase 01 exposes `/health`, `/api/status`, and `/api/telemetry`. When LogicHost ingest arrives, we will add `/api/upload` but reuse the same telemetry sink for validation.

## 5. Legacy Alignment & Gaps
| Legacy Pattern (`HVOv9*`) | Current Implementation | Gap / Action |
| --- | --- | --- |
| Framebuffer (`CaptureFrameCacheService`) served `/latest` via MinIO. | `ILatestFrameAccessor` keeps latest frame pointer in-memory and serves Blazor dashboard. | Add MinIO-backed cache once multi-tenant LogicHost requires shared access. |
| `FrameProcessingWorker` executed steps + derivative planner. | `CapturePipelineRunner` + JSON-defined steps. | Need to port derivative planner concept as a dedicated processing step (e.g., stacking). |
| Central queue wrote to RabbitMQ/Redis with tenant partitions. | Queue not yet wired; `NoOpUploadProcessingStep` is placeholder. | Future ingest doc should describe how to swap in actual queue/upload step; see `contracts.md`. |

## 6. Implementation Checklist
1. **Finalize RAW persistence** – extend `FileSystemFrameStorageService` to write RAW alongside PNG/JPEG and document retention policy per format.
2. **Introduce real upload step** – replace `NoOpUploadProcessingStep` with a client that batches RAW blobs + telemetry to LogicHost once `/api/v1.0/frames` exists.
3. **Incorporate MinIO optionally** – allow processing steps to target MinIO buckets similar to `HVOv9` so cloud deployments skip local disk.
4. **Surface telemetry via API** – add versioned endpoints (e.g., `/api/v1.0/telemetry/capture`) to avoid scraping UI-specific data.
5. **Unit/integration coverage** – add tests mirroring `tests/HVO.SkyMonitor.CameraAgent.Tests/Telemetry/*` for any new steps.

This document should remain the hub for agent-side architecture; cross-link to `auth-strategy.md` for identity specifics and `contracts.md` for data schema details.
