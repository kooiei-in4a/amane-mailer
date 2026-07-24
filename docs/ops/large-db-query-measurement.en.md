[日本語](large-db-query-measurement.md)

# Large DB query measurement (#288)

Reproducible measurement notes for full-table aggregates used by `/metrics`,
Admin ops, and CLI `db stats`; batched retention DELETE; and primary Admin list /
dead-letter COUNT queries on a seeded SQLite database. This is **measurement +
EXPLAIN + recorded results**, not a production behavior change.

Related:

- [metrics-and-alerts.en.md](metrics-and-alerts.en.md)
- [sqlite-disk-and-retention.en.md](sqlite-disk-and-retention.en.md)

## Running the harness (skipped by default in CI)

Always-on CI covers EXPLAIN shape only:

```powershell
dotnet test Amane.Mailer.slnx -c Release --no-build `
  --filter "FullyQualifiedName~MailerLargeDbQueryPlanTests"
```

Large-seed timing is opt-in via environment variables (`Assert.Skip` otherwise):

```powershell
$env:AMANE_LARGE_DB_BENCH = '1'
# optional: default 50000
$env:AMANE_LARGE_DB_BENCH_ROWS = '50000'
# optional: summary path (default: %TEMP%\amane-large-db-bench-last.txt)
$env:AMANE_LARGE_DB_BENCH_OUT = (Join-Path (Get-Location) 'artifacts/large-db-measurement-summary.txt')

dotnet test Amane.Mailer.slnx -c Release --no-build `
  --filter "FullyQualifiedName~MailerLargeDbMeasurementTests"
```

Measured operations:

| Operation | Implementation |
|-----------|----------------|
| Metrics / ops / `db stats` aggregate | `MailerDbStatsReader.LoadStatsAsync` |
| Admin queued list (LIMIT 50) | `MailRequestRepository.ListForAdminAsync` |
| Admin dead-letter COUNT | `MailRequestRepository.CountDeadLettersForAdminAsync` |
| Retention one batch (LIMIT 100) | `MailRequestRepository.DeleteExpiredCompletedAsync` |

Seeds are synthetic only (`bench-n@example.com`, etc.). Summaries never include
recipient, subject, body, or provider raw errors.

## Representative results (2026-07-24, Windows / .NET 10.0.10)

Conditions: `row_count=50000`, single-tenant synthetic seed, after `ANALYZE`,
temp-file DB.

| Metric | Value |
|--------|-------|
| DB size | ~39 MB |
| Seed | ~4323 ms |
| `LoadStatsAsync` | ~102 ms |
| `ListForAdminAsync` (status=Queued, page 50) | ~6 ms |
| `CountDeadLettersForAdminAsync` | ~2 ms |
| `DeleteExpiredCompletedAsync` (batch 100) | ~92 ms (deleted=100) |

Absolute numbers vary by disk, CPU, and concurrent load. Relative expectations:

- Indexed Admin status list should stay well under tens of milliseconds.
- Metrics aggregate is full-scan class and roughly linear in row count (see EXPLAIN).
- One retention batch (select + dual DELETE) is heavier than list; ~100 ms for
  batch size 100 under these conditions.

### EXPLAIN QUERY PLAN (same conditions)

Metrics aggregate (`mail_requests` CTE + COUNT):

```text
SCAN mail_requests USING COVERING INDEX sqlite_autoindex_mail_requests_1
```

→ Full scan via the unique covering index. Partial status indexes cannot cover
every `COUNT(CASE ...)` branch in one pass.

Retention SELECT (status IN (2,3,4,5) + `completed_at` range + ORDER BY
`completed_at`, id):

```text
SEARCH mail_requests USING INDEX idx_mail_requests_status_updated (status=?)
USE TEMP B-TREE FOR ORDER BY
```

→ Status lookup via `idx_mail_requests_status_updated`, then TEMP B-TREE for
`completed_at` order. Dead-letter-only `idx_mail_requests_deadletter_completed`
does not cover multi-status retention.

Admin queued list / dead-letter COUNT are expected to use existing Admin indexes
(`MailerAdminIndexMigrationTests`). Always-on EXPLAIN regressions live in
`MailerLargeDbQueryPlanTests`.

## Thresholds and ops notes

| Surface | Note |
|---------|------|
| `/metrics` scrape | Aggregate cost scales with rows. Avoid very short scrape intervals (e.g. prefer 15–60s). Admin ops / `db stats` share the same cost. |
| Retention | Wall-clock for the full batch loop and lock contention vs Worker/API are not timed here; measure separately in maintenance windows. |
| Cancelled | `LoadStatsAsync` omits Cancelled; retention includes it. |
| PII | Do not put real mail addresses or bodies into harness output or this doc. |

## Follow-up (index)

Clear gap: no multi-status / `completed_at`-ordered index for retention
`ORDER BY completed_at ASC, id ASC` (TEMP B-TREE today). Candidate:

```sql
CREATE INDEX IF NOT EXISTS idx_mail_requests_retention_completed
    ON mail_requests (completed_at ASC, id ASC)
    WHERE status IN (2, 3, 4, 5) AND completed_at IS NOT NULL;
```

Issue #288 stops at measurement and documentation. Adding the index is a separate
change (migration + EXPLAIN updates + retention regression). Metrics full scan is
inherent to whole-table gauges; mitigate with scrape interval and DB size
monitoring, or a future incremental-counter design if needed.

## Reproduction checklist

1. Run the measurement test with `AMANE_LARGE_DB_BENCH=1`.
2. Inspect the summary at `AMANE_LARGE_DB_BENCH_OUT` (or the temp last file).
3. Confirm EXPLAIN lines show full SCAN (metrics) and status index + TEMP B-TREE
   (retention).
4. Update the table in this doc if needed (note host differences).
