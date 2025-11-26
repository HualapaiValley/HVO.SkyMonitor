# HVO.SkyMonitor.AgentCore

Shared contracts and primitives for AllSky camera agents, simulators, and downstream services. This library hosts DTOs for rig configuration, frame metadata, camera modules, and the common projector contract so that the camera agent host and future modules remain decoupled from concrete implementations.

Key types include:

- `CameraFrame`/`FrameMetadata` – immutable representations of captured data.
- `CameraModuleConfig`/`AgentRigConfig` – strongly typed configuration surface shared by agents and modules.
- `CaptureRequest`/`CaptureResult`/`CaptureSetpoint` – capture-cadence contracts passed between the host loop and modules.
- `ExposureEnvelope` – optional min/max bands plus defaults that modules can honor when selecting exposure and gain.
- `ICameraModule` and `ICameraModuleFactory` – base abstractions for camera implementations plus the factory interface consumed by the capture loop.
- `IImageProjector` – bidirectional mapping between sky coordinates and image pixels for renderers and overlay pipelines.
