-- Persist sanitized ACS delivery-report details on the inbox (#460 / ADR 0020 D-08).
-- status_message must be sanitized before insert; raw provider JSON is never stored.
-- occurred_at holds Event Grid eventTime when present; NULL means Worker falls back
-- to processing time when writing bounce_events.
ALTER TABLE provider_event_inbox ADD COLUMN status_message TEXT NULL;
ALTER TABLE provider_event_inbox ADD COLUMN occurred_at TEXT NULL;
