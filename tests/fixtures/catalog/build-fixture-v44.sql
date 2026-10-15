.bail on
PRAGMA page_size = 4096;
PRAGMA journal_mode = DELETE;
PRAGMA synchronous = OFF;
PRAGMA user_version = 3;
CREATE TEMP TABLE source_objects (
  id TEXT, display_name TEXT, right_ascension_hours TEXT, declination_degrees TEXT, magnitude TEXT,
  color_index TEXT, hipparcos_id TEXT, proper_motion_ra_cos_dec_mas_per_year TEXT,
  proper_motion_dec_mas_per_year TEXT, proper_name TEXT, bayer TEXT, flamsteed TEXT, constellation TEXT,
  bayer_flamsteed TEXT, henry_draper_id TEXT, harvard_revised_id TEXT, gliese_id TEXT
);
CREATE TEMP TABLE source_aliases (alias TEXT, object_id TEXT, kind TEXT);
.mode csv
.import --skip 1 hyg-v44-subset-objects.csv source_objects
.import --skip 1 hyg-v44-subset-aliases.csv source_aliases
CREATE TABLE catalog_metadata (key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL) WITHOUT ROWID;
CREATE TABLE celestial_objects (
  id TEXT PRIMARY KEY NOT NULL,
  display_name TEXT NOT NULL,
  right_ascension_hours REAL NOT NULL,
  declination_degrees REAL NOT NULL,
  magnitude REAL NOT NULL,
  color_index REAL,
  hipparcos_id TEXT,
  proper_motion_ra_cos_dec_mas_per_year REAL,
  proper_motion_dec_mas_per_year REAL,
  proper_name TEXT,
  bayer TEXT,
  flamsteed TEXT,
  constellation TEXT,
  bayer_flamsteed TEXT,
  henry_draper_id TEXT,
  harvard_revised_id TEXT,
  gliese_id TEXT
) WITHOUT ROWID;
CREATE TABLE celestial_object_aliases (
  alias TEXT NOT NULL,
  object_id TEXT NOT NULL,
  kind TEXT NOT NULL,
  PRIMARY KEY (alias, object_id)
) WITHOUT ROWID;
INSERT INTO catalog_metadata VALUES
  ('catalog_version', '4.4-fixture.1'),
  ('color_index', 'B-V'),
  ('coordinate_epoch', 'J2000.0'),
  ('coordinate_equinox', 'J2000.0'),
  ('coordinate_frame', 'equatorial-j2000-icrs-aligned'),
  ('license', 'CC BY-SA 4.0'),
  ('magnitude_band', 'V-heterogeneous'),
  ('name', 'HYG 4.4 subset test fixture'),
  ('preprocessing_version', '4'),
  ('proper_motion_convention', 'mu-alpha-cos-delta-and-mu-delta-mas-per-year'),
  ('proper_motion_unknown_rule', 'null-when-upstream-pair-is-zero-or-reaches-9999.99'),
  ('schema_version', '3'),
  ('source_commit', '53e3df311869e813ace5f1ad2ec4ce909f13256c'),
  ('source_url', 'https://codeberg.org/astronexus/hyg');
INSERT INTO celestial_objects
SELECT id, display_name, CAST(right_ascension_hours AS REAL), CAST(declination_degrees AS REAL),
       CAST(magnitude AS REAL), CAST(NULLIF(color_index, '') AS REAL), NULLIF(hipparcos_id, ''),
       CAST(NULLIF(proper_motion_ra_cos_dec_mas_per_year, '') AS REAL),
       CAST(NULLIF(proper_motion_dec_mas_per_year, '') AS REAL),
       NULLIF(proper_name, ''), NULLIF(bayer, ''), NULLIF(flamsteed, ''), NULLIF(constellation, ''),
       NULLIF(bayer_flamsteed, ''), NULLIF(henry_draper_id, ''), NULLIF(harvard_revised_id, ''), NULLIF(gliese_id, '')
FROM source_objects ORDER BY id;
INSERT INTO celestial_object_aliases SELECT alias, object_id, kind FROM source_aliases ORDER BY alias, object_id;
DROP TABLE source_objects;
DROP TABLE source_aliases;
CREATE INDEX celestial_objects_magnitude_id ON celestial_objects (magnitude, id COLLATE BINARY);
VACUUM;
