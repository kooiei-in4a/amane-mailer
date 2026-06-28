-- Admin server-side sessions, credential epoch, and durable login throttle (ADR 0014 Phase 1).
-- Stores opaque session ids, applied password hash / credential epoch for immediate revocation,
-- and restart-durable login throttle state. No PII beyond normalized actor username.

CREATE TABLE IF NOT EXISTS admin_config (
    id                      INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
    applied_password_hash   TEXT NOT NULL,
    credential_epoch        INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS admin_sessions (
    session_id              TEXT NOT NULL PRIMARY KEY,
    actor                   TEXT NOT NULL,
    issued_at               TEXT NOT NULL,
    last_seen_at            TEXT NOT NULL,
    absolute_expires_at     TEXT NOT NULL,
    idle_expires_at         TEXT NOT NULL,
    revoked_at              TEXT NULL,
    revoke_reason           TEXT NULL,
    credential_epoch        INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_admin_sessions_actor_active
    ON admin_sessions (actor, revoked_at, issued_at);

CREATE TABLE IF NOT EXISTS admin_login_throttle (
    throttle_key            TEXT NOT NULL PRIMARY KEY,
    failure_count           INTEGER NOT NULL,
    locked_until            TEXT NULL,
    updated_at              TEXT NOT NULL
);
