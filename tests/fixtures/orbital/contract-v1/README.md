# Orbital contract conformance pack v1 (#1402)

These are HVO-authored **synthetic contract examples**, not downloaded elements,
real ISS predictions, optical truth or a qualified propagator. The nine-digit
identifier, duplicate-object/different-epoch records, aliases and numerical
coefficients deliberately exercise the shared schema. Raw hashes, software
hashes and source lengths are conspicuous synthetic placeholders. Source URIs
identify the approved profile; they do not claim that these bytes were fetched.
No provider feed or new third-party implementation is redistributed here.
The separately pinned #527 reference pack retains its own licenses and terms.

`fixture-manifest.json` pins each file's exact UTF-8 byte length and uppercase
SHA256. `snapshot-manifest.json` also carries the acquisition identity (self
field excluded from hashing); `records.json` is ordered by record identity.
The payload can remain unchanged across a new acquisition manifest. Complete
requested active-group coverage is explicitly different from global coverage.

`exposure-track.json` is a two-second exposure with two drawable segments and
an intervening projection gap. Unknown illumination, orbit/clock/calibration
errors and nominal zero EOP remain explicit. Its pixels are illustrative schema
values, not independently computed projection coordinates. `pass.json` has a
separate ten-minute pass inside a one-hour search interval. Neither product
claims measured geometry or actual sensor timing.

`derivative-coefficients.json` freezes nonzero GP DOT/DDOT and the expected
rad/min² and rad/min³ coefficients, matching the pinned python-sgp4 2.27 OMM
initializer documented in the #527 ledger. This is unit conversion only, with
no additional two/six factors. #529 must verify its actual adapter against it.

Reproduce and check all canonical bytes from the typed examples (SDK in
`global.json`):

```bash
dotnet test tests/HVO.SkyMonitor.Astronomy.Tests/HVO.SkyMonitor.Astronomy.Tests.csproj --configuration Release --filter 'FullyQualifiedName~Orbital'
```

`OrbitalContractExamples.cs` is the deterministic source for these examples.
`CanonicalFixturesRoundTripWithPinnedByteHashes` rebuilds each typed example and
compares its exact canonical bytes, then verifies every declared file hash.
Other focused tests exercise missing/stale/expired/future data, source ranking,
multiple records, corrupt/unsupported/oversized input, defensive copies,
measured-evidence mismatch, readout transforms, clipping and partial budgets.
`selection-cases.json` pins inclusive age boundaries and the stale-diagnostic
opt-in; `selection-policy.json` pins its canonical policy identity.
`measured-geometry.json`, `measured-calibration.json` and
`measured-assessment.json` retain an accepted synthetic astrometric fit using
existing solver conventions. Tests load those exact retained inputs and verify
the relationship; they do not require a fresh floating-point solve to reproduce
its bytes on every architecture. The separate live synthetic-fit regression
checks that newly generated evidence is accepted and mismatched evidence is
rejected. This is contract conformance, not physical calibration acceptance.

A separately selected Manual-category diagnostic reports unchecked typed JSON
baseline, validated materialization, and cached indexed selection costs for
16,754 synthetic records:

```bash
dotnet test tests/HVO.SkyMonitor.Astronomy.Tests/HVO.SkyMonitor.Astronomy.Tests.csproj --configuration Release --filter 'FullyQualifiedName~OrbitalContractResourceEvidenceTests' --logger trx
```

It records CPU, elapsed time, allocations, working set, runtime/architecture,
workload bytes and hashes. It uses three observations after JIT warm-up and
forced GC, and makes no full-frame cadence or physical-accuracy claim.

No network is used. Batch B's provider/HTTP/crash/retention fixtures belong in
`tests/fixtures/orbital-acquisition/` and reference these identities without
forking the canonical files.
