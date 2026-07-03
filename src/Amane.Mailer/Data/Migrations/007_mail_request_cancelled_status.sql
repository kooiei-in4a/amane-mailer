-- Expand mail_requests.status CHECK to include Cancelled (5).
-- SQLite cannot ALTER CHECK constraints; recreate the table in-place.

PRAGMA foreign_keys = OFF;

CREATE TABLE mail_requests_new (
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
                            CHECK (status IN (0, 1, 2, 3, 4, 5)),

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

INSERT INTO mail_requests_new
SELECT
    id, tenant_id, source_service, mail_request_id, purpose,
    payload_json, payload_hash, subject, html_body, text_body, reply_to,
    recipient_email, recipient_display_name, metadata_json,
    status, attempt_count, max_attempts, next_attempt_at,
    lock_token, lock_expires_at, delivered_at, failed_at, last_error_message,
    accepted_at, created_at, updated_at, completed_at
FROM mail_requests;

DROP TABLE mail_requests;

ALTER TABLE mail_requests_new RENAME TO mail_requests;

CREATE INDEX IF NOT EXISTS idx_mail_requests_queued_due
    ON mail_requests (next_attempt_at, created_at)
    WHERE status = 0;

CREATE INDEX IF NOT EXISTS idx_mail_requests_processing_expired
    ON mail_requests (lock_expires_at, created_at)
    WHERE status = 1;

CREATE INDEX IF NOT EXISTS idx_mail_requests_status_updated
    ON mail_requests (status, updated_at DESC);

CREATE INDEX IF NOT EXISTS idx_mail_requests_tenant_status_updated
    ON mail_requests (tenant_id, status, updated_at DESC);

CREATE INDEX IF NOT EXISTS idx_mail_requests_source_service_status_updated
    ON mail_requests (source_service, status, updated_at DESC);

CREATE INDEX IF NOT EXISTS idx_mail_requests_deadletter_completed
    ON mail_requests (status, completed_at DESC)
    WHERE status = 4;

PRAGMA foreign_keys = ON;
