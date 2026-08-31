# HVO.SkyMonitor.AgentCore

Stable transport-neutral contracts for camera agents and downstream services. This library hosts DTOs for rig configuration, frame metadata, artifact provenance, and camera modules so hosts and modules remain decoupled from concrete infrastructure.

Key types include:

- `CameraFrame`/`FrameMetadata` – immutable representations of captured data.
- `CameraModuleConfig`/`AgentRigConfig` – strongly typed configuration surface shared by agents and modules.
- `CaptureRequest`/`CaptureResult`/`CaptureSetpoint` - capture-cadence contracts passed between the host loop and modules.
- `CaptureCycleEvidence` - optional cadence, control ownership, sparse-metering, active-to-decided setpoint, and ingress-handoff facts. Absence means legacy/unrecorded evidence.
- `ReconstructionDescriptor`/`ArtifactManifestV2` - versioned identity, timing, layout, profile, recipe, checksum, and ordered-lineage facts needed to reconstruct an immutable artifact.
- `CaptureContractJson`/`FrameReconstructor` - deterministic manifest serialization and zero-copy frame reconstruction with stable validation reason codes. Retired manifest versions fail validation; missing facts are never inferred.
- `ExposureEnvelope` - optional min/max bands plus day/twilight/night defaults, preference, hysteresis, and bounded adjustment settings.
- `ICameraModule`/`ICameraSetpointController` and `ICameraModuleFactory` - capture plus pre-ingress setpoint application abstractions consumed by the host loop.

Astronomy projection contracts belong in `HVO.SkyMonitor.Astronomy`; this project must not gain ASP.NET, EF Core, object-storage SDK, SkiaSharp, or camera-SDK dependencies.

The manifest-v2 wire contract and compatibility behavior are documented in
[`docs/contracts/capture-manifest-v2.md`](../../docs/contracts/capture-manifest-v2.md).
