-- Recruitee board tokens — table + seed slugs found via public search.
-- Safe to re-run: CREATE IF NOT EXISTS + ON CONFLICT DO NOTHING.
--
-- HONEST CAVEATS
--   • Recruitee skews European (parent company Tellent is Dutch). The handler
--     filters every job to US-only via UsLocationHelper, so non-US tenants
--     contribute few or zero raw_jobs. That's fine — the table exists,
--     no more "relation does not exist" log spam, and any US-located jobs
--     posted by these tenants will flow through normally.
--   • Add or remove slugs as you go — handler reads the table on every run.

CREATE TABLE IF NOT EXISTS api.recruitee_board_tokens (
    id           SERIAL PRIMARY KEY,
    company_name VARCHAR(255) NOT NULL,
    board_token  VARCHAR(255) NOT NULL UNIQUE,
    industry     VARCHAR(255),
    status       VARCHAR(50)  NOT NULL DEFAULT 'UNKNOWN',
    http_code    SMALLINT,
    job_count    INTEGER,
    api_url      VARCHAR(500),
    board_url    VARCHAR(500),
    created_at   TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at   TIMESTAMP NOT NULL DEFAULT NOW()
);

-- ── Seed slugs (verified from public web search) ─────────────────────────
INSERT INTO api.recruitee_board_tokens
    (company_name, board_token, api_url, board_url, status, created_at, updated_at)
SELECT
    INITCAP(REPLACE(slug, '-', ' '))                  AS company_name,
    slug                                              AS board_token,
    'https://' || slug || '.recruitee.com/api/offers/' AS api_url,
    'https://' || slug || '.recruitee.com/o'           AS board_url,
    'UNKNOWN'                                         AS status,
    NOW(), NOW()
FROM jsonb_array_elements_text(
    $jsonbody$
[
  "nextbigthingag2",
  "rocketpeople",
  "mainstream",
  "orbem",
  "ideas2ittechnologies",
  "surfly",
  "sunroom",
  "tether",
  "huaweicanada",
  "syntho",
  "jobs",
  "vacancies"
]
    $jsonbody$::jsonb
) AS t(slug)
WHERE slug IS NOT NULL AND slug <> ''
ON CONFLICT (board_token) DO NOTHING;

-- Verify:
-- SELECT COUNT(*) FROM api.recruitee_board_tokens;
