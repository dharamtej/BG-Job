-- Migration 008: Add schedule columns to cp.api_background_tasks
-- Supports Daily (run at a fixed UTC time) and Interval (every N hours) schedules.
-- Hangfire uses these values to register recurring jobs on app startup.

ALTER TABLE cp.api_background_tasks
    ADD COLUMN IF NOT EXISTS schedule_type          VARCHAR(20)  NULL,
    ADD COLUMN IF NOT EXISTS schedule_daily_time    TIME         NULL,
    ADD COLUMN IF NOT EXISTS schedule_interval_hours INT         NULL,
    ADD COLUMN IF NOT EXISTS next_run_at            TIMESTAMPTZ  NULL,
    ADD COLUMN IF NOT EXISTS last_scheduled_run_at  TIMESTAMPTZ  NULL;

-- Optional: add the new Scheduled status to any CHECK constraint if one exists.
-- If your status column has no constraint, this is a no-op.
-- ALTER TABLE cp.api_background_tasks DROP CONSTRAINT IF EXISTS api_background_tasks_status_check;
