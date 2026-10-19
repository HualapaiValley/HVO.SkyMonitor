# Offline orbital research reference pack

This pack supports #527's decision checkpoint. It is not the executable #1402
snapshot schema, a production satellite catalog, or a detection-sensitivity
evaluation set. All paths are repository-relative and all inputs are pinned in
`manifest.json`, so another host can use the reviewed commit without access to
the original POC workstation.

## Inputs and attribution

- `gp-source-facts.json` retains six factual orbital records from the single
  CelesTrak active-group acquisition identified in the manifest: ISS 25544,
  Starlink 44714/44718, six-digit object 100000, deep-space object 2866 and
  the provider-named CELESTIS/rocket-body object 25160. Original field names and
  values are preserved; model defaults and UTC interpretation come from the
  approved provider profile. Names are not authoritative physical-type labels.
  Credit CelesTrak and its underlying USSF/Space-Track GP source. This small
  factual research subset does not grant or imply a whole-feed release license.
- `SGP4-VER.TLE` retains the verification input published by aholinch/sgp4 at
  `552cb1489a52c3023ae70cb6c7e239e84c5950fe`, matching the official AIAA archive before the documented text-only transform.
  The retained text uses LF and trims trailing whitespace; fixed-width fields
  are preserved. The manifest records the original input hash and transform.
  Its complete upstream Unlicense is `AHOLINCH-LICENSE.txt`.
- `vallado-afspc-vectors.json` preserves the 641 numeric position/velocity rows
  from 32 `.e` entries in the pinned official
  [AIAA 2006-6753 archive](https://celestrak.org/publications/AIAA/2006-6753/AIAA-2006-6753.zip).
  Credit David Vallado and coauthors / CelesTrak. Source entries are retained
  per case. Times are converted from seconds to minutes without changing
  position/velocity values. Count actual rows: some source headers declare a
  different point count. Case 33334 is an expected propagation error 3, not a
  successful zero state. The input TLE file has 33 cases; cases without a
  published `.e` output remain separate #529 error-case qualification.

No production or reference third-party implementation is vendored here.
`verify.py` uses separately installed python-sgp4 2.27 (MIT;
[pinned source](https://github.com/brandon-rhodes/python-sgp4/tree/092d206d6e6556a3cbbbe86f6ba4293c1f4ec646))
to check AFSPC `a` / WGS72 numerical consistency offline. It does not execute or
qualify the selected .NET candidate. #529 must independently qualify that
candidate against the approved oracle and the complete failure cases.

## Reproduce

Use Python 3 with `sgp4==2.27` installed in a dedicated environment, then:

```bash
python docs/validation/orbital-reference-v1/verify.py
```

The verification checks every manifest length/hash, inventory, expected error
and both near-Earth/deep-space branches, then enforces position-vector residual
at most 1e-6 km and velocity-vector residual at most 1e-8 km/s. It has no
download path. Do not regenerate expected vectors with the implementation under
test. Report output with `--output <new-file>`; retained reports are immutable.

The existing ISS POC separately used improved mode `i`, Skyfield 1.55, retained
EOP and DE421, and synthetic brightness. Preserve its lineage. These TEME
vectors do not qualify EOP/observer conversion, pass search, shadow, rendered
pixels, orbital physical accuracy, calibrated association or five-second live
performance. Those have separate #529/#530/#1395 gates.
