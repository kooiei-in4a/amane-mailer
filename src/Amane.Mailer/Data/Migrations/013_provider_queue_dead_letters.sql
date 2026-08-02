-- Queue-envelope poison isolation for ACS Storage Queue Pull (#461).
-- Separate from provider_event_inbox: broken envelopes may lack Event Grid event IDs.
-- Raw queue body / recipient / provider raw errors are intentionally not stored.

CREATE TABLE provider_queue_dead_letters (
    id                  TEXT NOT NULL PRIMARY KEY,
    provider            TEXT NOT NULL,
    queue_message_id    TEXT NOT NULL,
    failure_stage       TEXT NOT NULL
                        CHECK (failure_stage IN ('decode', 'parse')),
    last_error_code     TEXT NOT NULL,
    dequeue_count       INTEGER NOT NULL
                        CHECK (dequeue_count >= 0),
    created_at          TEXT NOT NULL,
    updated_at          TEXT NOT NULL,
    CONSTRAINT uq_provider_queue_dead_letters_provider_message
        UNIQUE (provider, queue_message_id)
);

CREATE INDEX IF NOT EXISTS idx_provider_queue_dead_letters_created
    ON provider_queue_dead_letters (created_at);
