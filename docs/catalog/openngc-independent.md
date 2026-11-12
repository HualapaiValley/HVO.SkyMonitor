# Independent OpenNGC catalog

Refs #1388

The OpenNGC catalog is a separate read-only SQLite snapshot. HYG 4.2 and HYG
4.4 remain star catalogs. Selecting either family must preserve the other
family's selection and immutable evidence. The composed HYG/OpenNGC lineage
remains readable for retained captures; it is not the new deep-sky package.

## Package and runtime contracts

The standalone package uses catalog ID `openngc-production`, schema 5,
preprocessing 6 and manifest 4. It contains catalog metadata and the four
existing deep-sky tables, with no star tables or constellation topology. Its
row count means deep-sky objects. Registry version 3 pins the serializer,
database, source inputs, transformations, counts and retained notices. Earlier
approved packages keep their identities. Unknown or altered packages fail
closed. Production checksums come from two offline builds of the pinned public
OpenNGC v20260501 inputs, never from installed-instance data or fixtures.

Star and deep-sky snapshots resolve independently. A missing explicitly selected
snapshot is an error. Scene/frame evidence binds the exact identity of each
input; changing either changes new output identity. Retained captures retain
their original inputs. With an independent catalog installed, VirtualSky freezes
the validated active graph before rendering and records that revision on the
frame; raw ingress binds the same revision, even if activation changes during
the exposure. Legacy captures do not gain this optional field. Existing storage-neutral Astronomy contracts own query
and projection behavior; SQLite remains in the optional infrastructure adapter.

## Selection and presentation

NGC, Messier and IC query options form a union, deduplicated by canonical object
identity. Messier membership includes objects whose canonical IDs are neither
NGC nor IC. Disputed aliases do not create display identities. Magnitude and
angular-size limits preserve unknown source values explicitly. Selection is
bounded and deterministic; supported approximate ellipses and symbols reuse
the existing geometry envelope. Sourced irregular contours and new object
classes remain with their existing work.

Expected deep-sky annotations have their own Current Sky layer and settings
through the existing typed graph editor. Settings use the existing immutable
draft, validation, activation and rollback path. They cover designations,
magnitude, size, labels, approximate outlines and style. The visible
expected-position explanation remains. Measured associations retain their
separate meaning. This adds only the deep-sky configuration subset of the open
configuration parent.

## Lifecycle and release evidence

Catalog install, selection, rollback and collection preserve independent
family identities and use the existing product lock, drain barrier, journal,
recovery and historical references. Collection must protect selected, previous,
backup and interrupted-operation references from both families. A failed or
cancelled transition restores the previous selection without touching the other
family. Image compatibility must cover both selected packages.

Release assembly carries approved star and deep-sky bundles as individually
versioned, checksummed assets with their exact license and attribution files.
No capture path downloads or preprocesses a catalog. OpenNGC-derived data stays
CC BY-SA 4.0, credits Mattia Verga and upstream contributors, identifies the
upstream version/commit and describes HVO's transformations.

## Validation boundary

Use disposable fixtures for behavioral tests and temporary public inputs for
reproducible artifact identities. Cover independent transitions, malformed
snapshots, query boundaries, cancellation/recovery/reference protection, frame
lineage, graph revision behavior and layer toggles in affected suites only.
Every heavy build/test uses the host's shared heavy-build lock and short private
temporary directory. Local evidence does not assert live adoption, installed
qualification or release burn-in. The requirement/evidence handoff records
executed results and remaining limitations explicitly. Independent review is
left to Kepler under the explicit implementation-owner instructions.

## Operator workflow

Build public-source artifacts explicitly, outside the checkout:

```bash
scripts/catalog/fetch-openngc-inputs.sh "$scratch/inputs"
scripts/catalog/build-openngc.sh --openngc-source "$scratch/inputs" "$scratch/build"
```

Install the bundle with `hvo-sky-install catalog install --catalog-bundle BUNDLE`.
Select it for an instance with `catalog select --instance-id UUID --catalog-version
openngc-v20260501-p6-s5-r1`; the package identity selects the deep-sky family.
Use `catalog rollback --instance-id UUID --catalog-family deep-sky` to restore
that family's previous selection, including the initially disabled selection.
The unqualified rollback command continues to select the previous star catalog.
Catalog GC retains both selected/previous families and historical, backup and
incomplete-operation references. A deep-sky transition retires the previous
image rollback because its Compose environment predates the new catalog mount.

On the graph's Projected scene query panel enable deep-sky annotations, choose
NGC/Messier/IC and the V-magnitude and angular-size limits. On its Scene overlay
panel enable the separate deep-sky layer and choose labels, approximate outlines,
colour, thickness, scale and packaged font. Set `sceneDeepSkyVariant` to `scene-deep-sky-v1` in the overlay manifest to retain its optional product. An absent variant keeps the legacy dependency contract unchanged. Save, validate and explicitly activate the revision. Current
Sky then shows **Deep-sky catalog** independently of **Sky annotations**; toggles
are remembered separately. Software & catalog reports both identities and the
OpenNGC attribution. Existing archived scenes keep their own geometry and input
identity. The selection cache holds at most 32 plans per immutable catalog, each
with at most 128 candidates; caps are applied before view-dependent geometry. Plans prioritize Messier membership, then featured objects, angular size, known V magnitude and canonical ID so alphabetical IC rows cannot exhaust the default budget.

Releases build approved HYG 4.2 and standalone OpenNGC by default. HYG 4.4 remains held for separate release approval; registry membership alone does not approve that adoption. The release
tool accepts `--catalog-bundle` for stars and `--deep-sky-catalog-bundle` for
OpenNGC. The shell bootstrap selects the star bundle and leaves deep-sky
installation/selection explicit; release assembly itself installs nothing.
