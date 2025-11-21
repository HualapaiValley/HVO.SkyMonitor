# Data & Messaging Contracts

> Purpose: document the payloads exchanged between camera agents, LogicHost, and supporting services. These contracts are shared across codebases today (`HVO.SkyMonitor.*`) and take inspiration from legacy implementations (`HVOv9`, `HVOv9-SkyMonitorv6`).

## 1. Capture Payloads
| Contract | File | Key Fields | Notes |
| --- | --- | --- | --- |
| `CameraFrame` | `src/HVO.SkyMonitor.AgentCore/CameraFrame.cs` | `Id`, `CapturedAtUtc`, `Width`, `Height`, `PixelFormat`, `RawImage`, `DerivedImage`, metadata dictionaries. | Carries RAW bytes + derived data. In `HVOv9`, RAW and derivative frames were separate records; we consolidate both in one payload with clear property names. |
| `CaptureRequest` | `AgentCore/CaptureRequest.cs` | `RequestedStartUtc`, `TargetInterval`, `CaptureMode`. | Given to modules each loop iteration so they can adjust exposures (per `docs/CameraAgent-CaptureCadence.md`). |
| `CaptureResult` | `AgentCore/CaptureResult.cs` | `CameraFrame? Frame`, `CaptureSetpoint NextSetpoint`, `ProcessingLatency`, `Mode`, `RequiresImmediateUpload`. | Modules return this; pipeline steps rely on `RequiresImmediateUpload` to bypass batch queues. |
| `CaptureSetpoint` | `AgentCore/CaptureSetpoint.cs` | `Exposure`, `Gain`, `NextIntervalOverride`, `TargetFps`. | Equivalent to `HVOv9`’s `ModuleSetpoints` but explicitly typed. |
| `CaptureProcessingStepConfig` | `AgentCore/CaptureProcessingStepConfig.cs` | `Id`, `Type`, `Order`, `Options`. | JSON-defined pipeline. Mirrors `HVOv9` config but leverages reflection with DI.

## 2. Telemetry & Health
| Contract | File | Purpose |
| --- | --- | --- |
| `CaptureTelemetrySample` | `CameraAgent.Common/Telemetry/CaptureTelemetryContracts.cs` | Single-cycle metrics (interval, exposure, gain, flags). |
| `CaptureTelemetryAggregate` | same | Rolling averages / counts for UI dashboards. |
| `CaptureTelemetrySnapshot` | same | Combined samples + aggregate; served via API. |
| Metrics | `CaptureTelemetryMetricsRecorder.cs` | Emits OpenTelemetry metrics matching the above shapes. |

*Legacy note*: `HVOv9` exported Prometheus metrics (`camera_capture_interval_seconds`, etc.). The new meter names should remain semantically identical so dashboards map cleanly when we migrate.

## 3. Identity & Auth
| Contract | File | Description |
| --- | --- | --- |
| `CentralIdentityOptions` | `CameraAgent/Configuration/CentralIdentityOptions.cs` | Shared options for both client credentials and API-key fallback. Includes nested `InteractiveClientOptions`. |
| `ClientCredentialsOptions.DefaultScopes` | same | `api.camera`, `api.frames`, `api.images`. Align with LogicHost scopes seeded in `DatabaseSeeder.cs`. |
| `ApiKeyOptions` | same | Describes fallback API key shape (Name, Key, Scopes). |

These contracts are referenced by both `CentralAuthenticationService` (agent) and `Program.cs` in LogicHost when binding configuration. Keep this document in sync with `auth-strategy.md` whenever scopes or issuers evolve.

## 4. Storage & Queue Agreements
| Topic | Current State | Planned Contract |
| --- | --- | --- |
| Local storage layout | `FileSystemFrameStorageService` writes to `storageRoot/frames/YYYY/MM/DD/HH/mm/{frameId}` with sidecar metadata JSON. | Maintain same layout for backwards compatibility with `HVOv9` tools; add RAW extension (`.raw` or `.fits`) once RAW-first is enforced. |
| Latest frame cache | `ILatestFrameAccessor` holds in-process pointer only. | Introduce `LatestFrameEnvelope` DTO (TBD) to mirror `HVOv9`’s MinIO-based cache; store JSON + JPEG in Redis/MinIO keyed by tenant. |
| Upload queue | Not implemented (see `NoOpUploadProcessingStep`). | Adopt `FrameEnvelope` (below) for queue payload; can target Azure Queue, SQS, or Redis stream. |
| MinIO buckets | LogicHost uses `MinioOptions.DefaultBucket` for diagnostics only. | Reuse bucket layout from `HVOv9-SkyMonitorv6`: `{tenant}/frames/raw/{yyyy}/{MM}/{dd}/...` and `{tenant}/frames/derived/...`. Document in `future-work.md`. |

### FrameEnvelope (Planned)
```
FrameEnvelope
├── FrameId (Guid)
├── TenantId (Guid/String)
├── CaptureMetadata (CameraFrame metadata subset)
├── RawObjectKey (MinIO/S3 key)
├── DerivedObjectKey
├── Telemetry { ExposureMs, IntervalMs, Gain, TemperatureC, Flags }
└── Integrity { Sha256, SizeBytes }
```
- Inspired by `HVOv9`’s `FrameRecord` written to Redis.
- Will be produced by the upload processing step and consumed by LogicHost’s ingest API/worker.

## 5. API Contracts (current vs. future)
| Endpoint | Status | Contract |
| --- | --- | --- |
| `/api/status` (agent) | Implemented | Returns simple DTO containing uptime, capture stats, build info. Add schema to `tests/HVO.SkyMonitor.CameraAgent.IntegrationTests/SampleEndpointTests`. |
| `/latest` (agent UI) | Implemented | Streams derived image bytes. Not versioned; use only for dashboards. |
| `/api/v1.0/frames` (LogicHost) | **Planned** | Accepts `FrameEnvelope` + blob references. Future implementation should reference `HVOv9`’s `FrameIngestController`. |
| `/api/v1.0/telemetry/capture` | **Planned** | Returns `CaptureTelemetrySnapshot`. Useful to keep agent UI thin and allow Ops dashboards to query centrally. |

## 6. Versioning Strategy
- All new APIs under LogicHost must use URL versions (matching existing `Asp.Versioning` setup in `Program.cs`).
- JSON payloads should include `schemaVersion` when they leave the agent (queue/upload) so we can introduce additive fields without breaking older workers.
- Keep `CameraFrame.PixelFormat` values aligned with SkiaSharp enumerations and document allowable values in this file if we expand beyond `Mono8`/`Rgb24`.

## 7. Action Items
1. **Define `FrameEnvelope` in `HVO.SkyMonitor.Common`** so both agent and LogicHost reference the same type.
2. **Document storage bucket conventions** pulling examples from `HVOv9-SkyMonitorv6` (MinIO layout) and include them here once finalized.
3. **Codify integrity metadata** (hash + size) to catch transfer corruption.
4. **Add schema tests** – e.g., snapshot tests for `CaptureTelemetrySnapshot` to guarantee serialization stability.

Consult `docs/architecture/diagrams/tenant-queue.mmd` (to be added) for a visual depiction of the queue/upload flow once implemented.
