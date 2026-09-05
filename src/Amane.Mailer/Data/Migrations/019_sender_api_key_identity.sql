-- Issue #730: Sender is the durable owner; API keys are revocable credentials.
-- The migration runner rejects populated v1 state before applying this schema.
CREATE TABLE senders (
    sender_id       TEXT NOT NULL PRIMARY KEY,
    email           TEXT NOT NULL UNIQUE,
    display_name    TEXT NULL,
    enabled         INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    created_at      TEXT NOT NULL,
    disabled_at     TEXT NULL,
    CHECK ((enabled = 1 AND disabled_at IS NULL) OR enabled = 0)
);

CREATE TABLE api_keys (
    key_id          TEXT NOT NULL PRIMARY KEY,
    sender_id       TEXT NOT NULL REFERENCES senders(sender_id),
    name            TEXT NOT NULL CHECK (length(name) > 0),
    secret_digest   BLOB NOT NULL CHECK (length(secret_digest) = 32),
    created_at      TEXT NOT NULL,
    revoked_at      TEXT NULL
);

CREATE INDEX ix_api_keys_sender_id ON api_keys (sender_id, created_at);

ALTER TABLE mail_requests
ADD COLUMN accepted_api_key_id TEXT NULL REFERENCES api_keys(key_id);
