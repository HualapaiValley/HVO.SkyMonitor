#!/usr/bin/env bash
# HYG 4.4 + OpenNGC v20260501 schema-4 constants, composer, and validators, sourced by build-hyg-v44-openngc.sh and
# bundle-hyg-v44-openngc.sh. The stars component is the unchanged database of the approved HYG 4.4 package, so this
# file sources hyg-v44-common.sh first and never redefines or weakens a HYG value. The values below are the shell copy
# of the hyg-v44-openngc-production entry in src/HVO.SkyMonitor.Catalog.Sqlite/ApprovedCatalogSpecifications.json,
# and a unit test proves they agree.
#
# Every function here relies on the caller's `set -e`: call them as plain statements, never inside `if` or after
# `||`, so that the first hyg_fail stops the build (see the note on hyg_fail in catalog-common.sh).
# shellcheck disable=SC2034

HYGNGC_LIBRARY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly HYGNGC_LIBRARY_DIR
# shellcheck source=scripts/catalog/hyg-v44-common.sh
source "$HYGNGC_LIBRARY_DIR/hyg-v44-common.sh"

readonly HYGNGC_CATALOG_NAME="HYG 4.4 + OpenNGC v20260501"
readonly HYGNGC_CATALOG_ID="hyg-v44-openngc-production"
readonly HYGNGC_CATALOG_VERSION="4.4+openngc-v20260501"
readonly HYGNGC_PACKAGE_VERSION="hyg-v4.4-openngc-v20260501-p5-s4-r1"
readonly HYGNGC_PACKAGE_LINEAGE="hyg-v44-openngc-production-p5-s4"
readonly HYGNGC_MANIFEST_VERSION="3"
readonly HYGNGC_SCHEMA_VERSION="4"
readonly HYGNGC_PREPROCESSING_VERSION="5"
# The source block names the stars component's upstream input, which this package carries unchanged.
readonly HYGNGC_SOURCE_PROJECT_URL="$HYG44_SOURCE_PROJECT_URL"
readonly HYGNGC_SOURCE_URL="$HYG44_SOURCE_URL"
readonly HYGNGC_SOURCE_OID="$HYG44_SOURCE_OID"
readonly HYGNGC_COMPRESSED_SHA256="$HYG44_COMPRESSED_SHA256"
readonly HYGNGC_COMPRESSED_LENGTH="$HYG44_COMPRESSED_LENGTH"
readonly HYGNGC_DECOMPRESSED_SHA256="$HYG44_DECOMPRESSED_SHA256"
readonly HYGNGC_DECOMPRESSED_LENGTH="$HYG44_DECOMPRESSED_LENGTH"
readonly HYGNGC_EXPECTED_ROWS="$HYG44_EXPECTED_ROWS"
readonly HYGNGC_DATABASE_FILE="hyg_v44_openngc.sqlite"
readonly HYGNGC_DATABASE_SHA256="0000000000000000000000000000000000000000000000000000000000000000"
readonly HYGNGC_DATABASE_LENGTH="1"
readonly HYGNGC_LICENSE_IDENTIFIER="CC BY-SA 4.0"
readonly HYGNGC_LICENSE_URL="https://creativecommons.org/licenses/by-sa/4.0/"
readonly HYGNGC_LICENSE_FILE="LICENSE-HYG-OPENNGC.md"
readonly HYGNGC_LICENSE_LENGTH="1273"
readonly HYGNGC_LICENSE_SHA256="35e3828ef35eb0d4bbaf5d9b4e88e7e235f502d60ecdb8b98e1b721f7a33b49e"
readonly HYGNGC_ATTRIBUTION_FILE="ATTRIBUTION-HYG-OPENNGC.md"
readonly HYGNGC_ATTRIBUTION_LENGTH="5647"
readonly HYGNGC_ATTRIBUTION_SHA256="15a3ce6129d61310bc6405734f0cdc18dc59d5e237519669244436a6cd0a19d2"
readonly HYGNGC_DEEP_SKY_AXIS_UNIT="arcminute"
readonly HYGNGC_DEEP_SKY_OUTLINE_LEVEL_CONVENTION="1-widest-2-standard-3-narrowest"
readonly HYGNGC_DEEP_SKY_POSITION_ANGLE_CONVENTION="degrees-north-through-east-0-inclusive-to-180-exclusive"
readonly HYGNGC_DEEP_SKY_SURFACE_BRIGHTNESS_UNIT="b-mag-per-square-arcsecond-within-25-mag-isophote"

# The stars tables, digested by the sqlite3 shell's `.sha3sum --sha3-256`, equal the approved HYG 4.4 database's.
readonly HYGNGC_STAR_OBJECTS_SHA3="79f071b94db9d77fddd1db1949af6162b2e6b2a713bf8ac834d8555fc3591a15"
readonly HYGNGC_STAR_ALIASES_SHA3="9fef7733add7c7d11b6938e270d7a5d348aa80f1d5086d371a07fdaae4f965fc"

readonly OPENNGC_NAME="OpenNGC"
readonly OPENNGC_VERSION="v20260501"
readonly OPENNGC_COMMIT="36cb178a0f69dba8bfc03a99c10512831edf1c6b"
readonly OPENNGC_PROJECT_URL="https://github.com/mattiaverga/OpenNGC"
readonly OPENNGC_LICENSE_IDENTIFIER="CC BY-SA 4.0"
readonly OPENNGC_NGC_FILE="database_files/NGC.csv"
readonly OPENNGC_NGC_SHA256="840fe0c9ee1332e551b2e722a0e92726cd7b157914a3d2177602832aadd3aa9e"
readonly OPENNGC_NGC_LENGTH="3876288"
readonly OPENNGC_NGC_ROWS="13969"
readonly OPENNGC_ADDENDUM_FILE="database_files/addendum.csv"
readonly OPENNGC_ADDENDUM_SHA256="1d8f0914e643ada325a5a94d88d8fefad6a4937a2f77cc34f21483af22b11983"
readonly OPENNGC_ADDENDUM_LENGTH="17484"
readonly OPENNGC_ADDENDUM_ROWS="64"
readonly OPENNGC_OUTLINE_DIRECTORY="outlines/objects"
readonly OPENNGC_OUTLINE_LISTING="$HYGNGC_LIBRARY_DIR/openngc-v20260501-outlines.sha256"
readonly OPENNGC_OUTLINE_LISTING_SHA256="f8db977ecb1ecd2f7c9156f570bacddce5316b601c3a92e4a530ab6bc0d5de1f"
readonly OPENNGC_OUTLINE_LISTING_LENGTH="37407"
readonly OPENNGC_OUTLINE_FILE_COUNT="379"
readonly OPENNGC_OUTLINE_POINT_COUNT="102239"

readonly HYGNGC_DEEP_SKY_OBJECT_COUNT="13371"
readonly HYGNGC_DEEP_SKY_ALIAS_COUNT="65822"
readonly HYGNGC_DEEP_SKY_TOMBSTONE_COUNT="10"
readonly HYGNGC_OUTLINE_OBJECT_COUNT="182"
readonly HYGNGC_OUTLINE_SET_COUNT="359"
readonly HYGNGC_OUTLINE_RING_COUNT="441"
readonly HYGNGC_OUTLINE_POINT_COUNT="90861"
readonly HYGNGC_OUTLINE_LEVEL_COUNTS="1=97,2=164,3=98"
readonly HYGNGC_CONSTELLATION_COUNT="89"
readonly HYGNGC_ALIAS_KIND_COUNTS="caldwell=105,common=182,cross-reference=74,designation=13371,disputed=1,duplicate=651,hipparcos=28,identifier=51302,messier=108"
readonly HYGNGC_OBJECT_TYPE_COUNTS="*=546,**=244,*Ass=64,Cl+N=67,DrkN=2,EmN=8,G=10521,GCl=208,GGroup=13,GPair=231,GTrpl=26,HII=83,Neb=94,Nova=3,OCl=663,Other=419,PN=130,RfN=38,SNR=11"
# Every OpenNGC object that names a Hipparcos star must lie within this many arcseconds of the HYG 4.4 star, and,
# where OpenNGC gives a V magnitude, within this many magnitudes of the HYG value.
readonly HYGNGC_HIPPARCOS_MAXIMUM_SEPARATION_ARCSECONDS="5"
readonly HYGNGC_HIPPARCOS_MAXIMUM_V_DIFFERENCE="0.1"
# Each recorded transformation is id|count|subjects. Subjects, joined by ';' in binary order, are listed for a
# transformation that affects at most 11 items and are empty otherwise. The build recomputes every entry from the
# inputs (build_transformations in the composition SQL) and fails on any difference.
readonly -a HYGNGC_TRANSFORMATIONS=(
    "position-angle-180-to-0|44|"
    "duplicate-resolved|651|"
    "duplicate-disputed|1|M102->NGC5457"
    "duplicate-tombstoned|0|"
    "nonexistent-tombstoned|10|IC0067;IC0068;IC1064;IC1326;IC1642;IC2688;IC2915;IC3398;IC5112;NGC0412"
    "caldwell-identifier-normalised|105|"
    "hipparcos-identifier-normalised|23|"
    "sh2-identifier-dropped|1|C009:SH 2-155"
    "live-cross-reference-skipped|6|NGC0554A->NGC0554;NGC0704B->NGC0704;NGC0755->NGC0764;NGC6838->NGC6839;NGC7368->IC1459;NGC7418->IC1459"
    "list-token-trimmed|16|"
    "empty-list-token-dropped|1|UGC04305:identifiers"
    "bare-prefix-identifier-dropped|3|NGC0234:HD;NGC7437:HD;NGC7659:HD"
    "duplicate-row-identifier-dropped|1|IC0117:TYC 4682-651-1"
    "alias-precedence-collapsed|14|"
    "outline-stem-caldwell-padding|1|C9->C009"
    "outline-stem-messier|1|M045->Mel022"
    "outline-regional-stem-dropped|11|Antlia;Cepheus;CoronaAustralis;Cygnus;LBN1032;LBN1037;LMC;Orion;Puppis;Scorpion;Vela"
    "outline-consecutive-duplicate-removed|588|"
)

hygngc_is_supported_package_version() {
    local value="$1"
    local revision
    [[ "$value" =~ ^hyg-v4\.4-openngc-v20260501-p5-s4-r([1-9][0-9]*)$ ]] || return 1
    revision="${BASH_REMATCH[1]}"
    (( ${#revision} < 10 )) || { [[ ${#revision} -eq 10 ]] && (( 10#$revision <= 2147483647 )); }
}

# Fills HYGNGC_OUTLINE_NAMES with the outline file names in the committed listing, after proving the listing is the
# pinned one and that every line names a level 1 to 3 outline file whose name is safe to embed in SQL. It sets a
# global rather than printing so that a failure stops the build instead of vanishing in a command substitution.
HYGNGC_OUTLINE_NAMES=()
hygngc_load_outline_names() {
    local line

    HYGNGC_OUTLINE_NAMES=()
    hyg_verify_file "$OPENNGC_OUTLINE_LISTING" "$OPENNGC_OUTLINE_LISTING_LENGTH" "$OPENNGC_OUTLINE_LISTING_SHA256" "OpenNGC outline listing"
    while IFS= read -r line; do
        [[ "$line" =~ ^[0-9a-f]{64}\ \ outlines/objects/([A-Za-z0-9]+_lv[123]\.txt)$ ]] || \
            hyg_fail "OpenNGC outline listing has an unsupported line: $line"
        HYGNGC_OUTLINE_NAMES+=("${BASH_REMATCH[1]}")
    done < "$OPENNGC_OUTLINE_LISTING"
    [[ "${#HYGNGC_OUTLINE_NAMES[@]}" == "$OPENNGC_OUTLINE_FILE_COUNT" ]] || \
        hyg_fail "OpenNGC outline listing names ${#HYGNGC_OUTLINE_NAMES[@]} files, expected $OPENNGC_OUTLINE_FILE_COUNT"
}

# Copies the pinned OpenNGC inputs from an upstream checkout into DESTINATION and verifies the copies, so that the
# composer reads only bytes it has verified. The outline directory must hold exactly the listed regular files: a
# missing, extra, renamed, or symlinked entry fails the build.
hygngc_stage_openngc_source() {
    local source="$1"
    local destination="$2"
    local directory
    local file
    local name
    local expected
    local actual

    [[ -d "$source" && ! -L "$source" ]] || hyg_fail "OpenNGC source is missing or is not a directory: $source"
    for directory in database_files outlines "$OPENNGC_OUTLINE_DIRECTORY"; do
        [[ -d "$source/$directory" && ! -L "$source/$directory" ]] || hyg_fail "OpenNGC source directory is missing or is not a directory: $directory"
    done
    hygngc_load_outline_names
    expected="$(printf 'f %s\n' "${HYGNGC_OUTLINE_NAMES[@]}" | LC_ALL=C sort)"
    actual="$(find "$source/$OPENNGC_OUTLINE_DIRECTORY" -mindepth 1 -maxdepth 1 -printf '%y %f\n' | LC_ALL=C sort)"
    [[ "$actual" == "$expected" ]] || hyg_fail "OpenNGC outline directory does not hold exactly the listed regular files"

    mkdir -p "$destination/database_files" "$destination/$OPENNGC_OUTLINE_DIRECTORY"
    for file in "$OPENNGC_NGC_FILE" "$OPENNGC_ADDENDUM_FILE"; do
        [[ -f "$source/$file" && ! -L "$source/$file" ]] || hyg_fail "OpenNGC input is missing or is not a regular file: $file"
        cp -- "$source/$file" "$destination/$file"
    done
    for name in "${HYGNGC_OUTLINE_NAMES[@]}"; do
        cp -- "$source/$OPENNGC_OUTLINE_DIRECTORY/$name" "$destination/$OPENNGC_OUTLINE_DIRECTORY/$name"
    done
    hyg_verify_file "$destination/$OPENNGC_NGC_FILE" "$OPENNGC_NGC_LENGTH" "$OPENNGC_NGC_SHA256" "OpenNGC NGC.csv"
    hyg_verify_file "$destination/$OPENNGC_ADDENDUM_FILE" "$OPENNGC_ADDENDUM_LENGTH" "$OPENNGC_ADDENDUM_SHA256" "OpenNGC addendum.csv"
    (cd "$destination" && sha256sum --check --strict --quiet "$OPENNGC_OUTLINE_LISTING") || \
        hyg_fail "OpenNGC outline files do not match the committed listing"
}

# Prints the first composition pass for a staged OpenNGC directory. It imports the verified inputs, derives the
# build_ tables, writes the four deep_sky_ tables and the schema-4 metadata, and leaves the source_ and build_
# tables in place for hygngc_validate_composition; hygngc_finish_database removes them. The SQL is the whole
# transformation recipe for preprocessing version 5; any change to it is a new preprocessing version, never a new
# revision.
hygngc_composition_sql() {
    local openngc="$1"
    local name

    cat <<'SQL'
.bail on
PRAGMA journal_mode = DELETE;
PRAGMA synchronous = OFF;
CREATE TABLE source_openngc_ngc (
  name TEXT, type TEXT, ra TEXT, dec TEXT, const TEXT, majax TEXT, minax TEXT, posang TEXT, bmag TEXT, vmag TEXT,
  jmag TEXT, hmag TEXT, kmag TEXT, surfbr TEXT, hubble TEXT, pax TEXT, pmra TEXT, pmdec TEXT, radvel TEXT,
  redshift TEXT, cstar_umag TEXT, cstar_bmag TEXT, cstar_vmag TEXT, m TEXT, ngc TEXT, ic TEXT, cstar_names TEXT,
  identifiers TEXT, common_names TEXT, ned_notes TEXT, openngc_notes TEXT, sources TEXT
);
CREATE TABLE source_openngc_addendum AS SELECT * FROM source_openngc_ngc WHERE 0;
CREATE TABLE source_outline_file (
  object TEXT, cont_flag TEXT, ra TEXT, dec TEXT, x TEXT, y TEXT, label_flag TEXT, info TEXT
);
CREATE TABLE source_outline (
  stem TEXT NOT NULL, level INTEGER NOT NULL, ordinal INTEGER NOT NULL,
  object TEXT, cont_flag TEXT, ra TEXT, dec TEXT, x TEXT, y TEXT, label_flag TEXT, info TEXT
);
.mode csv
.separator ;
SQL
    printf '.import --skip 1 "%s" source_openngc_ngc\n' "$openngc/$OPENNGC_NGC_FILE"
    printf '.import --skip 1 "%s" source_openngc_addendum\n' "$openngc/$OPENNGC_ADDENDUM_FILE"
    printf '.mode tabs\n'
    # Each file is imported alone so that its rows keep their file order as ordinal 1, 2, ...; the stem and level
    # come from the validated file name.
    for name in "${HYGNGC_OUTLINE_NAMES[@]}"; do
        printf 'DELETE FROM source_outline_file;\n'
        printf '.import --skip 1 "%s" source_outline_file\n' "$openngc/$OPENNGC_OUTLINE_DIRECTORY/$name"
        printf "INSERT INTO source_outline SELECT '%s', %s, rowid, * FROM source_outline_file ORDER BY rowid;\n" \
            "${name%_lv?.txt}" "${name: -5:1}"
    done
    cat <<'SQL'
CREATE TABLE source_openngc AS
SELECT 'NGC.csv' AS source_file, * FROM source_openngc_ngc
UNION ALL
SELECT 'addendum.csv', * FROM source_openngc_addendum;
DROP TABLE source_openngc_ngc;
DROP TABLE source_openngc_addendum;
DROP TABLE source_outline_file;

-- Every name a row carries or references, and its display designation.
CREATE TABLE build_names (name TEXT PRIMARY KEY NOT NULL) WITHOUT ROWID;
INSERT OR IGNORE INTO build_names SELECT name FROM source_openngc;
CREATE TABLE build_list_tokens AS
WITH RECURSIVE split(name, list, position, rest, token) AS (
  SELECT name, 'ngc', 0, ngc || ',', NULL FROM source_openngc WHERE ngc != ''
  UNION ALL SELECT name, 'ic', 0, ic || ',', NULL FROM source_openngc WHERE ic != ''
  UNION ALL SELECT name, 'identifiers', 0, identifiers || ',', NULL FROM source_openngc WHERE identifiers != ''
  UNION ALL SELECT name, 'common_names', 0, common_names || ',', NULL FROM source_openngc WHERE common_names != ''
  UNION ALL
  SELECT name, list, position + 1, substr(rest, instr(rest, ',') + 1), substr(rest, 1, instr(rest, ',') - 1)
  FROM split WHERE rest != ''
)
SELECT name, list, position, trim(token) AS token, token AS raw_token FROM split WHERE token IS NOT NULL;
INSERT OR IGNORE INTO build_names
SELECT CASE list WHEN 'ngc' THEN 'NGC' ELSE 'IC' END || token FROM build_list_tokens WHERE list IN ('ngc', 'ic');
CREATE TABLE build_designations AS
SELECT name,
  CASE
    WHEN name GLOB 'NGC[0-9][0-9][0-9][0-9]*' THEN 'NGC ' || CAST(CAST(substr(name, 4, 4) AS INTEGER) AS TEXT) || substr(name, 8)
    WHEN name GLOB 'IC[0-9][0-9][0-9][0-9]*' THEN 'IC ' || CAST(CAST(substr(name, 3, 4) AS INTEGER) AS TEXT) || substr(name, 7)
    WHEN name GLOB 'ESO[0-9][0-9][0-9]-[0-9][0-9][0-9]' THEN 'ESO ' || substr(name, 4)
    WHEN name GLOB 'Mel[0-9][0-9][0-9]' THEN 'Mel ' || CAST(CAST(substr(name, 4) AS INTEGER) AS TEXT)
    WHEN name GLOB 'PGC[0-9][0-9][0-9][0-9][0-9][0-9]' THEN 'PGC ' || CAST(CAST(substr(name, 4) AS INTEGER) AS TEXT)
    WHEN name GLOB 'C[0-9][0-9][0-9]' THEN 'C' || CAST(CAST(substr(name, 2) AS INTEGER) AS TEXT)
    WHEN name GLOB 'H[0-9][0-9]' THEN 'H ' || CAST(CAST(substr(name, 2) AS INTEGER) AS TEXT)
    WHEN name GLOB 'MWSC[0-9][0-9][0-9][0-9]' THEN 'MWSC ' || CAST(CAST(substr(name, 5) AS INTEGER) AS TEXT)
    WHEN name GLOB 'UGC[0-9][0-9][0-9][0-9][0-9]' THEN 'UGC ' || CAST(CAST(substr(name, 4) AS INTEGER) AS TEXT)
    WHEN name GLOB 'HCG[0-9][0-9][0-9]' THEN 'HCG ' || CAST(CAST(substr(name, 4) AS INTEGER) AS TEXT)
    WHEN name GLOB 'M[0-9][0-9][0-9]' THEN 'M' || CAST(CAST(substr(name, 2) AS INTEGER) AS TEXT)
    WHEN name GLOB 'B[0-9][0-9][0-9]' THEN 'B ' || CAST(CAST(substr(name, 2) AS INTEGER) AS TEXT)
    WHEN name GLOB 'Cl[0-9][0-9][0-9]' THEN 'Cl ' || CAST(CAST(substr(name, 3) AS INTEGER) AS TEXT)
  END AS designation
FROM build_names;

-- Duplicate rows resolve through their NGC, IC and Messier columns; UNION in the walk stops at a cycle.
CREATE TABLE build_live AS SELECT name FROM source_openngc WHERE type NOT IN ('Dup', 'NonEx');
CREATE TABLE build_dup_edges AS
SELECT t.name AS origin, CASE t.list WHEN 'ngc' THEN 'NGC' ELSE 'IC' END || t.token AS target
FROM build_list_tokens AS t JOIN source_openngc AS s ON s.name = t.name
WHERE s.type = 'Dup' AND t.list IN ('ngc', 'ic')
UNION
SELECT s.name, l.name FROM source_openngc AS s JOIN source_openngc AS l ON l.m = s.m AND l.type != 'Dup'
WHERE s.type = 'Dup' AND s.m != '';
CREATE TABLE build_dup_reach AS
WITH RECURSIVE reach(origin, node) AS (
  SELECT name, name FROM source_openngc WHERE type = 'Dup'
  UNION
  SELECT r.origin, e.target FROM reach AS r JOIN build_dup_edges AS e ON e.origin = r.node
)
SELECT origin, node FROM reach;
CREATE TABLE build_dup_resolution AS
SELECT d.name AS origin,
       (SELECT count(*) FROM build_dup_reach AS r JOIN build_live AS l ON l.name = r.node WHERE r.origin = d.name) AS terminals,
       (SELECT min(r.node) FROM build_dup_reach AS r JOIN build_live AS l ON l.name = r.node WHERE r.origin = d.name) AS target,
       (SELECT group_concat(node, ',') FROM (SELECT r.node FROM build_dup_reach AS r JOIN build_live AS l ON l.name = r.node
          WHERE r.origin = d.name ORDER BY r.node)) AS candidates,
       EXISTS (SELECT 1 FROM build_dup_edges AS e JOIN source_openngc AS t ON t.name = e.target
               WHERE e.origin = d.name AND t.type != 'Dup' AND t.m = d.m AND d.m != '') AS via_messier
FROM source_openngc AS d WHERE d.type = 'Dup';

-- Caldwell numbers come from an addendum C### name or a "C ###" identifier; Hipparcos numbers lose their padding.
CREATE TABLE build_caldwell AS
SELECT name, CAST(substr(name, 2) AS INTEGER) AS number FROM source_openngc WHERE name GLOB 'C[0-9][0-9][0-9]'
UNION ALL
SELECT name, CAST(substr(token, 3) AS INTEGER) FROM build_list_tokens
WHERE list = 'identifiers' AND token GLOB 'C [0-9][0-9][0-9]';
CREATE TABLE build_hipparcos AS
SELECT name, CAST(CAST(substr(token, 5) AS INTEGER) AS TEXT) AS hip, token FROM build_list_tokens
WHERE list = 'identifiers' AND token GLOB 'HIP [0-9]*' AND substr(token, 5) NOT GLOB '*[^0-9]*';

CREATE TABLE deep_sky_objects (
  id TEXT PRIMARY KEY NOT NULL,
  designation TEXT NOT NULL,
  display_name TEXT NOT NULL,
  object_type TEXT NOT NULL,
  right_ascension_hours REAL NOT NULL,
  declination_degrees REAL NOT NULL,
  constellation TEXT NOT NULL,
  major_axis_arcminutes REAL,
  minor_axis_arcminutes REAL,
  position_angle_degrees REAL,
  b_magnitude REAL,
  v_magnitude REAL,
  surface_brightness REAL,
  hubble_type TEXT,
  messier_number INTEGER,
  caldwell_number INTEGER,
  hipparcos_id TEXT,
  common_name TEXT
) WITHOUT ROWID;
CREATE TABLE deep_sky_aliases (
  alias TEXT NOT NULL,
  object_id TEXT NOT NULL,
  kind TEXT NOT NULL,
  PRIMARY KEY (alias, object_id)
) WITHOUT ROWID;
CREATE TABLE deep_sky_tombstones (
  id TEXT PRIMARY KEY NOT NULL,
  designation TEXT NOT NULL,
  reason TEXT NOT NULL,
  candidates TEXT
) WITHOUT ROWID;
CREATE TABLE deep_sky_outline_points (
  object_id TEXT NOT NULL,
  level INTEGER NOT NULL,
  ring INTEGER NOT NULL,
  sequence INTEGER NOT NULL,
  right_ascension_degrees REAL NOT NULL,
  declination_degrees REAL NOT NULL,
  PRIMARY KEY (object_id, level, ring, sequence)
) WITHOUT ROWID;

INSERT INTO deep_sky_objects
SELECT s.name,
       d.designation,
       CASE WHEN s.m != '' THEN 'M' || CAST(CAST(s.m AS INTEGER) AS TEXT) ELSE d.designation END,
       s.type,
       CAST(substr(s.ra, 1, 2) AS INTEGER) + CAST(substr(s.ra, 4, 2) AS INTEGER) / 60.0 + CAST(substr(s.ra, 7) AS REAL) / 3600.0,
       (CASE substr(s.dec, 1, 1) WHEN '-' THEN -1 ELSE 1 END) *
         (CAST(substr(s.dec, 2, 2) AS INTEGER) + CAST(substr(s.dec, 5, 2) AS INTEGER) / 60.0 + CAST(substr(s.dec, 8) AS REAL) / 3600.0),
       s.const,
       CASE WHEN s.majax = '' THEN NULL ELSE CAST(s.majax AS REAL) END,
       CASE WHEN s.minax = '' THEN NULL ELSE CAST(s.minax AS REAL) END,
       CASE WHEN s.posang = '' THEN NULL WHEN s.posang = '180' THEN 0.0 ELSE CAST(s.posang AS REAL) END,
       CASE WHEN s.bmag = '' THEN NULL ELSE CAST(s.bmag AS REAL) END,
       CASE WHEN s.vmag = '' THEN NULL ELSE CAST(s.vmag AS REAL) END,
       CASE WHEN s.surfbr = '' THEN NULL ELSE CAST(s.surfbr AS REAL) END,
       NULLIF(s.hubble, ''),
       CASE WHEN s.m = '' THEN NULL ELSE CAST(s.m AS INTEGER) END,
       c.number,
       h.hip,
       (SELECT t.token FROM build_list_tokens AS t WHERE t.name = s.name AND t.list = 'common_names' AND t.token != ''
        ORDER BY t.position LIMIT 1)
FROM source_openngc AS s
JOIN build_designations AS d ON d.name = s.name
LEFT JOIN build_caldwell AS c ON c.name = s.name
LEFT JOIN build_hipparcos AS h ON h.name = s.name
WHERE s.type NOT IN ('Dup', 'NonEx')
ORDER BY s.name;

-- One alias row per (alias, object); where sources agree, the lowest rank names the kind.
CREATE TABLE build_alias_candidates (alias TEXT NOT NULL, object_id TEXT NOT NULL, kind TEXT NOT NULL, rank INTEGER NOT NULL);
INSERT INTO build_alias_candidates
SELECT d.designation, o.id, 'designation', 1 FROM deep_sky_objects AS o JOIN build_designations AS d ON d.name = o.id
UNION ALL
SELECT 'M' || CAST(messier_number AS TEXT), id, 'messier', 2 FROM deep_sky_objects WHERE messier_number IS NOT NULL
UNION ALL
SELECT 'C' || CAST(caldwell_number AS TEXT), id, 'caldwell', 3 FROM deep_sky_objects WHERE caldwell_number IS NOT NULL
UNION ALL
SELECT d.designation, r.target, CASE WHEN r.via_messier THEN 'disputed' ELSE 'duplicate' END, CASE WHEN r.via_messier THEN 4 ELSE 5 END
FROM build_dup_resolution AS r JOIN build_designations AS d ON d.name = r.origin WHERE r.terminals = 1
UNION ALL
SELECT d.designation, t.name, 'cross-reference', 6
FROM build_list_tokens AS t
JOIN build_live AS l ON l.name = t.name
JOIN build_designations AS d ON d.name = CASE t.list WHEN 'ngc' THEN 'NGC' ELSE 'IC' END || t.token
LEFT JOIN source_openngc AS target ON target.name = d.name
WHERE t.list IN ('ngc', 'ic') AND target.name IS NULL
UNION ALL
SELECT 'HIP ' || hipparcos_id, id, 'hipparcos', 7 FROM deep_sky_objects WHERE hipparcos_id IS NOT NULL
UNION ALL
SELECT t.token, t.name, 'common', 8 FROM build_list_tokens AS t JOIN build_live AS l ON l.name = t.name
WHERE t.list = 'common_names' AND t.token != ''
UNION ALL
SELECT t.token, t.name, 'identifier', 9 FROM build_list_tokens AS t JOIN build_live AS l ON l.name = t.name
WHERE t.list = 'identifiers' AND t.token != '' AND (t.token GLOB '*[0-9]*' OR t.token GLOB '* *')
  AND NOT t.token GLOB 'C [0-9][0-9][0-9]' AND NOT t.token GLOB 'HIP [0-9]*' AND NOT t.token GLOB 'SH 2-*';
INSERT INTO deep_sky_aliases (alias, object_id, kind)
SELECT alias, object_id, kind FROM (
  SELECT alias, object_id, kind, row_number() OVER (PARTITION BY alias, object_id ORDER BY rank) AS choice
  FROM build_alias_candidates
) WHERE choice = 1
ORDER BY alias, object_id;
INSERT INTO deep_sky_tombstones (id, designation, reason, candidates)
SELECT s.name, d.designation, 'nonexistent', NULL
FROM source_openngc AS s JOIN build_designations AS d ON d.name = s.name WHERE s.type = 'NonEx'
UNION ALL
SELECT r.origin, d.designation, CASE WHEN r.terminals = 0 THEN 'duplicate-unresolved' ELSE 'duplicate-conflict' END, r.candidates
FROM build_dup_resolution AS r JOIN build_designations AS d ON d.name = r.origin WHERE r.terminals != 1
ORDER BY 1;

-- Outline files map to an object by exact name, by Caldwell padding, or by Messier number; the regional
-- outlines that name no single object are dropped, and any other stem is left unmatched for validation to reject.
CREATE TABLE build_outline_stem_map (stem TEXT PRIMARY KEY NOT NULL, object_id TEXT, rule TEXT NOT NULL) WITHOUT ROWID;
INSERT INTO build_outline_stem_map
WITH stems(stem) AS (SELECT DISTINCT stem FROM source_outline),
candidates AS (
  SELECT stem,
         (SELECT id FROM deep_sky_objects WHERE id = stem) AS exact_id,
         CASE WHEN stem GLOB 'C[0-9]*' AND substr(stem, 2) NOT GLOB '*[^0-9]*'
              THEN (SELECT id FROM deep_sky_objects WHERE id = printf('C%03d', CAST(substr(stem, 2) AS INTEGER))) END AS caldwell_id,
         CASE WHEN stem GLOB 'M[0-9]*' AND substr(stem, 2) NOT GLOB '*[^0-9]*'
              THEN (SELECT id FROM deep_sky_objects WHERE messier_number = CAST(substr(stem, 2) AS INTEGER)) END AS messier_id,
         stem IN ('Antlia', 'Cepheus', 'CoronaAustralis', 'Cygnus', 'LBN1032', 'LBN1037', 'LMC', 'Orion', 'Puppis',
                  'Scorpion', 'Vela') AS regional
  FROM stems)
SELECT stem,
       CASE WHEN regional THEN NULL ELSE coalesce(exact_id, caldwell_id, messier_id) END,
       CASE WHEN regional THEN 'regional-dropped'
            WHEN exact_id IS NOT NULL THEN 'exact'
            WHEN caldwell_id IS NOT NULL THEN 'caldwell-padding'
            WHEN messier_id IS NOT NULL THEN 'messier'
            ELSE 'unmatched' END
FROM candidates ORDER BY stem;
-- A '*' continuation flag closes a ring, so a row's ring is the number of '*' rows before it in its file.
CREATE TABLE build_outline_rows AS
SELECT m.object_id, o.stem, o.level, o.ordinal, o.cont_flag, o.ra, o.dec,
       coalesce(sum(o.cont_flag = '*') OVER (PARTITION BY o.stem, o.level ORDER BY o.ordinal
                                            ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0) AS ring
FROM source_outline AS o JOIN build_outline_stem_map AS m ON m.stem = o.stem
WHERE m.object_id IS NOT NULL;
CREATE TABLE build_outline_kept AS
SELECT * FROM (
  SELECT r.*, lag(r.ra) OVER w AS previous_ra, lag(r.dec) OVER w AS previous_dec
  FROM build_outline_rows AS r WINDOW w AS (PARTITION BY r.stem, r.level, r.ring ORDER BY r.ordinal))
WHERE previous_ra IS NULL OR NOT (ra = previous_ra AND dec = previous_dec);
INSERT INTO deep_sky_outline_points
SELECT object_id, level, ring,
       row_number() OVER (PARTITION BY object_id, level, ring ORDER BY ordinal) - 1,
       CAST(ra AS REAL), CAST(dec AS REAL)
FROM build_outline_kept ORDER BY object_id, level, ring, ordinal;

-- The recorded transformations, recomputed from the inputs for comparison with the pinned list.
CREATE TABLE build_transformations (ordinal INTEGER PRIMARY KEY, id TEXT NOT NULL, count INTEGER NOT NULL, subjects TEXT NOT NULL);
INSERT INTO build_transformations
WITH ids(ordinal, id) AS (VALUES
  (1, 'position-angle-180-to-0'), (2, 'duplicate-resolved'), (3, 'duplicate-disputed'), (4, 'duplicate-tombstoned'),
  (5, 'nonexistent-tombstoned'), (6, 'caldwell-identifier-normalised'), (7, 'hipparcos-identifier-normalised'),
  (8, 'sh2-identifier-dropped'), (9, 'live-cross-reference-skipped'), (10, 'list-token-trimmed'),
  (11, 'empty-list-token-dropped'), (12, 'bare-prefix-identifier-dropped'), (13, 'duplicate-row-identifier-dropped'),
  (14, 'alias-precedence-collapsed'), (15, 'outline-stem-caldwell-padding'), (16, 'outline-stem-messier'),
  (17, 'outline-regional-stem-dropped'), (18, 'outline-consecutive-duplicate-removed')),
subjects(id, subject) AS (
  SELECT 'position-angle-180-to-0', s.name FROM source_openngc AS s JOIN build_live AS l ON l.name = s.name WHERE s.posang = '180'
  UNION ALL
  SELECT CASE WHEN via_messier THEN 'duplicate-disputed' ELSE 'duplicate-resolved' END, origin || '->' || target
  FROM build_dup_resolution WHERE terminals = 1
  UNION ALL
  SELECT 'duplicate-tombstoned', origin FROM build_dup_resolution WHERE terminals != 1
  UNION ALL
  SELECT 'nonexistent-tombstoned', name FROM source_openngc WHERE type = 'NonEx'
  UNION ALL
  SELECT 'caldwell-identifier-normalised', t.name || ':' || t.token FROM build_list_tokens AS t JOIN build_live AS l ON l.name = t.name
  WHERE t.list = 'identifiers' AND t.token GLOB 'C [0-9][0-9][0-9]'
  UNION ALL
  SELECT 'hipparcos-identifier-normalised', h.name || ':' || h.token FROM build_hipparcos AS h WHERE substr(h.token, 5) != h.hip
  UNION ALL
  SELECT 'sh2-identifier-dropped', t.name || ':' || t.token FROM build_list_tokens AS t JOIN build_live AS l ON l.name = t.name
  WHERE t.list = 'identifiers' AND t.token GLOB 'SH 2-*'
  UNION ALL
  SELECT 'live-cross-reference-skipped', t.name || '->' || target.name
  FROM build_list_tokens AS t JOIN build_live AS l ON l.name = t.name
  JOIN source_openngc AS target ON target.name = CASE t.list WHEN 'ngc' THEN 'NGC' ELSE 'IC' END || t.token
  WHERE t.list IN ('ngc', 'ic') AND target.type NOT IN ('Dup', 'NonEx')
  UNION ALL
  SELECT 'list-token-trimmed', name || ':' || token FROM build_list_tokens WHERE token != raw_token
  UNION ALL
  SELECT 'empty-list-token-dropped', name || ':' || list FROM build_list_tokens WHERE token = ''
  UNION ALL
  SELECT 'bare-prefix-identifier-dropped', t.name || ':' || t.token FROM build_list_tokens AS t JOIN build_live AS l ON l.name = t.name
  WHERE t.list = 'identifiers' AND t.token != '' AND NOT (t.token GLOB '*[0-9]*' OR t.token GLOB '* *')
  UNION ALL
  SELECT 'duplicate-row-identifier-dropped', t.name || ':' || t.token FROM build_list_tokens AS t JOIN source_openngc AS s ON s.name = t.name
  WHERE s.type = 'Dup' AND t.list IN ('identifiers', 'common_names') AND t.token != ''
  UNION ALL
  SELECT 'alias-precedence-collapsed', alias || '->' || object_id || ':' || kind FROM (
    SELECT alias, object_id, kind, row_number() OVER (PARTITION BY alias, object_id ORDER BY rank) AS choice
    FROM build_alias_candidates) WHERE choice > 1
  UNION ALL
  SELECT CASE rule WHEN 'caldwell-padding' THEN 'outline-stem-caldwell-padding' ELSE 'outline-stem-messier' END, stem || '->' || object_id
  FROM build_outline_stem_map WHERE rule IN ('caldwell-padding', 'messier')
  UNION ALL
  SELECT 'outline-regional-stem-dropped', stem FROM build_outline_stem_map WHERE rule = 'regional-dropped'
  UNION ALL
  SELECT 'outline-consecutive-duplicate-removed', r.stem || '_lv' || r.level || ':' || r.ordinal
  FROM build_outline_rows AS r LEFT JOIN build_outline_kept AS k ON k.stem = r.stem AND k.level = r.level AND k.ordinal = r.ordinal
  WHERE k.ordinal IS NULL)
SELECT i.ordinal, i.id, count(s.subject),
       CASE WHEN count(s.subject) <= 11 THEN coalesce(group_concat(s.subject, ';' ORDER BY s.subject), '') ELSE '' END
FROM ids AS i LEFT JOIN subjects AS s ON s.id = i.id
GROUP BY i.ordinal, i.id ORDER BY i.ordinal;
SQL
    cat <<SQL
UPDATE catalog_metadata SET value = CASE key
  WHEN 'catalog_version' THEN '$HYGNGC_CATALOG_VERSION'
  WHEN 'name' THEN '$HYGNGC_CATALOG_NAME'
  WHEN 'preprocessing_version' THEN '$HYGNGC_PREPROCESSING_VERSION'
  WHEN 'schema_version' THEN '$HYGNGC_SCHEMA_VERSION'
END
WHERE key IN ('catalog_version', 'name', 'preprocessing_version', 'schema_version');
INSERT INTO catalog_metadata VALUES
  ('deep_sky_axis_unit', '$HYGNGC_DEEP_SKY_AXIS_UNIT'),
  ('deep_sky_coordinate_epoch', '$HYG44_COORDINATE_EPOCH'),
  ('deep_sky_coordinate_frame', '$HYG44_COORDINATE_FRAME'),
  ('deep_sky_license', '$OPENNGC_LICENSE_IDENTIFIER'),
  ('deep_sky_outline_level_convention', '$HYGNGC_DEEP_SKY_OUTLINE_LEVEL_CONVENTION'),
  ('deep_sky_position_angle_convention', '$HYGNGC_DEEP_SKY_POSITION_ANGLE_CONVENTION'),
  ('deep_sky_source_commit', '$OPENNGC_COMMIT'),
  ('deep_sky_source_name', '$OPENNGC_NAME'),
  ('deep_sky_source_url', '$OPENNGC_PROJECT_URL'),
  ('deep_sky_source_version', '$OPENNGC_VERSION'),
  ('deep_sky_surface_brightness_unit', '$HYGNGC_DEEP_SKY_SURFACE_BRIGHTNESS_UNIT');
PRAGMA user_version = $HYGNGC_SCHEMA_VERSION;
SQL
}

# Composes DATABASE, which must be a writable copy of the verified HYG 4.4 database, with a directory staged by
# hygngc_stage_openngc_source. Any sqlite3 diagnostic, including an .import warning about a short or long row, fails
# the build.
hygngc_compose_database() {
    local openngc="$1"
    local database="$2"
    local script="$database.compose.sql"
    local diagnostics="$database.compose.err"

    case "$openngc" in
        *$'\n'*|*'"'*) hyg_fail "staging path contains a character unsupported by sqlite3 .import" ;;
    esac
    [[ ! -e "$script" && ! -L "$script" && ! -e "$diagnostics" && ! -L "$diagnostics" ]] || \
        hyg_fail "composition scratch files already exist beside $database"
    hygngc_load_outline_names
    hygngc_composition_sql "$openngc" > "$script"
    sqlite3 -batch "$database" < "$script" > /dev/null 2> "$diagnostics" || {
        cat -- "$diagnostics" >&2
        hyg_fail "sqlite3 failed to compose the catalog database"
    }
    [[ ! -s "$diagnostics" ]] || { cat -- "$diagnostics" >&2; hyg_fail "sqlite3 reported diagnostics while composing the catalog database"; }
    rm -f -- "$script" "$diagnostics"
}

# Removes the source_ and build_ tables and rewrites the file, so that two builds from the same inputs are
# byte-identical.
hygngc_finish_database() {
    sqlite3 -batch "$1" > /dev/null <<'SQL'
.bail on
DROP TABLE build_transformations;
DROP TABLE build_outline_kept;
DROP TABLE build_outline_rows;
DROP TABLE build_outline_stem_map;
DROP TABLE build_alias_candidates;
DROP TABLE build_hipparcos;
DROP TABLE build_caldwell;
DROP TABLE build_dup_resolution;
DROP TABLE build_dup_reach;
DROP TABLE build_dup_edges;
DROP TABLE build_live;
DROP TABLE build_designations;
DROP TABLE build_list_tokens;
DROP TABLE build_names;
DROP TABLE source_outline;
DROP TABLE source_openngc;
VACUUM;
SQL
}

hygngc_expect() {
    local database="$1"
    local description="$2"
    local expected="$3"
    local query="$4"
    local result

    result="$(hyg_sqlite_scalar "$database" "$query")"
    [[ "$result" == "$expected" ]] || hyg_fail "$description: found '$result', expected '$expected'"
}

# Checks the first composition pass against its inputs while the source_ and build_ tables still exist. HYG_DATABASE
# is the verified HYG 4.4 database the composition started from.
hygngc_validate_composition() {
    local database="$1"
    local hyg_database="$2"
    local quoted_hyg="${hyg_database//\'/\'\'}"
    local expected
    local result

    hygngc_expect "$database" "OpenNGC NGC.csv rows" "$OPENNGC_NGC_ROWS" "SELECT count(*) FROM source_openngc WHERE source_file = 'NGC.csv';"
    hygngc_expect "$database" "OpenNGC addendum.csv rows" "$OPENNGC_ADDENDUM_ROWS" "SELECT count(*) FROM source_openngc WHERE source_file = 'addendum.csv';"
    hygngc_expect "$database" "OpenNGC rows with a missing column" 0 "SELECT count(*) FROM source_openngc WHERE name IS NULL OR sources IS NULL;"
    hygngc_expect "$database" "OpenNGC names listed more than once" 0 "SELECT count(*) - count(DISTINCT name) FROM source_openngc;"
    hygngc_expect "$database" "OpenNGC rows with an unsupported type" 0 \
        "SELECT count(*) FROM source_openngc WHERE type NOT IN ('*', '**', '*Ass', 'OCl', 'GCl', 'Cl+N', 'G', 'GPair', 'GTrpl', 'GGroup', 'PN', 'HII', 'DrkN', 'EmN', 'Neb', 'RfN', 'SNR', 'Nova', 'NonEx', 'Dup', 'Other');"
    hygngc_expect "$database" "OpenNGC live, duplicate, and nonexistent rows" "13371,652,10" \
        "SELECT sum(type NOT IN ('Dup', 'NonEx')) || ',' || sum(type = 'Dup') || ',' || sum(type = 'NonEx') FROM source_openngc;"
    hygngc_expect "$database" "OpenNGC live rows with an unsupported field" 0 \
        "SELECT count(*) FROM source_openngc AS s JOIN build_live AS l ON l.name = s.name WHERE
           NOT s.ra REGEXP '^([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]\\.[0-9]+\$' OR
           NOT s.dec REGEXP '^[-+][0-8][0-9]:[0-5][0-9]:[0-5][0-9](\\.[0-9]+)?\$' OR
           NOT s.const REGEXP '^([A-Z][A-Za-z][A-Za-z]|Se[12])\$' OR
           (s.majax != '' AND NOT (s.majax REGEXP '^[0-9]+(\\.[0-9]+)?\$' AND CAST(s.majax AS REAL) > 0)) OR
           (s.minax != '' AND NOT (s.minax REGEXP '^[0-9]+(\\.[0-9]+)?\$' AND CAST(s.minax AS REAL) > 0)) OR
           (s.minax != '' AND (s.majax = '' OR CAST(s.minax AS REAL) > CAST(s.majax AS REAL))) OR
           (s.posang != '' AND NOT (s.posang REGEXP '^[0-9]+\$' AND CAST(s.posang AS INTEGER) BETWEEN 0 AND 180)) OR
           (s.bmag != '' AND NOT s.bmag REGEXP '^-?[0-9]+(\\.[0-9]+)?\$') OR
           (s.vmag != '' AND NOT s.vmag REGEXP '^-?[0-9]+(\\.[0-9]+)?\$') OR
           (s.surfbr != '' AND NOT s.surfbr REGEXP '^-?[0-9]+(\\.[0-9]+)?\$') OR
           (s.m != '' AND NOT s.m REGEXP '^[0-9][0-9][0-9]\$');"
    hygngc_expect "$database" "OpenNGC nonexistent rows with identifiers or common names" 0 \
        "SELECT count(*) FROM build_list_tokens AS t JOIN source_openngc AS s ON s.name = t.name WHERE s.type = 'NonEx' AND t.list IN ('identifiers', 'common_names') AND t.token != '';"
    hygngc_expect "$database" "names without a designation" 0 "SELECT count(*) FROM build_designations WHERE designation IS NULL;"
    hygngc_expect "$database" "names sharing a designation" 0 "SELECT count(*) - count(DISTINCT designation) FROM build_designations;"
    hygngc_expect "$database" "duplicate-row links to an unknown or nonexistent row" 0 \
        "SELECT count(*) FROM build_dup_edges AS e LEFT JOIN source_openngc AS s ON s.name = e.target WHERE s.name IS NULL OR s.type = 'NonEx';"
    hygngc_expect "$database" "live cross-references to a duplicate row that resolves elsewhere" 0 \
        "SELECT count(*) FROM build_list_tokens AS t JOIN build_live AS l ON l.name = t.name
         JOIN build_dup_resolution AS r ON r.origin = CASE t.list WHEN 'ngc' THEN 'NGC' ELSE 'IC' END || t.token
         WHERE t.list IN ('ngc', 'ic') AND r.target != t.name;"
    hygngc_expect "$database" "Caldwell numbers (count, objects, numbers, min, max, not live)" "109,109,109,1,109,0" \
        "SELECT count(*) || ',' || count(DISTINCT name) || ',' || count(DISTINCT number) || ',' || min(number) || ',' || max(number) || ',' ||
                (SELECT count(*) FROM build_caldwell WHERE name NOT IN (SELECT name FROM build_live)) FROM build_caldwell;"
    hygngc_expect "$database" "Hipparcos identifiers (count, objects, numbers, not live)" "28,28,28,0" \
        "SELECT count(*) || ',' || count(DISTINCT name) || ',' || count(DISTINCT hip) || ',' ||
                (SELECT count(*) FROM build_hipparcos WHERE name NOT IN (SELECT name FROM build_live)) FROM build_hipparcos;"
    hygngc_expect "$database" "Messier objects (count, numbers)" "109,109" \
        "SELECT count(*) || ',' || count(DISTINCT messier_number) FROM deep_sky_objects WHERE messier_number IS NOT NULL;"
    # 15 of the 28 Hipparcos-linked rows give a V magnitude, copied verbatim; the other 13 give none and are checked for
    # position only.
    result="$(sqlite3 -batch -noheader -readonly "$database" "ATTACH '$quoted_hyg' AS hyg;
        WITH pairs AS (
          SELECT d.v_magnitude, h.id AS hyg_id, h.magnitude,
                 radians(d.right_ascension_hours * 15) AS ra1, radians(d.declination_degrees) AS de1,
                 radians(h.right_ascension_hours * 15) AS ra2, radians(h.declination_degrees) AS de2
          FROM deep_sky_objects AS d LEFT JOIN hyg.celestial_objects AS h ON h.hipparcos_id = d.hipparcos_id
          WHERE d.hipparcos_id IS NOT NULL)
        SELECT count(*) || ',' || sum(hyg_id IS NULL) || ',' ||
               sum(degrees(2 * asin(sqrt(pow(sin((de2 - de1) / 2), 2) + cos(de1) * cos(de2) * pow(sin((ra2 - ra1) / 2), 2)))) * 3600
                   > $HYGNGC_HIPPARCOS_MAXIMUM_SEPARATION_ARCSECONDS) || ',' ||
               sum(v_magnitude IS NOT NULL AND abs(v_magnitude - magnitude) > $HYGNGC_HIPPARCOS_MAXIMUM_V_DIFFERENCE + 1e-9) || ',' ||
               sum(v_magnitude IS NOT NULL)
        FROM pairs;")"
    [[ "$result" == "28,0,0,0,15" ]] || \
        hyg_fail "OpenNGC Hipparcos stars (count, missing from HYG, beyond the position limit, beyond the V limit, with V) are '$result', expected '28,0,0,0,15'"

    hygngc_expect "$database" "outline source rows" "$OPENNGC_OUTLINE_POINT_COUNT" "SELECT count(*) FROM source_outline;"
    hygngc_expect "$database" "outline source files" "$OPENNGC_OUTLINE_FILE_COUNT" "SELECT count(*) FROM (SELECT DISTINCT stem, level FROM source_outline);"
    hygngc_expect "$database" "outline rows with an unsupported fixed column" 0 \
        "SELECT count(*) FROM source_outline WHERE object IS NOT 'line' OR x IS NOT '0.0' OR y IS NOT '0.0' OR label_flag IS NOT 'false'
           OR cont_flag NOT IN ('+', '*') OR cont_flag IS NULL OR info IS NULL OR info NOT IN ('', 'null');"
    hygngc_expect "$database" "outline rows with an unsupported coordinate" 0 \
        "SELECT count(*) FROM source_outline WHERE ra IS NULL OR dec IS NULL OR
           NOT ra REGEXP '^[0-9]+\\.[0-9]+\$' OR NOT dec REGEXP '^-?[0-9]+\\.[0-9]+(E-?[0-9]+)?\$' OR
           CAST(ra AS REAL) < 0 OR CAST(ra AS REAL) >= 360 OR CAST(dec AS REAL) < -90 OR CAST(dec AS REAL) > 90;"
    hygngc_expect "$database" "outline files whose last row does not close a ring" 0 \
        "SELECT count(*) FROM (SELECT cont_flag, row_number() OVER (PARTITION BY stem, level ORDER BY ordinal DESC) AS position FROM source_outline)
         WHERE position = 1 AND cont_flag != '*';"
    hygngc_expect "$database" "outline stem rules" "caldwell-padding=1,exact=180,messier=1,regional-dropped=11" \
        "SELECT group_concat(rule || '=' || n, ',' ORDER BY rule) FROM (SELECT rule, count(*) AS n FROM build_outline_stem_map GROUP BY rule);"
    hygngc_expect "$database" "outline sets claimed by more than one file" 0 \
        "SELECT count(*) FROM (SELECT object_id, level FROM build_outline_rows GROUP BY object_id, level HAVING count(DISTINCT stem) > 1);"
    hygngc_expect "$database" "outline rings that are open in the source" 0 \
        "SELECT count(*) FROM (SELECT first_value(ra) OVER w AS first_ra, first_value(dec) OVER w AS first_dec,
                                      last_value(ra) OVER w AS last_ra, last_value(dec) OVER w AS last_dec
                               FROM build_outline_kept
                               WINDOW w AS (PARTITION BY stem, level, ring ORDER BY ordinal ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING))
         WHERE first_ra != last_ra OR first_dec != last_dec;"

    expected=""
    for result in "${HYGNGC_TRANSFORMATIONS[@]}"; do
        expected+="${expected:+$'\n'}$result"
    done
    hygngc_expect "$database" "recorded transformations" "$expected" \
        "SELECT group_concat(id || '|' || count || '|' || subjects, char(10) ORDER BY ordinal) FROM build_transformations;"
    hygngc_validate_deep_sky_content "$database"
}

hygngc_expected_metadata() {
    printf '%s\n' \
        "catalog_version=$HYGNGC_CATALOG_VERSION" \
        "color_index=$HYG44_COLOR_INDEX" \
        "coordinate_epoch=$HYG44_COORDINATE_EPOCH" \
        "coordinate_equinox=$HYG44_COORDINATE_EQUINOX" \
        "coordinate_frame=$HYG44_COORDINATE_FRAME" \
        "deep_sky_axis_unit=$HYGNGC_DEEP_SKY_AXIS_UNIT" \
        "deep_sky_coordinate_epoch=$HYG44_COORDINATE_EPOCH" \
        "deep_sky_coordinate_frame=$HYG44_COORDINATE_FRAME" \
        "deep_sky_license=$OPENNGC_LICENSE_IDENTIFIER" \
        "deep_sky_outline_level_convention=$HYGNGC_DEEP_SKY_OUTLINE_LEVEL_CONVENTION" \
        "deep_sky_position_angle_convention=$HYGNGC_DEEP_SKY_POSITION_ANGLE_CONVENTION" \
        "deep_sky_source_commit=$OPENNGC_COMMIT" \
        "deep_sky_source_name=$OPENNGC_NAME" \
        "deep_sky_source_url=$OPENNGC_PROJECT_URL" \
        "deep_sky_source_version=$OPENNGC_VERSION" \
        "deep_sky_surface_brightness_unit=$HYGNGC_DEEP_SKY_SURFACE_BRIGHTNESS_UNIT" \
        "license=$HYG44_LICENSE_IDENTIFIER" \
        "magnitude_band=$HYG44_MAGNITUDE_BAND" \
        "name=$HYGNGC_CATALOG_NAME" \
        "preprocessing_version=$HYGNGC_PREPROCESSING_VERSION" \
        "proper_motion_convention=$HYG44_PROPER_MOTION_CONVENTION" \
        "proper_motion_unknown_rule=$HYG44_PROPER_MOTION_UNKNOWN_RULE" \
        "schema_version=$HYGNGC_SCHEMA_VERSION" \
        "source_commit=$HYG44_SOURCE_COMMIT" \
        "source_url=$HYG44_SOURCE_PROJECT_URL"
}

# Checks the stars tables, metadata, and deep-sky tables of a composed database; it needs no build_ table, so it
# runs both on the first pass and on the finished file.
hygngc_validate_deep_sky_content() {
    local database="$1"
    local result

    result="$(sqlite3 -batch -noheader -readonly "$database" '.sha3sum --sha3-256 celestial_objects' '.sha3sum --sha3-256 celestial_object_aliases')"
    [[ "$result" == "$HYGNGC_STAR_OBJECTS_SHA3|celestial_objects"$'\n'"$HYGNGC_STAR_ALIASES_SHA3|celestial_object_aliases" ]] || \
        hyg_fail "the stars tables differ from the approved HYG 4.4 database"
    hygngc_expect "$database" "stars" "$HYGNGC_EXPECTED_ROWS" "SELECT count(*) FROM celestial_objects;"
    hygngc_expect "$database" "catalog metadata" "$(hygngc_expected_metadata)" \
        "SELECT group_concat(key || '=' || value, char(10)) FROM (SELECT key, value FROM catalog_metadata ORDER BY key);"
    hygngc_expect "$database" "deep-sky objects" "$HYGNGC_DEEP_SKY_OBJECT_COUNT" "SELECT count(*) FROM deep_sky_objects;"
    hygngc_expect "$database" "deep-sky aliases" "$HYGNGC_DEEP_SKY_ALIAS_COUNT" "SELECT count(*) FROM deep_sky_aliases;"
    hygngc_expect "$database" "deep-sky tombstones" "$HYGNGC_DEEP_SKY_TOMBSTONE_COUNT" "SELECT count(*) FROM deep_sky_tombstones;"
    hygngc_expect "$database" "deep-sky alias kinds" "$HYGNGC_ALIAS_KIND_COUNTS" \
        "SELECT group_concat(kind || '=' || n, ',' ORDER BY kind) FROM (SELECT kind, count(*) AS n FROM deep_sky_aliases GROUP BY kind);"
    hygngc_expect "$database" "deep-sky object types" "$HYGNGC_OBJECT_TYPE_COUNTS" \
        "SELECT group_concat(object_type || '=' || n, ',' ORDER BY object_type) FROM (SELECT object_type, count(*) AS n FROM deep_sky_objects GROUP BY object_type);"
    hygngc_expect "$database" "deep-sky constellations" "$HYGNGC_CONSTELLATION_COUNT" "SELECT count(DISTINCT constellation) FROM deep_sky_objects;"
    hygngc_expect "$database" "deep-sky objects with an unsupported value" 0 \
        "SELECT count(*) FROM deep_sky_objects WHERE
           typeof(right_ascension_hours) != 'real' OR right_ascension_hours < 0 OR right_ascension_hours >= 24 OR
           typeof(declination_degrees) != 'real' OR declination_degrees < -90 OR declination_degrees > 90 OR
           typeof(major_axis_arcminutes) NOT IN ('real', 'null') OR major_axis_arcminutes <= 0 OR
           typeof(minor_axis_arcminutes) NOT IN ('real', 'null') OR minor_axis_arcminutes <= 0 OR
           (minor_axis_arcminutes IS NOT NULL AND (major_axis_arcminutes IS NULL OR minor_axis_arcminutes > major_axis_arcminutes)) OR
           typeof(position_angle_degrees) NOT IN ('real', 'null') OR position_angle_degrees < 0 OR position_angle_degrees >= 180 OR
           typeof(b_magnitude) NOT IN ('real', 'null') OR typeof(v_magnitude) NOT IN ('real', 'null') OR
           typeof(surface_brightness) NOT IN ('real', 'null') OR
           typeof(messier_number) NOT IN ('integer', 'null') OR typeof(caldwell_number) NOT IN ('integer', 'null') OR
           typeof(hipparcos_id) NOT IN ('text', 'null') OR hipparcos_id GLOB '0*' OR hipparcos_id GLOB '*[^0-9]*' OR
           trim(designation) = '' OR trim(display_name) = '' OR hubble_type = '' OR common_name = '';"
    hygngc_expect "$database" "deep-sky identities that are not unique" "0,0,0,0,0" \
        "SELECT (SELECT count(*) - count(DISTINCT designation) FROM deep_sky_objects) || ',' ||
                (SELECT count(*) - count(DISTINCT display_name) FROM deep_sky_objects) || ',' ||
                (SELECT count(messier_number) - count(DISTINCT messier_number) FROM deep_sky_objects) || ',' ||
                (SELECT count(caldwell_number) - count(DISTINCT caldwell_number) FROM deep_sky_objects) || ',' ||
                (SELECT count(hipparcos_id) - count(DISTINCT hipparcos_id) FROM deep_sky_objects);"
    hygngc_expect "$database" "deep-sky aliases that are orphaned, blank, untrimmed, or unsupported" 0 \
        "SELECT count(*) FROM deep_sky_aliases AS a LEFT JOIN deep_sky_objects AS o ON o.id = a.object_id
         WHERE o.id IS NULL OR trim(a.alias) = '' OR a.alias != trim(a.alias) OR
               a.kind NOT IN ('caldwell', 'common', 'cross-reference', 'designation', 'disputed', 'duplicate', 'hipparcos', 'identifier', 'messier');"
    hygngc_expect "$database" "deep-sky objects whose designation is not their designation alias" 0 \
        "SELECT count(*) FROM deep_sky_objects AS o
         WHERE NOT EXISTS (SELECT 1 FROM deep_sky_aliases AS a WHERE a.object_id = o.id AND a.alias = o.designation AND a.kind = 'designation');"
    hygngc_expect "$database" "the disputed alias" "M102|NGC5457" "SELECT group_concat(alias || '|' || object_id) FROM deep_sky_aliases WHERE kind = 'disputed';"
    hygngc_expect "$database" "deep-sky tombstones that are live or unsupported" 0 \
        "SELECT count(*) FROM deep_sky_tombstones AS t
         WHERE t.id IN (SELECT id FROM deep_sky_objects) OR trim(t.designation) = '' OR
               t.reason NOT IN ('nonexistent', 'duplicate-unresolved', 'duplicate-conflict') OR
               (t.reason = 'duplicate-conflict') != (t.candidates IS NOT NULL);"
    hygngc_expect "$database" "outline totals (objects, sets, rings, points)" \
        "$HYGNGC_OUTLINE_OBJECT_COUNT,$HYGNGC_OUTLINE_SET_COUNT,$HYGNGC_OUTLINE_RING_COUNT,$HYGNGC_OUTLINE_POINT_COUNT" \
        "SELECT count(DISTINCT object_id) || ',' || (SELECT count(*) FROM (SELECT DISTINCT object_id, level FROM deep_sky_outline_points)) || ',' ||
                (SELECT count(*) FROM (SELECT DISTINCT object_id, level, ring FROM deep_sky_outline_points)) || ',' || count(*)
         FROM deep_sky_outline_points;"
    hygngc_expect "$database" "outline objects per level" "$HYGNGC_OUTLINE_LEVEL_COUNTS" \
        "SELECT group_concat(level || '=' || n, ',' ORDER BY level) FROM (SELECT level, count(DISTINCT object_id) AS n FROM deep_sky_outline_points GROUP BY level);"
    hygngc_expect "$database" "outline points that are orphaned or out of range" 0 \
        "SELECT count(*) FROM deep_sky_outline_points AS p LEFT JOIN deep_sky_objects AS o ON o.id = p.object_id
         WHERE o.id IS NULL OR p.level NOT IN (1, 2, 3) OR p.ring < 0 OR p.sequence < 0 OR
               typeof(p.right_ascension_degrees) != 'real' OR p.right_ascension_degrees < 0 OR p.right_ascension_degrees >= 360 OR
               typeof(p.declination_degrees) != 'real' OR p.declination_degrees < -90 OR p.declination_degrees > 90;"
    hygngc_expect "$database" "outline rings that are short, open, gapped, or repeat a point" 0 \
        "SELECT count(*) FROM (
           SELECT object_id, level, ring, count(*) AS n, max(sequence) AS last,
                  sum(right_ascension_degrees = previous_ra AND declination_degrees = previous_dec) AS repeats,
                  max(CASE WHEN sequence = 0 THEN right_ascension_degrees END) AS first_ra,
                  max(CASE WHEN sequence = 0 THEN declination_degrees END) AS first_dec,
                  max(CASE WHEN is_last THEN right_ascension_degrees END) AS last_ra,
                  max(CASE WHEN is_last THEN declination_degrees END) AS last_dec
           FROM (SELECT p.*, lag(right_ascension_degrees) OVER w AS previous_ra, lag(declination_degrees) OVER w AS previous_dec,
                        lead(sequence) OVER w IS NULL AS is_last
                 FROM deep_sky_outline_points AS p WINDOW w AS (PARTITION BY object_id, level, ring ORDER BY sequence))
           GROUP BY object_id, level, ring)
         WHERE n < 4 OR last != n - 1 OR repeats != 0 OR first_ra != last_ra OR first_dec != last_dec;"
    hygngc_expect "$database" "outline sets whose rings are not numbered from zero" 0 \
        "SELECT count(*) FROM (SELECT object_id, level, count(DISTINCT ring) AS n, max(ring) AS last FROM deep_sky_outline_points GROUP BY object_id, level)
         WHERE last != n - 1;"
}

hygngc_validate_database_structure() {
    local database="$1"
    local result
    local table
    local expected

    result="$(hyg_sqlite_scalar "$database" "SELECT group_concat(name || ':' || type, ',') FROM (SELECT name, type FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name);")"
    [[ "$result" == "celestial_objects_magnitude_id:index,catalog_metadata:table,celestial_object_aliases:table,celestial_objects:table,deep_sky_aliases:table,deep_sky_objects:table,deep_sky_outline_points:table,deep_sky_tombstones:table" ]] || \
        hyg_fail "catalog database contains an unexpected table or index set"
    result="$(hyg_sqlite_scalar "$database" "SELECT group_concat(name || ':' || type || ':' || ncol || ':' || wr || ':' || strict, ',') FROM (SELECT name, type, ncol, wr, strict FROM pragma_table_list WHERE schema = 'main' AND name NOT LIKE 'sqlite_%' ORDER BY name);")"
    [[ "$result" == "catalog_metadata:table:2:1:0,celestial_object_aliases:table:3:1:0,celestial_objects:table:17:1:0,deep_sky_aliases:table:3:1:0,deep_sky_objects:table:18:1:0,deep_sky_outline_points:table:6:1:0,deep_sky_tombstones:table:4:1:0" ]] || \
        hyg_fail "catalog database tables have incompatible options"
    for table in catalog_metadata celestial_objects celestial_object_aliases deep_sky_objects deep_sky_aliases deep_sky_tombstones deep_sky_outline_points; do
        case "$table" in
            catalog_metadata) expected="key:TEXT:1:-:1:0,value:TEXT:1:-:0:0" ;;
            celestial_objects) expected="id:TEXT:1:-:1:0,display_name:TEXT:1:-:0:0,right_ascension_hours:REAL:1:-:0:0,declination_degrees:REAL:1:-:0:0,magnitude:REAL:1:-:0:0,color_index:REAL:0:-:0:0,hipparcos_id:TEXT:0:-:0:0,proper_motion_ra_cos_dec_mas_per_year:REAL:0:-:0:0,proper_motion_dec_mas_per_year:REAL:0:-:0:0,proper_name:TEXT:0:-:0:0,bayer:TEXT:0:-:0:0,flamsteed:TEXT:0:-:0:0,constellation:TEXT:0:-:0:0,bayer_flamsteed:TEXT:0:-:0:0,henry_draper_id:TEXT:0:-:0:0,harvard_revised_id:TEXT:0:-:0:0,gliese_id:TEXT:0:-:0:0" ;;
            celestial_object_aliases|deep_sky_aliases) expected="alias:TEXT:1:-:1:0,object_id:TEXT:1:-:2:0,kind:TEXT:1:-:0:0" ;;
            deep_sky_objects) expected="id:TEXT:1:-:1:0,designation:TEXT:1:-:0:0,display_name:TEXT:1:-:0:0,object_type:TEXT:1:-:0:0,right_ascension_hours:REAL:1:-:0:0,declination_degrees:REAL:1:-:0:0,constellation:TEXT:1:-:0:0,major_axis_arcminutes:REAL:0:-:0:0,minor_axis_arcminutes:REAL:0:-:0:0,position_angle_degrees:REAL:0:-:0:0,b_magnitude:REAL:0:-:0:0,v_magnitude:REAL:0:-:0:0,surface_brightness:REAL:0:-:0:0,hubble_type:TEXT:0:-:0:0,messier_number:INTEGER:0:-:0:0,caldwell_number:INTEGER:0:-:0:0,hipparcos_id:TEXT:0:-:0:0,common_name:TEXT:0:-:0:0" ;;
            deep_sky_tombstones) expected="id:TEXT:1:-:1:0,designation:TEXT:1:-:0:0,reason:TEXT:1:-:0:0,candidates:TEXT:0:-:0:0" ;;
            deep_sky_outline_points) expected="object_id:TEXT:1:-:1:0,level:INTEGER:1:-:2:0,ring:INTEGER:1:-:3:0,sequence:INTEGER:1:-:4:0,right_ascension_degrees:REAL:1:-:0:0,declination_degrees:REAL:1:-:0:0" ;;
        esac
        result="$(hyg_sqlite_scalar "$database" "SELECT group_concat(name || ':' || type || ':' || \"notnull\" || ':' || coalesce(dflt_value, '-') || ':' || pk || ':' || hidden, ',') FROM (SELECT name, type, \"notnull\", dflt_value, pk, hidden FROM pragma_table_xinfo('$table') ORDER BY cid);")"
        [[ "$result" == "$expected" ]] || hyg_fail "$table has an incompatible schema"
    done
    result="$(hyg_sqlite_scalar "$database" "SELECT group_concat(name || ':' || \"unique\" || ':' || origin || ':' || partial, ',') FROM (SELECT name, \"unique\", origin, partial FROM pragma_index_list('celestial_objects') WHERE origin != 'pk' ORDER BY name);")"
    [[ "$result" == "celestial_objects_magnitude_id:0:c:0" ]] || hyg_fail "catalog database contains an unexpected secondary index"
    result="$(hyg_sqlite_scalar "$database" "SELECT group_concat(name || ':' || desc || ':' || coll || ':' || key, ',') FROM (SELECT name, desc, coll, key FROM pragma_index_xinfo('celestial_objects_magnitude_id') ORDER BY seqno);")"
    [[ "$result" == "magnitude:0:BINARY:1,id:0:BINARY:1" ]] || hyg_fail "catalog ordering index is missing or incompatible"
    for table in catalog_metadata celestial_object_aliases deep_sky_objects deep_sky_aliases deep_sky_tombstones deep_sky_outline_points; do
        result="$(hyg_sqlite_scalar "$database" "SELECT count(*) FROM pragma_index_list('$table') WHERE origin != 'pk';")"
        [[ "$result" == 0 ]] || hyg_fail "$table contains an unexpected secondary index"
    done
}

# Validates a finished composed database: the pinned bytes, then everything the bytes are claimed to contain.
hygngc_validate_database() {
    local database="$1"
    local result
    local suffix

    hyg_verify_file "$database" "$HYGNGC_DATABASE_LENGTH" "$HYGNGC_DATABASE_SHA256" "catalog database"
    for suffix in -journal -wal -shm; do
        [[ ! -e "$database$suffix" && ! -L "$database$suffix" ]] || hyg_fail "catalog database has an unexpected SQLite sidecar: $database$suffix"
    done
    result="$(hyg_sqlite_scalar "$database" 'PRAGMA integrity_check;')"
    [[ "$result" == "ok" ]] || hyg_fail "catalog database integrity_check failed: $result"
    result="$(hyg_sqlite_scalar "$database" 'PRAGMA user_version;')"
    [[ "$result" == "$HYGNGC_SCHEMA_VERSION" ]] || hyg_fail "catalog database user_version is $result, expected $HYGNGC_SCHEMA_VERSION"
    hygngc_validate_database_structure "$database"
    hyg44_validate_database_invariants "$database"
    hygngc_validate_deep_sky_content "$database"
    for suffix in -journal -wal -shm; do
        [[ ! -e "$database$suffix" && ! -L "$database$suffix" ]] || hyg_fail "read-only validation created an SQLite sidecar: $database$suffix"
    done
}

# Prints its arguments as a JSON string list; hygngc_manifest_document has already rejected any character that
# would need escaping.
hygngc_json_strings() {
    local value
    local separator=""
    for value in "$@"; do
        printf '%s"%s"' "$separator" "$value"
        separator=", "
    done
}

# Prints the canonical manifest. Every value is a pinned constant, so a bundle manifest is valid only if it is
# byte-identical to this document.
hygngc_manifest_document() {
    local entry
    local id
    local count
    local subjects
    local separator=""
    local -a subject_list

    cat <<JSON
{
  "manifestVersion": $HYGNGC_MANIFEST_VERSION,
  "package": {
    "kind": "production",
    "version": "$HYGNGC_PACKAGE_VERSION"
  },
  "catalog": {
    "id": "$HYGNGC_CATALOG_ID",
    "name": "$HYGNGC_CATALOG_NAME",
    "version": "$HYGNGC_CATALOG_VERSION"
  },
  "source": {
    "projectUrl": "$HYGNGC_SOURCE_PROJECT_URL",
    "downloadUrl": "$HYGNGC_SOURCE_URL",
    "oid": "$HYGNGC_SOURCE_OID",
    "compressed": {
      "sha256": "$HYGNGC_COMPRESSED_SHA256",
      "length": $HYGNGC_COMPRESSED_LENGTH
    },
    "decompressed": {
      "sha256": "$HYGNGC_DECOMPRESSED_SHA256",
      "length": $HYGNGC_DECOMPRESSED_LENGTH
    }
  },
  "schemaVersion": "$HYGNGC_SCHEMA_VERSION",
  "preprocessingVersion": "$HYGNGC_PREPROCESSING_VERSION",
  "serializer": {
    "name": "sqlite3",
    "version": "$HYG_REQUIRED_SQLITE_VERSION"
  },
  "database": {
    "relativePath": "$HYGNGC_DATABASE_FILE",
    "sha256": "$HYGNGC_DATABASE_SHA256",
    "length": $HYGNGC_DATABASE_LENGTH,
    "rowCount": $HYGNGC_EXPECTED_ROWS,
    "solCount": 0,
    "requiredColumn": "hipparcos_id",
    "deepSky": {
      "objectCount": $HYGNGC_DEEP_SKY_OBJECT_COUNT,
      "aliasCount": $HYGNGC_DEEP_SKY_ALIAS_COUNT,
      "tombstoneCount": $HYGNGC_DEEP_SKY_TOMBSTONE_COUNT,
      "outlineObjectCount": $HYGNGC_OUTLINE_OBJECT_COUNT,
      "outlineSetCount": $HYGNGC_OUTLINE_SET_COUNT,
      "outlineRingCount": $HYGNGC_OUTLINE_RING_COUNT,
      "outlinePointCount": $HYGNGC_OUTLINE_POINT_COUNT
    }
  },
  "license": {
    "identifier": "$HYGNGC_LICENSE_IDENTIFIER",
    "url": "$HYGNGC_LICENSE_URL",
    "file": {
      "relativePath": "$HYGNGC_LICENSE_FILE",
      "sha256": "$HYGNGC_LICENSE_SHA256",
      "length": $HYGNGC_LICENSE_LENGTH
    },
    "attribution": {
      "relativePath": "$HYGNGC_ATTRIBUTION_FILE",
      "sha256": "$HYGNGC_ATTRIBUTION_SHA256",
      "length": $HYGNGC_ATTRIBUTION_LENGTH
    }
  },
  "topology": {
    "identity": "$HYG_TOPOLOGY_VERSION",
    "sha256": "$HYG_TOPOLOGY_SHA256",
    "constellationCount": 88,
    "segmentCount": 743
  },
  "components": [
    {
      "role": "stars",
      "name": "HYG",
      "catalogId": "$HYG44_CATALOG_ID",
      "packageVersion": "$HYG44_PACKAGE_VERSION",
      "database": {
        "sha256": "$HYG44_DATABASE_SHA256",
        "length": $HYG44_DATABASE_LENGTH,
        "rowCount": $HYG44_EXPECTED_ROWS
      }
    },
    {
      "role": "deep-sky",
      "name": "$OPENNGC_NAME",
      "version": "$OPENNGC_VERSION",
      "commit": "$OPENNGC_COMMIT",
      "projectUrl": "$OPENNGC_PROJECT_URL",
      "license": "$OPENNGC_LICENSE_IDENTIFIER",
      "inputs": [
        {
          "relativePath": "$OPENNGC_NGC_FILE",
          "sha256": "$OPENNGC_NGC_SHA256",
          "length": $OPENNGC_NGC_LENGTH,
          "rowCount": $OPENNGC_NGC_ROWS
        },
        {
          "relativePath": "$OPENNGC_ADDENDUM_FILE",
          "sha256": "$OPENNGC_ADDENDUM_SHA256",
          "length": $OPENNGC_ADDENDUM_LENGTH,
          "rowCount": $OPENNGC_ADDENDUM_ROWS
        }
      ],
      "outlines": {
        "relativePath": "$OPENNGC_OUTLINE_DIRECTORY",
        "fileCount": $OPENNGC_OUTLINE_FILE_COUNT,
        "listingSha256": "$OPENNGC_OUTLINE_LISTING_SHA256",
        "pointCount": $OPENNGC_OUTLINE_POINT_COUNT
      }
    }
  ],
  "transformations": [
JSON
    for entry in "${HYGNGC_TRANSFORMATIONS[@]}"; do
        [[ "$entry" =~ ^[a-z0-9-]+\|(0|[1-9][0-9]*)\|[^\"\\]*$ ]] || hyg_fail "transformation entry cannot be written to the manifest: $entry"
        IFS='|' read -r id count subjects <<< "$entry"
        subject_list=()
        if [[ -n "$subjects" ]]; then
            IFS=';' read -r -a subject_list <<< "$subjects"
        fi
        printf '%s    {\n      "id": "%s",\n      "count": %s,\n      "subjects": [%s]\n    }' \
            "$separator" "$id" "$count" "$(hygngc_json_strings "${subject_list[@]}")"
        separator=$',\n'
    done
    printf '\n  ]\n}\n'
}

hygngc_validate_bundle() {
    local bundle="$1"
    local manifest="$bundle/$HYG_MANIFEST_FILE"
    local -a entries

    HYG_VALIDATION_FAILED=0
    [[ -d "$bundle" && ! -L "$bundle" ]] || hyg_fail "bundle is missing or is not a directory: $bundle"
    shopt -s nullglob dotglob
    entries=("$bundle"/*)
    shopt -u nullglob dotglob
    [[ ${#entries[@]} -eq 4 ]] || hyg_fail "bundle must contain exactly the manifest and three retained payload files"
    hyg_validate_manifest_document "$manifest"
    cmp -s -- "$manifest" <(hygngc_manifest_document) || hyg_fail "bundle manifest is not the canonical $HYGNGC_PACKAGE_VERSION manifest"
    hyg_verify_file "$bundle/$HYGNGC_LICENSE_FILE" "$HYGNGC_LICENSE_LENGTH" "$HYGNGC_LICENSE_SHA256" "composed catalog license"
    hyg_verify_file "$bundle/$HYGNGC_ATTRIBUTION_FILE" "$HYGNGC_ATTRIBUTION_LENGTH" "$HYGNGC_ATTRIBUTION_SHA256" "composed catalog attribution"
    hygngc_validate_database "$bundle/$HYGNGC_DATABASE_FILE"
    [[ "$HYG_VALIDATION_FAILED" == 0 ]]
}
