# Reads bamboohr_companies.json from project root and produces
# BackgroundJobs/tools/sql/bamboohr_board_tokens.sql ready for Railway.

$jsonPath = Join-Path $PSScriptRoot '..\..\..\bamboohr_companies.json'
$outPath  = Join-Path $PSScriptRoot 'bamboohr_board_tokens.sql'

if (-not (Test-Path $jsonPath)) {
    Write-Error "Missing $jsonPath — save the BambooHR companies JSON to project root first."
    exit 1
}

$slugs = (Get-Content -Raw $jsonPath | ConvertFrom-Json) |
    ForEach-Object { $_.Trim() } |
    Where-Object   { $_ } |
    Sort-Object -Unique

"Unique slugs: $($slugs.Count)"

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine("-- BambooHR Board Tokens — $($slugs.Count) company slugs")
[void]$sb.AppendLine("-- API pattern : GET https://{slug}.bamboohr.com/careers/list")
[void]$sb.AppendLine("-- Board URL   : https://{slug}.bamboohr.com/careers")
[void]$sb.AppendLine("-- Usage: Run once in Railway PostgreSQL console.")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("CREATE TABLE IF NOT EXISTS api.bamboohr_board_tokens (")
[void]$sb.AppendLine("    id           SERIAL PRIMARY KEY,")
[void]$sb.AppendLine("    company_name VARCHAR(255) NOT NULL,")
[void]$sb.AppendLine("    board_token  VARCHAR(255) NOT NULL UNIQUE,")
[void]$sb.AppendLine("    industry     VARCHAR(255),")
[void]$sb.AppendLine("    status       VARCHAR(50)  NOT NULL DEFAULT 'UNKNOWN',")
[void]$sb.AppendLine("    http_code    SMALLINT,")
[void]$sb.AppendLine("    job_count    INTEGER,")
[void]$sb.AppendLine("    api_url      VARCHAR(500),")
[void]$sb.AppendLine("    board_url    VARCHAR(500),")
[void]$sb.AppendLine("    created_at   TIMESTAMP NOT NULL DEFAULT NOW(),")
[void]$sb.AppendLine("    updated_at   TIMESTAMP NOT NULL DEFAULT NOW()")
[void]$sb.AppendLine(");")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("INSERT INTO api.bamboohr_board_tokens (company_name, board_token, api_url, board_url, status, created_at, updated_at)")
[void]$sb.AppendLine("SELECT")
[void]$sb.AppendLine("    INITCAP(REPLACE(slug, '-', ' ')) AS company_name,")
[void]$sb.AppendLine("    slug                             AS board_token,")
[void]$sb.AppendLine("    'https://' || slug || '.bamboohr.com/careers/list' AS api_url,")
[void]$sb.AppendLine("    'https://' || slug || '.bamboohr.com/careers'      AS board_url,")
[void]$sb.AppendLine("    'UNKNOWN'                        AS status,")
[void]$sb.AppendLine("    NOW(), NOW()")
[void]$sb.AppendLine("FROM (VALUES")
for ($i = 0; $i -lt $slugs.Count; $i++) {
    $safe = $slugs[$i] -replace "'", "''"
    $sep  = if ($i -eq $slugs.Count - 1) { "" } else { "," }
    [void]$sb.AppendLine("    ('$safe')$sep")
}
[void]$sb.AppendLine(") AS t(slug)")
[void]$sb.AppendLine("ON CONFLICT (board_token) DO NOTHING;")

[System.IO.File]::WriteAllText($outPath, $sb.ToString(), [System.Text.UTF8Encoding]::new($false))
"Wrote $outPath"
