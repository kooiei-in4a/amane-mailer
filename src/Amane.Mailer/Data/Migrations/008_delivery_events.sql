CREATE TABLE delivery_events (
    id                      TEXT NOT NULL PRIMARY KEY,
    tenant_id               TEXT NOT NULL,
    source_service          TEXT NOT NULL,
    mail_request_id         TEXT NOT NULL,
    event_type              TEXT NOT NULL
                            CHECK (event_type IN ('delivered', 'failed', 'dead_lettered', 'cancelled')),
    payload_json            TEXT NOT NULL,
    status                  INTEGER NOT NULL DEFAULT 0
                            CHECK (status IN (0, 1, 2, 3)),
    attempt_count           INTEGER NOT NULL DEFAULT 0
                            CHECK (attempt_count >= 0),
    max_attempts            INTEGER NOT NULL
                            CHECK (max_attempts >= 1),
    next_attempt_at         TEXT NULL,
    lock_token              TEXT NULL,
    lock_expires_at         TEXT NULL,
    last_error_code         TEXT NULL,
    created_at              TEXT NOT NULL,
    updated_at              TEXT NOT NULL,
    completed_at            TEXT NULL,
    CONSTRAINT uq_delivery_events_mail_request
        UNIQUE (tenant_id, source_service, mail_request_id)
);

CREATE INDEX IF NOT EXISTS idx_delivery_events_pending_due
    ON delivery_events (next_attempt_at, created_at)
    WHERE status = 0;

CREATE INDEX IF NOT EXISTS idx_delivery_events_delivering_expired
    ON delivery_events (lock_expires_at, created_at)
    WHERE status = 1;

CREATE INDEX IF NOT EXISTS idx_delivery_events_deadletter_completed
    ON delivery_events (status, completed_at DESC)
    WHERE status = 3;
