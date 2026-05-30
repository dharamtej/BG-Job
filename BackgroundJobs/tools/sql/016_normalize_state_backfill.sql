-- ============================================================
-- CareerPanda Migration 016
-- Backfill: normalize api.raw_jobs.state to 2-letter US codes.
-- New fetches are already normalized at parse time (Sprint 3).
-- This one-time UPDATE fixes all historical rows.
-- ============================================================

UPDATE api.raw_jobs
SET state = CASE TRIM(state)
    WHEN 'Alabama'              THEN 'AL'  WHEN 'Alaska'               THEN 'AK'
    WHEN 'Arizona'              THEN 'AZ'  WHEN 'Arkansas'             THEN 'AR'
    WHEN 'California'           THEN 'CA'  WHEN 'Colorado'             THEN 'CO'
    WHEN 'Connecticut'          THEN 'CT'  WHEN 'Delaware'             THEN 'DE'
    WHEN 'Florida'              THEN 'FL'  WHEN 'Georgia'              THEN 'GA'
    WHEN 'Hawaii'               THEN 'HI'  WHEN 'Idaho'                THEN 'ID'
    WHEN 'Illinois'             THEN 'IL'  WHEN 'Indiana'              THEN 'IN'
    WHEN 'Iowa'                 THEN 'IA'  WHEN 'Kansas'               THEN 'KS'
    WHEN 'Kentucky'             THEN 'KY'  WHEN 'Louisiana'            THEN 'LA'
    WHEN 'Maine'                THEN 'ME'  WHEN 'Maryland'             THEN 'MD'
    WHEN 'Massachusetts'        THEN 'MA'  WHEN 'Michigan'             THEN 'MI'
    WHEN 'Minnesota'            THEN 'MN'  WHEN 'Mississippi'          THEN 'MS'
    WHEN 'Missouri'             THEN 'MO'  WHEN 'Montana'              THEN 'MT'
    WHEN 'Nebraska'             THEN 'NE'  WHEN 'Nevada'               THEN 'NV'
    WHEN 'New Hampshire'        THEN 'NH'  WHEN 'New Jersey'           THEN 'NJ'
    WHEN 'New Mexico'           THEN 'NM'  WHEN 'New York'             THEN 'NY'
    WHEN 'North Carolina'       THEN 'NC'  WHEN 'North Dakota'         THEN 'ND'
    WHEN 'Ohio'                 THEN 'OH'  WHEN 'Oklahoma'             THEN 'OK'
    WHEN 'Oregon'               THEN 'OR'  WHEN 'Pennsylvania'         THEN 'PA'
    WHEN 'Rhode Island'         THEN 'RI'  WHEN 'South Carolina'       THEN 'SC'
    WHEN 'South Dakota'         THEN 'SD'  WHEN 'Tennessee'            THEN 'TN'
    WHEN 'Texas'                THEN 'TX'  WHEN 'Utah'                 THEN 'UT'
    WHEN 'Vermont'              THEN 'VT'  WHEN 'Virginia'             THEN 'VA'
    WHEN 'Washington'           THEN 'WA'  WHEN 'West Virginia'        THEN 'WV'
    WHEN 'Wisconsin'            THEN 'WI'  WHEN 'Wyoming'              THEN 'WY'
    WHEN 'District of Columbia' THEN 'DC'  WHEN 'Washington DC'        THEN 'DC'
    WHEN 'Washington D.C.'      THEN 'DC'  WHEN 'D.C.'                 THEN 'DC'
    WHEN 'Puerto Rico'          THEN 'PR'  WHEN 'Guam'                 THEN 'GU'
    WHEN 'Virgin Islands'       THEN 'VI'  WHEN 'American Samoa'       THEN 'AS'
    ELSE state
END
WHERE state IS NOT NULL
  AND LENGTH(TRIM(state)) > 2;

-- Normalize JobLevel to standard tiers on existing rows.
-- Handles raw values stored before Sprint 2/3 (e.g. "Junior", "Internship", "Mid-Level").
UPDATE api.raw_jobs
SET job_level = CASE
    WHEN job_level ILIKE '%intern%'   OR job_level ILIKE '%co-op%'
      OR job_level ILIKE '%trainee%'                              THEN 'Entry'
    WHEN job_level ILIKE '%entry%'    OR job_level ILIKE '%junior%'
      OR job_level ILIKE '%associate%'
      OR job_level IN ('1','I','Level 1','level 1')              THEN 'Entry'
    WHEN job_level ILIKE '%mid%'      OR job_level ILIKE '%intermediate%'
      OR job_level IN ('2','II','Level 2','level 2')             THEN 'Mid'
    WHEN job_level ILIKE '%staff%'    OR job_level ILIKE '%principal%'
      OR (job_level ILIKE '%lead%'
          AND job_level NOT ILIKE '%manager%'
          AND job_level NOT ILIKE '%director%')                  THEN 'Lead'
    WHEN job_level ILIKE '%senior%'   OR job_level ILIKE '%sr.%'
      OR job_level ILIKE '% sr %'
      OR job_level IN ('3','III','Level 3','level 3')            THEN 'Senior'
    WHEN job_level ILIKE '%manager%'  OR job_level ILIKE '%mgr%'
      OR job_level ILIKE '%supervisor%'                          THEN 'Manager'
    WHEN job_level ILIKE '%director%' OR job_level ILIKE '%head of%' THEN 'Director'
    WHEN job_level ILIKE '%vp%'       OR job_level ILIKE '%vice president%'
      OR job_level ILIKE '%chief%'    OR job_level ILIKE '%executive%'
      OR job_level ILIKE '%partner%'                             THEN 'Executive'
    ELSE job_level
END
WHERE job_level IS NOT NULL
  AND job_level NOT IN ('Entry','Mid','Senior','Lead','Manager','Director','Executive');
