# OpenNGC v20260501 Attribution

This database derives from OpenNGC by Mattia Verga and upstream contributors:
https://github.com/mattiaverga/OpenNGC

- Upstream version: v20260501
- Upstream commit: `36cb178a0f69dba8bfc03a99c10512831edf1c6b`
- DOI: 10.21938/y.1ejWUD_MQ6b_eDFoVbbw
- License: Creative Commons Attribution-ShareAlike 4.0 International,
  https://creativecommons.org/licenses/by-sa/4.0/
- License notice: `LICENSE-OPENNGC.md`

HVO.SkyMonitor preprocessing 6 converts the pinned `NGC.csv`, `addendum.csv`
and object outlines into SQLite, retaining the deep-sky facts, aliases,
tombstones, nullable values and source semantics of preprocessing 5. Duplicate
identifications, M102's disputed identification, normalization, dropped Sh2
identifiers and outline transformations are recorded in the package manifest.
This standalone package omits HYG star tables and constellation topology. It
does not synthesize new measurements or source contours. The derived database
is distributed under CC BY-SA 4.0. HVO's changes are not endorsed by OpenNGC.

Consult the upstream project for its full source history, contributor credits
and original astronomical references. Source inputs and every retained artifact
are pinned by checksum in the approved catalog specification.
