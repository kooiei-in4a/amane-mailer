CREATE TABLE IF NOT EXISTS schema_migrations (
    version     TEXT NOT NULL PRIMARY KEY,
    applied_at  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS mail_requests (
    id                      TEXT NOT NULL PRIMARY KEY,

    tenant_id               TEXT NOT NULL,
    source_service          TEXT NOT NULL,
    mail_request_id         TEXT NOT NULL,

    purpose                 TEXT NOT NULL,

    payload_json            TEXT NOT NULL,
    payload_hash            TEXT NOT NULL CHECK (length(payload_hash) = 64),

    subject                 TEXT NOT NULL,
    html_body               TEXT NULL,
    text_body               TEXT NULL,
    reply_to                TEXT NULL,

    recipient_email         TEXT NOT NULL,
    recipient_display_name  TEXT NULL,

    metadata_json           TEXT NULL,

    status                  INTEGER NOT NULL DEFAULT 0
                            CHECK (status IN (0, 1, 2, 3, 4)),

    attempt_count           INTEGER NOT NULL DEFAULT 0
                            CHECK (attempt_count >= 0),
    max_attempts            INTEGER NOT NULL
                            CHECK (max_attempts >= 1),

    next_attempt_at         TEXT NULL,

    lock_token              TEXT NULL,
    lock_expires_at         TEXT NULL,

    delivered_at            TEXT NULL,
    failed_at               TEXT NULL,
    last_error_message      TEXT NULL,

    accepted_at             TEXT NOT NULL,
    created_at              TEXT NOT NULL,
    updated_at              TEXT NOT NULL,
    completed_at            TEXT NULL,

    CONSTRAINT uq_mail_requests_idempotency
        UNIQUE (tenant_id, source_service, mail_request_id)
);

CREATE TABLE IF NOT EXISTS mail_attempts (
    id                  INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    request_id          TEXT NOT NULL REFERENCES mail_requests(id) ON DELETE CASCADE,
    attempt_number      INTEGER NOT NULL CHECK (attempt_number >= 1),
    provider            TEXT NOT NULL,
    status              INTEGER NOT NULL CHECK (status IN (2, 3, 4)),
    provider_message_id TEXT NULL,
    error_code          TEXT NULL,
    error_message       TEXT NULL,
    retryable           INTEGER NOT NULL CHECK (retryable IN (0, 1)),
    lock_token          TEXT NOT NULL,
    started_at          TEXT NOT NULL,
    completed_at        TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_mail_requests_queued_due
    ON mail_requests (next_attempt_at, created_at)
    WHERE status = 0;

CREATE INDEX IF NOT EXISTS idx_mail_requests_processing_expired
    ON mail_requests (lock_expires_at, created_at)
    WHERE status = 1;

CREATE INDEX IF NOT EXISTS ix_mail_attempts_request_id
    ON mail_attempts (request_id, attempt_number);
