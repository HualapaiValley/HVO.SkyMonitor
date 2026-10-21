# Local satellite planning

Satellite planning consumes the shared [orbital producer](orbital-pass-arc-qualification-v1.md)
and [orbital contracts](../contracts/orbital-elements-v1.md). Acquisition and its
schedule remain the orbital snapshot adapter's responsibility. Planning performs
no network access and runs separately from capture and detection.

## Observer, time and selection

The Observatory page supplies its selected UTC instant and displayed observer/rig
identities. The planner reads the authoritative deployment location and active
rig again and rejects a changed context. It uses the existing rig projection
factory. There is no second satellite geometry setup or assumed measured
calibration. The observer's existing height is explicitly treated as an assumed
ellipsoidal height when a stronger datum is unavailable.

An explicit name or NORAD-number filter selects a bounded population from one
immutable local snapshot. Matching and selected counts remain separate from the
snapshot's requested-population completeness. No matching record does not mean
that the sky contains no satellites. A missing result, a search-window boundary,
and a resolved threshold crossing have different meanings.

All query and product instants are UTC. Local displays retain the site timezone
and UTC reference. Retrieval age describes the snapshot source; signed element
age describes the selected orbital record at the original search endpoints.
Neither establishes physical orbit accuracy, sunlight, brightness or visibility.

A valid filter with zero matching records has the explicit `NoMatches` outcome.
Its fixed UI description states matching zero and selected zero in this snapshot,
without treating the request as invalid or claiming global absence.

## Local time inputs

`AddCameraAgentSatellitePlanning(configuration)` provides the consumer seam in
`CameraAgent.Common.OrbitalPlanning`. Host UI registration is separate. The
CameraAgent host composes the planner and authenticated UI adapter after
`AddCameraAgentInfrastructure`. That infrastructure supplies the singleton
`IOrbitalSnapshotReader` from the local verified orbital store; it supplies
neither EOP nor leap-second data. The planning registration resolves those from
the pair bundled with each release, a newer pair adopted by the optional managed
refresh, or an explicit override (see
[Bundled and refreshed time data](bundled-time-data-v1.md)). Refresh and planning have independent enabled
options: disabled or halted refresh still leaves the same validated
last-known-good snapshot usable for explicitly enabled planning.

The `CameraAgent:SatellitePlanning` options are deliberately unconfigured by
default:

| Option | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Permit explicit local planning requests. |
| `AllowStaleDiagnostics` | `false` | Allow explicitly qualified stale diagnostic records under the selection policy. |
| `EarthOrientationPath` | absent | Override: fully qualified path to retained, approved finals2000A bytes. |
| `EarthOrientationSha256` | absent | Override: expected SHA-256 of those exact bytes. |
| `LeapSecondsPath` | absent | Override: fully qualified path to retained leap-second bytes. |
| `LeapSecondsSha256` | absent | Override: expected SHA-256 of those exact bytes. |
| `TimeDataRefreshEnabled` | `false` | Allow the reserved daily automation to download and adopt a newer pair. |

With none of the four override settings present, planning uses the pair bundled
with the release in `time-data/` beside the application (`/app/time-data/` in the
image): `finals2000A.daily.extended`, `leap-seconds.list` and their
`provenance.json`, or the managed pair under `<RawIngressRoot>/.time-data/` when
the refresh has adopted one whose planning coverage ends later (the bundled pair
wins a tie). Each resolution verifies the files against the SHA-256 values in
their provenance record and parses them; a mismatch or malformed record leaves
that pair unavailable. Setting any of the four selects the override, which must
then be complete and valid. An incomplete or invalid override makes planning
unavailable with a reason; it never falls back to another pair, and CameraAgent
never writes the configured files. Only an options change, a new release or a
refresh adoption makes another pair active.

The file source reads at most 8 MiB of EOP and 64 KiB of leap data. An embedding
host may instead provide `ICameraAgentOrbitalTimeSource` to return an already
resolved immutable `OrbitalTimeData` and its source. No planning or capture route
downloads time data; only the reserved `time-data-refresh` automation does, at
most once a day, when **Time-data refresh** is on. Repository test fixtures are
never default installed runtime inputs.

Absent, malformed, mismatched, expired or uncovered inputs remain unavailable.
The original query interval is checked before loading the orbital snapshot or
projecting the sky; selected element epochs are checked after record selection.
The existing time model rejects unsupported leap crossings and missing daily
EOP brackets. Bulletin A rapid observed-only rows are gaps, so the weeks between
the last final (Bulletin B) value and the first prediction are unsupported.
Planning is supported up to the earlier of the last EOP row and the leap-second
list's expiry; past it, the planner returns `TimeInputsUnavailable` with that
coverage end rather than an approximation. Geometry-free pass arcs require actual retained EOP; nominal-zero
diagnostics are not a fallback. Final/predicted EOP quality, coverage and hashes
remain in the producer's model facts. Returned messages omit local paths.

### Enable local planning

**Observatory & location → Orbital refresh & satellite setup** offers independent
**Orbital refresh**, **Satellite planning** and **Time-data refresh** switches from
the settings catalogue. The equivalent CLI commands are:

```bash
hvo-sky config get satellitePlanning.enabled
hvo-sky config set satellitePlanning.enabled=true
hvo-sky config set timeDataRefresh.enabled=true   # optional: keep time data current between releases
hvo-sky time-data                                 # active source, coverage, last refresh and adoption
```

Planning and the time-data refresh use the existing live options monitor: saving
either switch applies immediately. Orbital refresh needs a CameraAgent restart. The catalogue exposes
both switches as the existing v1 enum choices `false`/`true` (Off/On), and writes
JSON Booleans to `state/identity/appsettings.local.json`. Environment variables
and `config/secrets` override the file; the web UI and CLI show the source and
refuse ineffective writes. Use `satellitePlanning.enabled=false` to turn planning
off without changing retained snapshots or capture.

Turning planning on is enough: the bundled time data needs no hand-placed files.
To use other approved files, configure all four override paths/hashes in the
options table, using absolute paths readable inside CameraAgent. The setup panel
displays the active source (bundled, refreshed or override), each file's status,
path, SHA-256 and provenance, the final and predicted EOP spans, whether the
checked window uses final or predicted values, and the exclusive leap-second
expiry. Its refresh facts show the planning coverage end (and plainly when it has
expired), the refresh switch and whether the daily automation exists, the last
attempt with its outcome and reason, and the last adoption; a failed refresh is
shown there and never as expiry. The panel makes no download request and never
substitutes nominal zero EOP. Without the refresh, the bundled pair ages with the
release and an upgrade brings a newer one. To keep it current between releases,
turn **Time-data refresh** on and create the reserved daily automation from
`/operations/automations?setup=time-data` (see
[Managed refresh](bundled-time-data-v1.md#managed-refresh)). The panel checks the default six-hour window from the selected scene
instant; an explicit search still validates its actual window and selected
element epochs. Read-only paths are visible only through the authenticated setup
surface; planner responses continue to omit local paths.

The panel explains disabled planning, no validated orbital snapshot, missing or
invalid time data (an incomplete or invalid override, or neither the bundled nor a
refreshed pair verifying), a configured SHA-256 mismatch, and expired/outside time coverage.
Expired coverage names the source and the date coverage ends. Each reason links to the switch, Software & Catalog refresh setup, or
the read-only time-data setup instructions. A hash match by itself does not mean
the file parsed or its coverage is usable. After replacing hand-configured bytes,
update their pinned hashes to adopt the pair explicitly, then reload setup status.

Capture-time **Predicted satellite tracks** are not available yet: #529/#1393 own
their producer and integration. Astrometry and its residual layers are also not
available yet (#526 → #1187 → #523). Enabling planning does not enable either.

## Bounded planning and detail

Searches run outside the UI circuit through a single active-work gate. A busy
planner returns promptly instead of building a queue. The policy caps a search
at 24 hours, 32 selected objects and 128 pass results. Search and selected-arc
producer work each have a 1,500 ms bound. Caller cancellation propagates.
These bounds cover numerical work, not loading local data. The first request
after activation, cache eviction or relevant retention changes can load and
validate a snapshot on the background work path. The #528 synthetic workload
measured a cold read near 0.97 seconds with 2.1 GB allocated; #1425 and #1422 own
reader and canonical-serialization improvements. This is not a 1.5-second
end-to-end or five-second capture guarantee. The two-snapshot cache avoids I/O
only while its identity and retention-generation checks remain valid.

At most four original-query sessions are retained for ten minutes, with a
128 MiB aggregate source-byte/product allowance. That allowance counts retained
source payload and canonical product bytes; it is not a claim about exact
managed working set. Expired, evicted or incompatible details require a new
search. UI generations discard superseded completions.

Selecting a row uses its original search identity and exact canonical parent
pass hash. It never creates a narrower search, reselects an element epoch or
substitutes a newer snapshot. Site, rig, snapshot, model and time-input changes
invalidate incompatible detail requests. Numerical Complete/Partial/Unavailable
status remains independent of population completeness and facade availability.

## Presentation

The planning diagram shows the full angular pass, including portions outside
the rig. A separate configured-rig preview uses the existing projection. Both
respect declared segments and exact available anchors. Missing or clipped
boundaries are labeled accordingly; no gap is joined or extrapolated.
An isolated accepted sample remains a distinct prediction point with a UTC title,
including when a narrow rig admits only one non-anchor sample.

`OrbitalTrackPresentation` converts already-resolved exposure tracks into the
existing typed presentation payload. Its `PredictedSatelliteTracks` layer is
independent from measured transient trails. Orange dashed paths, endpoint
circles and bounded optional labels identify predictions without implying a
detection. No uncertainty band is invented. Optional label omission does not
remove the canonical source facts.
Image-layer strokes default to one pixel, with two-pixel-radius endpoint rings.
Planning paths also use a one-pixel stroke; pass-event and isolated-point radii
are three and two SVG units, with one-unit outlines. Per-overlay line width,
marker size and marker visibility settings remain future configuration work.
Mandatory caption text also retains applicable stale-element, source-validity,
configured-geometry and nominal-zero-EOP diagnostic warnings. These qualifications
remain visible when every optional object label is omitted; a raster unable to
carry the complete required caption is rejected explicitly.

`CreatePayloadAndSourceFacts` also returns the exact producer-versioned source
envelope bytes. Their SHA-256 equals the payload's source identity. An artifact
adapter can retain that envelope and its referenced canonical tracks through
the existing metadata/layer-product lineage. The converter never reads base
pixels, reapplies readout transforms, propagates an orbit or silently truncates
geometry that exceeds its explicit presentation bounds.

Current Sky shows a retained compatible prediction as a separate control.
Without one, it reports that no compatible prediction was retained for the
capture. A planner result is not substituted for historical capture evidence.
Saved stacks retain only selected layer identities through the existing frozen
manifest/materialization path. Raw and calibrated detector inputs receive none
of these annotations.

## Acceptance boundaries

Component and frozen-input examples do not establish refreshed live snapshots,
automatic time-source adoption, measured calibration, solar/shadow or optical
visibility qualification. Capture prediction persistence/composition must use
the exact capture snapshot and geometry; displaying a retained layer does not
create that evidence. The acquisition delivery remains #528, measured streak
association remains #530, and complete hardware/resolution-specific five-second
frame-available-to-local-publication qualification remains #1395. No main
promotion, live deployment or automatic meteor rejection is implied.
