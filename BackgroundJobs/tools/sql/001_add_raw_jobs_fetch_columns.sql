-- ============================================================
-- CareerPanda Migration 001
-- Adds all job-fetch columns to api.raw_jobs
-- Run once on your careerpanda PostgreSQL database.
-- ============================================================

-- Source tracking
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS source              VARCHAR(100);
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS source_id           VARCHAR(512);
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS fetch_run_id        VARCHAR(64);

-- Job metadata
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS hours_back_posted   INTEGER;
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS apply_type          VARCHAR(100);
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS contract_type       VARCHAR(100);
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS work_type           VARCHAR(100);
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS salary_range_text   VARCHAR(256);
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS experience_min      INTEGER;
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS experience_max      INTEGER;

-- Company info
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS company_logo_url    TEXT;
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS company_url         TEXT;
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS company_type        VARCHAR(100);

-- Poster / recruiter info
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS posted_by_name      VARCHAR(256);
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS posted_by_profile_url TEXT;

-- Boolean classification flags
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS is_c2c              BOOLEAN DEFAULT FALSE;
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS is_w2               BOOLEAN DEFAULT FALSE;
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS is_staffing         BOOLEAN DEFAULT FALSE;
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS is_sponsored        BOOLEAN DEFAULT FALSE;
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS is_h1b_sponsored    BOOLEAN DEFAULT FALSE;
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS is_university_job   BOOLEAN DEFAULT FALSE;
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS is_startup_job      BOOLEAN DEFAULT FALSE;
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS is_non_profit_job   BOOLEAN DEFAULT FALSE;
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS is_contract_job     BOOLEAN DEFAULT FALSE;
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS is_freelance_job    BOOLEAN DEFAULT FALSE;
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS is_prime_vendor     BOOLEAN DEFAULT FALSE;

-- Indexes
CREATE INDEX IF NOT EXISTS ix_raw_jobs_source          ON api.raw_jobs (source);
CREATE INDEX IF NOT EXISTS ix_raw_jobs_fetch_run_id    ON api.raw_jobs (fetch_run_id);
CREATE INDEX IF NOT EXISTS ix_raw_jobs_job_link        ON api.raw_jobs (job_link);
CREATE INDEX IF NOT EXISTS ix_raw_jobs_post_date       ON api.raw_jobs (post_date DESC);
CREATE INDEX IF NOT EXISTS ix_raw_jobs_is_h1b          ON api.raw_jobs (is_h1b_sponsored) WHERE is_h1b_sponsored = TRUE;
CREATE INDEX IF NOT EXISTS ix_raw_jobs_is_startup      ON api.raw_jobs (is_startup_job)   WHERE is_startup_job   = TRUE;
CREATE INDEX IF NOT EXISTS ix_raw_jobs_is_university   ON api.raw_jobs (is_university_job) WHERE is_university_job = TRUE;
CREATE INDEX IF NOT EXISTS ix_raw_jobs_is_nonprofit    ON api.raw_jobs (is_non_profit_job) WHERE is_non_profit_job = TRUE;
CREATE INDEX IF NOT EXISTS ix_raw_jobs_is_contract     ON api.raw_jobs (is_contract_job)  WHERE is_contract_job  = TRUE;
CREATE INDEX IF NOT EXISTS ix_raw_jobs_is_prime_vendor ON api.raw_jobs (is_prime_vendor)  WHERE is_prime_vendor  = TRUE;
