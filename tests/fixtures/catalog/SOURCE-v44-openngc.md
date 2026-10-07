# HYG 4.4 + OpenNGC schema-4 subset fixture

`hyg-v44-openngc-subset.sqlite` is a deterministic test-only derivative of two
catalogs, both licensed under the Creative Commons Attribution-ShareAlike 4.0
International license: https://creativecommons.org/licenses/by-sa/4.0/.

- **Stars:** the HYG 4.4 catalog by David Nash (Astronexus),
  https://codeberg.org/astronexus/hyg at commit
  `53e3df311869e813ace5f1ad2ec4ce909f13256c`.
- **Deep sky:** OpenNGC v20260501 by Mattia Verga,
  https://github.com/mattiaverga/OpenNGC at commit
  `36cb178a0f69dba8bfc03a99c10512831edf1c6b` (DOI
  10.21938/y.1ejWUD_MQ6b_eDFoVbbw). OpenNGC merges data from the NASA/IPAC
  Extragalactic Database, HyperLEDA, SIMBAD, HEASARC and Harold Corwin's NGC/IC
  positions and notes. Its nebula outlines made use of the "Aladin sky atlas"
  developed at CDS, Strasbourg Observatory, France.

The complete upstream acknowledgements, verbatim, are in
`docs/catalog/hyg-v44-openngc-attribution.md`. Neither upstream project
endorses HVO.SkyMonitor's changes.

## Derivation

`extract-fixture-v44-openngc.sh` copied the six CSV files, without
transformation, from the composed production database
`hyg-v4.4-openngc-v20260501-p5-s4-r1`. That database was built from the exact
sources above by preprocessing version 5
(`scripts/catalog/hyg-v44-openngc-common.sh`), and its SHA-256 is
`eea1181ffae1dca2935aeed2c7790305b28045061f38ca6dda5eaf19694a8f1f`. The
script writes each real value with the fewest of 15, 16 or 17 significant
digits that read back as the same double. It then builds the fixture and proves
that every fixture row equals its production row and that each selection below
is complete.

- `hyg-v44-openngc-subset-star-objects.csv` holds the 16 stars of the schema-3
  fixture (`SOURCE-v44.md`) and Alnilam (HYG Hipparcos ID `26311`).
- `hyg-v44-openngc-subset-star-aliases.csv` holds every star alias naming one of
  those 17 stars.
- `hyg-v44-openngc-subset-deep-sky-objects.csv` holds the deep-sky objects
  `M040`, `NGC0224`, `NGC0253`, `NGC0650`, `NGC1990`, `NGC2023`, `NGC5457` and
  `NGC5866`.
- `hyg-v44-openngc-subset-deep-sky-aliases.csv` holds every deep-sky alias
  naming one of those objects.
- `hyg-v44-openngc-subset-deep-sky-tombstones.csv` holds the tombstones
  `IC0067` and `NGC0412`.
- `hyg-v44-openngc-subset-deep-sky-outline-points.csv` holds every outline point
  of the selected objects.

The objects were chosen to exercise these behaviours:

- **Messier identity:** M31 is NGC 224 and carries its common name.
- **The disputed Messier 102:** M101 is NGC 5457, and `M102` is its disputed
  alias. NGC 5866, the competing identification, carries no `M102` alias.
- **A double star with a Messier label:** M40 is a `**` row from the OpenNGC
  addendum.
- **A stellar row that duplicates a star:** NGC 1990 is the `*` row for Alnilam,
  so the star and the deep-sky row share Hipparcos ID 26311.
- **A Caldwell identifier and common names:** NGC 253 is C65.
- **Extents:** M31, NGC 253, M101 and NGC 5866 have both axes and a position
  angle. M76 (NGC 650) has only a major axis. NGC 2023 has unequal axes and no
  position angle.
- **A resolved duplicate:** NGC 651 is a duplicate alias of NGC 650.
- **Outlines:** NGC 650 has outlines at levels 1, 2 and 3, and its level-3
  outline has two rings. NGC 2023 has outlines only at levels 2 and 3.
- **Tombstones:** IC 67 and NGC 412 are nonexistent objects.

Metadata differs from production only in `name`
(`HYG 4.4 + OpenNGC subset test fixture`) and `catalog_version`
(`4.4+openngc-fixture.1`), so the fixture can never pass as the approved
production package.

`build-fixture-v44-openngc.sh` rebuilds the database from the CSV files with
`build-fixture-v44-openngc.sql` and SQLite 3.45.1. It then verifies
`SHA256SUMS-v44-openngc`.

The fixture and its CSV subsets are distributed under CC BY-SA 4.0. This
attribution and derivation record must accompany redistribution.
