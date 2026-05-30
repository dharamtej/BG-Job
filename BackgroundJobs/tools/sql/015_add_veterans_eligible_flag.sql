-- Migration 015: Add is_veterans_eligible flag to api.raw_jobs
-- USAJobs HiringPath contains "veterans" for veteran-preference roles.
-- All other sources: null (unknown) unless description mentions veteran preference.

ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS is_veterans_eligible BOOLEAN DEFAULT FALSE;

CREATE INDEX IF NOT EXISTS ix_raw_jobs_is_veterans
    ON api.raw_jobs (is_veterans_eligible)
    WHERE is_veterans_eligible = TRUE;
