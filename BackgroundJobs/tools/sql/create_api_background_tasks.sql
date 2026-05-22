-- Run once on the Career Panda PostgreSQL database (e.g. carrer_panda_test).
-- Creates storage for API background/async tasks (separate from cp.jobs listings).

CREATE TABLE IF NOT EXISTS cp.api_background_tasks (
    id              varchar(64) PRIMARY KEY,
    name            varchar(256) NOT NULL DEFAULT '',
    description     text,
    job_type        varchar(128) NOT NULL DEFAULT 'Default',
    status          varchar(32) NOT NULL DEFAULT 'Pending',
    progress_percent integer NOT NULL DEFAULT 0,
    started_at      timestamptz,
    completed_at    timestamptz,
    result_payload  text,
    error_message   text,
    created_by_id   varchar(64),
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_api_background_tasks_created_at
    ON cp.api_background_tasks (created_at DESC);
