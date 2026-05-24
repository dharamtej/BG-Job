-- 009_seed_jobicy_industries.sql
-- Seeds md.industries with Jobicy's 21 industry categories.
-- Uses INSERT ... ON CONFLICT (slug) DO NOTHING — safe to run multiple times.
-- Jobicy's jobIndustry[] field returns these industry names in job listings.

INSERT INTO md.industries (slug, name, is_active, created_on)
VALUES
    ('admin-support',      'Admin & Virtual Assistant',  true, NOW()),
    ('business',           'Business Development',       true, NOW()),
    ('copywriting',        'Content & Editorial',        true, NOW()),
    ('design-multimedia',  'Creative & Design',          true, NOW()),
    ('supporting',         'Customer Success',           true, NOW()),
    ('data-science',       'Data Science & Analytics',   true, NOW()),
    ('admin',              'DevOps & Infrastructure',    true, NOW()),
    ('education',          'Education & E-learning',     true, NOW()),
    ('accounting-finance', 'Finance & Accounting',       true, NOW()),
    ('healthcare',         'Healthcare & Medical',       true, NOW()),
    ('hr',                 'HR & Recruiting',            true, NOW()),
    ('legal',              'Legal & Compliance',         true, NOW()),
    ('marketing',          'Marketing & Sales',          true, NOW()),
    ('management',         'Product & Operations',       true, NOW()),
    ('dev',                'Programming',                true, NOW()),
    ('seller',             'Sales',                      true, NOW()),
    ('seo',                'SEO',                        true, NOW()),
    ('smm',                'Social Media Marketing',     true, NOW()),
    ('engineering',        'Software Engineering',       true, NOW()),
    ('technical-support',  'Technical Support',          true, NOW()),
    ('web-app-design',     'Web & App Design',           true, NOW())
ON CONFLICT (slug) DO NOTHING;
