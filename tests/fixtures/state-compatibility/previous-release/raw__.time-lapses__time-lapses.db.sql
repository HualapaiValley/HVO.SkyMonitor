CREATE TABLE jobs(
    job_id TEXT PRIMARY KEY, occurrence_json TEXT NOT NULL, window_json TEXT NOT NULL, parent_job_id TEXT,
    preset_json TEXT NOT NULL, report_date TEXT NOT NULL, state TEXT NOT NULL
        CHECK(state IN ('Queued','Working','Produced','NoSources','Unavailable','Failed')),
    updated_ticks INTEGER NOT NULL, reason TEXT, product_id TEXT, plan_json TEXT, plan_sha256 TEXT,
    exclusions_json TEXT, exclusions_sha256 TEXT, revision INTEGER NOT NULL DEFAULT 1 CHECK(revision > 0)
) STRICT;
CREATE TABLE retries(request_id TEXT PRIMARY KEY, job_id TEXT NOT NULL REFERENCES jobs(job_id), expected_revision INTEGER NOT NULL,
    actor TEXT NOT NULL, reason TEXT NOT NULL, previous_state TEXT NOT NULL, previous_reason TEXT, requested_ticks INTEGER NOT NULL) STRICT;
CREATE TABLE publications(product_id TEXT PRIMARY KEY) STRICT;
CREATE TABLE products(
    product_id TEXT PRIMARY KEY, output_sha256 TEXT NOT NULL UNIQUE, job_id TEXT NOT NULL REFERENCES jobs(job_id),
    report_date TEXT NOT NULL, period_sha256 TEXT NOT NULL, window_sha256 TEXT NOT NULL, start_ticks INTEGER NOT NULL, end_ticks INTEGER NOT NULL,
    preset_sha256 TEXT NOT NULL, is_daily INTEGER NOT NULL CHECK(is_daily IN (0,1)), document_json TEXT NOT NULL,
    document_sha256 TEXT NOT NULL, payload_path TEXT NOT NULL UNIQUE, payload_bytes INTEGER NOT NULL CHECK(payload_bytes > 0), created_ticks INTEGER NOT NULL,
    width INTEGER NOT NULL CHECK(width BETWEEN 2 AND 1280), height INTEGER NOT NULL CHECK(height BETWEEN 2 AND 1280),
    has_gaps INTEGER NOT NULL CHECK(has_gaps IN (0,1))
) STRICT;
CREATE INDEX jobs_by_date ON jobs(report_date,updated_ticks);
CREATE INDEX jobs_by_state ON jobs(state,updated_ticks);
CREATE INDEX retries_by_job ON retries(job_id);
CREATE INDEX products_by_date ON products(report_date,created_ticks);
CREATE INDEX products_by_window ON products(period_sha256,start_ticks,end_ticks,preset_sha256,is_daily);
CREATE TRIGGER immutable_products_update BEFORE UPDATE ON products BEGIN SELECT RAISE(ABORT,'Video publications are immutable.'); END;
CREATE TRIGGER immutable_products_delete BEFORE DELETE ON products BEGIN SELECT RAISE(ABORT,'Video publications are immutable.'); END;
CREATE TRIGGER immutable_job_inputs BEFORE UPDATE OF occurrence_json,window_json,preset_json,plan_json,plan_sha256,exclusions_json,exclusions_sha256 ON jobs
WHEN OLD.occurrence_json != NEW.occurrence_json OR OLD.window_json != NEW.window_json OR OLD.preset_json != NEW.preset_json
  OR (OLD.plan_json IS NOT NULL AND (OLD.plan_json IS NOT NEW.plan_json OR OLD.plan_sha256 IS NOT NEW.plan_sha256))
  OR (OLD.exclusions_json IS NOT NULL AND (OLD.exclusions_json IS NOT NEW.exclusions_json OR OLD.exclusions_sha256 IS NOT NEW.exclusions_sha256))
BEGIN SELECT RAISE(ABORT,'Retained video inputs are immutable.'); END;
PRAGMA user_version = 1;
