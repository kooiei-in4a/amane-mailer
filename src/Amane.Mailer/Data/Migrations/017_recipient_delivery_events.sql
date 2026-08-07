-- Recipient-aware provider delivery feedback history (#553, ADR 0023 D-08).
-- Legacy bounce_events rows are backfilled by RecipientDeliveryEventMigration in the
-- same BEGIN IMMEDIATE transaction before the superseded table is dropped.

CREATE TABLE recipient_delivery_events (
    id                      TEXT NOT NULL PRIMARY KEY,
    tenant_id               TEXT NOT NULL,
    source_service          TEXT NOT NULL,
    mail_request_id         TEXT NOT NULL,
    recipient_role          INTEGER NOT NULL CHECK (recipient_role IN (0, 1, 2)),
    recipient_ordinal       INTEGER NOT NULL CHECK (recipient_ordinal >= 0 AND recipient_ordinal <= 9),
    provider                TEXT NOT NULL,
    provider_event_id       TEXT NOT NULL,
    provider_message_id     TEXT NOT NULL,
    provider_status         TEXT NOT NULL CHECK (length(provider_status) > 0),
    applied_delivery_state  INTEGER NULL CHECK (applied_delivery_state IN (2, 3, 4)),
    status_message          TEXT NULL,
    occurred_at             TEXT NOT NULL,
    created_at              TEXT NOT NULL,
    CONSTRAINT uq_recipient_delivery_events_provider_event
        UNIQUE (provider, provider_event_id)
);

CREATE INDEX ix_recipient_delivery_events_request_occurred
    ON recipient_delivery_events (
        tenant_id, source_service, mail_request_id, occurred_at, id);

CREATE INDEX ix_recipient_delivery_events_provider_message
    ON recipient_delivery_events (provider, provider_message_id);

ALTER TABLE mail_request_recipients ADD COLUMN last_feedback_occurred_at TEXT NULL;
ALTER TABLE mail_request_recipients ADD COLUMN last_feedback_provider TEXT NULL;
ALTER TABLE mail_request_recipients ADD COLUMN last_feedback_event_id TEXT NULL;
