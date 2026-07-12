# HYG 4.2 bright-star fixture

`hyg-v42-bright-stars.sqlite` is a deterministic test-only derivative of the
HYG 4.2 catalog by David Nash. HYG 4.2 is licensed under the Creative Commons
Attribution-ShareAlike 4.0 International license:
https://creativecommons.org/licenses/by-sa/4.0/.

Source catalog: https://codeberg.org/astronexus/hyg

Exact source object:
`data/hyg/OLDER/hyg_v42.csv.gz`, Git LFS SHA-256
`5ca9431ff364c8002a4a3efa91b2b9296746aea1543374db4cb6b4fab049d601`.
Its decompressed CSV SHA-256 is
`b2983a8d934e4f031cdb67bdd6c3437f8c5143cd6606a9573a9a9ac4b6375fd2`.

## Derivation

`hyg-v42-bright-stars.csv` contains the `id`, `proper`, `ra`, `dec`, `mag`, and
`ci`, and `hip` fields copied from nine selected bright-star rows in that exact source.
The selected source row IDs are 32263, 30365, 71456, 90979, 69451, 24549,
24378, 7574, and 27919. No coordinate or photometric transformations were
applied. The fixture names `hip` as `hipparcos_id` in adapter schema version 2.
`build-fixture.sh` imports those columns into the adapter schema and
verifies the checked-in SQLite checksum.

The fixture and its CSV subset are distributed under CC BY-SA 4.0. This
attribution and derivation record must accompany redistribution.

The historical `hyg_v42.sqlite` SHA-256 recorded elsewhere in this repository
is not used as provenance evidence. A checksum authenticates bytes only after
their origin and derivation are independently established.
