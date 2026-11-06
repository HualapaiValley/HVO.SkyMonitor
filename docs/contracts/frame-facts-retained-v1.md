# Retained Frame Facts context and presets v1

`hvo-frame-facts-retained-v1` is slice B of #1387 (#1551). It adds a catalogue,
an immutable context builder and explicit preset conversion in Processing. It
consumes [template v1](frame-facts-template-v1.md). Nothing registers or invokes
this code from a host, producer, renderer or graph. Existing graph identities,
product schema/producer versions, defaults and rendered bytes are unchanged.
Slice C (#1552, after #1541) owns that integration and explicit draft upgrades.

## Consumer contract

Both hosts use the same API:

```csharp
var frozen = FrameFactsRetainedContext.Create(retainedInput);
var parsed = FrameFactsTemplate.Parse(source, FrameFactsRetainedCatalog.Catalog);
// Handle parsed.Error before evaluation.
var result = FrameFactsEvaluator.Evaluate(parsed.Template!, frozen.Tokens);
```

`FrameFactsRetainedInputV1` requires a valid retained `ReconstructionDescriptor`.
Null optional inputs mean evidence was not retained (including older records).
An invalid required descriptor is a caller error, rejected with an argument
exception. Optional unsupported evidence is unavailable. The builder copies
scalars and provenance into immutable dictionaries; it holds no input records,
JSON documents, lists, pixel buffers, options, services or delegates. There is
no host selector: CameraAgent and LogicHost consume the identical context.
Changing a current profile, provider, catalogue, zone database or rig cannot
change an already constructed context. Re-evaluation requires no I/O or clock.

`FrameFactsRetainedCatalog.Entries` is the machine-readable per-token source,
unit, provenance and type/format inventory. `Catalog` adds the generated status
companions. Every scalar below has `.status`, formatted as `text`, with values
`AVAILABLE`, `MISSING`, `STALE`, `CONFLICTING`, `UNSUPPORTED`. Unavailable scalars
carry no value and render `UNAVAILABLE` (or use slice A's `DropLine` policy).
An explicit retained zero is allowed; absence never becomes zero.

The format column uses these closed sets, with the first format the default:

| Kind | Closed formats |
| --- | --- |
| T: text | `text` |
| I: whole number | `integer` |
| N: finite number | `number`, `fixed0`, `fixed1`, `fixed2`, `fixed3` |
| D: timestamp | `iso8601`, `date`, `time` |

Formatting is invariant and obeys slice A's limits: 4,096 UTF-16 source units,
8 lines, 64 Unicode scalars per rendered line. Fact text is never reparsed as
markup. `spaceWeather.*` stays reserved, with no active catalogue entries.

## Capture, acquisition, lineage and time mapping

In this table `d` means the retained reconstruction descriptor. The runtime
`Provenance` dictionary records the descriptor SHA-256 for these entries.

| Tokens | Kind / unit | Retained source and interpretation |
| --- | --- | --- |
| `capture.agentId`, `capture.rigId`, `capture.captureId` | T / identity | `d.Capture.AgentId`, `RigId`, `CaptureId` (GUID D format) |
| `capture.sequence` | I / count | `d.Capture.CaptureSequence` |
| `capture.sourceId`, `capture.artifactId` | T / identity | `d.Artifact.SourceId`, `ArtifactId` |
| `capture.width`, `capture.height` | I / pixels | `d.Layout.Width`, `Height`; retained source dimensions |
| `acquisition.exposureSeconds` | N / seconds | `d.Controls.EffectiveExposure.TotalSeconds` |
| `acquisition.gain`, `acquisition.offset` | N / camera units | `EffectiveGain`, nullable `EffectiveOffset`; no requested-control fallback |
| `acquisition.sensorSetpointC` | N / C | Nullable `TemperatureSetpointC`; configured setpoint, never a measurement |
| `acquisition.sensorTemperatureC` | N / C | Nullable `EffectiveTemperatureC`; module-reported value, not a weather observation or assertion of physical measurement |
| `acquisition.scheduleProfile` | T / identity | Nullable `d.CycleEvidence.ScheduleAdmission.SetpointProfileId` |
| `acquisition.cadenceMode` | T / enum | Nullable `d.CycleEvidence.CadenceMode` |
| `acquisition.cadenceSeconds` | N / seconds | Explicit retained submission interval via capture extras, not inferred from exposure or a gap |
| `stack.count` | I / frames | Retained stack source-artifact list count, not a live rolling-stack count |
| `stack.integrationSeconds` | N / seconds | Retained `TotalIntegration`, not count multiplied by current exposure |
| `stack.identity` | T / SHA-256 | Retained stack output identity |
| `lineage.calibration.name`, `.version`, `.sha256`, `.shortHash` | T / text or hash | `d.Profiles.Calibration`; short hash is exactly the first 12 retained characters |
| `lineage.processing.name`, `.version`, `.sha256`, `.shortHash` | T / text or hash | `d.Profiles.Processing`; no current processing profile lookup |
| `lineage.catalog.name`, `.version`, `.sha256` | T / text or hash | Source-bound `ProjectedSceneV1.Catalog.Name`, `Version`, `ChecksumSha256` |
| `time.sourceUtc` | D / UTC instant | Explicit source-authored `CameraFrame.TimestampUtc`, if retained, supplied through capture extras |
| `time.captureUtc`, `time.captureEndUtc` | D / UTC instant | `d.Timing.ExposureStartedUtc`, `ExposureEndedUtc`; command/status boundaries, not exact sensor timing |
| `time.artifactCreatedUtc` | D / UTC instant | `d.Artifact.CreatedUtc`, with no claim this is processing completion |
| `time.processingUtc` | D / UTC instant | Selected retained `GraphExecutionEvidenceV1.CompletedUtc` through capture extras; never inferred from capture/artifact time |
| `time.captureLegacyUtc` | T / UTC display | Exposure start formatted invariantly as `yyyy-MM-ddTHH:mm:ss.fffZ`; closed text token supports exact legacy presets |
| `time.captureLocal` | D / explicit local offset | Exposure start converted using the frozen offset described below |
| `time.zoneId`, `time.zoneRuleIdentity` | T / identity | The explicit frozen zone/conversion evidence used for local time |

`FrameFactsRetainedCaptureExtrasV1` is a producer-neutral projection of source
timestamp, submission cadence and processing-completion evidence, which the
descriptor does not retain. `FrameFactsRetainedStackV1` projects a retained
stack product's output identity, source artifact IDs and total integration.
Slice C must select those fields from the retained submission/result, never a
current configuration. Each projection includes the exact descriptor SHA-256;
a mismatch makes its fields `CONFLICTING`. Missing projections remain `MISSING`.
Stacks reject invalid identities, empty/duplicate source IDs, a capture ID used
as an artifact ID, more than 4,096 source IDs and negative integration as
`UNSUPPORTED`. Negative cadence and processing completion before exposure end
are also unsupported. Neither projection
fetches evidence or adds new persistence.

All UTC tokens normalize explicit `DateTimeOffset` values to offset zero.
`time.captureLocal` requires `FrameFactsRetainedLocalTimeV1`: the exact descriptor
identity, capture instant, zone ID, offset at that instant and retained rule or
conversion identity. The builder does **not** call `TimeZoneInfo`, resolve a zone
name, or use today's DST rules. Offset must be whole minutes within ±14 hours.
A zone ID is bounded to 128 characters and rule identity to 256 characters.
A mismatched capture instant/descriptor or matching-location zone disagreement
is `CONFLICTING`; malformed offset/evidence is `UNSUPPORTED`. Offset overflow
is unavailable. Older records without this evidence have missing local time;
there is no implicit UTC or host-local fallback. The explicit offset disambiguates
repeated local times. The caller must provide evidence for that instant, not
reuse an offset from another date.

## Location mapping

| Tokens | Kind / unit | Retained source and availability |
| --- | --- | --- |
| `location.id`, `location.source` | T / text | Coordinate-free `d.Location.LocationId`, `Source` |
| `location.version` | I / version | `d.Location.Version` |
| `location.latitude`, `location.longitude` | N / degrees | Matching historical `DeploymentLocationSnapshot.LatitudeDegrees`, `LongitudeDegrees` |
| `location.elevation` | N / metres | Matching historical snapshot's `ElevationMeters` |
| `location.timeZone` | T / zone ID | Matching historical snapshot's `TimeZoneId`; alone supplies no local offset |

Coordinates require the retained snapshot's **exact** coordinate-free provenance
(ID, version, source, accuracy and effective interval) to equal `d.Location`,
the capture instant to lie in its half-open effective interval, and its canonical
hash/coordinate ranges to validate. A snapshot without capture provenance does
not supply coordinates. Wrong provenance is `CONFLICTING`; invalid coordinates
or hash are `UNSUPPORTED`; absent evidence is `MISSING`. Provenance names the
snapshot hash and retained source. No live deployment, GPS, rig or observatory
fallback is consulted. Snapshot validation here checks canonical integrity
without resolving a host zone database.

## Environment mapping

`FrameFactsRetainedEnvironmentV1` projects a completed retained association:
capture ID, kind, availability, association identity, exact exposure interval
and optional structured `EnvironmentalObservationFactV1` (V1 or V2). It is
bounded to one association per defined kind (12 maximum). Duplicate/unknown
kinds are caller errors. Slice C maps Fresh/Available → `Available`, Stale →
`Stale`, Missing → `Missing`, Contradictory/Conflicting → `Conflicting`; unknown
association schemas/statuses must be `Unsupported`.

| Scalar token | Kind / unit | Observation value kind |
| --- | --- | --- |
| `environment.airTemperature` | N / C | AirTemperature |
| `environment.relativeHumidity` | N / percent | RelativeHumidity |
| `environment.atmosphericPressure` | N / Pa | AtmosphericPressure |
| `environment.windSpeed`, `environment.windGust` | N / m/s | WindSpeed, WindGust |
| `environment.windDirection` | N / degrees true | WindDirection |
| `environment.precipitationRate` | N / mm/h | PrecipitationRate |
| `environment.rainState` | T / `TRUE` or `FALSE` | RainState BooleanValue |
| `environment.skyBrightness`, `environment.skyQuality` | N / mag/arcsec² | SkyBrightness, SkyQuality |
| `environment.cloudCover` | N / fraction [0,1] | CloudCover; no percentage conversion or fabricated cloud value |
| `environment.cameraSensorTemperature` | N / C | CameraSensorTemperature, supported by V2 only |

Every scalar additionally has `.sourceKind` (T), `.quality` (T) and
`.observedUtc` (D), each with its own `.status`. These metadata describe a valid
retained observation even when the scalar association is stale or conflicting;
they do not assert availability of the scalar. Runtime provenance includes both
the association identity and observation content SHA-256. The observation hash
covers provider/source/version, algorithm/parameters and any derived lineage.

Association capture/interval mismatch, wrong observation kind or wrong rig
makes the scalar `CONFLICTING`. The association's unavailable decision cannot
be overridden by a supplied reading. For an available association, observation
validation checks supported schema, identity, source provenance, units, ranges,
time shape and lineage. Validity must span the exposure and `StaleAfterUtc`
must be later than exposure end; otherwise the scalar is `STALE`. Only `Good`
quality is scalar-available; unknown/suspect quality and unsupported schemas or
units are `UNSUPPORTED`. Malformed optional observation values are also
`UNSUPPORTED` without discarding unrelated capture facts. Missing structured observations remain `MISSING`.
Freshness uses this retained exposure interval, never wall-clock age at viewing.

`Measured`, `Simulated`, `Imported`, `Manual` and `Derived` are preserved as
authored; all may carry available values but none becomes a measured observation
by evaluation. Camera control setpoints and scene refraction are separate
configured facts. Legacy `PresentationEnvironmentalFactV1.DisplayLine` contains
no structured numeric reading and is **never parsed** to supply these tokens.

## Astronomy mapping

| Tokens | Kind / unit | Retained scene source |
| --- | --- | --- |
| `astronomy.kind` | T / enum | `Kind`: Predicted, VirtualRenderAuthoritative or ImageRegistered |
| `astronomy.effectiveUtc` | D / UTC instant | `EffectiveUtc`, distinct from capture/source/processing times |
| `astronomy.algorithm` | T / version | `AstronomyAlgorithmVersion` |
| `astronomy.refraction` | T / `TRUE` or `FALSE` | Configured `Refraction.Enabled` |
| `astronomy.observerLatitude`, `astronomy.observerLongitude` | N / degrees | Configured `Observer.LatitudeDegrees`, `LongitudeDegrees` |
| `astronomy.observerElevation` | N / metres | Configured `Observer.ElevationMeters` |
| `astronomy.boresightAltitude`, `astronomy.boresightAzimuth`, `astronomy.roll` | N / degrees | Retained `Projection` geometry; not measured pointing |
| `astronomy.sun.geometricAltitude`, `.apparentAltitude`, `.azimuth` | N / degrees | Retained `Objects` entry `solar-system:Sun`: geometric/apparent horizontal coordinates |
| `astronomy.moon.geometricAltitude`, `.apparentAltitude`, `.azimuth` | N / degrees | Retained `Objects` entry `solar-system:Moon`: same mapping |

The scene must pass the existing pure projected-scene validator, including its
identity, and its source must match the descriptor's capture ID, artifact ID
and descriptor identity. Unsupported/invalid scenes are `UNSUPPORTED`; source
disagreement is `CONFLICTING`. Missing scenes/objects remain `MISSING`.
Sun/Moon objects also require `CelestialObjectKind.SolarSystemBody`, the matching
retained solar-system selection and ephemeris provenance. A reserved body ID on
a catalogue star or an unselected body is `CONFLICTING`; it cannot supply solar
geometry. Missing ephemeris provenance invalidates the scene as `UNSUPPORTED`.
Configured observer/projection facts have explicit configured provenance;
solar/lunar geometry has derived provenance retaining the scene kind. Even an
ImageRegistered scene is not automatically a physical detection. No current
catalogue, ephemeris or solar/lunar computation runs here; unretained moon
phase, event times and measurements have no active tokens. Simulated scene
cloud scenarios never supply environmental measurements.

## Explicit legacy preset conversion

`FrameFactsCornerPresets.ConvertLegacy(facts)` escapes every template delimiter
in the four actual `PresentationMetadataFactsV1` corner arrays and returns
editable literal template source. It preserves the exact retained strings,
including any truncation performed by the original producer. Conversion never
adds further clipping, infers numbers or activates a graph. IDs/names describe
Capture, Acquisition, Environment and Lineage; they carry no placement.
After conversion, any preset may be edited and placed in any corner.

`FromRetained(context, retainedEnvironmentLines)` instead returns token-based
equivalents of today's fixed layout:

| Semantic preset | Current source placement | Retained template content |
| --- | --- | --- |
| Capture | Top left | CAPTURE sequence; width × height PIXELS; UTC exposure start |
| Acquisition | Top right | ACQUISITION; schedule; exposure F3 S; gain F3; offset F1; setpoint F1 C |
| Environment | Bottom left | ENVIRONMENT and escaped retained DisplayLine strings, or NOT RETAINED |
| Lineage | Bottom right | LINEAGE; catalogue name/version; calibration version/hash first 12; stack count/integration F3 S or STACK UNAVAILABLE; profile version/hash first 12 |

Absence decisions and legacy environment display text are frozen at explicit
conversion time. Other values remain tokens evaluated against the selected
retained context. The immutable result's `Source` is ordinary editable v1 source.
There is no substring formatter; the closed `shortHash` and `captureLegacyUtc`
tokens provide those exact existing displays. Conversion validates all four
templates and their expansion before returning them. An oversized source,
line, line count, invalid text or expansion fails the entire conversion with a
diagnostic and failed preset ID, with no partial presets. One empty retained
line is preserved separately from an absent corner. Literal line strings cannot
inject newlines. There is no implicit migration or saved-product mutation.

The existing annotation string keys are a source map only: agent-identity and
capture-sequence map to capture; utc to capture time; schedule-profile, exposure,
cadence, gain, offset and sensor-setpoint to acquisition; environment to retained
associations; catalog/calibration/stack/processing-profile to lineage. Their
current-configuration fallback paths are not used. Current annotation tokens,
fixed corner builders and default rendering continue unchanged.

Acceptance is hardware-free. Tests cover retained types, every family, explicit
time offsets, older optional fields, all availability statuses, source kinds,
invalid units/quality/schema, conflicting identities, immutable copies and all
four legacy corners, including syntax escaping and bounds.
