# Orbital acquisition fixtures v1 (#528)

HVO wrote these **synthetic, provider-shaped examples** of the CelesTrak GP JSON
format (`GROUP=active&FORMAT=JSON`, profile `celestrak-gp-json-v1`). None of the
bytes were downloaded from CelesTrak or any other provider, and the element
values are not real objects or predictions. The names are deliberately
conspicuous (`HVO SYNTHETIC ...`). No provider feed is redistributed here.

`fixture-manifest.json` pins every file's exact byte length and uppercase
SHA256. Every fixture is a single line of compact UTF-8 JSON with no trailing
newline, so line-ending conversion cannot change the pinned bytes.
`CelesTrakGpFixtureTests` verifies the manifest, and the normalizer tests pin
the valid fixture's normalized payload and manifest identities.

## Files

| File | Outcome when retrieved at `2026-10-19T17:28:18Z` |
| --- | --- |
| `celestrak-gp-active-valid.json` | Accepted: 8 records, 7 objects |
| `invalid-unknown-member.json` | `orbital.unknown-member`, record 1, `DECAY_DATE` |
| `invalid-duplicate-member.json` | `orbital.duplicate-member`, record 1, `MEAN_MOTION` |
| `invalid-conflicting-ref-frame.json` | `orbital.conflicting-metadata`, record 1, `REF_FRAME` (`GCRF`) |
| `invalid-norad-string.json` | `orbital.member-type`, record 1, a string catalog number |
| `invalid-norad-zero.json` | `orbital.invalid-object-identity`, record 1 |
| `invalid-norad-negative.json` | `orbital.invalid-object-identity`, record 1 |
| `invalid-norad-ten-digits.json` | `orbital.invalid-object-identity`, record 1 (`1000000000`) |
| `invalid-norad-fraction.json` | `orbital.invalid-object-identity`, record 1 (`45001.0`) |
| `invalid-norad-exponent.json` | `orbital.invalid-object-identity`, record 1 (`4.5001e4`) |
| `invalid-duplicate-record.json` | `orbital.duplicate-record`, record 1 duplicates record 0 |
| `invalid-root-object.json` | `orbital.json-root`, the root is an object rather than an array |

The valid fixture covers:

- two distinct records for NORAD 25544, which are both kept;
- catalog numbers at the bounds, 1 and 999999999, plus a six-digit number;
- a future epoch 22.5 hours after retrieval, which is inside the 30-day limit;
- old epochs, two weeks earlier and in 1958;
- exponent spellings (`1.2e-5`, `2.5E-6`, `-1.1e-4`);
- one record carrying every optional OMM metadata member with consistent values;
- omitted and `null` `ELEMENT_SET_NO`, `REV_AT_EPOCH`, `OBJECT_NAME` and
  `OBJECT_ID`, and empty or blank aliases, all of which normalize to `null`;
- epochs with a `Z` suffix, a one-digit fraction, no fraction and seven digits;
- an escaped non-ASCII name.

## Grammar

`EPOCH` is `YYYY-MM-DDTHH:MM:SS`, then an optional `.` with one to seven fraction
digits, then an optional `Z`. It accepts only ASCII digits and an uppercase `T`
and `Z`. Offsets, leap seconds and other separators are rejected. The value is
UTC and must fall within `[1957-10-04T00:00:00Z, RetrievedUtc + 30 days]`.

Tests generate the bounded cases in memory rather than storing them:

- oversize and length-mismatched bodies;
- gzip bombs and truncated or corrupt gzip;
- 100,001-record payloads;
- nonfinite numbers;
- trailing content, comments and BOMs.

Run the checks:

```bash
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj --configuration Release --filter 'FullyQualifiedName~CelesTrakGp'
```
