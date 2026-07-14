# HVO.SkyMonitor.AgentCore

Stable transport-neutral contracts for camera agents and downstream services. This library hosts DTOs for rig configuration, frame metadata, artifact provenance, and camera modules so hosts and modules remain decoupled from concrete infrastructure.

Key types include:

- `CameraFrame`/`FrameMetadata` – immutable representations of captured data.
- `CameraModuleConfig`/`AgentRigConfig` – strongly typed configuration surface shared by agents and modules.
- `CaptureRequest`/`CaptureResult`/`CaptureSetpoint` – capture-cadence contracts passed between the host loop and modules.
- `ReconstructionDescriptor`/`ArtifactManifestV2` – versioned identity, timing, layout, profile, recipe, checksum, and ordered-lineage facts needed to reconstruct an immutable artifact.
- `CaptureContractJson`/`FrameReconstructor` – deterministic manifest serialization and zero-copy frame reconstruction with stable validation reason codes. Manifest v1 parses as `LegacyIncomplete`; missing legacy facts are never inferred.
- `ExposureEnvelope` – optional min/max bands plus defaults that modules can honor when selecting exposure and gain.
- `ICameraModule` and `ICameraModuleFactory` – base abstractions for camera implementations plus the factory interface consumed by the capture loop.

Astronomy projection contracts belong in `HVO.SkyMonitor.Astronomy`; this project must not gain ASP.NET, EF Core, MinIO, SkiaSharp, or camera-SDK dependencies.

The manifest-v2 wire contract and compatibility behavior are documented in
[`docs/contracts/capture-manifest-v2.md`](../../docs/contracts/capture-manifest-v2.md).
