#!/usr/bin/env bash
# Copies the schema-4 fixture rows out of a composed production database, builds the fixture from the copies, proves
# that every fixture row equals its production row and that each selection is complete, and records the checksums.
# It runs once per derivation, against the composed package named in SOURCE-v44-openngc.md; an ordinary rebuild uses
# build-fixture-v44-openngc.sh and the committed CSV files.
set -euo pipefail

if [[ $# -ne 1 || ! -f "$1" ]]; then
    echo "usage: $0 <composed-production-database>" >&2
    exit 2
fi
production="$(realpath "$1")"
cd "$(dirname "$0")"

readonly star_ids="'103879','103883','11734','24129','26155','26156','26159','27919','30365','32263','57767','69451','70666','71456','87665','90979'"
readonly star_hipparcos="'26311'"
readonly deep_sky_ids="'M040','NGC0224','NGC0253','NGC0650','NGC1990','NGC2023','NGC5457','NGC5866'"
readonly tombstone_ids="'IC0067','NGC0412'"
readonly output="hyg-v44-openngc-subset.sqlite"

# Renders a REAL column with the fewest of 15, 16 or 17 significant digits that read back as the same double, so a
# CSV copy is exact; a NULL stays an empty field.
exact() {
    local c="$1"
    printf "CASE WHEN %s IS NULL THEN NULL WHEN CAST(printf('%%.15g', %s) AS REAL) = %s THEN printf('%%.15g', %s) " \
        "$c" "$c" "$c" "$c"
    printf "WHEN CAST(printf('%%.16g', %s) AS REAL) = %s THEN printf('%%.16g', %s) ELSE printf('%%!.17g', %s) END AS %s" \
        "$c" "$c" "$c" "$c" "$c"
}

star_selection="(id IN ($star_ids) OR hipparcos_id IN ($star_hipparcos))"

sqlite3 -readonly "$production" <<SQL
.bail on
.headers on
.mode csv
.once hyg-v44-openngc-subset-star-objects.csv
SELECT id, display_name, $(exact right_ascension_hours), $(exact declination_degrees), $(exact magnitude),
       $(exact color_index), hipparcos_id, $(exact proper_motion_ra_cos_dec_mas_per_year),
       $(exact proper_motion_dec_mas_per_year), proper_name, bayer, flamsteed, constellation, bayer_flamsteed,
       henry_draper_id, harvard_revised_id, gliese_id
FROM celestial_objects WHERE $star_selection ORDER BY id;
.once hyg-v44-openngc-subset-star-aliases.csv
SELECT alias, object_id, kind FROM celestial_object_aliases
WHERE object_id IN (SELECT id FROM celestial_objects WHERE $star_selection) ORDER BY alias, object_id;
.once hyg-v44-openngc-subset-deep-sky-objects.csv
SELECT id, designation, display_name, object_type, $(exact right_ascension_hours), $(exact declination_degrees),
       constellation, $(exact major_axis_arcminutes), $(exact minor_axis_arcminutes),
       $(exact position_angle_degrees), $(exact b_magnitude), $(exact v_magnitude), $(exact surface_brightness),
       hubble_type, messier_number, caldwell_number, hipparcos_id, common_name
FROM deep_sky_objects WHERE id IN ($deep_sky_ids) ORDER BY id;
.once hyg-v44-openngc-subset-deep-sky-aliases.csv
SELECT alias, object_id, kind FROM deep_sky_aliases WHERE object_id IN ($deep_sky_ids) ORDER BY alias, object_id;
.once hyg-v44-openngc-subset-deep-sky-tombstones.csv
SELECT id, designation, reason, candidates FROM deep_sky_tombstones WHERE id IN ($tombstone_ids) ORDER BY id;
.once hyg-v44-openngc-subset-deep-sky-outline-points.csv
SELECT object_id, level, ring, sequence, $(exact right_ascension_degrees), $(exact declination_degrees)
FROM deep_sky_outline_points WHERE object_id IN ($deep_sky_ids) ORDER BY object_id, level, ring, sequence;
SQL

rm -f "$output"
sqlite3 "$output" < build-fixture-v44-openngc.sql

# Each line is table|fixture rows|fixture rows absent from production|selected production rows absent from the fixture.
comparison="$(sqlite3 -readonly "$output" <<SQL
.bail on
ATTACH DATABASE '$production' AS production;
SELECT 'celestial_objects', (SELECT count(*) FROM main.celestial_objects),
  (SELECT count(*) FROM (SELECT * FROM main.celestial_objects EXCEPT SELECT * FROM production.celestial_objects)),
  (SELECT count(*) FROM (SELECT * FROM production.celestial_objects WHERE $star_selection
                         EXCEPT SELECT * FROM main.celestial_objects));
SELECT 'celestial_object_aliases', (SELECT count(*) FROM main.celestial_object_aliases),
  (SELECT count(*) FROM (SELECT * FROM main.celestial_object_aliases
                         EXCEPT SELECT * FROM production.celestial_object_aliases)),
  (SELECT count(*) FROM (SELECT * FROM production.celestial_object_aliases
                         WHERE object_id IN (SELECT id FROM production.celestial_objects WHERE $star_selection)
                         EXCEPT SELECT * FROM main.celestial_object_aliases));
SELECT 'deep_sky_objects', (SELECT count(*) FROM main.deep_sky_objects),
  (SELECT count(*) FROM (SELECT * FROM main.deep_sky_objects EXCEPT SELECT * FROM production.deep_sky_objects)),
  (SELECT count(*) FROM (SELECT * FROM production.deep_sky_objects WHERE id IN ($deep_sky_ids)
                         EXCEPT SELECT * FROM main.deep_sky_objects));
SELECT 'deep_sky_aliases', (SELECT count(*) FROM main.deep_sky_aliases),
  (SELECT count(*) FROM (SELECT * FROM main.deep_sky_aliases EXCEPT SELECT * FROM production.deep_sky_aliases)),
  (SELECT count(*) FROM (SELECT * FROM production.deep_sky_aliases WHERE object_id IN ($deep_sky_ids)
                         EXCEPT SELECT * FROM main.deep_sky_aliases));
SELECT 'deep_sky_tombstones', (SELECT count(*) FROM main.deep_sky_tombstones),
  (SELECT count(*) FROM (SELECT * FROM main.deep_sky_tombstones EXCEPT SELECT * FROM production.deep_sky_tombstones)),
  (SELECT count(*) FROM (SELECT * FROM production.deep_sky_tombstones WHERE id IN ($tombstone_ids)
                         EXCEPT SELECT * FROM main.deep_sky_tombstones));
SELECT 'deep_sky_outline_points', (SELECT count(*) FROM main.deep_sky_outline_points),
  (SELECT count(*) FROM (SELECT * FROM main.deep_sky_outline_points
                         EXCEPT SELECT * FROM production.deep_sky_outline_points)),
  (SELECT count(*) FROM (SELECT * FROM production.deep_sky_outline_points WHERE object_id IN ($deep_sky_ids)
                         EXCEPT SELECT * FROM main.deep_sky_outline_points));
SELECT 'catalog_metadata', (SELECT count(*) FROM main.catalog_metadata),
  (SELECT count(*) FROM (SELECT * FROM main.catalog_metadata WHERE key NOT IN ('catalog_version', 'name')
                         EXCEPT SELECT * FROM production.catalog_metadata)),
  (SELECT count(*) FROM (SELECT * FROM production.catalog_metadata WHERE key NOT IN ('catalog_version', 'name')
                         EXCEPT SELECT * FROM main.catalog_metadata));
SQL
)"
printf '%s\n' "$comparison"

expected_rows="celestial_objects|17 deep_sky_objects|8 deep_sky_tombstones|2 catalog_metadata|25"
for expected in $expected_rows; do
    grep -qx "${expected%%|*}|${expected#*|}|0|0" <<<"$comparison" || {
        echo "fixture table ${expected%%|*} does not hold exactly ${expected#*|} production rows" >&2
        exit 1
    }
done
if awk -F'|' '$3 != 0 || $4 != 0 { bad = 1 } END { exit !bad }' <<<"$comparison"; then
    echo "a fixture table differs from its production selection" >&2
    exit 1
fi

# Aliases that also name an object outside the subset, compared case-insensitively as searches are; SOURCE records them.
sqlite3 -readonly "$output" <<SQL
.bail on
ATTACH DATABASE '$production' AS production;
SELECT 'shared-alias', f.alias, p.object_id FROM main.deep_sky_aliases AS f
JOIN production.deep_sky_aliases AS p ON lower(p.alias) = lower(f.alias) AND p.object_id NOT IN ($deep_sky_ids)
ORDER BY f.alias, p.object_id;
SQL

sha256sum "$output" hyg-v44-openngc-subset-*.csv > SHA256SUMS-v44-openngc
cat SHA256SUMS-v44-openngc
