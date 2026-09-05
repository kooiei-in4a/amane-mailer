-- Issue #731: one-way instance initialization gate and the first-run provider state.
-- Setup progress is intentionally derived from this row, admin_users, senders, and
-- the protected provider secret file; no setup workflow history is persisted.
CREATE TABLE instance_configuration (
    id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
    initialized_at TEXT NULL,
    live_sending INTEGER NOT NULL DEFAULT 0 CHECK (live_sending IN (0, 1)),
    provider_type TEXT NULL,
    provider_secret_ref TEXT NULL,
    provider_configured_at TEXT NULL
);

INSERT INTO instance_configuration (id, initialized_at, live_sending)
VALUES (1, NULL, 0);

-- Keep the retained admin persistence graph compatible while distinguishing the
-- instance owner from the existing break-glass product capability.
ALTER TABLE admin_users
ADD COLUMN is_instance_owner INTEGER NOT NULL DEFAULT 0
    CHECK (is_instance_owner IN (0, 1));
