#!/usr/bin/env bash
set -euo pipefail

readonly SOURCE_OID="5ca9431ff364c8002a4a3efa91b2b9296746aea1543374db4cb6b4fab049d601"
readonly COMPRESSED_SHA256="$SOURCE_OID"
readonly DECOMPRESSED_SHA256="b2983a8d934e4f031cdb67bdd6c3437f8c5143cd6606a9573a9a9ac4b6375fd2"
readonly GENERATED_SHA256="b51d18b722199e89aa8fe4622ebe507346c75effb375e546881452a263f0b9e2"
readonly SOURCE_URL="https://codeberg.org/astronexus/hyg.git/info/lfs/objects/$SOURCE_OID"

if [[ $# -ne 1 ]]; then
  printf 'usage: %s OUTPUT_DIRECTORY\n' "$0" >&2
  exit 2
fi

for command in curl cut gzip sqlite3 sha256sum; do
  command -v "$command" >/dev/null || { printf 'missing required command: %s\n' "$command" >&2; exit 1; }
done

readonly REQUIRED_SQLITE_VERSION="3.45.1"
actual_sqlite_version="$(sqlite3 --version | cut -d' ' -f1)"
if [[ "$actual_sqlite_version" != "$REQUIRED_SQLITE_VERSION" ]]; then
  printf 'sqlite3 %s is required for reproducible bytes; found %s\n' "$REQUIRED_SQLITE_VERSION" "$actual_sqlite_version" >&2
  exit 1
fi

output_dir="$1"
mkdir -p "$output_dir"
compressed="$output_dir/hyg_v42.csv.gz"
csv="$output_dir/hyg_v42.csv"
database="$output_dir/hyg_v42.sqlite"

curl --fail --location --proto '=https' --tlsv1.2 "$SOURCE_URL" --output "$compressed"
printf '%s  %s\n' "$COMPRESSED_SHA256" "$compressed" | sha256sum --check --status
gzip --decompress --stdout "$compressed" > "$csv"
printf '%s  %s\n' "$DECOMPRESSED_SHA256" "$csv" | sha256sum --check --status
rm -f "$database"
sqlite3 "$database" <<SQL
.bail on
PRAGMA page_size = 4096;
PRAGMA journal_mode = DELETE;
PRAGMA synchronous = OFF;
PRAGMA user_version = 2;
CREATE TABLE source_hyg (
  id TEXT, hip TEXT, hd TEXT, hr TEXT, gl TEXT, bf TEXT, proper TEXT, ra TEXT,
  dec TEXT, dist TEXT, pmra TEXT, pmdec TEXT, rv TEXT, mag TEXT, absmag TEXT,
  spect TEXT, ci TEXT, x TEXT, y TEXT, z TEXT, vx TEXT, vy TEXT, vz TEXT,
  rarad TEXT, decrad TEXT, pmrarad TEXT, pmdecrad TEXT, bayer TEXT, flam TEXT,
  con TEXT, comp TEXT, comp_primary TEXT, base TEXT, lum TEXT, var TEXT,
  var_min TEXT, var_max TEXT
);
.mode csv
.import --skip 1 "$csv" source_hyg
CREATE TABLE catalog_metadata (key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL) WITHOUT ROWID;
CREATE TABLE celestial_objects (
  id TEXT PRIMARY KEY NOT NULL,
  display_name TEXT NOT NULL,
  right_ascension_hours REAL NOT NULL,
  declination_degrees REAL NOT NULL,
  magnitude REAL NOT NULL,
  color_index REAL,
  hipparcos_id TEXT
) WITHOUT ROWID;
INSERT INTO catalog_metadata VALUES
  ('catalog_version', '4.2'),
  ('license', 'CC BY-SA 4.0'),
  ('name', 'HYG 4.2'),
  ('preprocessing_version', '3'),
  ('schema_version', '2'),
  ('source_url', 'https://codeberg.org/astronexus/hyg');
INSERT INTO celestial_objects
SELECT id, CASE WHEN proper = '' THEN id ELSE proper END,
       CAST(ra AS REAL), CAST(dec AS REAL), CAST(mag AS REAL),
       CASE WHEN ci = '' THEN NULL ELSE CAST(ci AS REAL) END,
       NULLIF(hip, '')
FROM source_hyg
WHERE id != '' AND id != '0' AND ra != '' AND dec != '' AND mag != ''
ORDER BY id;
DROP TABLE source_hyg;
CREATE INDEX celestial_objects_magnitude_id ON celestial_objects (magnitude, id COLLATE BINARY);
VACUUM;
SQL
printf '%s  %s\n' "$GENERATED_SHA256" "$database" | sha256sum --check --status

printf 'Validated HYG 4.2 snapshot: %s\n' "$database"
