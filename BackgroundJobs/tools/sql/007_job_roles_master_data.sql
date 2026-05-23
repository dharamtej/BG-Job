-- ============================================================
-- CareerPanda Migration 007
-- Adds is_active + search_query columns to md.job_roles and md.industries.
-- Seeds comprehensive master data: 20 industries, 200+ roles.
-- search_query = what gets sent to JSearch API for that role.
-- ============================================================

-- ── Schema changes ───────────────────────────────────────────────────────────

ALTER TABLE md.industries
    ADD COLUMN IF NOT EXISTS is_active BOOLEAN NOT NULL DEFAULT true;

ALTER TABLE md.job_roles
    ADD COLUMN IF NOT EXISTS search_query VARCHAR(512),
    ADD COLUMN IF NOT EXISTS is_active    BOOLEAN NOT NULL DEFAULT true;

-- Unique slug constraints (safe if already exists)
DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'uix_md_industries_slug'
    ) THEN
        ALTER TABLE md.industries ADD CONSTRAINT uix_md_industries_slug UNIQUE (slug);
    END IF;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'uix_md_job_roles_slug'
    ) THEN
        ALTER TABLE md.job_roles ADD CONSTRAINT uix_md_job_roles_slug UNIQUE (slug);
    END IF;
END $$;

-- ── Industries ───────────────────────────────────────────────────────────────

INSERT INTO md.industries (slug, name, description, is_active, created_on) VALUES
    ('technology-software',       'Technology & Software',          'Software development, engineering, and IT',                        true, NOW()),
    ('data-analytics',            'Data & Analytics',               'Data science, analytics, AI, and machine learning',                true, NOW()),
    ('cybersecurity-networks',    'Cybersecurity & Networks',       'Information security, network infrastructure, and IT operations',  true, NOW()),
    ('product-design',            'Product & Design',               'Product management, UX/UI design, and technical writing',          true, NOW()),
    ('qa-support',                'QA & Technical Support',         'Quality assurance, testing, and customer technical support',       true, NOW()),
    ('healthcare-medical',        'Healthcare & Medical',           'Clinical care, nursing, pharmacy, therapy, and healthcare admin',  true, NOW()),
    ('finance-banking',           'Finance & Banking',              'Financial analysis, accounting, investment, and risk management',  true, NOW()),
    ('business-operations',       'Business & Operations',          'Business analysis, project management, and operations',            true, NOW()),
    ('marketing-sales',           'Marketing & Sales',              'Digital marketing, sales, growth, and customer success',           true, NOW()),
    ('engineering-non-software',  'Engineering (Non-Software)',     'Mechanical, electrical, civil, chemical, and industrial engineering', true, NOW()),
    ('legal-compliance',          'Legal & Compliance',             'Legal counsel, compliance, regulatory affairs, and contracts',     true, NOW()),
    ('human-resources',           'Human Resources',                'Recruiting, HR management, compensation, and training',            true, NOW()),
    ('education-training',        'Education & Training',           'Instructional design, corporate training, and curriculum development', true, NOW()),
    ('creative-design',           'Creative & Design',              'Graphic design, video, animation, and creative direction',         true, NOW()),
    ('supply-chain-logistics',    'Supply Chain & Logistics',       'Supply chain, logistics, procurement, and inventory',              true, NOW()),
    ('real-estate-construction',  'Real Estate & Construction',     'Real estate, construction management, architecture, and planning', true, NOW()),
    ('research-science',          'Research & Science',             'Scientific research, laboratory, biotech, and pharma',             true, NOW()),
    ('media-communications',      'Media & Communications',         'Public relations, journalism, broadcasting, and communications',   true, NOW()),
    ('manufacturing',             'Manufacturing',                  'Production, quality control, plant operations, and lean manufacturing', true, NOW()),
    ('consulting',                'Consulting',                     'Management consulting, IT consulting, and strategy',               true, NOW())
ON CONFLICT (slug) DO UPDATE SET
    is_active  = EXCLUDED.is_active,
    name       = EXCLUDED.name;

-- ── Job Roles ────────────────────────────────────────────────────────────────
-- search_query = exact string sent to JSearch (lowercase, optimized for results)

-- Technology & Software
INSERT INTO md.job_roles (industry_id, slug, name, search_query, is_active, created_on)
SELECT i.id, r.slug, r.name, r.search_query, true, NOW()
FROM md.industries i,
(VALUES
    ('software-engineer',          'Software Engineer',          'software engineer'),
    ('software-developer',         'Software Developer',         'software developer'),
    ('full-stack-developer',       'Full Stack Developer',       'full stack developer'),
    ('backend-engineer',           'Backend Engineer',           'backend engineer'),
    ('frontend-developer',         'Frontend Developer',         'frontend developer'),
    ('mobile-developer',           'Mobile Developer',           'mobile developer'),
    ('android-developer',          'Android Developer',          'android developer'),
    ('ios-developer',              'iOS Developer',              'ios developer'),
    ('devops-engineer',            'DevOps Engineer',            'devops engineer'),
    ('site-reliability-engineer',  'Site Reliability Engineer',  'site reliability engineer'),
    ('cloud-engineer',             'Cloud Engineer',             'cloud engineer aws azure'),
    ('solutions-architect',        'Solutions Architect',        'solutions architect'),
    ('platform-engineer',          'Platform Engineer',          'platform engineer'),
    ('embedded-systems-engineer',  'Embedded Systems Engineer',  'embedded systems engineer'),
    ('game-developer',             'Game Developer',             'game developer unity unreal')
) AS r(slug, name, search_query)
WHERE i.slug = 'technology-software'
ON CONFLICT (slug) DO UPDATE SET search_query = EXCLUDED.search_query, is_active = true;

-- Data & Analytics
INSERT INTO md.job_roles (industry_id, slug, name, search_query, is_active, created_on)
SELECT i.id, r.slug, r.name, r.search_query, true, NOW()
FROM md.industries i,
(VALUES
    ('data-scientist',              'Data Scientist',               'data scientist'),
    ('data-engineer',               'Data Engineer',                'data engineer'),
    ('data-analyst',                'Data Analyst',                 'data analyst'),
    ('machine-learning-engineer',   'Machine Learning Engineer',    'machine learning engineer'),
    ('ai-engineer',                 'AI Engineer',                  'artificial intelligence engineer'),
    ('bi-analyst',                  'Business Intelligence Analyst','business intelligence analyst'),
    ('database-administrator',      'Database Administrator',       'database administrator'),
    ('data-architect',              'Data Architect',               'data architect'),
    ('statistician',                'Statistician',                 'statistician data'),
    ('nlp-engineer',                'NLP Engineer',                 'natural language processing engineer'),
    ('computer-vision-engineer',    'Computer Vision Engineer',     'computer vision engineer'),
    ('analytics-engineer',          'Analytics Engineer',           'analytics engineer dbt')
) AS r(slug, name, search_query)
WHERE i.slug = 'data-analytics'
ON CONFLICT (slug) DO UPDATE SET search_query = EXCLUDED.search_query, is_active = true;

-- Cybersecurity & Networks
INSERT INTO md.job_roles (industry_id, slug, name, search_query, is_active, created_on)
SELECT i.id, r.slug, r.name, r.search_query, true, NOW()
FROM md.industries i,
(VALUES
    ('cybersecurity-analyst',         'Cybersecurity Analyst',          'cybersecurity analyst'),
    ('network-engineer',              'Network Engineer',               'network engineer'),
    ('systems-administrator',         'Systems Administrator',          'systems administrator'),
    ('information-security-engineer', 'Information Security Engineer',  'information security engineer'),
    ('penetration-tester',            'Penetration Tester',             'penetration tester ethical hacker'),
    ('security-operations-analyst',   'Security Operations Analyst',    'security operations center analyst'),
    ('cloud-security-engineer',       'Cloud Security Engineer',        'cloud security engineer'),
    ('it-security-manager',           'IT Security Manager',            'it security manager'),
    ('devsecops-engineer',            'DevSecOps Engineer',             'devsecops engineer')
) AS r(slug, name, search_query)
WHERE i.slug = 'cybersecurity-networks'
ON CONFLICT (slug) DO UPDATE SET search_query = EXCLUDED.search_query, is_active = true;

-- Product & Design
INSERT INTO md.job_roles (industry_id, slug, name, search_query, is_active, created_on)
SELECT i.id, r.slug, r.name, r.search_query, true, NOW()
FROM md.industries i,
(VALUES
    ('product-manager',       'Product Manager',        'product manager'),
    ('product-designer',      'Product Designer',       'product designer'),
    ('ux-designer',           'UX Designer',            'ux designer user experience'),
    ('ui-designer',           'UI Designer',            'ui designer'),
    ('ux-researcher',         'UX Researcher',          'ux researcher user research'),
    ('technical-writer',      'Technical Writer',       'technical writer'),
    ('product-analyst',       'Product Analyst',        'product analyst'),
    ('design-systems',        'Design Systems Engineer','design systems engineer')
) AS r(slug, name, search_query)
WHERE i.slug = 'product-design'
ON CONFLICT (slug) DO UPDATE SET search_query = EXCLUDED.search_query, is_active = true;

-- QA & Technical Support
INSERT INTO md.job_roles (industry_id, slug, name, search_query, is_active, created_on)
SELECT i.id, r.slug, r.name, r.search_query, true, NOW()
FROM md.industries i,
(VALUES
    ('qa-engineer',              'QA Engineer',               'quality assurance engineer'),
    ('test-engineer',            'Test Engineer',             'test engineer automation'),
    ('technical-support',        'Technical Support Engineer','technical support engineer'),
    ('sdet',                     'SDET',                      'software development engineer in test'),
    ('performance-test-engineer','Performance Test Engineer', 'performance test engineer load testing')
) AS r(slug, name, search_query)
WHERE i.slug = 'qa-support'
ON CONFLICT (slug) DO UPDATE SET search_query = EXCLUDED.search_query, is_active = true;

-- Healthcare & Medical
INSERT INTO md.job_roles (industry_id, slug, name, search_query, is_active, created_on)
SELECT i.id, r.slug, r.name, r.search_query, true, NOW()
FROM md.industries i,
(VALUES
    ('registered-nurse',              'Registered Nurse',               'registered nurse'),
    ('physician',                     'Physician',                      'physician doctor'),
    ('pharmacist',                    'Pharmacist',                     'pharmacist'),
    ('physical-therapist',            'Physical Therapist',             'physical therapist'),
    ('medical-coder',                 'Medical Coder',                  'medical coder billing'),
    ('healthcare-analyst',            'Healthcare Analyst',             'healthcare analyst'),
    ('radiologist',                   'Radiologist',                    'radiologist'),
    ('occupational-therapist',        'Occupational Therapist',         'occupational therapist'),
    ('healthcare-administrator',      'Healthcare Administrator',       'healthcare administrator'),
    ('clinical-research-coordinator', 'Clinical Research Coordinator',  'clinical research coordinator'),
    ('medical-lab-technician',        'Medical Laboratory Technician',  'medical laboratory technician'),
    ('health-information-manager',    'Health Information Manager',     'health information manager'),
    ('dental-hygienist',              'Dental Hygienist',               'dental hygienist'),
    ('speech-language-pathologist',   'Speech Language Pathologist',    'speech language pathologist'),
    ('nurse-practitioner',            'Nurse Practitioner',             'nurse practitioner')
) AS r(slug, name, search_query)
WHERE i.slug = 'healthcare-medical'
ON CONFLICT (slug) DO UPDATE SET search_query = EXCLUDED.search_query, is_active = true;

-- Finance & Banking
INSERT INTO md.job_roles (industry_id, slug, name, search_query, is_active, created_on)
SELECT i.id, r.slug, r.name, r.search_query, true, NOW()
FROM md.industries i,
(VALUES
    ('financial-analyst',     'Financial Analyst',      'financial analyst'),
    ('accountant',            'Accountant',             'accountant'),
    ('investment-analyst',    'Investment Analyst',     'investment analyst'),
    ('risk-analyst',          'Risk Analyst',           'risk analyst'),
    ('compliance-analyst',    'Compliance Analyst',     'compliance analyst'),
    ('financial-advisor',     'Financial Advisor',      'financial advisor'),
    ('actuary',               'Actuary',                'actuary'),
    ('credit-analyst',        'Credit Analyst',         'credit analyst'),
    ('treasury-analyst',      'Treasury Analyst',       'treasury analyst'),
    ('tax-accountant',        'Tax Accountant',         'tax accountant'),
    ('auditor',               'Auditor',                'auditor'),
    ('quantitative-analyst',  'Quantitative Analyst',  'quantitative analyst quant'),
    ('fund-accountant',       'Fund Accountant',        'fund accountant'),
    ('banking-analyst',       'Banking Analyst',        'banking analyst investment banking')
) AS r(slug, name, search_query)
WHERE i.slug = 'finance-banking'
ON CONFLICT (slug) DO UPDATE SET search_query = EXCLUDED.search_query, is_active = true;

-- Business & Operations
INSERT INTO md.job_roles (industry_id, slug, name, search_query, is_active, created_on)
SELECT i.id, r.slug, r.name, r.search_query, true, NOW()
FROM md.industries i,
(VALUES
    ('business-analyst',          'Business Analyst',           'business analyst'),
    ('project-manager',           'Project Manager',            'project manager'),
    ('program-manager',           'Program Manager',            'program manager'),
    ('operations-manager',        'Operations Manager',         'operations manager'),
    ('scrum-master',              'Scrum Master',               'scrum master agile'),
    ('business-development',      'Business Development Manager','business development manager'),
    ('strategy-analyst',          'Strategy Analyst',           'strategy analyst'),
    ('management-consultant',     'Management Consultant',      'management consultant'),
    ('operations-research-analyst','Operations Research Analyst','operations research analyst'),
    ('change-management',         'Change Management Analyst',  'change management analyst'),
    ('process-improvement',       'Process Improvement Manager','process improvement manager')
) AS r(slug, name, search_query)
WHERE i.slug = 'business-operations'
ON CONFLICT (slug) DO UPDATE SET search_query = EXCLUDED.search_query, is_active = true;

-- Marketing & Sales
INSERT INTO md.job_roles (industry_id, slug, name, search_query, is_active, created_on)
SELECT i.id, r.slug, r.name, r.search_query, true, NOW()
FROM md.industries i,
(VALUES
    ('marketing-manager',          'Marketing Manager',            'marketing manager'),
    ('digital-marketing-specialist','Digital Marketing Specialist', 'digital marketing specialist'),
    ('sales-manager',              'Sales Manager',                'sales manager'),
    ('account-executive',          'Account Executive',            'account executive'),
    ('growth-analyst',             'Growth Analyst',               'growth analyst'),
    ('content-marketer',           'Content Marketer',             'content marketer'),
    ('seo-specialist',             'SEO Specialist',               'seo specialist'),
    ('brand-manager',              'Brand Manager',                'brand manager'),
    ('social-media-manager',       'Social Media Manager',         'social media manager'),
    ('email-marketing-specialist', 'Email Marketing Specialist',   'email marketing specialist'),
    ('sales-development-rep',      'Sales Development Representative','sales development representative'),
    ('customer-success-manager',   'Customer Success Manager',     'customer success manager'),
    ('demand-generation-manager',  'Demand Generation Manager',    'demand generation manager'),
    ('performance-marketing',      'Performance Marketing Manager','performance marketing manager')
) AS r(slug, name, search_query)
WHERE i.slug = 'marketing-sales'
ON CONFLICT (slug) DO UPDATE SET search_query = EXCLUDED.search_query, is_active = true;

-- Engineering (Non-Software)
INSERT INTO md.job_roles (industry_id, slug, name, search_query, is_active, created_on)
SELECT i.id, r.slug, r.name, r.search_query, true, NOW()
FROM md.industries i,
(VALUES
    ('mechanical-engineer',     'Mechanical Engineer',      'mechanical engineer'),
    ('electrical-engineer',     'Electrical Engineer',      'electrical engineer'),
    ('civil-engineer',          'Civil Engineer',           'civil engineer'),
    ('chemical-engineer',       'Chemical Engineer',        'chemical engineer'),
    ('manufacturing-engineer',  'Manufacturing Engineer',   'manufacturing engineer'),
    ('aerospace-engineer',      'Aerospace Engineer',       'aerospace engineer'),
    ('biomedical-engineer',     'Biomedical Engineer',      'biomedical engineer'),
    ('industrial-engineer',     'Industrial Engineer',      'industrial engineer'),
    ('environmental-engineer',  'Environmental Engineer',   'environmental engineer'),
    ('structural-engineer',     'Structural Engineer',      'structural engineer'),
    ('process-engineer',        'Process Engineer',         'process engineer'),
    ('systems-engineer',        'Systems Engineer',         'systems engineer'),
    ('controls-engineer',       'Controls Engineer',        'controls engineer automation'),
    ('hvac-engineer',           'HVAC Engineer',            'hvac engineer mechanical')
) AS r(slug, name, search_query)
WHERE i.slug = 'engineering-non-software'
ON CONFLICT (slug) DO UPDATE SET search_query = EXCLUDED.search_query, is_active = true;

-- Legal & Compliance
INSERT INTO md.job_roles (industry_id, slug, name, search_query, is_active, created_on)
SELECT i.id, r.slug, r.name, r.search_query, true, NOW()
FROM md.industries i,
(VALUES
    ('attorney',                  'Attorney',                     'attorney lawyer'),
    ('paralegal',                 'Paralegal',                    'paralegal'),
    ('compliance-officer',        'Compliance Officer',           'compliance officer'),
    ('regulatory-affairs',        'Regulatory Affairs Specialist','regulatory affairs specialist'),
    ('corporate-counsel',         'Corporate Counsel',            'corporate counsel'),
    ('legal-analyst',             'Legal Analyst',                'legal analyst'),
    ('contract-manager',          'Contract Manager',             'contract manager'),
    ('privacy-counsel',           'Privacy Counsel',              'privacy counsel data privacy')
) AS r(slug, name, search_query)
WHERE i.slug = 'legal-compliance'
ON CONFLICT (slug) DO UPDATE SET search_query = EXCLUDED.search_query, is_active = true;

-- Human Resources
INSERT INTO md.job_roles (industry_id, slug, name, search_query, is_active, created_on)
SELECT i.id, r.slug, r.name, r.search_query, true, NOW()
FROM md.industries i,
(VALUES
    ('hr-manager',              'Human Resources Manager',      'human resources manager'),
    ('recruiter',               'Recruiter',                    'recruiter talent acquisition'),
    ('talent-acquisition',      'Talent Acquisition Specialist','talent acquisition specialist'),
    ('hr-business-partner',     'HR Business Partner',          'hr business partner'),
    ('compensation-analyst',    'Compensation Analyst',         'compensation benefits analyst'),
    ('training-specialist',     'Training Specialist',          'training development specialist'),
    ('hr-analyst',              'HR Analyst',                   'hr analyst human resources'),
    ('people-operations',       'People Operations Manager',    'people operations manager')
) AS r(slug, name, search_query)
WHERE i.slug = 'human-resources'
ON CONFLICT (slug) DO UPDATE SET search_query = EXCLUDED.search_query, is_active = true;

-- Education & Training
INSERT INTO md.job_roles (industry_id, slug, name, search_query, is_active, created_on)
SELECT i.id, r.slug, r.name, r.search_query, true, NOW()
FROM md.industries i,
(VALUES
    ('instructional-designer',  'Instructional Designer',   'instructional designer'),
    ('corporate-trainer',       'Corporate Trainer',         'corporate trainer'),
    ('curriculum-developer',    'Curriculum Developer',      'curriculum developer'),
    ('elearning-developer',     'eLearning Developer',       'elearning developer'),
    ('learning-development',    'Learning & Development Manager','learning development manager')
) AS r(slug, name, search_query)
WHERE i.slug = 'education-training'
ON CONFLICT (slug) DO UPDATE SET search_query = EXCLUDED.search_query, is_active = true;

-- Creative & Design
INSERT INTO md.job_roles (industry_id, slug, name, search_query, is_active, created_on)
SELECT i.id, r.slug, r.name, r.search_query, true, NOW()
FROM md.industries i,
(VALUES
    ('graphic-designer',    'Graphic Designer',     'graphic designer'),
    ('video-editor',        'Video Editor',          'video editor'),
    ('motion-designer',     'Motion Graphics Designer','motion graphics designer'),
    ('content-creator',     'Content Creator',       'content creator'),
    ('art-director',        'Art Director',          'art director'),
    ('creative-director',   'Creative Director',     'creative director'),
    ('animator',            'Animator',              'animator 3d animation'),
    ('illustrator',         'Illustrator',           'illustrator graphic artist')
) AS r(slug, name, search_query)
WHERE i.slug = 'creative-design'
ON CONFLICT (slug) DO UPDATE SET search_query = EXCLUDED.search_query, is_active = true;

-- Supply Chain & Logistics
INSERT INTO md.job_roles (industry_id, slug, name, search_query, is_active, created_on)
SELECT i.id, r.slug, r.name, r.search_query, true, NOW()
FROM md.industries i,
(VALUES
    ('logistics-coordinator',   'Logistics Coordinator',    'logistics coordinator'),
    ('supply-chain-analyst',    'Supply Chain Analyst',     'supply chain analyst'),
    ('procurement-manager',     'Procurement Manager',      'procurement manager'),
    ('warehouse-manager',       'Warehouse Manager',        'warehouse manager'),
    ('transportation-manager',  'Transportation Manager',   'transportation manager'),
    ('inventory-analyst',       'Inventory Analyst',        'inventory analyst'),
    ('demand-planner',          'Demand Planner',           'demand planner'),
    ('import-export-specialist','Import Export Specialist',  'import export specialist')
) AS r(slug, name, search_query)
WHERE i.slug = 'supply-chain-logistics'
ON CONFLICT (slug) DO UPDATE SET search_query = EXCLUDED.search_query, is_active = true;

-- Real Estate & Construction
INSERT INTO md.job_roles (industry_id, slug, name, search_query, is_active, created_on)
SELECT i.id, r.slug, r.name, r.search_query, true, NOW()
FROM md.industries i,
(VALUES
    ('real-estate-analyst',     'Real Estate Analyst',      'real estate analyst'),
    ('construction-manager',    'Construction Manager',     'construction manager'),
    ('property-manager',        'Property Manager',         'property manager'),
    ('architect',               'Architect',                'architect'),
    ('urban-planner',           'Urban Planner',            'urban planner'),
    ('estimator',               'Estimator',                'construction estimator'),
    ('project-superintendent',  'Project Superintendent',   'project superintendent construction')
) AS r(slug, name, search_query)
WHERE i.slug = 'real-estate-construction'
ON CONFLICT (slug) DO UPDATE SET search_query = EXCLUDED.search_query, is_active = true;

-- Research & Science
INSERT INTO md.job_roles (industry_id, slug, name, search_query, is_active, created_on)
SELECT i.id, r.slug, r.name, r.search_query, true, NOW()
FROM md.industries i,
(VALUES
    ('research-scientist',      'Research Scientist',           'research scientist'),
    ('lab-technician',          'Laboratory Technician',        'laboratory technician'),
    ('biotech-scientist',       'Biotech Research Scientist',   'biotech research scientist'),
    ('pharmaceutical-scientist','Pharmaceutical Scientist',     'pharmaceutical scientist'),
    ('materials-scientist',     'Materials Scientist',          'materials scientist'),
    ('environmental-scientist', 'Environmental Scientist',      'environmental scientist'),
    ('clinical-scientist',      'Clinical Scientist',           'clinical scientist'),
    ('genomics-scientist',      'Genomics Scientist',           'genomics bioinformatics scientist')
) AS r(slug, name, search_query)
WHERE i.slug = 'research-science'
ON CONFLICT (slug) DO UPDATE SET search_query = EXCLUDED.search_query, is_active = true;

-- Media & Communications
INSERT INTO md.job_roles (industry_id, slug, name, search_query, is_active, created_on)
SELECT i.id, r.slug, r.name, r.search_query, true, NOW()
FROM md.industries i,
(VALUES
    ('communications-manager',  'Communications Manager',       'communications manager'),
    ('public-relations',        'Public Relations Specialist',  'public relations specialist'),
    ('journalist',              'Journalist',                   'journalist reporter'),
    ('media-analyst',           'Media Analyst',                'media analyst'),
    ('copywriter',              'Copywriter',                   'copywriter content writer'),
    ('broadcast-producer',      'Broadcast Producer',           'broadcast producer')
) AS r(slug, name, search_query)
WHERE i.slug = 'media-communications'
ON CONFLICT (slug) DO UPDATE SET search_query = EXCLUDED.search_query, is_active = true;

-- Manufacturing
INSERT INTO md.job_roles (industry_id, slug, name, search_query, is_active, created_on)
SELECT i.id, r.slug, r.name, r.search_query, true, NOW()
FROM md.industries i,
(VALUES
    ('production-manager',      'Production Manager',           'production manager manufacturing'),
    ('quality-control',         'Quality Control Inspector',    'quality control inspector'),
    ('plant-manager',           'Plant Manager',                'plant manager'),
    ('lean-engineer',           'Lean Engineer',                'lean engineer six sigma'),
    ('manufacturing-technician','Manufacturing Technician',     'manufacturing technician'),
    ('cnc-machinist',           'CNC Machinist',                'cnc machinist programmer'),
    ('maintenance-engineer',    'Maintenance Engineer',         'maintenance engineer')
) AS r(slug, name, search_query)
WHERE i.slug = 'manufacturing'
ON CONFLICT (slug) DO UPDATE SET search_query = EXCLUDED.search_query, is_active = true;

-- Consulting
INSERT INTO md.job_roles (industry_id, slug, name, search_query, is_active, created_on)
SELECT i.id, r.slug, r.name, r.search_query, true, NOW()
FROM md.industries i,
(VALUES
    ('it-consultant',           'IT Consultant',                'it consultant'),
    ('strategy-consultant',     'Strategy Consultant',          'strategy consultant'),
    ('business-process',        'Business Process Consultant',  'business process consultant'),
    ('technology-consultant',   'Technology Consultant',        'technology consultant'),
    ('erp-consultant',          'ERP Consultant',               'erp consultant sap oracle'),
    ('sap-consultant',          'SAP Consultant',               'sap consultant'),
    ('salesforce-consultant',   'Salesforce Consultant',        'salesforce consultant')
) AS r(slug, name, search_query)
WHERE i.slug = 'consulting'
ON CONFLICT (slug) DO UPDATE SET search_query = EXCLUDED.search_query, is_active = true;

-- Entry-level broad sweeps (no specific industry — link to technology as default)
INSERT INTO md.job_roles (industry_id, slug, name, search_query, is_active, created_on)
SELECT i.id, r.slug, r.name, r.search_query, true, NOW()
FROM md.industries i,
(VALUES
    ('entry-level-engineer',        'Entry Level Engineer',         'entry level engineer'),
    ('entry-level-analyst',         'Entry Level Analyst',          'entry level analyst'),
    ('junior-developer',            'Junior Developer',             'junior developer'),
    ('associate-product-manager',   'Associate Product Manager',    'associate product manager'),
    ('internship-engineering',      'Engineering Intern',           'engineering internship'),
    ('internship-data',             'Data Science Intern',          'data science internship'),
    ('internship-finance',          'Finance Intern',               'finance internship'),
    ('internship-marketing',        'Marketing Intern',             'marketing internship')
) AS r(slug, name, search_query)
WHERE i.slug = 'technology-software'
ON CONFLICT (slug) DO UPDATE SET search_query = EXCLUDED.search_query, is_active = true;
