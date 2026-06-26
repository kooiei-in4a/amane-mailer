-- Admin operation audit events (ADR 0013 D-08).
-- Append-only operational audit trail for the experimental Admin UI.
-- PII (recipient, subject, body, metadata values, payload JSON) must never be
-- written to this table. Only actor, timing, normalized network identifiers,
-- the target reference, the operation result, and a short error code are kept.
CREATE TABLE IF NOT EXISTS admin_audit_events (
    id                  INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    event_type          TEXT NOT NULL,
    actor               TEXT NOT NULL,
    occurred_at         TEXT NOT NULL,
    source_ip           TEXT NULL,
    user_agent_summary  TEXT NULL,
    target_type         TEXT NULL,
    target_id           TEXT NULL,
    field_name          TEXT NULL,
    result              TEXT NOT NULL,
    error_code          TEXT NULL
);

CREATE INDEX IF NOT EXISTS idx_admin_audit_events_occurred_at
    ON admin_audit_events (occurred_at DESC, id DESC);
