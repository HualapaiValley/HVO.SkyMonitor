# HYG 4.4 schema-3 subset fixture

`hyg-v44-subset.sqlite` is a deterministic test-only derivative of the HYG 4.4
catalog by David Nash. HYG 4.4 is licensed under the Creative Commons
Attribution-ShareAlike 4.0 International license:
https://creativecommons.org/licenses/by-sa/4.0/.

Source catalog: https://codeberg.org/astronexus/hyg at commit
`53e3df311869e813ace5f1ad2ec4ce909f13256c`. The source is the HYG 4.4 compressed CSV
with Git LFS SHA-256
`00b349893b9a53106dd488d8371e8d2fa586043e500bb3cdb8bff3931682197d`.
Its decompressed CSV SHA-256 is
`ea0e1699a1e7d48daa197b5fe567fcf447420e367f37619862ee1d6f077599cd`.

## Derivation

The two CSV files were copied without transformation from the schema-3 production
database `hyg-v4.4-p4-s3-r1`. That database was built from the exact source above by
preprocessing version 4 (`scripts/catalog/hyg-v44-common.sh`). Its SHA-256 is
`2cc06890de1d4168f1eb91ca4ef5cde1c4923f275a52a1aedfec002df42a3321`.

- `hyg-v44-subset-objects.csv` holds every column of 16 selected
  `celestial_objects` rows. Their source row IDs are 11734, 24129, 26155, 26156,
  26159, 27919, 30365, 32263, 57767, 69451, 70666, 71456, 87665, 90979, 103879 and
  103883.
- `hyg-v44-subset-aliases.csv` holds every `celestial_object_aliases` row naming
  one of those objects. No selected alias also names an object outside the subset.

The rows were chosen to exercise these behaviours:

- **Bright-star parity with the HYG 4.2 fixture:** Sirius, Canopus, Rigil
  Kentaurus, Vega, Arcturus and Betelgeuse.
- **High proper motion:** 61 Cygni A and B, Kapteyn's Star, Groombridge 1830,
  Proxima Centauri, Sirius and Arcturus.
- **The unknown-motion rule:** Barnard's Star. Its upstream declination motion
  reaches 9999.99 mas/yr, so both components are `NULL`.
- **A designation that names several objects:** θ¹ Orionis (`The-1 Ori`, `41 Ori`)
  names rows 26155, 26156 and 26159.
- **Constellation-topology endpoints:** Polaris, Vega and Betelgeuse.

Metadata differs from production only in `name` (`HYG 4.4 subset test fixture`)
and `catalog_version` (`4.4-fixture.1`), so the fixture can never pass as the
approved production package.

`build-fixture-v44.sh` rebuilds the database from the CSV files with
`build-fixture-v44.sql` and SQLite 3.45.1. It then verifies `SHA256SUMS-v44`.

The fixture and its CSV subsets are distributed under CC BY-SA 4.0. This
attribution and derivation record must accompany redistribution.
