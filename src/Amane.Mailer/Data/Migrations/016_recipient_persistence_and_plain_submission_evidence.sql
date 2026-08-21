-- Canonical recipient persistence and request-level legacy submission evidence (#544, ADR 0023).
-- Existing rows are backfilled by RecipientPersistenceMigration inside the same transaction;
-- this script intentionally creates schema only.

CREATE TABLE mail_request_recipients (
    request_id              TEXT NOT NULL REFERENCES mail_requests(id) ON DELETE CASCADE,
    recipient_role          INTEGER NOT NULL CHECK (recipient_role IN (0, 1, 2)),
    ordinal                 INTEGER NOT NULL CHECK (ordinal >= 0 AND ordinal <= 9),
    address                 TEXT NOT NULL,
    address_key             TEXT NOT NULL,
    display_name            TEXT NULL,
    delivery_state          INTEGER NOT NULL CHECK (delivery_state IN (0, 1, 2, 3, 4, 5, 6)),
    provider_message_id     TEXT NULL,
    provider_status_detail  TEXT NULL,
    created_at              TEXT NOT NULL,
    updated_at              TEXT NOT NULL,
    PRIMARY KEY (request_id, recipient_role, ordinal),
    CONSTRAINT uq_mail_request_recipients_request_address_key
        UNIQUE (request_id, address_key)
);

CREATE INDEX idx_mail_request_recipients_request
    ON mail_request_recipients (request_id);

CREATE TABLE mail_plain_submissions (
    request_id           TEXT NOT NULL PRIMARY KEY
                         REFERENCES mail_requests(id) ON DELETE CASCADE,
    evidence_state       INTEGER NOT NULL CHECK (evidence_state IN (0, 1, 2, 3, 4)),
    evidence_origin      INTEGER NOT NULL CHECK (evidence_origin IN (0, 1)),
    provider             TEXT NULL,
    claim_token          TEXT NULL,
    started_at           TEXT NULL,
    provider_message_id  TEXT NULL,
    resolved_at          TEXT NULL,
    created_at           TEXT NOT NULL,
    updated_at           TEXT NOT NULL,
    CHECK (
        evidence_origin = 1
        OR (provider IS NOT NULL AND claim_token IS NOT NULL AND started_at IS NOT NULL)
    ),
    CHECK (evidence_origin = 0 OR evidence_state IN (2, 3, 4))
);

CREATE INDEX idx_mail_plain_submissions_state
    ON mail_plain_submissions (evidence_state, started_at);
