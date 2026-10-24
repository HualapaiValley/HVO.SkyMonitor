# HYG 4.2 Attribution

This bundle contains a transformed subset of the HYG Star Database version 4.2,
compiled by David Nash (Astronexus).

- Upstream project: https://codeberg.org/astronexus/hyg
- Pinned source object: `5ca9431ff364c8002a4a3efa91b2b9296746aea1543374db4cb6b4fab049d601`
- License: Creative Commons Attribution-ShareAlike 4.0 International
- License text and link: `LICENSE-HYG.md`

HYG combines identifiable objects from the Hipparcos, Yale Bright Star, and
Gliese nearby-star catalogs. It also includes identifiers, proper names, and
other data from the sources documented by the upstream HYG project. Consult the
upstream project for its complete source history and contributor
acknowledgments.

The upstream HYG acknowledgment file credits Astronexus as primary developer,
`ashnur` for Markdown edits, `jasondavies` for whitespace cleanup, `bryant1410`
for Markdown syntax corrections, and `pdyxs` for contributing IAU-approved
proper names of bright stars.

HVO.SkyMonitor preprocessing selects catalog ID, proper-name fallback, J2000
right ascension and declination, apparent magnitude, color index, and Hipparcos
ID; converts numeric fields to SQLite values; and excludes HYG row `0` (`Sol`).
The transformed database is distributed under the same CC BY-SA 4.0 license.
HVO.SkyMonitor's changes are not endorsed by the upstream project.
