-- Seed: staffing / IT-consulting firms that publish their job boards on Greenhouse.
-- The JobClassifier will tag these companies' descriptions with IsStaffing /
-- (where applicable) IsContractJob / IsFreelanceJob, broadening contractor coverage.
--
-- HONEST CAVEAT — most pure US IT staffing firms (TEKsystems, Robert Half, Insight
-- Global, Beacon Hill, Kforce) use their own ATSes, NOT Greenhouse. These entries
-- are management / IT consulting firms whose jobs trend toward FTE consultant roles
-- rather than pure C2C postings. They still broaden the dataset and the classifier
-- will pick up "consulting"/"staffing"-keyword descriptions.
--
-- ON CONFLICT DO NOTHING — safe to re-run; existing tokens are left untouched.

INSERT INTO api.greenhouse_board_tokens
    (company_name, board_token, industry, status, api_url, board_url, created_at, updated_at)
VALUES
    ('Andela',                          'andela',             'IT Staffing',              'UNKNOWN', 'https://boards-api.greenhouse.io/v1/boards/andela/jobs',             'https://boards.greenhouse.io/andela',             NOW(), NOW()),
    ('Capco',                           'capco',              'IT Consulting',            'UNKNOWN', 'https://boards-api.greenhouse.io/v1/boards/capco/jobs',              'https://boards.greenhouse.io/capco',              NOW(), NOW()),
    ('Cook Systems',                    'cooksys',            'IT Consulting',            'UNKNOWN', 'https://boards-api.greenhouse.io/v1/boards/cooksys/jobs',            'https://boards.greenhouse.io/cooksys',            NOW(), NOW()),
    ('Kx Advisors',                     'kxadvisors',         'Life Sciences Consulting', 'UNKNOWN', 'https://boards-api.greenhouse.io/v1/boards/kxadvisors/jobs',         'https://boards.greenhouse.io/kxadvisors',         NOW(), NOW()),
    ('Envision Consulting',             'envisionconsulting', 'Consulting',               'UNKNOWN', 'https://boards-api.greenhouse.io/v1/boards/envisionconsulting/jobs', 'https://boards.greenhouse.io/envisionconsulting', NOW(), NOW()),
    ('Syner-G',                         'synerg',             'BioPharm Consulting',      'UNKNOWN', 'https://boards-api.greenhouse.io/v1/boards/synerg/jobs',             'https://boards.greenhouse.io/synerg',             NOW(), NOW()),
    ('Kinetic Communities Consulting',  'kc3',                'Consulting',               'UNKNOWN', 'https://boards-api.greenhouse.io/v1/boards/kc3/jobs',                'https://boards.greenhouse.io/kc3',                NOW(), NOW()),
    ('Delivery Associates',             'deliveryassociates', 'Consulting',               'UNKNOWN', 'https://boards-api.greenhouse.io/v1/boards/deliveryassociates/jobs', 'https://boards.greenhouse.io/deliveryassociates', NOW(), NOW())
ON CONFLICT (board_token) DO NOTHING;
