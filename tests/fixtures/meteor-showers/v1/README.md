# Meteor-shower golden fixtures v1

HVO SkyMonitor authored this offline corpus on 2026-11-27 for
[#1396 Slice M0](../../../../docs/planning/sky-layers-source-decision-1396.md#c4-follow-on-implementation-slices-and-verification-matrix).
The governing [meteor-shower contract](../../../../docs/contracts/meteor-shower-context-v1.md)
and source decision are pinned at `770fb1f2017e368dd2221f859768fb8126fea4dc`.
[Roy's Part 2 authorization](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1396#issuecomment-7216125734)
authorizes preparing this corpus. Slice M1 begins after this corpus merges and
its approval is recorded on #1396.

## Delivery and provenance

Third-party MDC and IMO snapshots are **operator-import only**. External gate
**M2** awaits verified primary MDC terms or written MDC redistribution
confirmation. External gate **M1** awaits written IMO confirmation for
bundling IMO-derived activity. Operators may explicitly import locally acquired,
schema-validated, SHA-256-verified snapshots. Repository test data contains
zero third-party dataset files or provider payloads. The synthetic identities
and editions use HVO attribution, a fictional export date, and null raw-export
hashes and approval references. Test data is for offline CI.

- The snapshot records, rates, time uncertainties, and observing notes are
  HVO-authored. QUA, GEM, and URS are case labels from the approved decision.
  The dates reproduce the contract's boundary inputs. The synthetic QUA
  forecast peak is 08:40 UTC. The separate 03:25 UTC horizontal vector is
  the published **geometry test instant** from contract §5.2.
- The two HVO QUA horizontal outputs come from contract §5.2. The other
  vectors were calculated using an independent IAU 1976 Cartesian precession
  rotation, mean sidereal time, and a local east/north/up rotation. Altitude
  is unrefracted. Upper transit altitude is the geometric daily upper bound
  used to demonstrate never-rising radiants at the synthetic southern site.
- Vernal longitudes and the nominal 0° crossing come from pinned Astronomy
  Engine 2.1.19, using the contract's J2000 geometric geocentric Sun vector
  and 23.439291111° mean obliquity. The nominal crossing is rounded to the
  first whole second on or after the crossing. Exact angle boundary inputs
  and drift coefficients are synthetic.
- Civil times use IANA `America/Phoenix` and `America/Los_Angeles` rules.
  Cloud examples are synthetic projections of optional `cloud-assessment-v1`
  facts with fixture-defined site and evidence validity. They are inputs to
  the card, rather than serialized Processing artifacts.
- JCS reference values use contract §3.1 and RFC 8785. The extended probe
  adds UTF-16 ordering, minimal escaping, binary64 extremes, and both
  exponent cutoffs.

## Files and verification

| Files | Frozen evidence |
| --- | --- |
| `annual-2026.json`, `annual-2027.json` | Single-edition cases, annual mode with null nominal activity, broad/variable peaks, equinox wrap and drift. |
| `annual-dual.json`, `annual-cutoff.json` | December retention, year-end rollover, Working opt-in, Removed tombstone and precedence before the lookahead cutoff. |
| `identity-only.json` | Null activity sources, empty forecasts, reference-only radiant, source-qualified nominal peak with null bounds. |
| `reject-*.json`, `corrupt.json` | Canonically hashed invalid edition inputs and deliberately truncated JSON. The absent case names an intentionally missing file. |
| `occurrences.json` | Per-forecast Stage 1 and Stage 2 dispositions, mode, selected peak/ZHR facts, fallback, status and import outcomes. The decision and reason labels describe fixture expectations. |
| `observing.json` | Solar longitude, drift, horizontal coordinates, IANA civil dates, optional cloud context, and load/retention outcomes. |
| `jcs.json` | Noncanonical input text, exact canonical text and UTF-8 hex, and SHA-256 for canonical bytes. |
| `manifest.json` | Provenance and uppercase SHA-256 over each other file's full byte stream, including this README. Git pins the manifest itself. |

JSON fixtures use compact RFC 8785 UTF-8 without BOM or trailing newline.
Each snapshot also carries its own SHA-256 over canonical JSON with only
`snapshotSha256` omitted. `corrupt.json` is deliberately invalid JSON.
The Astronomy Unit tests read this directory from a source checkout and
validate the corpus offline with fixture-only checks and existing astronomy/time
primitives. Slices M1 and M2 are tracked by #1396 for production schema,
evaluation, verified import/retention, and card behavior.
