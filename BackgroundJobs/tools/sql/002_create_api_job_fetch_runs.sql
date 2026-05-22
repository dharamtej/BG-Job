-- ============================================================
-- CareerPanda Migration 002
-- Creates api.job_fetch_runs — one row per background fetch trigger.
-- Tracks statistics: inserted, updated, skipped, errors, timing.
-- ============================================================

CREATE TABLE IF NOT EXISTS api.job_fetch_runs (
    id                  VARCHAR(64)   PRIMARY KEY,
    background_task_id  VARCHAR(64)   NOT NULL,
    job_category        VARCHAR(100)  NOT NULL,
    api_source          VARCHAR(100),
    status              VARCHAR(32)   NOT NULL DEFAULT 'Running',
    started_at          TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
    completed_at        TIMESTAMPTZ,
    duration_seconds    INTEGER,
    total_fetched       INTEGER       NOT NULL DEFAULT 0,
    total_inserted      INTEGER       NOT NULL DEFAULT 0,
    total_updated       INTEGER       NOT NULL DEFAULT 0,
    total_skipped       INTEGER       NOT NULL DEFAULT 0,
    total_errors        INTEGER       NOT NULL DEFAULT 0,
    pages_fetched       INTEGER       NOT NULL DEFAULT 0,
    hours_back          INTEGER,
    max_pages           INTEGER,
    search_query        TEXT,
    location_filter     VARCHAR(256),
    error_message       TEXT,
    created_by_id       VARCHAR(64),
    created_at          TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ   NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_job_fetch_runs_task_id    ON api.job_fetch_runs (background_task_id);
CREATE INDEX IF NOT EXISTS ix_job_fetch_runs_category   ON api.job_fetch_runs (job_category);
CREATE INDEX IF NOT EXISTS ix_job_fetch_runs_started_at ON api.job_fetch_runs (started_at DESC);
CREATE INDEX IF NOT EXISTS ix_job_fetch_runs_status     ON api.job_fetch_runs (status);
