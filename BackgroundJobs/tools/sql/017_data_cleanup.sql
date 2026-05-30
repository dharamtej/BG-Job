-- ============================================================
-- CareerPanda Migration 017
-- Data cleanup pass on api.raw_jobs:
--   1. Delete genuinely non-US rows
--   2. Fix US state abbreviation stored in country column
--   3. Normalize country variants → "US"
--   4. Null out bad job_level values (schedule text, hours)
--   5. Normalize salary_type raw codes → standard values
--   6. Normalize contract_type raw values → standard values
-- ============================================================

BEGIN;

-- ── Step 1: Delete genuinely non-US rows ─────────────────────────────────────
-- Rows where country is a known non-US country AND state is not a valid US code.
-- This removes ~34,172 rows left over from before the US filter was applied.

DELETE FROM api.raw_jobs
WHERE country IS NOT NULL
  AND country NOT IN (
    -- Standard US identifiers
    'US','USA','U.S.','U.S.A.','United States','United States of America',
    'Remote','D.C.',
    -- US state full names stored in country
    'Alabama','Alaska','Arizona','Arkansas','California','Colorado','Connecticut',
    'Delaware','Florida','Georgia','Hawaii','Idaho','Illinois','Indiana','Iowa',
    'Kansas','Kentucky','Louisiana','Maine','Maryland','Massachusetts','Michigan',
    'Minnesota','Mississippi','Missouri','Montana','Nebraska','Nevada',
    'New Hampshire','New Jersey','New Mexico','New York','North Carolina',
    'North Dakota','Ohio','Oklahoma','Oregon','Pennsylvania','Rhode Island',
    'South Carolina','South Dakota','Tennessee','Texas','Utah','Vermont',
    'Virginia','Washington','West Virginia','Wisconsin','Wyoming',
    'District of Columbia','Puerto Rico','Guam',
    -- US state abbreviations stored in country
    'AL','AK','AZ','AR','CA','CO','CT','DE','FL','GA','HI','ID','IL','IN','IA',
    'KS','KY','LA','ME','MD','MA','MI','MN','MS','MO','MT','NE','NV','NH','NJ',
    'NM','NY','NC','ND','OH','OK','OR','PA','RI','SC','SD','TN','TX','UT','VT',
    'VA','WA','WV','WI','WY','DC','PR','GU'
  )
  -- Safety net: keep if state column has a valid US state code
  AND (
    state IS NULL
    OR state NOT IN (
      'AL','AK','AZ','AR','CA','CO','CT','DE','FL','GA','HI','ID','IL','IN','IA',
      'KS','KY','LA','ME','MD','MA','MI','MN','MS','MO','MT','NE','NV','NH','NJ',
      'NM','NY','NC','ND','OH','OK','OR','PA','RI','SC','SD','TN','TX','UT','VT',
      'VA','WA','WV','WI','WY','DC','PR','GU'
    )
  );

-- ── Step 2: Fix US state abbreviation stored in country column ────────────────
-- Move state abbr from country → state (when state is empty), then set country = 'US'

UPDATE api.raw_jobs
SET
  state   = CASE WHEN state IS NULL THEN country ELSE state END,
  country = 'US'
WHERE country IN (
  'AL','AK','AZ','AR','CA','CO','CT','DE','FL','GA','HI','ID','IL','IN','IA',
  'KS','KY','LA','ME','MD','MA','MI','MN','MS','MO','MT','NE','NV','NH','NJ',
  'NM','NY','NC','ND','OH','OK','OR','PA','RI','SC','SD','TN','TX','UT','VT',
  'VA','WA','WV','WI','WY','DC','PR','GU'
);

-- ── Step 3: Normalize country variants → "US" ────────────────────────────────

UPDATE api.raw_jobs
SET country = 'US'
WHERE country IN (
  'USA','U.S.','U.S.A.','United States','United States of America','D.C.',
  'Alabama','Alaska','Arizona','Arkansas','California','Colorado','Connecticut',
  'Delaware','Florida','Georgia','Hawaii','Idaho','Illinois','Indiana','Iowa',
  'Kansas','Kentucky','Louisiana','Maine','Maryland','Massachusetts','Michigan',
  'Minnesota','Mississippi','Missouri','Montana','Nebraska','Nevada',
  'New Hampshire','New Jersey','New Mexico','New York','North Carolina',
  'North Dakota','Ohio','Oklahoma','Oregon','Pennsylvania','Rhode Island',
  'South Carolina','South Dakota','Tennessee','Texas','Utah','Vermont',
  'Virginia','Washington','West Virginia','Wisconsin','Wyoming',
  'District of Columbia','Puerto Rico','Guam'
)
AND country != 'US';

-- Compound country strings like "United States; San Francisco" or "USA; New York"
UPDATE api.raw_jobs
SET country = 'US'
WHERE country ILIKE 'United States%'
   OR country ILIKE 'USA;%'
   OR country ILIKE 'US;%';

-- ── Step 4: Null out bad job_level values ─────────────────────────────────────
-- Only the 7 standard tiers are valid. Everything else (schedule text, hours,
-- "Full Time", "Full-Time, Permanent", "35-40 hours per week", etc.) is nulled.

UPDATE api.raw_jobs
SET job_level = NULL
WHERE job_level IS NOT NULL
  AND job_level NOT IN (
    'Entry','Mid','Senior','Lead','Manager','Director','Executive'
  );

-- ── Step 5: Normalize salary_type raw API codes ───────────────────────────────

UPDATE api.raw_jobs
SET salary_type = CASE
  WHEN salary_type IN ('PA','Annual','annual','ANNUAL','per annum','yearly','Yearly','per year','Per Year') THEN 'Annual'
  WHEN salary_type IN ('PH','Hourly','hourly','HOURLY','per hour','Per Hour')                               THEN 'Hourly'
  WHEN salary_type IN ('PM','Monthly','monthly','MONTHLY','per month','Per Month')                          THEN 'Monthly'
  WHEN salary_type IN ('PW','Weekly','weekly','WEEKLY','per week','Per Week')                               THEN 'Weekly'
  ELSE NULL  -- WC, SY, PD, FB and other unknown codes → null
END
WHERE salary_type IS NOT NULL
  AND salary_type NOT IN ('Annual','Hourly','Monthly','Weekly');

-- ── Step 6: Normalize contract_type raw values ────────────────────────────────

UPDATE api.raw_jobs
SET contract_type = CASE
  -- Canonical values — pass through unchanged
  WHEN contract_type IN ('FullTime','PartTime','Contract','Internship','Temporary') THEN contract_type
  -- Common variants → FullTime
  WHEN contract_type ILIKE '%full%time%'
    OR contract_type ILIKE '%full time%'
    OR contract_type IN ('Permanent','Regular','Federal','Career','Career-Conditional') THEN 'FullTime'
  -- Part-time
  WHEN contract_type ILIKE '%part%time%'
    OR contract_type ILIKE '%part time%' THEN 'PartTime'
  -- Contract/Temporary
  WHEN contract_type ILIKE '%temp%'
    OR contract_type ILIKE '%seasonal%'
    OR contract_type IN ('Term','Limited Term','NTE') THEN 'Temporary'
  WHEN contract_type IN ('T32 Excepted','Excepted Service','Schedule A','Schedule B') THEN 'Contract'
  -- Internship
  WHEN contract_type ILIKE '%intern%' THEN 'Internship'
  -- Everything else (duration strings "2 years", "NON-MERIT", "external", raw junk) → null
  ELSE NULL
END
WHERE contract_type IS NOT NULL
  AND contract_type NOT IN ('FullTime','PartTime','Contract','Internship','Temporary');

COMMIT;

-- ── Verify results ────────────────────────────────────────────────────────────

SELECT 'total rows' AS check_name, COUNT(*)::text AS result FROM api.raw_jobs
UNION ALL
SELECT 'non-US country remaining',
  COUNT(*)::text FROM api.raw_jobs
  WHERE country IS NOT NULL AND country NOT IN ('US','Remote')
UNION ALL
SELECT 'bad job_level remaining',
  COUNT(*)::text FROM api.raw_jobs
  WHERE job_level IS NOT NULL
    AND job_level NOT IN ('Entry','Mid','Senior','Lead','Manager','Director','Executive')
UNION ALL
SELECT 'unnormalized salary_type remaining',
  COUNT(*)::text FROM api.raw_jobs
  WHERE salary_type IS NOT NULL
    AND salary_type NOT IN ('Annual','Hourly','Monthly','Weekly')
UNION ALL
SELECT 'unnormalized contract_type remaining',
  COUNT(*)::text FROM api.raw_jobs
  WHERE contract_type IS NOT NULL
    AND contract_type NOT IN ('FullTime','PartTime','Contract','Internship','Temporary');
