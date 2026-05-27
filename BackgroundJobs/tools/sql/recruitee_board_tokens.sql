-- Recruitee Board Tokens — per-company subdomain registry
-- API pattern : GET https://{slug}.recruitee.com/api/offers/
-- Board URL   : https://{slug}.recruitee.com/o
--
-- USAGE
-- 1. Run the CREATE TABLE block once.
-- 2. Seed slugs via the JSON-array INSERT block (same pattern as icims_board_tokens.sql).

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

-- ─── Seed from JSON array ─────────────────────────────────────────────────
-- Paste your Recruitee slug array between the $jsonbody$ tags below, then run.
INSERT INTO api.recruitee_board_tokens
    (company_name, board_token, api_url, board_url, status, created_at, updated_at)
SELECT
    INITCAP(REPLACE(slug, '-', ' '))                  AS company_name,
    slug                                              AS board_token,
    'https://' || slug || '.recruitee.com/api/offers/' AS api_url,
    'https://' || slug || '.recruitee.com/o'          AS board_url,
    'UNKNOWN'                                         AS status,
    NOW(), NOW()
FROM jsonb_array_elements_text(
    $jsonbody$
[
  "example-company"
]
    $jsonbody$::jsonb
) AS t(slug)
WHERE slug IS NOT NULL AND slug <> ''
ON CONFLICT (board_token) DO NOTHING;

-- Verify
-- SELECT COUNT(*) AS total FROM api.recruitee_board_tokens;
