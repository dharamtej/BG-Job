-- Reset every ATS token currently marked INVALID back to UNKNOWN.
-- Run this ONCE after deploying the handler fix that no longer marks 401/403 as
-- permanently INVALID. The next run of each ATS handler will re-check all of
-- these boards. Genuine 404s will flip back to INVALID; valid boards that were
-- previously WAF-blocked (403/401) will flip to VALID or EMPTY this time.
--
-- Safe to re-run.

UPDATE api.greenhouse_board_tokens SET status = 'UNKNOWN', updated_at = NOW() WHERE status = 'INVALID';
UPDATE api.lever_board_tokens      SET status = 'UNKNOWN', updated_at = NOW() WHERE status = 'INVALID';
UPDATE api.ashby_board_tokens      SET status = 'UNKNOWN', updated_at = NOW() WHERE status = 'INVALID';
UPDATE api.workday_board_tokens    SET status = 'UNKNOWN', updated_at = NOW() WHERE status = 'INVALID';
UPDATE api.recruitee_board_tokens  SET status = 'UNKNOWN', updated_at = NOW() WHERE status = 'INVALID';
-- BambooHR and iCIMS are parked, but reset their tables too for consistency:
UPDATE api.bamboohr_board_tokens   SET status = 'UNKNOWN', updated_at = NOW() WHERE status = 'INVALID';
UPDATE api.icims_board_tokens      SET status = 'UNKNOWN', updated_at = NOW() WHERE status = 'INVALID';

-- Verify
-- SELECT 'greenhouse' AS src, status, COUNT(*) FROM api.greenhouse_board_tokens GROUP BY status
-- UNION ALL SELECT 'lever',  status, COUNT(*) FROM api.lever_board_tokens GROUP BY status
-- UNION ALL SELECT 'ashby',  status, COUNT(*) FROM api.ashby_board_tokens GROUP BY status
-- UNION ALL SELECT 'workday',status, COUNT(*) FROM api.workday_board_tokens GROUP BY status
-- UNION ALL SELECT 'recruitee',status,COUNT(*) FROM api.recruitee_board_tokens GROUP BY status;
