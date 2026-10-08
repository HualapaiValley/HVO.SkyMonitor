# HYG 4.4 Attribution

This bundle contains a transformed subset of the HYG Star Database version 4.4,
compiled by David Nash (Astronexus).

- Upstream project: https://codeberg.org/astronexus/hyg
- Pinned upstream commit: `53e3df311869e813ace5f1ad2ec4ce909f13256c`
- Pinned source object: `00b349893b9a53106dd488d8371e8d2fa586043e500bb3cdb8bff3931682197d`
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

HVO.SkyMonitor preprocessing version 4 selects catalog ID, proper-name
fallback, J2000 right ascension and declination, apparent magnitude, color
index, Hipparcos ID, proper motion, proper name, Bayer and Flamsteed
designations, constellation, and the Henry Draper, Harvard Revised, and Gliese
identifiers; derives a designation alias table from those values; converts
numeric fields to SQLite values; records an unknown proper motion where the
upstream pair is exactly zero or reaches the upstream 9999.99 mas/yr clamp; and
excludes HYG row `0` (`Sol`). The transformed database is distributed under the
same CC BY-SA 4.0 license. HVO.SkyMonitor's changes are not endorsed by the
upstream project.
