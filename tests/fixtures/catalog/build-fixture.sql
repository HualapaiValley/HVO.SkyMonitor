.bail on
PRAGMA page_size = 4096;
PRAGMA journal_mode = DELETE;
PRAGMA synchronous = OFF;
PRAGMA user_version = 2;
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
  ('catalog_version', '4.2-fixture.1'),
  ('license', 'CC BY-SA 4.0'),
  ('name', 'HYG bright-star test fixture'),
  ('preprocessing_version', '3'),
  ('schema_version', '2'),
  ('source_url', 'https://codeberg.org/astronexus/hyg');
.mode csv
.import --skip 1 hyg-v42-bright-stars.csv celestial_objects
CREATE INDEX celestial_objects_magnitude_id ON celestial_objects (magnitude, id COLLATE BINARY);
VACUUM;
