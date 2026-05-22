-- API application logging table (public schema, separate from cp/md business tables).
CREATE TABLE IF NOT EXISTS public.application_logs (
    id              uuid PRIMARY KEY,
    timestamp       timestamptz NOT NULL DEFAULT now(),
    level           varchar(32) NOT NULL,
    message         text NOT NULL,
    details         text,
    source          varchar(512),
    user_id         varchar(64),
    correlation_id  varchar(64)
);

CREATE INDEX IF NOT EXISTS ix_application_logs_timestamp
    ON public.application_logs (timestamp DESC);
