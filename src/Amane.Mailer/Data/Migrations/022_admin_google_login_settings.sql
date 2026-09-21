-- Issue #806: Admin UI managed Google Login settings on instance_configuration.
-- Client Secret stays in a protected file referenced by google_client_secret_ref;
-- never persist the secret plaintext in SQLite.
ALTER TABLE instance_configuration
ADD COLUMN google_login_enabled INTEGER NOT NULL DEFAULT 0
    CHECK (google_login_enabled IN (0, 1));

ALTER TABLE instance_configuration
ADD COLUMN google_client_id TEXT NULL;

ALTER TABLE instance_configuration
ADD COLUMN google_client_secret_ref TEXT NULL;

ALTER TABLE instance_configuration
ADD COLUMN google_configured_at TEXT NULL;
