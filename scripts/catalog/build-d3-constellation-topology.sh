#!/usr/bin/env bash
set -euo pipefail

readonly VERSION="v0.7.32"
readonly LINES_SHA256="294f66bef5d5cf50b1e17f16d2efa1d97a15131612c68dd935adef6e7373e13c"
readonly STARS_SHA256="8e76cd774d38f8d232cfccfb0b72d6fba5832d36eeff1f514e46c25423e2ecb8"
readonly GENERATED_SHA256="70c253a00e0909ae0236dec0411afe837ebf8e493b2be7f84373b63c95c91621"
readonly BASE_URL="https://raw.githubusercontent.com/ofrohn/d3-celestial/$VERSION/data"

if [[ $# -ne 1 ]]; then
  printf 'usage: %s OUTPUT_FILE\n' "$0" >&2
  exit 2
fi

for command in curl dirname jq mv realpath sha256sum sqlite3 sync; do
  command -v "$command" >/dev/null || { printf 'missing required command: %s\n' "$command" >&2; exit 1; }
done

temporary_directory="$(mktemp -d)"
trap 'rm -rf "$temporary_directory"' EXIT
lines="$temporary_directory/constellations.lines.json"
stars="$temporary_directory/stars.14.json"

curl --fail --location --proto '=https' --tlsv1.2 "$BASE_URL/constellations.lines.json" --output "$lines"
curl --fail --location --proto '=https' --tlsv1.2 "$BASE_URL/stars.14.json" --output "$stars"
printf '%s  %s\n' "$LINES_SHA256" "$lines" | sha256sum --check --status
printf '%s  %s\n' "$STARS_SHA256" "$stars" | sha256sum --check --status

star_rows="$temporary_directory/stars.tsv"
segment_rows="$temporary_directory/segments.tsv"
database="$temporary_directory/topology.sqlite"
jq -r '.features[] | [.geometry.coordinates[0], .geometry.coordinates[1], .id] | @tsv' "$stars" > "$star_rows"
jq -r '
  .features[] as $feature |
  $feature.geometry.coordinates[] |
  . as $line |
  range(0; length - 1) as $index |
  [($feature.id | ascii_upcase), $line[$index][0], $line[$index][1],
   $line[$index + 1][0], $line[$index + 1][1]] |
  @tsv
' "$lines" > "$segment_rows"

output="$(realpath -m "$1")"
output_parent="$(dirname "$output")"
[[ -d "$output_parent" && ! -L "$output_parent" ]] || { printf 'output parent is missing or unsafe: %s\n' "$output_parent" >&2; exit 1; }
generated="$temporary_directory/generated-topology.tsv"
sqlite3 "$database" <<SQL
.bail on
CREATE TABLE stars (longitude TEXT NOT NULL, latitude TEXT NOT NULL, hip TEXT NOT NULL);
CREATE UNIQUE INDEX stars_coordinates ON stars (longitude, latitude);
CREATE TABLE segments (
  constellation_id TEXT NOT NULL,
  from_longitude TEXT NOT NULL,
  from_latitude TEXT NOT NULL,
  to_longitude TEXT NOT NULL,
  to_latitude TEXT NOT NULL
);
.mode tabs
.import '$star_rows' stars
.import '$segment_rows' segments
.output '$generated'
SELECT '# D3-Celestial v0.7.32; BSD-3-Clause; constellation,from-HIP,to-HIP';
SELECT s.constellation_id, f.hip, t.hip
FROM segments s
JOIN stars f ON f.longitude = s.from_longitude AND f.latitude = s.from_latitude
JOIN stars t ON t.longitude = s.to_longitude AND t.latitude = s.to_latitude
ORDER BY s.rowid;
.output stdout
SQL

segment_count="$(sqlite3 "$database" 'SELECT count(*) FROM segments;')"
resolved_count="$(sqlite3 "$database" "SELECT count(*) FROM segments s JOIN stars f ON f.longitude = s.from_longitude AND f.latitude = s.from_latitude JOIN stars t ON t.longitude = s.to_longitude AND t.latitude = s.to_latitude;")"
constellation_count="$(sqlite3 "$database" 'SELECT count(DISTINCT constellation_id) FROM segments;')"
if [[ "$segment_count" -ne 743 || "$resolved_count" -ne 743 || "$constellation_count" -ne 88 ]]; then
  printf 'unexpected topology counts: %s constellations, %s segments, %s resolved\n' "$constellation_count" "$segment_count" "$resolved_count" >&2
  exit 1
fi
printf '%s  %s\n' "$GENERATED_SHA256" "$generated" | sha256sum --check --status
sync -f "$generated"
mv -Tf -- "$generated" "$output"
sync -f "$output_parent"

printf 'Generated %s constellations and %s segments: %s\n' "$constellation_count" "$segment_count" "$output"
