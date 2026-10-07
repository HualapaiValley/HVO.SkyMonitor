.bail on
PRAGMA page_size = 4096;
PRAGMA journal_mode = DELETE;
PRAGMA synchronous = OFF;
PRAGMA user_version = 4;
CREATE TEMP TABLE source_objects (
  id TEXT, display_name TEXT, right_ascension_hours TEXT, declination_degrees TEXT, magnitude TEXT,
  color_index TEXT, hipparcos_id TEXT, proper_motion_ra_cos_dec_mas_per_year TEXT,
  proper_motion_dec_mas_per_year TEXT, proper_name TEXT, bayer TEXT, flamsteed TEXT, constellation TEXT,
  bayer_flamsteed TEXT, henry_draper_id TEXT, harvard_revised_id TEXT, gliese_id TEXT
);
CREATE TEMP TABLE source_aliases (alias TEXT, object_id TEXT, kind TEXT);
CREATE TEMP TABLE source_deep_sky_objects (
  id TEXT, designation TEXT, display_name TEXT, object_type TEXT, right_ascension_hours TEXT,
  declination_degrees TEXT, constellation TEXT, major_axis_arcminutes TEXT, minor_axis_arcminutes TEXT,
  position_angle_degrees TEXT, b_magnitude TEXT, v_magnitude TEXT, surface_brightness TEXT, hubble_type TEXT,
  messier_number TEXT, caldwell_number TEXT, hipparcos_id TEXT, common_name TEXT
);
CREATE TEMP TABLE source_deep_sky_aliases (alias TEXT, object_id TEXT, kind TEXT);
CREATE TEMP TABLE source_deep_sky_tombstones (id TEXT, designation TEXT, reason TEXT, candidates TEXT);
CREATE TEMP TABLE source_deep_sky_outline_points (
  object_id TEXT, level TEXT, ring TEXT, sequence TEXT, right_ascension_degrees TEXT, declination_degrees TEXT
);
.mode csv
.import --skip 1 hyg-v44-openngc-subset-star-objects.csv source_objects
.import --skip 1 hyg-v44-openngc-subset-star-aliases.csv source_aliases
.import --skip 1 hyg-v44-openngc-subset-deep-sky-objects.csv source_deep_sky_objects
.import --skip 1 hyg-v44-openngc-subset-deep-sky-aliases.csv source_deep_sky_aliases
.import --skip 1 hyg-v44-openngc-subset-deep-sky-tombstones.csv source_deep_sky_tombstones
.import --skip 1 hyg-v44-openngc-subset-deep-sky-outline-points.csv source_deep_sky_outline_points
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
INSERT INTO catalog_metadata VALUES
  ('catalog_version', '4.4+openngc-fixture.1'),
  ('color_index', 'B-V'),
  ('coordinate_epoch', 'J2000.0'),
  ('coordinate_equinox', 'J2000.0'),
  ('coordinate_frame', 'equatorial-j2000-icrs-aligned'),
  ('deep_sky_axis_unit', 'arcminute'),
  ('deep_sky_coordinate_epoch', 'J2000.0'),
  ('deep_sky_coordinate_frame', 'equatorial-j2000-icrs-aligned'),
  ('deep_sky_license', 'CC BY-SA 4.0'),
  ('deep_sky_outline_level_convention', '1-widest-2-standard-3-narrowest'),
  ('deep_sky_position_angle_convention', 'degrees-north-through-east-0-inclusive-to-180-exclusive'),
  ('deep_sky_source_commit', '36cb178a0f69dba8bfc03a99c10512831edf1c6b'),
  ('deep_sky_source_name', 'OpenNGC'),
  ('deep_sky_source_url', 'https://github.com/mattiaverga/OpenNGC'),
  ('deep_sky_source_version', 'v20260501'),
  ('deep_sky_surface_brightness_unit', 'b-mag-per-square-arcsecond-within-25-mag-isophote'),
  ('license', 'CC BY-SA 4.0'),
  ('magnitude_band', 'V-heterogeneous'),
  ('name', 'HYG 4.4 + OpenNGC subset test fixture'),
  ('preprocessing_version', '5'),
  ('proper_motion_convention', 'mu-alpha-cos-delta-and-mu-delta-mas-per-year'),
  ('proper_motion_unknown_rule', 'null-when-upstream-pair-is-zero-or-reaches-9999.99'),
  ('schema_version', '4'),
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
INSERT INTO deep_sky_objects
SELECT id, designation, display_name, object_type, CAST(right_ascension_hours AS REAL),
       CAST(declination_degrees AS REAL), constellation, CAST(NULLIF(major_axis_arcminutes, '') AS REAL),
       CAST(NULLIF(minor_axis_arcminutes, '') AS REAL), CAST(NULLIF(position_angle_degrees, '') AS REAL),
       CAST(NULLIF(b_magnitude, '') AS REAL), CAST(NULLIF(v_magnitude, '') AS REAL),
       CAST(NULLIF(surface_brightness, '') AS REAL), NULLIF(hubble_type, ''),
       CAST(NULLIF(messier_number, '') AS INTEGER), CAST(NULLIF(caldwell_number, '') AS INTEGER),
       NULLIF(hipparcos_id, ''), NULLIF(common_name, '')
FROM source_deep_sky_objects ORDER BY id;
INSERT INTO deep_sky_aliases SELECT alias, object_id, kind FROM source_deep_sky_aliases ORDER BY alias, object_id;
INSERT INTO deep_sky_tombstones
SELECT id, designation, reason, NULLIF(candidates, '') FROM source_deep_sky_tombstones ORDER BY id;
INSERT INTO deep_sky_outline_points
SELECT object_id, CAST(level AS INTEGER), CAST(ring AS INTEGER), CAST(sequence AS INTEGER),
       CAST(right_ascension_degrees AS REAL), CAST(declination_degrees AS REAL)
FROM source_deep_sky_outline_points
ORDER BY object_id, CAST(level AS INTEGER), CAST(ring AS INTEGER), CAST(sequence AS INTEGER);
DROP TABLE source_objects;
DROP TABLE source_aliases;
DROP TABLE source_deep_sky_objects;
DROP TABLE source_deep_sky_aliases;
DROP TABLE source_deep_sky_tombstones;
DROP TABLE source_deep_sky_outline_points;
CREATE INDEX celestial_objects_magnitude_id ON celestial_objects (magnitude, id COLLATE BINARY);
VACUUM;
