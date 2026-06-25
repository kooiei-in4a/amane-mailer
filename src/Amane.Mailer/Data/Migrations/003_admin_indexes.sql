CREATE INDEX IF NOT EXISTS idx_mail_requests_status_updated
    ON mail_requests (status, updated_at DESC);

CREATE INDEX IF NOT EXISTS idx_mail_requests_tenant_status_updated
    ON mail_requests (tenant_id, status, updated_at DESC);

CREATE INDEX IF NOT EXISTS idx_mail_requests_source_service_status_updated
    ON mail_requests (source_service, status, updated_at DESC);

CREATE INDEX IF NOT EXISTS idx_mail_requests_deadletter_completed
    ON mail_requests (status, completed_at DESC)
    WHERE status = 4;

DROP INDEX IF EXISTS ix_mail_attempts_request_id;

CREATE INDEX IF NOT EXISTS idx_mail_attempts_request_id_attempt
    ON mail_attempts (request_id, attempt_number ASC);
