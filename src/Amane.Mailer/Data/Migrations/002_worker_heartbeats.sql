CREATE TABLE IF NOT EXISTS worker_heartbeats (
    name              TEXT NOT NULL PRIMARY KEY,
    last_heartbeat_at TEXT NOT NULL
);
