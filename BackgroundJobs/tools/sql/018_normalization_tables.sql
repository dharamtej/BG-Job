-- ============================================================
-- CareerPanda Migration 018
-- Adds industry/job-role normalization infrastructure:
--   1. norm_status column on api.raw_jobs
--   2. md.industry_aliases  — maps raw source text → md.industries.id
--   3. md.job_role_aliases  — maps raw source text → md.job_roles.id
-- ============================================================

-- ── 1. norm_status on raw_jobs ───────────────────────────────────────────────
-- 'pending'   = not yet processed by NormalizeJobs handler
-- 'auto_high' = matched with high confidence (≥0.85) — no human needed
-- 'auto_low'  = matched with low confidence — worth reviewing
-- 'manual'    = manually assigned via admin
-- 'failed'    = could not match after all strategies
ALTER TABLE api.raw_jobs
    ADD COLUMN IF NOT EXISTS norm_status TEXT NOT NULL DEFAULT 'pending';

-- Index so the handler can page through unprocessed rows fast.
CREATE INDEX IF NOT EXISTS ix_raw_jobs_norm_status
    ON api.raw_jobs (norm_status, id);

-- ── 2. md.industry_aliases ───────────────────────────────────────────────────
-- Each row maps one raw text value (lowercased, trimmed) coming from any of the
-- 16 fetch sources → a canonical md.industries row.
CREATE TABLE IF NOT EXISTS md.industry_aliases (
    id          SERIAL      PRIMARY KEY,
    alias       TEXT        NOT NULL,   -- lowercased trimmed raw text from source
    industry_id INT         NOT NULL REFERENCES md.industries(id) ON DELETE CASCADE,
    source      TEXT,                   -- which handler produced it (for auditing)
    created_on  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS uix_industry_aliases_alias
    ON md.industry_aliases (alias);

CREATE INDEX IF NOT EXISTS ix_industry_aliases_industry_id
    ON md.industry_aliases (industry_id);

-- ── 3. md.job_role_aliases ───────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS md.job_role_aliases (
    id          SERIAL      PRIMARY KEY,
    alias       TEXT        NOT NULL,
    job_role_id INT         NOT NULL REFERENCES md.job_roles(id) ON DELETE CASCADE,
    source      TEXT,
    created_on  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS uix_job_role_aliases_alias
    ON md.job_role_aliases (alias);

CREATE INDEX IF NOT EXISTS ix_job_role_aliases_job_role_id
    ON md.job_role_aliases (job_role_id);

-- ── 4. Pre-seed well-known aliases ──────────────────────────────────────────
-- These cover the most common raw strings from Greenhouse, TheMuse, JSearch,
-- Adzuna, Arbeitnow, and USAJobs so the first normalization pass already lands
-- the majority of jobs without any human review.

-- Industry aliases
INSERT INTO md.industry_aliases (alias, industry_id, source)
SELECT lower(a.alias), i.id, 'seed'
FROM md.industries i,
(VALUES
    -- technology-software
    ('technology-software',              'technology-software'),
    ('technology & software',            'technology-software'),
    ('technology',                       'technology-software'),
    ('software',                         'technology-software'),
    ('software engineering',             'technology-software'),
    ('engineering',                      'technology-software'),
    ('tech',                             'technology-software'),
    ('it',                               'technology-software'),
    ('information technology',           'technology-software'),
    ('internet',                         'technology-software'),
    ('computer science',                 'technology-software'),
    ('it jobs',                          'technology-software'),
    ('it & software',                    'technology-software'),
    ('software development',             'technology-software'),
    ('cloud computing',                  'technology-software'),
    ('devops',                           'technology-software'),
    -- data-analytics
    ('data-analytics',                   'data-analytics'),
    ('data & analytics',                 'data-analytics'),
    ('data science',                     'data-analytics'),
    ('data',                             'data-analytics'),
    ('analytics',                        'data-analytics'),
    ('machine learning',                 'data-analytics'),
    ('artificial intelligence',          'data-analytics'),
    ('ai/ml',                            'data-analytics'),
    ('business intelligence',            'data-analytics'),
    -- cybersecurity-networks
    ('cybersecurity-networks',           'cybersecurity-networks'),
    ('cybersecurity',                    'cybersecurity-networks'),
    ('information security',             'cybersecurity-networks'),
    ('security',                         'cybersecurity-networks'),
    ('network',                          'cybersecurity-networks'),
    ('networking',                       'cybersecurity-networks'),
    -- product-design
    ('product-design',                   'product-design'),
    ('product',                          'product-design'),
    ('product management',               'product-design'),
    ('ux/ui design',                     'product-design'),
    ('design',                           'product-design'),
    ('ux design',                        'product-design'),
    ('user experience',                  'product-design'),
    -- qa-support
    ('qa-support',                       'qa-support'),
    ('quality assurance',                'qa-support'),
    ('qa',                               'qa-support'),
    ('testing',                          'qa-support'),
    ('technical support',                'qa-support'),
    ('customer support',                 'qa-support'),
    -- healthcare-medical
    ('healthcare-medical',               'healthcare-medical'),
    ('healthcare',                       'healthcare-medical'),
    ('health care',                      'healthcare-medical'),
    ('medical',                          'healthcare-medical'),
    ('nursing',                          'healthcare-medical'),
    ('clinical',                         'healthcare-medical'),
    ('health',                           'healthcare-medical'),
    ('biotech',                          'healthcare-medical'),
    ('pharmaceutical',                   'healthcare-medical'),
    ('pharma',                           'healthcare-medical'),
    -- finance-banking
    ('finance-banking',                  'finance-banking'),
    ('finance',                          'finance-banking'),
    ('banking',                          'finance-banking'),
    ('financial services',               'finance-banking'),
    ('accounting',                       'finance-banking'),
    ('investment',                       'finance-banking'),
    ('insurance',                        'finance-banking'),
    ('accounting & finance jobs',        'finance-banking'),
    ('financial',                        'finance-banking'),
    -- business-operations
    ('business-operations',              'business-operations'),
    ('business',                         'business-operations'),
    ('operations',                       'business-operations'),
    ('business operations',              'business-operations'),
    ('project management',               'business-operations'),
    ('management',                       'business-operations'),
    ('administration',                   'business-operations'),
    -- marketing-sales
    ('marketing-sales',                  'marketing-sales'),
    ('marketing',                        'marketing-sales'),
    ('sales',                            'marketing-sales'),
    ('marketing & sales',                'marketing-sales'),
    ('digital marketing',                'marketing-sales'),
    ('growth',                           'marketing-sales'),
    ('customer success',                 'marketing-sales'),
    ('advertising',                      'marketing-sales'),
    -- engineering-non-software
    ('engineering-non-software',         'engineering-non-software'),
    ('mechanical engineering',           'engineering-non-software'),
    ('electrical engineering',           'engineering-non-software'),
    ('civil engineering',                'engineering-non-software'),
    ('aerospace',                        'engineering-non-software'),
    ('manufacturing & engineering',      'engineering-non-software'),
    -- legal-compliance
    ('legal-compliance',                 'legal-compliance'),
    ('legal',                            'legal-compliance'),
    ('compliance',                       'legal-compliance'),
    ('law',                              'legal-compliance'),
    ('regulatory',                       'legal-compliance'),
    -- human-resources
    ('human-resources',                  'human-resources'),
    ('human resources',                  'human-resources'),
    ('hr',                               'human-resources'),
    ('recruiting',                       'human-resources'),
    ('talent acquisition',               'human-resources'),
    ('people',                           'human-resources'),
    -- education-training
    ('education-training',               'education-training'),
    ('education',                        'education-training'),
    ('training',                         'education-training'),
    ('learning & development',           'education-training'),
    -- creative-design
    ('creative-design',                  'creative-design'),
    ('creative',                         'creative-design'),
    ('graphic design',                   'creative-design'),
    ('media',                            'creative-design'),
    ('content',                          'creative-design'),
    -- supply-chain-logistics
    ('supply-chain-logistics',           'supply-chain-logistics'),
    ('supply chain',                     'supply-chain-logistics'),
    ('logistics',                        'supply-chain-logistics'),
    ('operations & logistics',           'supply-chain-logistics'),
    ('procurement',                      'supply-chain-logistics'),
    -- real-estate-construction
    ('real-estate-construction',         'real-estate-construction'),
    ('real estate',                      'real-estate-construction'),
    ('construction',                     'real-estate-construction'),
    ('architecture',                     'real-estate-construction'),
    -- research-science
    ('research-science',                 'research-science'),
    ('research',                         'research-science'),
    ('science',                          'research-science'),
    ('r&d',                              'research-science'),
    -- media-communications
    ('media-communications',             'media-communications'),
    ('communications',                   'media-communications'),
    ('public relations',                 'media-communications'),
    ('journalism',                       'media-communications'),
    -- manufacturing
    ('manufacturing',                    'manufacturing'),
    ('production',                       'manufacturing'),
    ('industrial',                       'manufacturing'),
    -- consulting
    ('consulting',                       'consulting'),
    ('management consulting',            'consulting'),
    ('professional services',            'consulting'),
    ('it consulting',                    'consulting')
) AS a(alias, slug)
WHERE i.slug = a.slug
ON CONFLICT (alias) DO NOTHING;

-- Job role aliases
INSERT INTO md.job_role_aliases (alias, job_role_id, source)
SELECT lower(a.alias), r.id, 'seed'
FROM md.job_roles r,
(VALUES
    -- software-engineer
    ('software engineer',           'software-engineer'),
    ('software engineers',          'software-engineer'),
    ('swe',                         'software-engineer'),
    -- software-developer
    ('software developer',          'software-developer'),
    ('developer',                   'software-developer'),
    ('programmer',                  'software-developer'),
    -- full-stack-developer
    ('full stack developer',        'full-stack-developer'),
    ('fullstack developer',         'full-stack-developer'),
    ('full-stack developer',        'full-stack-developer'),
    ('full stack engineer',         'full-stack-developer'),
    -- backend-engineer
    ('backend engineer',            'backend-engineer'),
    ('back-end engineer',           'backend-engineer'),
    ('back end engineer',           'backend-engineer'),
    ('backend developer',           'backend-engineer'),
    ('back-end developer',          'backend-engineer'),
    -- frontend-developer
    ('frontend developer',          'frontend-developer'),
    ('front-end developer',         'frontend-developer'),
    ('front end developer',         'frontend-developer'),
    ('frontend engineer',           'frontend-developer'),
    -- devops-engineer
    ('devops engineer',             'devops-engineer'),
    ('devops',                      'devops-engineer'),
    ('dev ops engineer',            'devops-engineer'),
    ('site reliability engineer',   'site-reliability-engineer'),
    ('sre',                         'site-reliability-engineer'),
    -- cloud-engineer
    ('cloud engineer',              'cloud-engineer'),
    ('aws engineer',                'cloud-engineer'),
    ('azure engineer',              'cloud-engineer'),
    -- data-scientist
    ('data scientist',              'data-scientist'),
    ('data science',                'data-scientist'),
    -- data-engineer
    ('data engineer',               'data-engineer'),
    ('data pipeline engineer',      'data-engineer'),
    -- data-analyst
    ('data analyst',                'data-analyst'),
    ('analyst',                     'data-analyst'),
    -- machine-learning-engineer
    ('machine learning engineer',   'machine-learning-engineer'),
    ('ml engineer',                 'machine-learning-engineer'),
    ('mlops engineer',              'machine-learning-engineer'),
    ('ai engineer',                 'ai-engineer'),
    -- product-manager
    ('product manager',             'product-manager'),
    ('product management',          'product-manager'),
    ('pm',                          'product-manager'),
    -- ux-designer
    ('ux designer',                 'ux-designer'),
    ('ux/ui designer',              'ux-designer'),
    ('user experience designer',    'ux-designer'),
    ('product designer',            'product-designer'),
    -- qa-engineer
    ('qa engineer',                 'qa-engineer'),
    ('quality assurance engineer',  'qa-engineer'),
    ('test engineer',               'test-engineer'),
    ('sdet',                        'sdet'),
    -- cybersecurity-analyst
    ('cybersecurity analyst',       'cybersecurity-analyst'),
    ('security analyst',            'cybersecurity-analyst'),
    ('information security analyst','cybersecurity-analyst'),
    ('network engineer',            'network-engineer'),
    ('systems administrator',       'systems-administrator'),
    -- financial-analyst
    ('financial analyst',           'financial-analyst'),
    ('finance analyst',             'financial-analyst'),
    ('accountant',                  'accountant'),
    ('staff accountant',            'accountant'),
    -- business-analyst
    ('business analyst',            'business-analyst'),
    ('ba',                          'business-analyst'),
    ('project manager',             'project-manager'),
    ('scrum master',                'scrum-master'),
    -- marketing-manager
    ('marketing manager',           'marketing-manager'),
    ('digital marketing specialist','digital-marketing-specialist'),
    ('seo specialist',              'seo-specialist'),
    ('content marketer',            'content-marketer'),
    ('customer success manager',    'customer-success-manager'),
    -- recruiter
    ('recruiter',                   'recruiter'),
    ('talent acquisition specialist','talent-acquisition'),
    ('hr manager',                  'hr-manager'),
    -- mechanical-engineer
    ('mechanical engineer',         'mechanical-engineer'),
    ('electrical engineer',         'electrical-engineer'),
    ('civil engineer',              'civil-engineer'),
    ('research scientist',          'research-scientist'),
    ('data architect',              'data-architect'),
    ('solutions architect',         'solutions-architect'),
    ('technical writer',            'technical-writer'),
    ('graphic designer',            'graphic-designer'),
    ('attorney',                    'attorney'),
    ('paralegal',                   'paralegal'),
    ('registered nurse',            'registered-nurse'),
    ('nurse practitioner',          'nurse-practitioner'),
    ('physician',                   'physician'),
    ('pharmacist',                  'pharmacist'),
    ('logistics coordinator',       'logistics-coordinator'),
    ('supply chain analyst',        'supply-chain-analyst'),
    ('construction manager',        'construction-manager'),
    ('it consultant',               'it-consultant'),
    ('salesforce consultant',       'salesforce-consultant'),
    ('erp consultant',              'erp-consultant'),
    ('sap consultant',              'sap-consultant')
) AS a(alias, slug)
WHERE r.slug = a.slug
ON CONFLICT (alias) DO NOTHING;
