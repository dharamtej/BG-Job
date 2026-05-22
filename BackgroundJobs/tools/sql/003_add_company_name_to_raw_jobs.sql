-- ============================================================
-- CareerPanda Migration 003
-- Adds company_name to api.raw_jobs and supporting indexes.
-- Run once on your careerpanda PostgreSQL database.
-- ============================================================

-- Company name (raw value from the external API)
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS company_name VARCHAR(512);

-- Index for fast company-name-based deduplication lookups
CREATE INDEX IF NOT EXISTS ix_raw_jobs_company_name
    ON api.raw_jobs (lower(company_name));

-- Index so we can efficiently find all jobs for a given internal company
CREATE INDEX IF NOT EXISTS ix_raw_jobs_our_company_id
    ON api.raw_jobs (our_company_id)
    WHERE our_company_id <> 0;

-- Index on api.companies for fast case-insensitive name lookup
CREATE INDEX IF NOT EXISTS ix_companies_company_name_lower
    ON api.companies (lower(company_name));
