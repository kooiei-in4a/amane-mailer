-- Issue #798: explicit Google identity mapping onto existing admin_users.
-- issuer + subject is the only identity key. Email is not stored and must not
-- be used for automatic linking or login.
CREATE TABLE admin_google_identities (
    admin_user_id INTEGER NOT NULL PRIMARY KEY
        REFERENCES admin_users(id) ON DELETE CASCADE,
    issuer TEXT NOT NULL,
    subject TEXT NOT NULL,
    created_at TEXT NOT NULL,
    CONSTRAINT uq_admin_google_identities_issuer_subject UNIQUE (issuer, subject)
);
