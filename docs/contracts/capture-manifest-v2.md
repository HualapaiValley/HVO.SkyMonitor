# Capture Manifest v2

`HVO.SkyMonitor.AgentCore` owns the transport-neutral contracts required to
reconstruct immutable capture evidence. The contracts contain no persistence,
HTTP, image-processing, camera-SDK, or host orchestration behavior.

## Compatibility

`ArtifactUploadManifest` remains the shipped `v1` upload contract and retains
its existing validation and idempotency semantics. `CaptureContractJson` parses
a valid v1 document as `LegacyIncomplete`; its absent layout, timing, profile,
variant, and lineage facts remain absent. It does not derive them from current
registration or rig state.

`ArtifactManifestV2` uses schema version `v2` and contains one
`ReconstructionDescriptor`. Unknown schema versions and malformed v2 documents
return stable validation results. Existing unversioned local sidecars and
history remain readable and are not rewritten. A v2 sidecar is never treated as
legacy when v2 validation fails.

## Descriptor

The reconstruction descriptor records:

- stable agent, rig, module/source, capture, artifact, and ordered
  source-artifact identities;
- a positive per-agent capture sequence whose restart-safe allocation belongs
  to CameraAgent infrastructure;
- requested start, exposure start/end, readout completion, and durable ingress
  as ordered UTC instants;
- requested and effective exposure, gain, offset, setpoint, and temperature;
- capture-time rig, calibration, mask, sensor, and processing profile
  name/version/SHA-256 identities;
- width, height, row stride, pixel format, byte order, sample/container depth,
  packing, CFA, known levels, and exact payload length;
- artifact role, non-empty variant, creation time, media type, payload SHA-256,
  and ordered lineage;
- descriptive recipe name, semantic version, implementation version, canonical
  options, and options SHA-256.

Raw artifacts have no source artifacts. Derivatives require at least one source;
source order is significant and duplicate or empty identifiers are invalid.
Capture IDs, artifact IDs, and source-artifact IDs occupy distinct identity
roles. `ArtifactDescriptor.SourceId` identifies the module or operation that
produced that artifact and is intentionally separate from both capture identity
and the capture-time sensor profile.
Current byte-layout validation accepts byte-aligned Mono8, Mono16 little-endian,
RGB24, and RGGB16 little-endian payloads. Unsupported packing or byte order fails
explicitly rather than being silently converted.

## Determinism

Manifest JSON uses camel-case property names and string enum values. Recipe
option object keys are sorted ordinally at every nesting level before hashing or
serialization; array order is preserved. The v2 idempotency key hashes the
canonical reconstruction descriptor, so moving an unchanged payload does not
change logical identity while a variant, recipe, lineage, profile, layout, or
checksum change does.

`FrameReconstructor.TryReconstruct` validates the descriptor and exact payload
length, verifies SHA-256 by default, and wraps the caller's original
`ReadOnlyMemory<byte>`. It does not repack, byte-swap, demosaic, copy, or dispose
the payload. The caller retains ownership and must keep the backing memory valid
and must not mutate it while storage or a reconstructed frame is using it.
Stream checksums use
`PayloadChecksum.ComputeSha256Async` and do not materialize a second frame-sized
buffer.

## Validation

Validation returns a stable reason code and field path. Reason-code families are
`schema.*`, `identity.*`, `capture-sequence.*`, `timing.*`, `controls.*`,
`layout.*`, `artifact.*`, `lineage.*`, `profile.*`, `recipe.*`, `checksum.*`,
`path.*`, and `payload.*`. Human-readable exception text is not a protocol.

The canonical correctness and performance harnesses are
`ReconstructableCaptureContractTests`, `ReconstructableFrameContractTests`, and
`CaptureContractPerformanceTests`. The performance harness executes W1 and W2
with five warm-ups and 30 measured operations and writes ignored evidence to
`TestResults/issue-92/contracts-performance.json`.
