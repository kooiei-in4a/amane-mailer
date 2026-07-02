-- Admin multi-user tenant scope authorization (ADR 0014 Phase 2).
-- Stores admin credentials and tenant scope boundaries. Passwords are PBKDF2
-- hashes only; tenant scopes contain tenant ids but no mail payload PII.

CREATE TABLE IF NOT EXISTS admin_users (
    id                  INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    username            TEXT NOT NULL UNIQUE,
    password_hash       TEXT NOT NULL,
    disabled            INTEGER NOT NULL DEFAULT 0 CHECK (disabled IN (0, 1)),
    credential_epoch    INTEGER NOT NULL DEFAULT 0,
    is_break_glass      INTEGER NOT NULL DEFAULT 0 CHECK (is_break_glass IN (0, 1)),
    created_at          TEXT NOT NULL,
    updated_at          TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS admin_user_tenant_scopes (
    admin_user_id       INTEGER NOT NULL REFERENCES admin_users(id) ON DELETE CASCADE,
    tenant_id           TEXT NOT NULL,
    PRIMARY KEY (admin_user_id, tenant_id)
);

CREATE INDEX IF NOT EXISTS idx_admin_user_tenant_scopes_tenant
    ON admin_user_tenant_scopes (tenant_id, admin_user_id);
