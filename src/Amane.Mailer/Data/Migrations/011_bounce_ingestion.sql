-- Bounce ingestion and suppression persistence (ADR 0020 / #301).
-- provider_event_inbox lease idiom mirrors delivery_events (008), including
-- #388/#402 terminal convergence for expired leases at max_attempts.
-- Raw provider event JSON is intentionally not stored (#26).

CREATE TABLE provider_event_inbox (
    id                      TEXT NOT NULL PRIMARY KEY,
    provider                TEXT NOT NULL,
    event_id                TEXT NOT NULL,
    provider_message_id     TEXT NULL,
    delivery_status         TEXT NULL,
    recipient_email         TEXT NULL,
    status                  INTEGER NOT NULL DEFAULT 0
                            CHECK (status IN (0, 1, 2, 3)),
    disposition             TEXT NULL
                            CHECK (
                                disposition IS NULL
                                OR disposition IN ('processed', 'discarded', 'dead_lettered')
                            ),
    attempt_count           INTEGER NOT NULL DEFAULT 0
                            CHECK (attempt_count >= 0),
    max_attempts            INTEGER NOT NULL
                            CHECK (max_attempts >= 1),
    next_attempt_at         TEXT NULL,
    lock_token              TEXT NULL,
    lock_expires_at         TEXT NULL,
    last_error_code         TEXT NULL,
    created_at              TEXT NOT NULL,
    updated_at              TEXT NOT NULL,
    completed_at            TEXT NULL,
    CONSTRAINT uq_provider_event_inbox_provider_event
        UNIQUE (provider, event_id)
);

CREATE INDEX IF NOT EXISTS idx_provider_event_inbox_pending_due
    ON provider_event_inbox (next_attempt_at, created_at)
    WHERE status = 0;

CREATE INDEX IF NOT EXISTS idx_provider_event_inbox_processing_expired
    ON provider_event_inbox (lock_expires_at, created_at)
    WHERE status = 1;

CREATE INDEX IF NOT EXISTS idx_provider_event_inbox_deadletter_completed
    ON provider_event_inbox (status, completed_at DESC)
    WHERE status = 3;

-- Domain bounce history. No FK to mail_requests (ADR 0020 D-05): #306 must
-- tolerate a missing request detail target. Retention aligns with mail_requests
-- (same RetentionDays; purged in the same DeleteExpiredCompletedAsync batch).
CREATE TABLE bounce_events (
    id                      TEXT NOT NULL PRIMARY KEY,
    tenant_id               TEXT NOT NULL,
    source_service          TEXT NOT NULL,
    mail_request_id         TEXT NOT NULL,
    provider                TEXT NOT NULL,
    provider_event_id       TEXT NOT NULL,
    provider_message_id     TEXT NOT NULL,
    delivery_status         TEXT NOT NULL,
    status_message          TEXT NULL,
    occurred_at             TEXT NOT NULL,
    created_at              TEXT NOT NULL,
    CONSTRAINT uq_bounce_events_provider_event
        UNIQUE (provider, provider_event_id)
);

CREATE INDEX IF NOT EXISTS ix_bounce_events_tenant_occurred
    ON bounce_events (tenant_id, occurred_at DESC);

CREATE INDEX IF NOT EXISTS ix_bounce_events_mail_request
    ON bounce_events (tenant_id, source_service, mail_request_id);

-- Tenant-scoped suppression list. Physical delete on removal (#400); audit
-- trail belongs in admin_audit_events (ADR 0013 D-08), not soft-delete rows.
-- recipient_email must be stored via RecipientEmailNormalizer.Normalize.
CREATE TABLE mail_suppressions (
    id                      TEXT NOT NULL PRIMARY KEY,
    tenant_id               TEXT NOT NULL,
    recipient_email         TEXT NOT NULL,
    reason                  TEXT NOT NULL
                            CHECK (reason IN ('hard_bounce', 'manual')),
    source_bounce_event_id  TEXT NULL,
    created_at              TEXT NOT NULL,
    CONSTRAINT uq_mail_suppressions_tenant_recipient
        UNIQUE (tenant_id, recipient_email)
);

CREATE INDEX IF NOT EXISTS ix_mail_suppressions_tenant_created
    ON mail_suppressions (tenant_id, created_at DESC);

-- Correlation reverse lookup for ACS delivery reports (ADR 0020 D-03).
CREATE INDEX IF NOT EXISTS ix_mail_attempts_provider_message_id
    ON mail_attempts (provider_message_id)
    WHERE provider_message_id IS NOT NULL;
