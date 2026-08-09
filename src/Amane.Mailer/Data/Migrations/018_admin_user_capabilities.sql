-- Explicit Admin capability grants (Issue #560, ADR 0014 D-07 / ADR 0023 D-09).
-- Capabilities are default-deny and never contain recipient data.

CREATE TABLE admin_user_capabilities (
    admin_user_id INTEGER NOT NULL
        REFERENCES admin_users(id) ON DELETE CASCADE,
    capability    TEXT NOT NULL CHECK (length(capability) > 0),
    PRIMARY KEY (admin_user_id, capability)
);
