-- ============================================================
-- CareerPanda Migration 013
-- Adds dedicated visa classification flags to api.raw_jobs
-- Covers: OPT/CPT, TN Visa, E-3 Visa, J-1 Visa, Green Card
-- Run once on your careerpanda PostgreSQL database.
-- After running, trigger ReclassifyExistingJobs to backfill.
-- ============================================================

ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS is_opt_cpt    BOOLEAN DEFAULT FALSE;
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS is_tn_visa    BOOLEAN DEFAULT FALSE;
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS is_e3_visa    BOOLEAN DEFAULT FALSE;
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS is_j1_visa    BOOLEAN DEFAULT FALSE;
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS is_green_card BOOLEAN DEFAULT FALSE;

CREATE INDEX IF NOT EXISTS ix_raw_jobs_is_opt_cpt    ON api.raw_jobs (is_opt_cpt)    WHERE is_opt_cpt    = TRUE;
CREATE INDEX IF NOT EXISTS ix_raw_jobs_is_tn_visa    ON api.raw_jobs (is_tn_visa)    WHERE is_tn_visa    = TRUE;
CREATE INDEX IF NOT EXISTS ix_raw_jobs_is_e3_visa    ON api.raw_jobs (is_e3_visa)    WHERE is_e3_visa    = TRUE;
CREATE INDEX IF NOT EXISTS ix_raw_jobs_is_j1_visa    ON api.raw_jobs (is_j1_visa)    WHERE is_j1_visa    = TRUE;
CREATE INDEX IF NOT EXISTS ix_raw_jobs_is_green_card ON api.raw_jobs (is_green_card) WHERE is_green_card = TRUE;
