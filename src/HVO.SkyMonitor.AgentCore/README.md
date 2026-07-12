# HVO.SkyMonitor.AgentCore

Stable transport-neutral contracts for camera agents and downstream services. This library hosts DTOs for rig configuration, frame metadata, artifact provenance, and camera modules so hosts and modules remain decoupled from concrete infrastructure.

Key types include:

- `CameraFrame`/`FrameMetadata` – immutable representations of captured data.
- `CameraModuleConfig`/`AgentRigConfig` – strongly typed configuration surface shared by agents and modules.
- `CaptureRequest`/`CaptureResult`/`CaptureSetpoint` – capture-cadence contracts passed between the host loop and modules.
- `ExposureEnvelope` – optional min/max bands plus defaults that modules can honor when selecting exposure and gain.
- `ICameraModule` and `ICameraModuleFactory` – base abstractions for camera implementations plus the factory interface consumed by the capture loop.

Astronomy projection contracts belong in `HVO.SkyMonitor.Astronomy`; this project must not gain ASP.NET, EF Core, MinIO, SkiaSharp, or camera-SDK dependencies.
