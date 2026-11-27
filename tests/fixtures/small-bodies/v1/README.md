# Small-body golden fixtures v1

These fixtures freeze the offline input and expected output for Slice S0 of
[#1396][issue] under the [source decision][decision] and [v1 ephemeris
contract][contract]. Every fixture is HVO-authored synthetic data. The provider
files imitate the documented grammar with fictional data and synthetic
signature sources. They contain no captured API responses, upstream headers,
literature references, or MPC catalogs. NASA/JPL-Caltech attribution and
provider identifiers in the expected manifests are required contract fields.

## File integrity

`fixture-manifest.json` describes every other file in this directory, with
per-file provenance, exact byte lengths, and uppercase SHA-256 digests. Its own
`manifestSha256` hashes the manifest with that property omitted. All JSON files
use compact RFC 8785 JCS UTF-8 without a BOM or trailing newline. Provider
response text and synthetic time-data bytes are carried inside JSON strings.
Hash those strings only where an embedded digest explicitly identifies them.
The snapshot `rawSha256` fields identify the complete provider envelope files
as recorded in the fixture manifest.

## Inputs and expectations

- `providers/` contains the asteroid and comet OBSERVER, SBDB and Lookup
  responses. The whole-second and `.000` asteroid responses have identical
  scientific content and normalize to the same series. The off-grid variant
  changes only the usable odd-indexed sample to `00:30:00.125` UTC and must be
  rejected before interpolation. The first sample is the padded start, even
  rows are retained knots, and odd rows supply independent half-step evidence.
  The open interval between the usable endpoints for the asteroid contains one
  reference midpoint. The two usable comet intervals contain two.
- `snapshots/` contains both two-body outputs and their matching manifests. The
  v1 and v2 filenames identify producing parser and normalizer versions. Both
  conform to `hvo-small-body-manifest-v1` and `hvo-small-body-ephemeris-v1`.
  The second refresh changes asteroid photometry and carries the original comet
  forward after a solution-ID mismatch. The corrupt manifest hashes itself
  correctly and points to the wrong payload digest. The separate unverified
  series hashes correctly and has no retained midpoint evidence.
- `analytical/half-step.json` records the independent directions and both
  reference residuals for each usable midpoint. For either body, let
  `tau = (UTC - referenceUtc) / stepSeconds`. In AU, the astrometric position
  is `originAu + tau × deltaAuPerStep`. The apparent vector applies the right
  handed X rotation followed by the Z rotation. Angles are rounded to nine
  decimal places in the synthetic wire rows. Range is the norm of the original
  vector. Range rate is its time derivative using the IAU AU in kilometres.
  The comet crosses the RA meridian between knots. Photometry, elongation,
  phase and formal uncertainty are authored fixture values. They do not
  describe a physical object or the output of an orbit fit.
- `analytical/rectilinear-flyby.json` freezes the contract's high-precision
  maxima for the planar direction `(rangeAtClosestApproachKm, speedKmPerSecond
  × elapsedSeconds, 0)`. `k` starts an aligned interval in units of its step
  and `u` is fractional position within the interval. `startInStepUnits`
  includes the grid phase. The offline test also bounds errors outside the
  scanned domain and checks the motion cap independently of the frozen values.
- `analytical/oscillation.json` places a `2″ sin(π t/h)` RA perturbation at
  declination zero. The retained seven knots miss it and the independent odd
  samples expose it. The example uses the smallest supported step.
- `evaluation/` gives the later evaluator and replay slices explicit inputs
  and expected outcomes, including a case-specific freshness age. The time
  strings contain short HVO-authored NTP leap and fixed-column EOP examples.
  The replay artifact contains authored retained display coordinates and both
  available replacement candidates. The clipping sequence uses geometric
  altitude, an obstruction at the north wrap, unknown terrain and sensor
  clipping. Segment arrays contain zero-based sample indices.
- `canonical/jcs-vectors.json` freezes all contract §3.3 byte/digest examples,
  both spellings of the integral value and further HVO strings, numeric
  boundaries and malformed input. The outer file is canonical. Its input
  strings and hex bytes deliberately include noncanonical and invalid JSON.

[issue]: https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1396
[decision]: ../../../../docs/planning/sky-layers-source-decision-1396.md
[contract]: ../../../../docs/contracts/small-body-ephemeris-v1.md
