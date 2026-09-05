[日本語](capacity-and-scaling.md)

# Capacity and scaling boundary

This document summarizes the current architecture envelope for adopting and
operating Amane Mailer, and the boundary at which load measurement or a different
architecture must be considered. It is not a benchmark, sizing table, SLA, or
performance guarantee.

[ADR 0019](../adr/0019-sqlite-single-process-boundaries.md) is the authority for
the architecture decision. The [service specification](../service-spec.en.md) and
implementation are the authorities for runtime behavior and configuration. This
document is an operational summary of those sources and defines no new limits.

## Supported architecture envelope

The supported deployment currently assumes the following boundaries.

| Area | Current boundary | Operational meaning |
|---|---|---|
| Process / replica | Run the API, mail Worker, Sweep, Retention, and Webhook / bounce workers in the same Mailer process with **one replica** | Independently deployed API and Worker processes, multiple Workers, active-active operation, and horizontal scale-out are out of scope |
| Persistence | One host-local SQLite file (WAL) is authoritative | Sharing the file among multiple Mailer processes, managed PostgreSQL, and distributed databases are out of scope |
| Durable backlog | Mail requests and processing state are persisted in SQLite | The process-local capacity-1 Channel is a work signal, not a durable queue or backlog-size limit. Sweep / polling compensates for dropped signals |
| Tenant model | Logical `tenant_id` isolation inside one service and SQLite database | Per-tenant physical databases, independent restore, and independent performance isolation are not provided |
| Deployment storage | `infra/deploy/compose.yml` mounts a host path at `/app/data` | The operator designs and validates host affinity, volume durability, disk capacity, and recoverability |

Do not treat operation outside this envelope as achievable through configuration
alone. In particular, adding replicas or separating the API and Worker requires
redesign of claims, leases, fencing, Admin state, shutdown, and backup / restore.

## Worker and queue boundaries

The main mail Worker settings are shown below. See
[service specification §5.2](../service-spec.en.md#52-worker--sweep--retention-environment-variables)
for authoritative defaults, accepted ranges, and cross-field validation.

| Setting | Default | Startup-accepted range | What it does not mean |
|---|---:|---:|---|
| `Mailer__Worker__BatchClaimSize` | 4 | 1–100 | Maximum claims per drain; not deliveries per second or durable queue capacity |
| `Mailer__Worker__MaxSendConcurrency` | 4 | 1–64 | Maximum concurrent provider sends within one process; not guaranteed throughput, provider quota, or a per-tenant allocation |
| `Mailer__Worker__SendTimeoutSeconds` | 90 | 1–600 | Timeout for one provider invocation; not a delivery-latency guarantee |
| `Mailer__Worker__LeaseDurationSeconds` | 120 | 1–86400 | Claim lease; it has consistency constraints with batch size, concurrency, and send / finalize timeouts and is not a free-standing tuning knob |

These are **configured technical limits**. A value being accepted within its range
does not mean that value has been qualified for every workload, provider, CPU,
memory, or disk. When increasing a value, preserve the lease, healthcheck heartbeat,
shutdown drain, and `MAILER_STOP_GRACE_PERIOD` constraints, then remeasure database
contention, backlog, provider results, and shutdown with the actual workload.

Related sequential paths also matter:

- Outbound delivery-result Webhooks process one claim → one delivery → finalize
  sequentially. `Mailer__Webhook__ReconcileBatchSize` controls the search size for
  missing events, not delivery concurrency. A slow endpoint can cause cross-tenant
  head-of-line blocking ([ADR 0018](../adr/0018-webhook-delivery-sequential-concurrency.md)).
- The optional bounce ingestion worker also runs inside the process. Storage Queue
  is a pull transport for provider events; it does not externalize or horizontally
  scale the mail delivery Worker.

## SQLite and availability boundaries

SQLite uses WAL, `BEGIN IMMEDIATE`, `busy_timeout`, and database-row lock-token /
lease fencing. These provide the consistency design for one file and one process;
they do not eliminate write contention or disk latency. The API, Worker, metrics
aggregates, Retention, and Admin / CLI database operations use the same SQLite file.
As row count or concurrent load grows, evaluate full-scan queries and write-lock
contention under the real workload.

Because there is one replica, another replica does not automatically take over while
the Mailer process, host, or volume is unavailable. `/healthz` and `/readyz` report
the state of that instance; they are health signals, not an availability SLA.

The [backup runbook](backup-operations.en.md) has two paths. `backup-mailer.sh`
creates an online SQLite database-only snapshot, while
`backup-instance-state.sh` captures the database, canonical provider secret, and
committed spool at one cold point after Mailer and migration/admin mutators stop.
The [restore procedure](restore-procedure.en.md) restores the full archive into an
empty target before migration and readiness checks. A backup's existence does not
guarantee RPO or RTO. The operator must validate recovery against the target
storage, backup schedule, offsite copy, measured restore time, and caller shutdown
plan. Caddy named volumes are a separate recovery unit.

## Provider throttling and backpressure

`MaxSendConcurrency` limits concurrent sends inside the Mailer process only. It does
not define ACS / SMTP contracts, sender reputation, per-tenant quotas, daily limits,
or provider latency. Check provider-specific limits and choose concurrency and input
load from production-like observations.

ACS HTTP 429 is internally classified as a retryable provider failure, but the current
provider-submission boundary records durable evidence before invocation and does not
automatically resend the same request after `Started`. An ambiguous result can
converge to `DeliveryUnknown`. Do not interpret this internal classification as a
guarantee of safe replay for the same `mail_request_id`, or as adaptive throttling by
Mailer. See
the service specification's
[delivery uniqueness section](../service-spec.en.md#delivery-uniqueness-actual-send-guarantees)
for the exact submission and resend boundary.

HTTP `202 Accepted` means that the request was persisted in SQLite; it does not
guarantee the rate at which the provider can process it. If input exceeds delivery
rate, the durable backlog grows. Callers need their own admission-control /
backpressure policy informed by status and operational signals.

## How to use existing measurement records

The repository contains limited measurements and qualification records, but none
defines production end-to-end mail throughput or maximum capacity.

| Record | What it demonstrates | What must not be extrapolated |
|---|---|---|
| [Large DB query measurement](large-db-query-measurement.en.md) | Specific queries / one retention batch and EXPLAIN shapes against a synthetic SQLite seed in one environment | Maximum database size, request rate, concurrent API / Worker load, or an SLA |
| [ADR 0018 synthetic Webhook HOL measurement](../adr/0018-webhook-delivery-sequential-concurrency.md) | Sequential Webhook HOL under artificial endpoint delays | Production endpoint latency, mail delivery throughput, or availability |
| [Issue #532 Docker memory qualification](../cd/reports/2026-08-04-issue-532-docker-memory-qualification.md) | Attachment-envelope memory boundary under the recorded Docker/cgroup conditions | General mail rate, tenant count, database capacity, or memory / performance for every workload |

The repository therefore publishes no guaranteed TPS, mails per second or day,
maximum tenant count, maximum SQLite size, latency percentile, availability
percentage, RPO / RTO, or SLA.

## Capacity qualification before adoption

Before production adoption, validate at least the following against the operator's
own objectives in a production-like environment:

1. Define workload-specific targets for accept latency, ready backlog / oldest queued
   age, delivery completion time, disk usage, backup / restore, and recovery.
2. Measure steady state and bursts with one replica, the real CPU / memory / volume,
   real provider quotas, and a representative tenant / recipient / body / attachment
   mix.
3. Observe `/metrics` and the `db stats` values `ready_backlog_count`,
   `oldest_queued_age_seconds`, heartbeats, and failures, plus disk / WAL usage and
   provider-side throttling.
4. Take a backup, run restore verification in a disposable environment, and verify
   that backup freshness and measured restore time meet the operator's RPO / RTO
   objectives.
5. Run lease / health / shutdown validation after each configuration change and
   compare before and after under identical conditions.

## Triggers for scale-out design

If any of the following becomes a documented requirement or measured result, do not
simply tune toward the top of a configuration range. Follow
[ADR 0019 D-02 / D-03](../adr/0019-sqlite-single-process-boundaries.md#d-02-本格設計へ進む-trigger測定可能な条件)
and create a successor ADR and tracking issue:

- Production-like measurement shows that one database / one process persistently
  misses defined throughput, latency, backlog, availability, or RPO / RTO objectives.
- Active-active operation, multiple Workers, or independent API / Worker lifecycle
  or scaling becomes mandatory.
- Host-local SQLite, file backup, or host affinity violates platform or security
  requirements.
- Managed PostgreSQL or a per-tenant physical database / restore boundary becomes
  mandatory.

The successor design must define at least claim atomicity / fairness, lease time
source, finalize fencing, queue signaling, migration ownership, Admin state,
shutdown / in-flight behavior, backup / restore / DR, and migration from SQLite.
A configuration that adds replicas or separates the Worker without those decisions
is not supported.
