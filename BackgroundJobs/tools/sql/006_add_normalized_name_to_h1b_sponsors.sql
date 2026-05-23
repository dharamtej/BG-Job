-- ============================================================
-- CareerPanda Migration 006
-- Adds normalized_name column to api.h1b_sponsors.
-- Populated by H1BSponsorEnrichment background job via Wikipedia API.
-- ============================================================

ALTER TABLE api.h1b_sponsors
    ADD COLUMN IF NOT EXISTS normalized_name VARCHAR(512),
    ADD COLUMN IF NOT EXISTS enriched_at     TIMESTAMPTZ;

CREATE INDEX IF NOT EXISTS ix_h1b_sponsors_normalized_name
    ON api.h1b_sponsors (normalized_name)
    WHERE normalized_name IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_h1b_sponsors_unenriched
    ON api.h1b_sponsors (total_approvals DESC)
    WHERE normalized_name IS NULL;
