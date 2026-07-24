-- Persist tenant scope on admin audit rows (#282).
-- mail_requests retention (default 90d) and audit retention (default 180d) are
-- independent; scoped Admin list/get must not depend on EXISTS(mail_requests).
-- tenant_id is a GUID string (same format as mail_requests.tenant_id). Auth /
-- session / db_ops events remain NULL (service-wide for scoped viewers).
ALTER TABLE admin_audit_events ADD COLUMN tenant_id TEXT NULL;

-- Backfill from live mail_requests while the join target still exists.
UPDATE admin_audit_events
SET tenant_id = (
    SELECT mr.tenant_id
    FROM mail_requests mr
    WHERE mr.id = admin_audit_events.target_id
)
WHERE target_type = 'mail_request'
  AND tenant_id IS NULL
  AND target_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_admin_audit_events_tenant_id
    ON admin_audit_events (tenant_id)
    WHERE tenant_id IS NOT NULL;
