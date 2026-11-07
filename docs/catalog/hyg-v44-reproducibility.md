# Reproducing the approved HYG 4.4 and OpenNGC bytes

Investigation: [#1701](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1701),
2026-11-07. Starting source: `development/v1` at
`91f56c727d27acc0805a1fd1258778ba386b1af3` in the canonical
`HualapaiValley/HVO.SkyMonitor` repository.

## Cause and byte evidence

SQLite's version number alone did not specify the serializer. Ubuntu compiles
SQLite with `SQLITE_SECURE_DELETE`, so each connection defaults
`PRAGMA secure_delete` to `1`. The upstream 3.45.1 archive, built using the
repository installer's configure options, defaults it to `0`. The original
HYG recipe and both OpenNGC write connections inherited that difference.
`secure_delete` controls clearing unused B-tree storage and is connection-local;
it must also be set when the OpenNGC cleanup connection drops its work tables
and runs `VACUUM`.

On the same host, with the same verified CSV and unmodified recipe:

| SQLite default | HYG SHA-256 (16,166,912 bytes in both cases) |
| --- | --- |
| Ubuntu, ON | `2cc06890de1d4168f1eb91ca4ef5cde1c4923f275a52a1aedfec002df42a3321` |
| Upstream archive, OFF | `fbcfa886aea87d30584c038121310205b66840f1ccd470eb2f51e86ab9c024ea` |

The files differ in 18,097 byte positions across 861 pages (4,096-byte pages).
Every differing position contains zero in the approved file. The logical star
and alias tables are identical: `.sha3sum --sha3-256 TABLE` returns these values
for both files:

| Table | SHA3-256 |
| --- | --- |
| `celestial_objects` | `79f071b94db9d77fddd1db1949af6162b2e6b2a713bf8ac834d8555fc3591a15` |
| `celestial_object_aliases` | `9fef7733add7c7d11b6938e270d7a5d348aa80f1d5086d371a07fdaae4f965fc` |

Setting `secure_delete=ON` before the unchanged HYG SQL on the upstream CLI
produces a file byte-identical to the approved Ubuntu output. This controlled
change reproduces the original pin; changing the pin is unnecessary.
The pin and recipe originated in commit
`e44804d54108e6c0650336cc069d749f8d0feed2`,
[PR #1173](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1173), whose
package evidence records two byte-identical builds with that hash.

OpenNGC also reproduces the default difference independently, using the approved
HYG bundle as input. Before the fix, upstream SQLite produces
`e5655c18a61fd5794f6dfd656d5854b356662aa9d3c98673cc697c6d8c3d8aa8`
(23,244,800 bytes), which the unchanged checksum validator rejects. Ubuntu
produces the approved
`eea1181ffae1dca2935aeed2c7790305b28045061f38ca6dda5eaf19694a8f1f`.

The fix explicitly sets `PRAGMA secure_delete = ON` before writes in
`hyg44_build_database`, `hygngc_composition_sql`, and `hygngc_finish_database`.
It restores the serializer behavior used for the already-approved artifacts.
Transformation SQL, package versions, source pins, output pins, and fail-closed
validators are unchanged.

## Exact toolchains

The investigation host is Ubuntu 24.04.5 LTS, Linux x86_64, Bash
`5.2.21(1)-release`. The installed `sqlite3` and `libsqlite3-0:amd64` packages
are `3.45.1-1ubuntu2.8`. Its SQLite source ID is
`e876e51a0ed5c5b3126f52e532044363a014bc594cfefa87ffb5b82257ccalt1`.
`PRAGMA compile_options` includes `SECURE_DELETE`; `PRAGMA secure_delete` is `1`.

The second CLI is built in a private scratch directory, without installing or
replacing system tools, from the same archive pinned by
[`install-sqlite-3.45.1.sh`](../../scripts/catalog/install-sqlite-3.45.1.sh):

- URL: `https://www.sqlite.org/2024/sqlite-autoconf-3450100.tar.gz`
- Length: 3,232,682 bytes
- SHA-256: `cd9c27841b7a5932c9897651e20b86c701dd740556989b01ca596fcfa3d49a0a`
- Compiler: `cc (Ubuntu 13.3.0-6ubuntu2~24.04.1) 13.3.0`
- Source ID: `e876e51a0ed5c5b3126f52e532044363a014bc594cfefa87ffb5b82257cc467a`
- `PRAGMA compile_options` omits `SECURE_DELETE`; `PRAGMA secure_delete` is `0`.

After verifying the archive's length and checksum, extract it into an empty
scratch directory and build there with:

```bash
./configure --disable-shared --enable-static --enable-static-shell \
  --disable-readline --disable-editline --disable-dynamic-extensions CFLAGS='-O2'
make -j2 sqlite3
```

These are the installer's compiler/feature options; the investigation omits only
its private install prefix and never runs `make install`. The resulting CLI
SHA-256 was `5c09ccc5b2b3f0f7e17acab3f317817c3b9f04ab8815131e17d77a10e29b6d26`;
the Ubuntu `/usr/bin/sqlite3` executable was
`04d261a472c6838700310da6c8abaec05534945c6fe1a2b3353e945313fc20b4`.
Binary checksums describe these particular tool builds, not a new catalog pin.

## Inputs and offline regression

HYG source provenance remains upstream commit
`53e3df311869e813ace5f1ad2ec4ce909f13256c`. The explicitly fetched LFS object is
13,636,362 bytes with SHA-256
`00b349893b9a53106dd488d8371e8d2fa586043e500bb3cdb8bff3931682197d`.
Its decompressed CSV is 33,929,696 bytes with SHA-256
`ea0e1699a1e7d48daa197b5fe567fcf447420e367f37619862ee1d6f077599cd`.
OpenNGC is checked out at
`36cb178a0f69dba8bfc03a99c10512831edf1c6b` (`v20260501`). The composer verifies
both CSV hashes and lengths and all 379 outlines against the committed listing.

Run the opt-in regression with previously acquired inputs; it performs no network
I/O, installation or deployment. Select the desired 3.45.1 CLI through `PATH`:

```bash
scripts/catalog/test-hyg-v44-reproducibility.sh \
  /absolute/path/hyg_v44.csv.gz /absolute/path/OpenNGC
```

The test wraps every SQLite connection to force the incoming `secure_delete`
default OFF or ON and verifies that setup actually took effect. For each default
it invokes the real HYG and OpenNGC builders twice in fresh output directories.
Every build must pass the existing pinned validators, including integrity,
schema, metadata, counts, transformations and unchanged-star checks. Every
bundle file, including manifests and notices, must compare byte-for-byte with
the first build. Temporary outputs are removed on exit. This uses production
inputs explicitly; normal fixture/Unit checks do not fetch them.

Before the fix this regression exits 1 at the HYG database checksum gate with
the upstream CLI and forced OFF default, reproducing the reported failure.

## Repeated clean-build evidence

On 2026-11-07, the fixed builders passed all four production-input build pairs
under the upstream source-built CLI above. Each row starts both builders in
fresh staging/output directories; the second repeat never reuses a database.

| Incoming connection default | Clean repeat | HYG result | OpenNGC result | All bundle files |
| --- | --- | --- | --- | --- |
| OFF | 1 | approved hash | approved hash | reference |
| OFF | 2 | approved hash | approved hash | byte-identical |
| ON | 1 | approved hash | approved hash | byte-identical |
| ON | 2 | approved hash | approved hash | byte-identical |

Every HYG result is 16,166,912 bytes, SHA-256
`2cc06890de1d4168f1eb91ca4ef5cde1c4923f275a52a1aedfec002df42a3321`,
119,613 stars. Every OpenNGC result is 23,244,800 bytes, SHA-256
`eea1181ffae1dca2935aeed2c7790305b28045061f38ca6dda5eaf19694a8f1f`,
with 13,371 deep-sky objects, 65,822 deep-sky aliases, 10 tombstones and 90,861
outline points. All existing structural and provenance validators passed.

The HYG manifest SHA-256 is
`a2771706b07e515b70c949db037255298f3e3cbde20d0a98ca89c5ba264d55f7`;
the composed manifest SHA-256 is
`fddd22a391095a4e235f090e569d3d609fbcae687073f7bca04719f2478014b4`.
Both equal the corresponding pre-fix Ubuntu build manifests. An additional
`build-hyg-v44.sh --fetch` run under Ubuntu's CLI passed the unchanged source and
output validators and produced the same HYG database and manifest hashes.

This is catalog build evidence, not deployment or production qualification.
The PR target profile is Development v1; full `ci.yml` qualification is not
dispatched for this isolated Tier B build-tool fix.
