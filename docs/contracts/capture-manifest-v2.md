# Capture Manifest v2

`HVO.SkyMonitor.AgentCore` owns the transport-neutral contracts required to
reconstruct immutable capture evidence. The contracts contain no persistence,
HTTP, image-processing, camera-SDK, or host orchestration behavior.

## Supported Schema

`ArtifactManifestV2` is the only capture upload and local frame-sidecar contract.
It uses schema version `v2` and contains one `ReconstructionDescriptor`. Unknown
schema versions, including retired v1 upload documents, and malformed v2
documents return stable validation results and are rejected before persistence.
Local frame storage reads and writes only versioned v2 sidecars. A malformed or
unsupported sidecar is never reinterpreted as an unversioned document.

`descriptor.cycleEvidence` and `descriptor.timing.setpointAppliedUtc` are
additive optional v2 fields. Their absence means the capture did not
record those facts; readers do not infer them from current configuration. Null
optional evidence is omitted during serialization, preserving existing bytes and
descriptor hashes.

`descriptor.location` is also additive and optional. It records the stable
deployment-location ID and version plus source, known horizontal accuracy, and
effective interval used for the capture. It omits latitude, longitude,
elevation, timezone, and the coordinate-derived canonical hash because the
complete immutable snapshot is protected in CameraAgent local state. A
coordinate-free hash of ID and version is used only as a processing
compatibility axis. Absence means location is unknown; existing captures are
never assigned the current location.

`descriptor.layout.readout`, `storedCodeTransform`, and `levelCodeSpace` are
additive optional v2 fields. Readout records native dimensions, native-coordinate
ROI, integer bins, a versioned bin algorithm, and CFA origin. The stored-code
transform distinguishes identity, right-aligned, left-shifted, and full-range
scaled samples; level code space states whether black and white levels use
native-sample or stored-container units. A full-depth existing byte-aligned layout
has unambiguous identity/stored-container defaults without changing its JSON.
A lower-depth layout that omits either fact is rejected as ambiguous; readers
must not infer the missing facts from free-form metadata.

All snapshot longitude values use decimal degrees east-positive in `[-180, 180]`;
west longitude is negative. The deployment-location version and canonical hash
freeze that interpretation. Capture provenance remains coordinate-free so the
manifest does not disclose coordinates.

Location intervals are half-open. An open-ended captured snapshot remains
immutable; when a later version is activated, protected CameraAgent history
records the new version's effective start as the prior version's supersession
boundary. This closes the operational interval without changing hashes already
referenced by older captures.

## Descriptor

The reconstruction descriptor records:

- stable agent, rig, module/source, capture, artifact, and ordered
  source-artifact identities;
- a positive per-agent capture sequence whose restart-safe allocation belongs
  to CameraAgent infrastructure;
- requested UTC deadline, module-reported exposure start/end, readout
  completion, optional setpoint application, and durable ingress UTC instants;
- requested and effective exposure, gain, offset, setpoint, and temperature;
- capture-time rig, calibration, mask, sensor, and processing profile
  name/version/SHA-256 identities;
- width, height, row stride, pixel format, byte order, sample/container depth,
  packing, CFA, known levels and their code space, stored-code transform, native
  ROI/binning geometry, and exact payload length;
- artifact role, non-empty variant, creation time, media type, payload SHA-256,
  and ordered lineage;
- descriptive recipe name, semantic version, implementation version, canonical
  options, and options SHA-256;
- optional coordinate-free capture-time deployment-location provenance.

New captures may also retain `CaptureCycleEvidence`: cadence mode and start
reason, host module-call time, exposure/gain ownership, solar regime, observed
inter-exposure gap, sparse-meter phase and sample/byte counts, the active and
decided setpoints, bounded decision reason, and ingress-handoff start. The
module-call time is a host observation; a deterministic virtual exposure may be
timestamped at its requested simulated deadline before that host observation.
Elapsed scheduling still uses monotonic timestamps rather than these UTC facts.
`cycleEvidence.monotonicStartJitter` and the observed host-start gap remain
nonnegative when the wall clock is corrected. A requested UTC deadline or a
setpoint applied during the preceding cycle may therefore compare later or
earlier than another wall-clock field without changing monotonic cadence.
`timing.durableIngressUtc` is sampled after the immutable raw payload has been
atomically published, flushed, and directory-synced. The raw-ingress journal's
`committed_unix_ms` is the injected-clock observation made inside the SQLite
transaction immediately before the capture, context, and lane rows are inserted.
Only a successful transaction commit produces a durable-success receipt or
telemetry event; the pre-commit timestamp is not itself a success inference.

Raw artifacts have no source artifacts. Derivatives require at least one source;
source order is significant and duplicate or empty identifiers are invalid.
Capture IDs, artifact IDs, and source-artifact IDs occupy distinct identity
roles. `ArtifactDescriptor.SourceId` identifies the module or operation that
produced that artifact and is intentionally separate from both capture identity
and the capture-time sensor profile.
Current byte-layout validation accepts 8-bit Mono8 and RGB24 payloads plus 8-,
10-, 12-, 14-, or 16-bit meaningful Mono16 and RGGB16 samples in 16-bit
containers and either little- or big-endian byte order. Explicit readout geometry
must fit the native sensor, divide exactly by its bins, and produce the declared
output dimensions. RGGB output currently requires an aligned native CFA origin
and unbinned readout; unsupported CFA-preserving binning, packing, or byte order
fails explicitly rather than being silently converted.

## Determinism

Manifest JSON uses camel-case property names and string enum values. Recipe
option object keys are sorted ordinally at every nesting level before hashing or
serialization; array order is preserved. The v2 idempotency key hashes the
canonical reconstruction descriptor, so moving an unchanged payload does not
change logical identity while a variant, recipe, lineage, profile, layout, or
checksum change does. Non-null cycle evidence and location provenance also
participate in descriptor identity. The coordinate-free location identity is a processing
compatibility axis, so a location-version change starts a new rolling or window
history. A retry of legacy evidence remains legacy and never rewrites an old
sidecar merely to add newly available fields.

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
`cadence.*`, `metering.*`,
`layout.*`, `artifact.*`, `lineage.*`, `profile.*`, `recipe.*`, `checksum.*`,
`path.*`, and `payload.*`. Human-readable exception text is not a protocol.

The canonical correctness and performance harnesses are
`ReconstructableCaptureContractTests`, `ReconstructableFrameContractTests`, and
`CaptureContractPerformanceTests`. The performance harness executes W1 and W2
with five warm-ups and 30 measured operations and writes ignored evidence to
`TestResults/issue-92/contracts-performance.json`.
