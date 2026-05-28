-- Add is_contract_to_hire column to api.raw_jobs (C2H detection).
ALTER TABLE api.raw_jobs ADD COLUMN IF NOT EXISTS is_contract_to_hire BOOLEAN;
CREATE INDEX IF NOT EXISTS ix_raw_jobs_is_contract_to_hire ON api.raw_jobs (is_contract_to_hire) WHERE is_contract_to_hire = TRUE;
