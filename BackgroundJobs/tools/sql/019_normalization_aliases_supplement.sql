-- ============================================================
-- CareerPanda Migration 019
-- Supplements the normalization taxonomy:
--   1. Adds 7 missing industries to md.industries
--   2. Adds government/executive/broad job roles to md.job_roles
--   3. Expands md.industry_aliases with all 16 source formats
--   4. Expands md.job_role_aliases with title-based matching
--      (NormalizeJobsJobHandler now matches on job_title, not role)
-- ============================================================

-- ── 1. Missing industries ────────────────────────────────────────────────────

INSERT INTO md.industries (slug, name, description, is_active, created_on) VALUES
    ('government',          'Government & Public Sector',    'Federal, state, and local government jobs',                         true, NOW()),
    ('nonprofit',           'Nonprofit & Social Impact',     'NGOs, charities, foundations, and mission-driven organizations',    true, NOW()),
    ('retail-ecommerce',    'Retail & E-Commerce',           'Retail, consumer goods, e-commerce, and merchandising',             true, NOW()),
    ('hospitality-travel',  'Hospitality & Travel',          'Hotels, restaurants, tourism, airlines, and events',                true, NOW()),
    ('telecommunications',  'Telecommunications',            'Telecom, wireless, cable, and internet service providers',          true, NOW()),
    ('energy-utilities',    'Energy & Utilities',            'Oil & gas, renewables, electric utilities, and clean energy',       true, NOW()),
    ('agriculture-food',    'Agriculture & Food',            'Food tech, agriculture, farming, and consumer food brands',         true, NOW())
ON CONFLICT (slug) DO UPDATE SET name = EXCLUDED.name, is_active = true;

-- ── 2. Missing job roles ─────────────────────────────────────────────────────

-- Government-specific roles
INSERT INTO md.job_roles (industry_id, slug, name, search_query, is_active, created_on)
SELECT i.id, r.slug, r.name, r.search_query, true, NOW()
FROM md.industries i,
(VALUES
    ('program-analyst',           'Program Analyst',           'program analyst government'),
    ('contract-specialist',       'Contract Specialist',       'contract specialist government'),
    ('budget-analyst',            'Budget Analyst',            'budget analyst federal'),
    ('policy-analyst',            'Policy Analyst',            'policy analyst government'),
    ('intelligence-analyst',      'Intelligence Analyst',      'intelligence analyst'),
    ('grants-manager',            'Grants Manager',            'grants manager'),
    ('public-health-analyst',     'Public Health Analyst',     'public health analyst'),
    ('it-specialist-govt',        'IT Specialist (Govt)',       'information technology specialist government')
) AS r(slug, name, search_query)
WHERE i.slug = 'government'
ON CONFLICT (slug) DO UPDATE SET search_query = EXCLUDED.search_query, is_active = true;

-- Executive / leadership roles (technology industry as default)
INSERT INTO md.job_roles (industry_id, slug, name, search_query, is_active, created_on)
SELECT i.id, r.slug, r.name, r.search_query, true, NOW()
FROM md.industries i,
(VALUES
    ('vp-engineering',            'VP of Engineering',              'vp engineering vice president engineering'),
    ('director-engineering',      'Director of Engineering',        'director of engineering'),
    ('engineering-manager',       'Engineering Manager',            'engineering manager'),
    ('cto',                       'CTO / Chief Technology Officer', 'chief technology officer cto'),
    ('cpo',                       'CPO / Chief Product Officer',    'chief product officer cpo'),
    ('staff-engineer',            'Staff Engineer',                 'staff engineer'),
    ('principal-engineer',        'Principal Engineer',             'principal engineer'),
    ('technical-lead',            'Technical Lead',                 'technical lead tech lead'),
    ('director-product',          'Director of Product',            'director of product management'),
    ('vp-product',                'VP of Product',                  'vp product vice president product')
) AS r(slug, name, search_query)
WHERE i.slug = 'technology-software'
ON CONFLICT (slug) DO UPDATE SET search_query = EXCLUDED.search_query, is_active = true;

-- Broad / generalist roles (technology-software as default)
INSERT INTO md.job_roles (industry_id, slug, name, search_query, is_active, created_on)
SELECT i.id, r.slug, r.name, r.search_query, true, NOW()
FROM md.industries i,
(VALUES
    ('administrative-assistant',  'Administrative Assistant',   'administrative assistant'),
    ('office-manager',            'Office Manager',             'office manager'),
    ('customer-service-rep',      'Customer Service Representative','customer service representative'),
    ('account-manager',           'Account Manager',            'account manager'),
    ('operations-analyst',        'Operations Analyst',         'operations analyst'),
    ('data-entry-specialist',     'Data Entry Specialist',      'data entry specialist'),
    ('executive-assistant',       'Executive Assistant',        'executive assistant'),
    ('office-coordinator',        'Office Coordinator',         'office coordinator'),
    ('general-manager',           'General Manager',            'general manager')
) AS r(slug, name, search_query)
WHERE i.slug = 'business-operations'
ON CONFLICT (slug) DO UPDATE SET search_query = EXCLUDED.search_query, is_active = true;

-- ── 3. Expanded industry aliases ─────────────────────────────────────────────

INSERT INTO md.industry_aliases (alias, industry_id, source)
SELECT lower(a.alias), i.id, 'seed'
FROM md.industries i,
(VALUES
    -- ── WeWorkRemotely category slugs ────────────────────────────────────────
    ('programming',                  'technology-software'),
    ('backend',                      'technology-software'),
    ('frontend',                     'technology-software'),
    ('full-stack',                   'technology-software'),
    ('mobile',                       'technology-software'),
    ('devops-sysadmin',              'cybersecurity-networks'),
    ('sysadmin',                     'cybersecurity-networks'),
    ('design-ux',                    'product-design'),
    ('all-other',                    'technology-software'),
    ('management-finance',           'finance-banking'),
    -- ── Remotive category names ──────────────────────────────────────────────
    ('software development',         'technology-software'),
    ('devops / sysadmin',            'cybersecurity-networks'),
    ('devops/sysadmin',              'cybersecurity-networks'),
    ('finance / legal',              'finance-banking'),
    ('finance/legal',                'finance-banking'),
    ('product',                      'product-design'),
    ('customer support',             'qa-support'),
    ('customer service',             'qa-support'),
    -- ── Adzuna category labels ───────────────────────────────────────────────
    ('healthcare & nursing jobs',    'healthcare-medical'),
    ('social work jobs',             'nonprofit'),
    ('charity & voluntary jobs',     'nonprofit'),
    ('graduate jobs',                'technology-software'),
    ('trade & construction jobs',    'real-estate-construction'),
    ('hospitality & catering jobs',  'hospitality-travel'),
    ('retail jobs',                  'retail-ecommerce'),
    ('energy, oil & gas jobs',       'energy-utilities'),
    ('transport & logistics jobs',   'supply-chain-logistics'),
    ('teaching jobs',                'education-training'),
    ('scientific & qa jobs',         'research-science'),
    ('pr, advertising & marketing jobs', 'marketing-sales'),
    -- ── USAJobs OPM occupational groups ─────────────────────────────────────
    ('information technology group', 'technology-software'),
    ('engineering and architecture group', 'engineering-non-software'),
    ('medical, dental, and public health group', 'healthcare-medical'),
    ('administrative and management', 'business-operations'),
    ('accounting and budget group',  'finance-banking'),
    ('legal and kindred group',      'legal-compliance'),
    ('social science, psychology and welfare group', 'research-science'),
    ('general administrative, clerical, and office services group', 'business-operations'),
    ('physical sciences group',      'research-science'),
    ('biological sciences group',    'research-science'),
    ('security and protective services', 'government'),
    -- ── Jobicy industry slugs ────────────────────────────────────────────────
    ('software-engineering',         'technology-software'),
    ('data-analytics',               'data-analytics'),
    ('product-management',           'product-design'),
    ('devops-infrastructure',        'cybersecurity-networks'),
    ('ui-ux-design',                 'product-design'),
    ('cloud-technology',             'technology-software'),
    ('cybersecurity',                'cybersecurity-networks'),
    ('it-support',                   'qa-support'),
    ('digital-marketing',            'marketing-sales'),
    ('sales-business-development',   'marketing-sales'),
    ('hr-recruiting',                'human-resources'),
    ('finance-accounting',           'finance-banking'),
    ('healthcare-biotech',           'healthcare-medical'),
    ('gaming-entertainment',         'media-communications'),
    ('non-profit',                   'nonprofit'),
    ('government-policy',            'government'),
    ('education',                    'education-training'),
    ('ecommerce',                    'retail-ecommerce'),
    -- ── TheMuse category names ───────────────────────────────────────────────
    ('tech',                         'technology-software'),
    ('engineering',                  'technology-software'),
    ('data science',                 'data-analytics'),
    ('product & ux',                 'product-design'),
    ('product & design',             'product-design'),
    ('dev & it',                     'technology-software'),
    ('design & ux',                  'product-design'),
    ('project & program management', 'business-operations'),
    ('hr & recruiting',              'human-resources'),
    ('business & strategy',          'business-operations'),
    ('finance',                      'finance-banking'),
    ('sales',                        'marketing-sales'),
    ('marketing & pr',               'marketing-sales'),
    ('operations',                   'business-operations'),
    ('customer service',             'qa-support'),
    ('content & writing',            'media-communications'),
    ('non-profit management',        'nonprofit'),
    ('social impact',                'nonprofit'),
    ('healthcare',                   'healthcare-medical'),
    ('social media & community',     'marketing-sales'),
    -- ── New industries just added ────────────────────────────────────────────
    ('government',                   'government'),
    ('federal government',           'government'),
    ('public sector',                'government'),
    ('public administration',        'government'),
    ('defense',                      'government'),
    ('military',                     'government'),
    ('usajobs',                      'government'),
    ('nonprofit',                    'nonprofit'),
    ('non-profit',                   'nonprofit'),
    ('501c3',                        'nonprofit'),
    ('ngo',                          'nonprofit'),
    ('foundation',                   'nonprofit'),
    ('charity',                      'nonprofit'),
    ('social impact',                'nonprofit'),
    ('retail',                       'retail-ecommerce'),
    ('e-commerce',                   'retail-ecommerce'),
    ('consumer goods',               'retail-ecommerce'),
    ('hospitality',                  'hospitality-travel'),
    ('travel',                       'hospitality-travel'),
    ('hotel',                        'hospitality-travel'),
    ('restaurant',                   'hospitality-travel'),
    ('food & beverage',              'hospitality-travel'),
    ('telecommunications',           'telecommunications'),
    ('telecom',                      'telecommunications'),
    ('wireless',                     'telecommunications'),
    ('energy',                       'energy-utilities'),
    ('oil & gas',                    'energy-utilities'),
    ('utilities',                    'energy-utilities'),
    ('renewables',                   'energy-utilities'),
    ('clean energy',                 'energy-utilities'),
    ('agriculture',                  'agriculture-food'),
    ('food tech',                    'agriculture-food'),
    ('food',                         'agriculture-food'),
    -- ── RemoteOK tag slugs (stored verbatim in raw_jobs.industry) ────────────
    ('software-engineer',            'technology-software'),
    ('developer',                    'technology-software'),
    ('full-stack',                   'technology-software'),
    ('ios',                          'technology-software'),
    ('android',                      'technology-software'),
    ('cloud',                        'technology-software'),
    ('infra',                        'technology-software'),
    ('embedded',                     'technology-software'),
    ('game',                         'technology-software'),
    ('data-science',                 'data-analytics'),
    ('data-engineer',                'data-analytics'),
    ('machine-learning',             'data-analytics'),
    ('ai',                           'data-analytics'),
    ('nlp',                          'data-analytics'),
    ('analytics',                    'data-analytics'),
    ('database',                     'technology-software'),
    ('sre',                          'cybersecurity-networks'),
    ('ux',                           'product-design'),
    ('ui',                           'product-design'),
    ('qa',                           'qa-support'),
    ('testing',                      'qa-support'),
    ('project-management',           'business-operations'),
    ('scrum',                        'business-operations'),
    ('business-development',         'marketing-sales'),
    ('strategy',                     'business-operations'),
    ('growth',                       'marketing-sales'),
    ('seo',                          'marketing-sales'),
    ('copywriting',                  'media-communications'),
    ('writing',                      'media-communications'),
    ('video',                        'media-communications'),
    ('hr',                           'human-resources'),
    ('non-tech',                     'business-operations'),
    -- ── Arbeitnow tag slugs (first tag stored as industry hint) ─────────────
    ('javascript',                   'technology-software'),
    ('python',                       'technology-software'),
    ('typescript',                   'technology-software'),
    ('java',                         'technology-software'),
    ('golang',                       'technology-software'),
    ('rust',                         'technology-software'),
    ('ruby',                         'technology-software'),
    ('php',                          'technology-software'),
    ('c#',                           'technology-software'),
    ('c++',                          'technology-software'),
    ('swift',                        'technology-software'),
    ('kotlin',                       'technology-software'),
    ('react',                        'technology-software'),
    ('node.js',                      'technology-software'),
    ('vue.js',                       'technology-software'),
    ('kubernetes',                   'cybersecurity-networks'),
    ('docker',                       'technology-software'),
    ('aws',                          'technology-software'),
    ('azure',                        'technology-software'),
    ('gcp',                          'technology-software'),
    ('postgresql',                   'technology-software'),
    ('mysql',                        'technology-software'),
    ('mongodb',                      'data-analytics'),
    ('terraform',                    'technology-software'),
    ('spark',                        'data-analytics'),
    ('kafka',                        'data-analytics'),
    ('dbt',                          'data-analytics')
) AS a(alias, slug)
WHERE i.slug = a.slug
ON CONFLICT (alias) DO NOTHING;

-- ── 4. Expanded job role aliases (title-based matching) ──────────────────────
-- NormalizeJobsJobHandler now resolves against job_title, not raw_jobs.role.
-- These cover the most common job title patterns seen across all 16 sources.

INSERT INTO md.job_role_aliases (alias, job_role_id, source)
SELECT lower(a.alias), r.id, 'seed'
FROM md.job_roles r,
(VALUES
    -- ── Title variants: software engineer ────────────────────────────────────
    ('software engineer i',               'software-engineer'),
    ('software engineer ii',              'software-engineer'),
    ('software engineer iii',             'software-engineer'),
    ('sr. software engineer',             'software-engineer'),
    ('sr software engineer',              'software-engineer'),
    ('senior software engineer',          'software-engineer'),
    ('associate software engineer',       'software-engineer'),
    ('junior software engineer',          'software-engineer'),
    ('staff software engineer',           'staff-engineer'),
    ('principal software engineer',       'principal-engineer'),
    -- ── Full-stack ───────────────────────────────────────────────────────────
    ('full stack software engineer',      'full-stack-developer'),
    ('fullstack engineer',                'full-stack-developer'),
    -- ── Backend ─────────────────────────────────────────────────────────────
    ('senior backend engineer',           'backend-engineer'),
    ('sr backend engineer',               'backend-engineer'),
    ('backend software engineer',         'backend-engineer'),
    -- ── Frontend ────────────────────────────────────────────────────────────
    ('senior frontend engineer',          'frontend-developer'),
    ('sr frontend engineer',              'frontend-developer'),
    ('react developer',                   'frontend-developer'),
    ('react engineer',                    'frontend-developer'),
    ('angular developer',                 'frontend-developer'),
    ('vue developer',                     'frontend-developer'),
    ('javascript developer',              'frontend-developer'),
    ('typescript developer',              'frontend-developer'),
    -- ── Mobile ──────────────────────────────────────────────────────────────
    ('mobile engineer',                   'mobile-developer'),
    ('react native developer',            'mobile-developer'),
    ('flutter developer',                 'mobile-developer'),
    -- ── DevOps / SRE ────────────────────────────────────────────────────────
    ('senior devops engineer',            'devops-engineer'),
    ('sr devops engineer',                'devops-engineer'),
    ('infrastructure engineer',           'devops-engineer'),
    ('platform engineer',                 'platform-engineer'),
    ('reliability engineer',              'site-reliability-engineer'),
    ('senior site reliability engineer',  'site-reliability-engineer'),
    -- ── Cloud ───────────────────────────────────────────────────────────────
    ('cloud infrastructure engineer',     'cloud-engineer'),
    ('cloud architect',                   'solutions-architect'),
    ('aws solutions architect',           'solutions-architect'),
    ('azure solutions architect',         'solutions-architect'),
    -- ── Data ────────────────────────────────────────────────────────────────
    ('senior data scientist',             'data-scientist'),
    ('sr data scientist',                 'data-scientist'),
    ('senior data engineer',              'data-engineer'),
    ('sr data engineer',                  'data-engineer'),
    ('senior data analyst',               'data-analyst'),
    ('sr data analyst',                   'data-analyst'),
    ('junior data analyst',               'data-analyst'),
    ('analytics engineer',                'data-engineer'),
    ('ai / ml engineer',                  'machine-learning-engineer'),
    ('ai/ml engineer',                    'machine-learning-engineer'),
    ('llm engineer',                      'machine-learning-engineer'),
    ('generative ai engineer',            'ai-engineer'),
    ('gen ai engineer',                   'ai-engineer'),
    ('applied scientist',                 'data-scientist'),
    ('deep learning engineer',            'machine-learning-engineer'),
    ('computer vision engineer',          'machine-learning-engineer'),
    ('nlp engineer',                      'machine-learning-engineer'),
    ('ml platform engineer',              'machine-learning-engineer'),
    -- ── Product ─────────────────────────────────────────────────────────────
    ('senior product manager',            'product-manager'),
    ('sr product manager',                'product-manager'),
    ('product owner',                     'product-manager'),
    ('group product manager',             'product-manager'),
    ('principal product manager',         'product-manager'),
    -- ── UX/Design ───────────────────────────────────────────────────────────
    ('senior product designer',           'product-designer'),
    ('sr product designer',               'product-designer'),
    ('senior ux designer',                'ux-designer'),
    ('sr ux designer',                    'ux-designer'),
    ('ui/ux designer',                    'ux-designer'),
    ('ux/ui designer',                    'ux-designer'),
    -- ── Security ────────────────────────────────────────────────────────────
    ('security engineer',                 'cybersecurity-analyst'),
    ('application security engineer',     'cybersecurity-analyst'),
    ('cloud security engineer',           'cybersecurity-analyst'),
    ('senior security engineer',          'cybersecurity-analyst'),
    ('penetration tester',                'cybersecurity-analyst'),
    ('devsecops engineer',                'devops-engineer'),
    -- ── Management ──────────────────────────────────────────────────────────
    ('vp of engineering',                 'vp-engineering'),
    ('vice president of engineering',     'vp-engineering'),
    ('vp engineering',                    'vp-engineering'),
    ('director of engineering',           'director-engineering'),
    ('director, engineering',             'director-engineering'),
    ('engineering manager',               'engineering-manager'),
    ('sr engineering manager',            'engineering-manager'),
    ('senior engineering manager',        'engineering-manager'),
    ('staff software engineer',           'staff-engineer'),
    ('senior staff engineer',             'staff-engineer'),
    ('principal engineer',                'principal-engineer'),
    ('technical lead',                    'technical-lead'),
    ('tech lead',                         'technical-lead'),
    ('lead software engineer',            'technical-lead'),
    ('lead engineer',                     'technical-lead'),
    ('director of product',               'director-product'),
    ('vp of product',                     'vp-product'),
    -- ── Finance ─────────────────────────────────────────────────────────────
    ('senior financial analyst',          'financial-analyst'),
    ('sr financial analyst',              'financial-analyst'),
    ('junior financial analyst',          'financial-analyst'),
    ('fp&a analyst',                      'financial-analyst'),
    ('cpa',                               'accountant'),
    ('senior accountant',                 'accountant'),
    ('staff accountant',                  'accountant'),
    ('controller',                        'accountant'),
    ('quantitative researcher',           'quantitative-analyst'),
    ('quant analyst',                     'quantitative-analyst'),
    -- ── Business / Operations ────────────────────────────────────────────────
    ('senior business analyst',           'business-analyst'),
    ('it business analyst',               'business-analyst'),
    ('senior project manager',            'project-manager'),
    ('pmp',                               'project-manager'),
    ('it project manager',                'project-manager'),
    ('agile coach',                       'scrum-master'),
    ('senior program manager',            'program-manager'),
    ('strategy manager',                  'strategy-analyst'),
    ('account executive',                 'account-manager'),
    ('key account manager',               'account-manager'),
    ('customer success manager',          'customer-success-manager'),
    ('csm',                               'customer-success-manager'),
    -- ── Marketing ───────────────────────────────────────────────────────────
    ('growth marketer',                   'growth-analyst'),
    ('performance marketer',              'performance-marketing'),
    ('paid media specialist',             'performance-marketing'),
    ('sem specialist',                    'seo-specialist'),
    ('seo/sem specialist',                'seo-specialist'),
    ('brand manager',                     'brand-manager'),
    ('content writer',                    'content-marketer'),
    ('content strategist',                'content-marketer'),
    ('email marketing manager',           'email-marketing-specialist'),
    ('lifecycle marketing manager',       'email-marketing-specialist'),
    ('sales development rep',             'sales-development-rep'),
    ('sdr',                               'sales-development-rep'),
    ('bdr',                               'sales-development-rep'),
    ('business development rep',          'sales-development-rep'),
    -- ── HR ──────────────────────────────────────────────────────────────────
    ('technical recruiter',               'recruiter'),
    ('senior recruiter',                  'recruiter'),
    ('corporate recruiter',               'recruiter'),
    ('talent partner',                    'talent-acquisition'),
    ('hr generalist',                     'hr-manager'),
    ('people operations manager',         'people-operations'),
    ('people operations specialist',      'people-operations'),
    -- ── Healthcare ──────────────────────────────────────────────────────────
    ('rn',                                'registered-nurse'),
    ('bsn',                               'registered-nurse'),
    ('np',                                'nurse-practitioner'),
    ('md',                                'physician'),
    ('pa',                                'physician'),
    ('clinical pharmacist',               'pharmacist'),
    -- ── Consulting ──────────────────────────────────────────────────────────
    ('management consultant',             'management-consultant'),
    ('senior consultant',                 'it-consultant'),
    ('associate consultant',              'it-consultant'),
    ('technology consultant',             'technology-consultant'),
    ('digital transformation consultant', 'technology-consultant'),
    -- ── Government roles ────────────────────────────────────────────────────
    ('program analyst',                   'program-analyst'),
    ('contract specialist',               'contract-specialist'),
    ('contracting officer',               'contract-specialist'),
    ('budget analyst',                    'budget-analyst'),
    ('policy analyst',                    'policy-analyst'),
    ('intelligence analyst',              'intelligence-analyst'),
    ('it specialist',                     'it-specialist-govt'),
    ('administrative officer',            'administrative-assistant'),
    -- ── Broad / general ─────────────────────────────────────────────────────
    ('administrative assistant',          'administrative-assistant'),
    ('executive assistant',               'executive-assistant'),
    ('office manager',                    'office-manager'),
    ('office coordinator',                'office-coordinator'),
    ('customer service representative',   'customer-service-rep'),
    ('customer support specialist',       'customer-service-rep'),
    ('customer support engineer',         'technical-support'),
    ('operations manager',                'operations-manager'),
    ('general manager',                   'general-manager'),
    ('associate',                         'data-analyst'),           -- broad, maps to analyst
    ('intern',                            'internship-engineering')  -- broad internship
) AS a(alias, slug)
WHERE r.slug = a.slug
ON CONFLICT (alias) DO NOTHING;
