-- Add first-dispatch schedule column, independent from next_attempt_at (retry backoff).
-- NULL scheduled_at means immediate (existing rows unchanged).
ALTER TABLE mail_requests ADD COLUMN scheduled_at TEXT NULL;

DROP INDEX IF EXISTS idx_mail_requests_queued_due;

CREATE INDEX IF NOT EXISTS idx_mail_requests_queued_due
    ON mail_requests (scheduled_at, next_attempt_at, created_at)
    WHERE status = 0;
