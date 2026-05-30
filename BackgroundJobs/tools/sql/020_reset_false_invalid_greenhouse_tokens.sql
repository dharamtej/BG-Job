-- ============================================================
-- CareerPanda Migration 020
-- Resets Greenhouse tokens that were incorrectly marked INVALID
-- due to Bug 1: 200 response with no 'jobs' property was treated
-- as a definitive board failure instead of a transient error.
--
-- Safe to run multiple times (idempotent WHERE clause).
-- After running, trigger GreenhouseJobs from the dashboard to
-- re-validate all reset tokens.
-- ============================================================

-- Reset ALL INVALID greenhouse tokens back to UNKNOWN so they
-- get re-evaluated on the next run.
-- The fixed handler now only marks INVALID on definitive 404s
-- from the /v1/boards/{token} endpoint.
UPDATE api.greenhouse_board_tokens
SET    status     = 'UNKNOWN',
       updated_at = NOW()
WHERE  status = 'INVALID';

-- Report how many were reset
SELECT COUNT(*) AS tokens_reset
FROM   api.greenhouse_board_tokens
WHERE  status = 'UNKNOWN'
  AND  updated_at >= NOW() - INTERVAL '5 seconds';
