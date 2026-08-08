-- Attachment spool metadata, request-unique provider submission evidence, and the durable
-- backup maintenance lease (ADR 0022 D-08/D-09, #533).
--
-- mail_attempts.status CHECK is widened to include DeliveryUnknown (6) so per-attempt history
-- can record an ambiguous outcome; the same 007/014-style table rewrite idiom applies since
-- SQLite cannot ALTER a CHECK constraint. No other table has a foreign key into mail_attempts,
-- so the rewrite does not need a backup/restore step.

CREATE TABLE mail_attempts_new (
    id                  INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    request_id          TEXT NOT NULL REFERENCES mail_requests(id) ON DELETE CASCADE,
    attempt_number      INTEGER NOT NULL CHECK (attempt_number >= 1),
    provider            TEXT NOT NULL,
    status              INTEGER NOT NULL CHECK (status IN (2, 3, 4, 6)),
    provider_message_id TEXT NULL,
    error_code          TEXT NULL,
    error_message       TEXT NULL,
    retryable           INTEGER NOT NULL CHECK (retryable IN (0, 1)),
    lock_token          TEXT NOT NULL,
    started_at          TEXT NOT NULL,
    completed_at        TEXT NOT NULL
);

INSERT INTO mail_attempts_new
SELECT
    id, request_id, attempt_number, provider, status, provider_message_id,
    error_code, error_message, retryable, lock_token, started_at, completed_at
FROM mail_attempts;

DROP TABLE mail_attempts;

ALTER TABLE mail_attempts_new RENAME TO mail_attempts;

CREATE INDEX IF NOT EXISTS idx_mail_attempts_request_id_attempt
    ON mail_attempts (request_id, attempt_number ASC);

CREATE INDEX IF NOT EXISTS ix_mail_attempts_provider_message_id
    ON mail_attempts (provider_message_id)
    WHERE provider_message_id IS NOT NULL;

-- attachment_count is a denormalized helper: it lets the Worker and manual retry/cancel paths
-- decide "is this an attachment request" without a join, and is only ever set once at accept
-- time from the same canonical attachment rows inserted in the same transaction (below).
-- delivery_unknown_at is a dedicated terminal timestamp so DeliveryUnknown is never conflated
-- with a real provider-confirmed failure (failed_at).
ALTER TABLE mail_requests ADD COLUMN attachment_count INTEGER NOT NULL DEFAULT 0
    CHECK (attachment_count >= 0 AND attachment_count <= 5);
ALTER TABLE mail_requests ADD COLUMN delivery_unknown_at TEXT NULL;

CREATE INDEX IF NOT EXISTS idx_mail_requests_delivery_unknown_completed
    ON mail_requests (status, completed_at DESC)
    WHERE status = 6;

-- Canonical attachment metadata (ADR 0022 D-03/D-05/D-06/D-08). One row per attachment,
-- ordered by the original request array position. file_name is stored as submitted
-- (NFC-normalized, validated) and is potential PII (D-14) -- masked/revealed only at the
-- Admin display layer, matching how subject/recipient_email are already handled.
-- spool_key is Mailer-generated and opaque; it never encodes tenant, recipient, subject, or
-- the original filename (D-08 storage identity). No raw Base64 or request body is stored here
-- or anywhere else.
CREATE TABLE mail_request_attachments (
    id                  TEXT NOT NULL PRIMARY KEY,
    request_id          TEXT NOT NULL REFERENCES mail_requests(id) ON DELETE CASCADE,
    attachment_order    INTEGER NOT NULL CHECK (attachment_order >= 0 AND attachment_order <= 4),
    file_name           TEXT NOT NULL,
    content_type        TEXT NOT NULL,
    byte_length         INTEGER NOT NULL CHECK (byte_length >= 0 AND byte_length <= 2097152),
    content_sha256      TEXT NOT NULL CHECK (length(content_sha256) = 64),
    spool_key           TEXT NOT NULL,
    created_at          TEXT NOT NULL,
    CONSTRAINT uq_mail_request_attachments_request_order
        UNIQUE (request_id, attachment_order)
);

-- Request-unique provider submission evidence (ADR 0022 D-08). The PRIMARY KEY on request_id
-- is the DB-level enforcement that an attachment request can never have more than one provider
-- submission lifecycle: a second INSERT attempt for the same request_id fails closed instead of
-- creating a second Started row. submission_state: 0=Started, 1=Succeeded, 2=DefinitiveFailed,
-- 3=Unknown. attempt_number is deliberately not part of this table's identity (#533 / ADR 0022
-- explicit exception to ADR 0015 D-05 attempt_number dispatch-cycle reuse).
CREATE TABLE mail_attachment_submissions (
    request_id              TEXT NOT NULL PRIMARY KEY
                            REFERENCES mail_requests(id) ON DELETE CASCADE,
    submission_state        INTEGER NOT NULL CHECK (submission_state IN (0, 1, 2, 3)),
    provider                TEXT NOT NULL,
    submission_started_at   TEXT NOT NULL,
    lock_token              TEXT NOT NULL,
    provider_message_id     TEXT NULL,
    completed_at             TEXT NULL,
    created_at               TEXT NOT NULL,
    updated_at               TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_mail_attachment_submissions_state
    ON mail_attachment_submissions (submission_state, submission_started_at);

-- Durable cross-process maintenance lease (ADR 0022 D-09). MVP uses a single row keyed by
-- lease_name = 'backup', shared by Admin backup, CLI backup, and the attachment acceptance
-- gate. fencing_token increments only on a fresh acquire (not on heartbeat renewal), so a
-- stale holder's renewal can be distinguished from a new acquire after expiry.
CREATE TABLE mailer_maintenance_leases (
    lease_name       TEXT NOT NULL PRIMARY KEY,
    owner_token      TEXT NOT NULL,
    fencing_token    INTEGER NOT NULL DEFAULT 0,
    expires_at       TEXT NOT NULL,
    acquired_at      TEXT NOT NULL,
    updated_at       TEXT NOT NULL
);
