-- ============================================================
-- CareerPanda Migration 014
-- Adds is_security_clearance_required flag to api.raw_jobs
-- Detected from USAJobs UserArea.Details and description keywords
-- (secret clearance, ts/sci, top secret, public trust, etc.)
-- ============================================================

ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS is_security_clearance_required BOOLEAN DEFAULT FALSE;

CREATE INDEX IF NOT EXISTS ix_raw_jobs_is_clearance
    ON api.raw_jobs (is_security_clearance_required)
    WHERE is_security_clearance_required = TRUE;
