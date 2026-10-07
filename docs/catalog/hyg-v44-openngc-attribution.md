# HYG 4.4 + OpenNGC v20260501 Attribution

This bundle composes two upstream catalogs into one read-only SQLite database.
Both are licensed under the Creative Commons Attribution-ShareAlike 4.0
International License; the license text and link are in
`LICENSE-HYG-OPENNGC.md`. The composed database is distributed under the same
license. HVO.SkyMonitor's changes are not endorsed by either upstream project.

## Stars: HYG 4.4

The stars component is the unchanged database of the approved HYG 4.4 package
`hyg-v4.4-p4-s3-r1`. Its attribution follows verbatim. In this bundle the
license file it names as `LICENSE-HYG.md` is `LICENSE-HYG-OPENNGC.md`.

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

## Deep sky: OpenNGC v20260501

This bundle contains a transformed subset of OpenNGC version v20260501, a
license-friendly database of NGC and IC objects with an addendum of other
notable objects and outlines for prominent nebulae.

- Credits: Mattia Verga, https://github.com/mattiaverga/OpenNGC
- DOI: 10.21938/y.1ejWUD_MQ6b_eDFoVbbw
- Pinned upstream commit: `36cb178a0f69dba8bfc03a99c10512831edf1c6b`
- Copyright: 2023 Mattia Verga
- License: Creative Commons Attribution-ShareAlike 4.0 International
- Contributors named in the upstream AUTHORS file: Patrick Chevalley,
  Christian Dersch, Jean-Sebastien Dieu, Sebastian Godelet, and Mattia Verga

The data sources acknowledged by the upstream README, verbatim:

    OpenNGC has been built by merging data from:

     - NASA/IPAC Extragalactic Database
       http://ned.ipac.caltech.edu/
       This research has made use of the NASA/IPAC Extragalactic Database (NED)
       which is operated by the Jet Propulsion Laboratory,
       California Institute of Technology, under contract with the
       National Aeronautics and Space Administration.

     - HyperLEDA database
       http://leda.univ-lyon1.fr
       We acknowledge the usage of the HyperLeda database (http://leda.univ-lyon1.fr)

     - SIMBAD Astronomical Database
       http://simbad.u-strasbg.fr/simbad/
       This research has made use of the SIMBAD database, operated at CDS, Strasbourg, France

     - HEASARC High Energy Astrophysics Science Archive Research Center
       http://heasarc.gsfc.nasa.gov/
       We used several databases from HEASARC such as messier, mwsc, lbn, plnebulae, lmcextobj and smcclustrs.

     - Harold Corwin's NGC/IC Positions and Notes
       http://haroldcorwin.net/ngcic/index.html

    Some common names are taken from internet sources like Wikipedia.

The acknowledgement for the nebula outlines in the upstream
`outlines/metodology.txt`, verbatim:

    This research has made use of "Aladin sky atlas" developed at CDS, Strasbourg Observatory, France

HVO.SkyMonitor preprocessing version 5 leaves the HYG 4.4 stars component
unchanged. From the OpenNGC `NGC.csv` and `addendum.csv` files it selects the
name, type, J2000 right ascension and declination, constellation, major and
minor axes, position angle, B and V magnitudes, surface brightness, Hubble
type, Messier number, NGC and IC cross-references, identifiers, and common
names. It converts sexagesimal coordinates to decimal hours and degrees and
numeric fields to SQLite values, and writes a position angle of 180 degrees
as 0. It resolves each duplicate row to its master object as an alias, records
the Messier 102 duplicate as a disputed alias of NGC 5457, and records each
nonexistent row as a tombstone. It derives Caldwell and Hipparcos identifiers,
trims list entries, and drops empty, bare-prefix, Sharpless 2, and
duplicate-row identifiers. From the level 1 to 3 outline files it maps the
`C9` and `M045` file names to their objects, drops the regional outlines that
name no single object, and removes consecutive duplicate points. It does not
apply the upstream `outlines/shape.py` simplification, and it excludes every
other column and file. Each transformation and its count is recorded in the
bundle manifest.
