# Transient Contracts v1

`HVO.SkyMonitor.Processing` owns host-neutral candidate, event, observation,
assessment, review, notification, derivative, source-evidence, and detector-input
contracts. Hosts own scheduling, persistence, transport, event convergence, review
policy, and notification delivery. Pixel conversion remains in
`HVO.SkyMonitor.Imaging`.

## Event Evidence

`TransientEventV1.EventId` is an opaque caller-assigned event identity. It does not
derive from capture IDs, artifact lists, chunk boundaries, or derivative packaging.
Each immutable event version has a distinct `EventVersionId`, integer `Version`, and
optional predecessor identity/time. `EventCreatedUtc` is stable across versions;
`VersionCreatedUtc` timestamps this immutable snapshot. Observation evidence ends no
later than event creation, the predecessor is earlier than the version, and every
contained assessment, review, notification, and derivative is no later than version
creation.

Assessments, reviews, and notifications are append-only chains. A predecessor can be
superseded only once, must be chronologically earlier, and therefore cannot fork.
Assessment reasons may cite only observations declared by that assessment. Review
chains retain one assessment ID. Notification chains retain one assessment ID and
case-sensitive channel. Independent roots are allowed for independent producers,
review scopes, or notification channels.

Event state is `Pending`, `Provisional`, `Validated`, `Rejected`, or `NeedsReview`.
Assessment authority is independently `Provisional` or `Authoritative`; the contract
does not decide whether edge or central execution is authoritative. Fireball is a
severity of `Meteor`, never a separate classification.

An observation binds geometry and measured features to one
`TransientSourceEvidenceReferenceV1`. Geometry uses continuous detector pixel-edge
coordinates. Width and length are pixels, brightness and integrated signal are linear
ADU, confidence and profile positions use inclusive millionths from `0` through
`1,000,000`, and observation intervals are UTC with start less than or equal to end.
Geometry coordinates and positive-size bounds must be finite and inside the declared
coordinate width/height; polylines contain at least two points. Feature lengths,
widths, signals, saturation counts, and profile values are nonnegative; maximum width
is at least mean width, fragment count is positive, and profile positions are strictly
increasing in caller-provided order. Confidence is inclusive `0..1,000,000`.
Array order is significant for observations, profiles, evidence, and derivative
sources. Each observation also retains ordered background artifact references plus
the detector-input, calibration, mask, and processing-profile identities used to
measure it. `TransientObservationExtractionV1` separately binds geometry/features to
an optional originating candidate, `transient-extraction-producer-v1` name/version,
and extraction recipe. Extraction producers are distinct from assessment producers,
so re-extraction and reassessment cannot be confused or overwrite each other. A
candidate carries this same extraction structure rather than assessment producer
fields and must identify itself as the originating candidate; promotion preserves
candidate extraction exactly while later observations may retain independent extractor
versions or omit candidate identity for retrospective extraction.

V1 source evidence uses `transient-source-locator-v1` and locates one complete
immutable artifact by artifact ID, role, variant, producing recipe identity, and
content SHA-256. A later locator schema may identify a frame, sample, or time range
inside temporal media. V1 never interprets an unknown locator as a whole still frame.
Event/candidate source locators and observation backgrounds accept only `Raw` or
`Calibrated` linear evidence; previews, annotations, and metadata are rejected.

V1 assessment producers use `transient-assessment-producer-v1` and identify one
deterministic algorithm name/version. Model checksums, preprocessing, execution
providers, hardware providers, and certified services require a later producer
schema. Prior producer and recipe versions coexist rather than being overwritten.

Overlays, masks, crops, previews, and reconstructions are derivative references.
They do not replace structured geometry or raw evidence. Reconstruction explicitly
records that exact intra-exposure timing and saturated photometry are unrecoverable.

## Detector Inputs

`TransientDetectorInputFactory.Create` accepts the same `ProcessingArtifact` boundary
used by in-memory CameraAgent execution and centrally reconstructed artifacts. It
requires a Raw or Calibrated artifact, versioned source evidence, and explicit
`TransientLinearLevelsV1` black, white, and saturation levels in inclusive 16-bit ADU.
It verifies source identity, role, variant, recipe identity, and exact ordered UTC
start/end against the artifact's explicit observation bounds. Artifact creation and
aggregate integration are not used to infer sensor timing. CameraAgent uses reported
acquisition start/end when available and otherwise falls back to frame timestamp plus
exposure; committed CameraAgent timing is canonically millisecond-aligned and LogicHost
uses those same reconstruction-descriptor bounds. The factory also
verifies compatibility profiles, exact byte length, SHA-256, format, CFA, depth,
packing, byte order, and levels before conversion.

When an accelerated capture reports identical acquisition start/end with positive
effective integration, both host adapters preserve the acquisition timestamp but use
`start + integration` as the observation end. Durable-ingress time remains the actual
post-publication instant and is never replaced by modeled observation time.

Little-endian Mono16 returns the exact source memory with `Borrowed` ownership. The
caller must keep borrowed memory alive and unchanged for the entire use of the result.
RGGB16 returns one `Owned`, tightly packed, half-resolution Mono16 buffer using rounded
`(R + G1 + G2 + B) / 4` per 2 by 2 CFA cell. It performs no display stretch or full
demosaic. Owned memory needs no disposal and is retained by the result. Results are
safe for concurrent readers when borrowed memory is not concurrently mutated.

The factory reports bytes scanned and copied. SHA-256 verification scans the source;
RGGB conversion additionally scans each logical source sample and copies exactly one
half-resolution output. Input identity covers normalized source evidence, output
layout and levels, compatibility profiles, conversion identity, and the source-to-
detector transform. It contains no persistence assertion, path, object key, scenario
label, expected classification, truth geometry, or truth mask. Timing provenance may
name a simulator or other source implementation and version, but hidden simulator
scenario identity and expected/truth data never enter detector contracts.
The V1 Mono tuple is its named borrow algorithm with scale `1`, offsets `0`; RGGB uses
its named cell-average algorithm with scale `0.5`, offsets `0`. Other representation,
algorithm, transform-version, scale, or offset combinations are rejected.

Malformed inputs return `TransientContractValidationResult` with a stable reason code
and field path. Cancellation throws `OperationCanceledException` and never returns a
partial input.

## Canonical JSON

Event, candidate, and detector-input descriptor JSON is UTF-8 camel case with
PascalCase string enums. Object properties are recursively sorted ordinally; array
order remains significant. SHA-256 values serialize uppercase. Parsing rejects
numeric enums, duplicate or case-colliding properties, unknown members, missing
required members, unsupported schemas, invalid identities/lineage, and oversized
payloads. After valid JSON and duplicate checks, root `schemaVersion` is checked first;
an unsupported root wins over nested schemas and structurally divergent members.
Nested locator and producer schemas are checked next, followed by strict V1 member
deserialization. Unknown locator and producer schemas have distinct failure reasons.
Accepted floating negative zero in geometry, feature dimensions, profile values,
transforms, and nullable layout levels normalizes to positive zero before canonical
bytes and identities are computed. Observation order and ordinal, source/context
order, assessment/review/notification history order, reason evidence order,
derivative source order, and profile order are semantic and are never sorted.

Schema limits are 4 MiB for an event, 1 MiB for a candidate, and 64 KiB for a detector
input descriptor. These are transport-safety limits, not semantic limits on an event's
observation count. Golden fixtures under `tests/fixtures/processing/` pin canonical
bytes and SHA-256 values.

## Exclusions

V1 is limited to already durable whole Mono16 or RGGB16 still-frame observations. It
does not implement streams, codecs, chunks, volatile promotion, clips, AI inference,
temporal backgrounds, candidate extraction, host persistence, runtime scheduling,
event convergence policy, physical sensitivity claims, or review/notification policy.
