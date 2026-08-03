# Issue #456 Release Qualification Plan

> **Completion (historical plan):** #456 attempt 13 sealed with
> `GO_ELIGIBLE` + `APPROVE`. v1.2.0 published; see
> [docs/releases/v1.2.0.md](../releases/v1.2.0.md). Issues #445 / #456 / #458 are
> **closed**. Do **not** treat the REVISE / B-MIG-SET / “does not authorize”
> planning wording below as the current product status.

> **Status:** **REVISE response** (Agent B Rev.11 finding **M-R11-01** digest canonicalization / `evidenceDigestSha256` circularity). Still does **not** authorize Go execution / publish / Phase 1 from this document alone. `migrationDecision=INCLUDE` for frozen 012/013; `dSeqAck=true`; `attestMode=EXTERNAL_PROVENANCE`. B-MIG clearance remains **SET** until phase-aware subgates (B-MIG-SCOPE / B-MIG-PIN / B-MIG-BIND) clear. Full nine-condition clear is **not** required before #458 Phase 1 version prep — only **B-MIG-SCOPE**. Sync-ready for sister [#458](https://github.com/kooiei-in4a/amane-mailer/issues/458) Rev.7 (identical digest-rule essence).
> **Issue:** [#456](https://github.com/kooiei-in4a/amane-mailer/issues/456)
> **Parent tracking:** [#445](https://github.com/kooiei-in4a/amane-mailer/issues/445)
> **Design authority:** [ADR 0021](../adr/0021-easy-setup-boundaries.md) ([#446](https://github.com/kooiei-in4a/amane-mailer/issues/446), Accepted)
> **Base branch at planning time:** `develop`
> **Inherited Rev.8 exploration base SHA:** `9d6c556ec758384f8f8f8b32e976178529032f9c` (#457 merge)
> **PR / planning base SHA (`origin/develop` after #458 Step 0.0):** `3f2b640c08294502a6796c2634de5fdf03ce776f`
> **Plan revision:** 12 (2026-08-01)
> **Supersedes:** Rev.11 (and Rev.1 through Rev.10; same path; do not implement prior revisions)
> **Encoding:** UTF-8 (no BOM)

This document plans **release-candidate qualification** for v1.2.0 Easy Setup. It does **not** authorize product code changes, publish, tag, real ACS send during planning, or Go/No-Go execution.

---

## 0. Change logs

### 0.0 Rev.12 change log (Agent B Rev.11 REVISE; M-R11-01)

| Finding | Verdict | Plan change |
|---------|---------|-------------|
| **M-R11-01** digest circularity / non-canonical PIN algorithm (`evidenceDigestSha256` inside digested `migrationPin`) | Correct | Sec. 4 / Sec. 5 / Sec. 6.3 / Sec. 12.2: **delete** `evidenceDigestSha256` from the PIN object. Normative algorithms (identical wording essence required in sister #458 Rev.7): `inventoryAlgorithm = "RFC8785-JCS-runner-order-migration-inventory-sha256/v1"`; `migrationInventoryDigestSha256 = SHA-256(UTF-8(RFC8785 JCS(inventoryDocument)))` where `inventoryDocument = {schemaVersion:1, releaseCommitSha, runnerOrderPaths[]}` (ALL `Data/Migrations/*.sql` in **SqlMigrationRunner apply order**; repo-relative forward slashes; no leading `./`; runner order wins over filename ordinal); `migrationPinWithoutDigest = {schemaVersion:1, releaseCommitSha, inventoryAlgorithm, inventoryDigestSha256, files[]}` with `files[]` = ONLY frozen INCLUDE 012/013, sorted by path Ordinal ascending; `migrationPinDigestSha256 = SHA-256(UTF-8(RFC8785 JCS(migrationPinWithoutDigest)))`. Digested objects **never** include their own digest fields. B-MIG-PIN produces `migrationPinWithoutDigest` + `migrationPinDigestSha256` + `migrationInventoryDigestSha256`. Binding stores `migrationPinDigestSha256`, `migrationInventoryDigestSha256`, `migrationFileDigests[]` matching `files[]`. G456-42/43/44 and B-MIG-BIND continue to require exact equality to these digests. If an envelope-level digest is ever needed later, it MUST be outside the PIN object (digest of sealed evidence AFTER writing) and MUST NOT be an input to `migrationPinDigestSha256`. |
| **M-R11-02** / **m-R11-01** | Note | Owned primarily by sister **#458** Rev.7; digest-rule essence is **shared** with this plan (do not diverge). No #456-only procedure change beyond M-R11-01 sync. |
| Dual base SHAs | Keep | Inherited Rev.8 exploration `9d6c556...` + planning base `3f2b640...`. |
| `variantRulesVersion` | Keep **4** | No mapper / cardinality change. |
| Rev.8 sealed-store / aggregator / FAIL -> PASS / optional-key rules | **Unchanged** | Do not weaken. |
| Pack H | **Unchanged** | No Gate class labels (procedure / variant / evidenceType only). |
| Required PII value canary | **Unchanged** | G456-44 `piiValueCanaryResult = pass \| fail` (no `not-run`); exact history equality retained. |
| B-MIG / Go / publish / Phase 1 | **SET** / **not authorized** | Overall B-MIG remains SET; this revision alone does not clear SCOPE/PIN/BIND or authorize Phase 1 / Go / publish. |

### 0.1 Rev.11 change log (Agent B Rev.10 REVISE; historical)

| Finding | Verdict | Plan change |
|---------|---------|-------------|
| **B-R10-01** G456-44 PII value canary optional / `not-run` can PASS | Correct | Sec. 12.2 G456-44: required `piiValueCanaryResult = pass \| fail` (no `not-run`). PASS iff `contractResult=pass` AND exact schema allowlist PASS AND `piiValueCanaryResult=pass` AND `prohibitedContentScan.result=PASS`. FAIL / reject if canary not run or raw body/recipient/secret/connection-string/provider-raw-error canary values persisted into 013 table. Sec. 14 NO_GO / M-19 tables synced. |
| **M-R10-01** B-MIG-PIN digests not carried into binding / evidence equality | Correct | Sec. 4 / Sec. 5 / Sec. 6.3: normative B-MIG-PIN output + binding PIN digest fields. **Superseded for algorithm/circularity by Rev.12 M-R11-01** (delete `evidenceDigestSha256` from PIN; RFC8785 JCS canonicalization). Binding required fields retained: `migrationPinDigestSha256`, `migrationInventoryDigestSha256`, `migrationFileDigests[]`. G456-42/43/44 PASS only if evidence migration digests **exactly equal** binding PIN values. B-MIG-BIND refuses bind without these PIN fields present and matching. |
| **M-R10-02** migration history still uses ends-at / includes | Correct | Sec. 12.2: canonicalize `expectedFullMigrationInventory[]` / `expectedThrough011[]` / `expectedPost011Inventory[] == [012, 013]` in **runner order** (not filename ordinal). G456-42/43 require full exact equality of pre/post applied/pending lists; directory inventory digest must be consistent with those lists. Remove ends-at / must-include wording. |
| **m-R10-01** Pack H reintroduces Gate class labels | Correct | Pack H: remove `Hard-only` and Conditional-exception Gate-class wording; keep procedure / variant / evidenceType only. Active-PASS / exception rules remain in Sec. 6.4 / 9.5 / 14 only. |
| Dual base SHAs | Keep | Inherited Rev.8 exploration `9d6c556...` + planning base `3f2b640...`. |
| `variantRulesVersion` | Keep **4** | No mapper / cardinality change. |
| Rev.8 sealed-store / aggregator / FAIL -> PASS / optional-key rules | **Unchanged** | Do not weaken. Pack H remains without Gate class labels. |
| B-MIG / Go / publish / Phase 1 | **SET** / **not authorized** | Overall B-MIG remains SET; this revision alone does not clear SCOPE/PIN/BIND or authorize Phase 1 / Go / publish. |

### 0.2 Rev.10 change log (Agent B Rev.9 REVISE; phase-aware B-MIG; historical)

| Finding | Verdict | Plan change |
|---------|---------|-------------|
| **B-01** monolithic B-MIG "9 conditions until clear" blocks Phase 1 circularly | Correct | Sec. 4 / Sec. 5 / Q15 / Sec. 16 / Sec. 18 / Next steps: split into **B-MIG-SCOPE** (conditions 1-7 + frozen filename list + no-extra-migration policy; required before Phase 1 version prep), **B-MIG-PIN** (condition 8; inventory filenames + digests exact-match on `releaseCommitSha` after version-prep merge, before #455; FAIL on extra migrations beyond frozen 012/013 since v1.1.0 tip), **B-MIG-BIND** (condition 9; new binding/run on final Issue body SHA + `planCommitSha`/`planFileSha256` + `releaseCommitSha` before #456 qualification start). Full B-MIG clear **not** required before Phase 1 — only B-MIG-SCOPE. Overall B-MIG clearance remains **SET** until all three subgates clear. |
| **B-02** Issue G456-44 wording / Hard Conditional ban | Already fixed on GitHub Issue #456 | No Issue edit in this file; Sec. 6.2 / Pack H / validators align to live Issue. Hard rows G456-42..44: Conditional exceptions **not** allowed — active PASS only (Sec. 6.4 / 9.5 / 12.2 / 14). |
| **B-03** G456-44 alias text stale vs live Issue | Correct | Sec. 6.2 alias + Pack H + Sec. 12.2 G456-44 validators match live Issue: migration-owned delta; `provider_event_inbox.recipient_email` is 011-owned (out of 012/013 delta scope); 012 adds only `status_message`/`occurred_at`; 013 exact allowlist. |
| **M-01** migration history uses contains / self-declared alone | Correct | Sec. 12.2 G456-42/43 typePayload: exact inventory digests + applied/pending lists; equality predicates (not MustContain). B-MIG-PIN FAIL if candidate tree has migrations beyond frozen inventory. |
| **M-02** schema checks use partial deny-list | Correct | Prefer exact allowlists: `addedColumnsByMigration.012`; `createdTableByMigration.013` columns/constraints/indexes. Optional canary: no raw body/secret/recipient VALUES into 013 table (separate from column names). Superseded for canary optionality by Rev.11 **B-R10-01**. |
| **M-03** Sec. 2 DB schema non-goal over-broad vs INCLUDE | Correct | Sec. 2: forbid schema **invented inside #456**; parent-approved frozen 012/013 are frozen inputs to verify; align ADR 0021 D-14 / #445 INCLUDE clarification. |
| **m-01** base SHA header ambiguous after Step 0.0 | Correct | Header shows both inherited Rev.8 exploration base `9d6c556...` and PR/planning base `3f2b640...`. |
| Authoritative decisions | Recorded | `migrationDecision=INCLUDE`; `dSeqAck=true`; `attestMode=EXTERNAL_PROVENANCE`. |
| `variantRulesVersion` | Keep **4** | New/rewritten rows still map via existing Windows/Linux and `自動` patterns; no mapper issue forcing bump to 5. |
| Rev.8 sealed-store / aggregator / FAIL -> PASS / optional-key / Pack H no Gate labels | **Unchanged** | Do not weaken. |
| Go / publish / Phase 1 | **Not authorized** by this revision alone. | |

### 0.3 Rev.9 change log (B-MIG INCLUDE of migrations 012/013; historical)

| Decision / fact | Plan change |
|-----------------|-------------|
| Parent #445 ACK `migrationDecision=INCLUDE` for frozen inventory `012_provider_event_inbox_details.sql`, `013_provider_queue_dead_letters.sql` (#460/#461; tests in `BounceIngestionMigrationTests` / BounceIngestionSchemaTests) | Sec. 6.2 appends **G456-42..44**; Sec. 8 Pack H; Sec. 12.2 migration typePayload validators; Sec. 6.3/12.1 `planRevision="9"`. |
| `dSeqAck=true` recorded in maintainer chat | No #456 process change this rev; sequencing remains #458 / parent concern. |
| Issue #456/#458 bodies amended in parallel with three Hard migration rows | Gate class remains **solely** in Issue snapshot (no Gate labels in Pack H). Issue body SHA change => **new binding/run** (INCLUDE condition #9). |
| B-MIG clearance still **SET** until nine INCLUDE conditions complete; this rev = condition **#5** (plan revision) | Sec. 4 / Sec. 5 / Sec. 18: inventory **list** freeze vs `releaseCommitSha` **SHA** freeze (#458 Phase 1) distinguished; do not bind until Issue snapshot includes G456-42..44. |
| `variantRulesVersion` | **Keep 4** — new rows reuse existing `Windows / Linux` -> `win-docker`+`linux-docker` and `自動` -> `ci-auto` patterns (no bump). |
| Rev.8 sealed-store / aggregator / FAIL -> PASS / optional-key rules | **Unchanged** — do not weaken. |
| Go / publish / Phase 1 | **Not authorized** by this revision. |

### 0.4 Rev.8 change log (Agent B seventh review; historical)

| Finding | Verdict | Plan change |
|---------|---------|-------------|
| **M-27** optional evidence outside aggregator / machine verdict | Correct | Sec. 9.3 / Sec. 14: aggregate all bound keys (`required ∪ optional`); optional missing/FAIL alone do not NO_GO; integrity/authz/PII/schema/seal violations still NO_GO; index records optional state. |
| **m-08** root digest algorithm ambiguous | Correct | Sec. 7.8: replace Merkle ambiguity with fixed RFC 8785 JCS sorted `{path,sha256}` root algorithm. |
| **M-28** FAIL -> PASS UTF-8 mojibake after re-encode | Correct | Replaced corrupted FAIL -> PASS sequences with ASCII `FAIL -> PASS` (7 sites); no design change. |

### 0.5 Rev.7 change log (Agent B sixth review; historical)

| Finding | Verdict | Plan change |
|---------|---------|-------------|
| **M-26** sealed event does not freeze full run inventory | Correct | Sec. 7.8: Phase-4 manifest carries `sealedObjectInventory` + high-water marks; sealed event digests that manifest; #458 re-verifies inventory completeness; run-status is a terminal state machine. |
| **m-07** Informational evidence owner undefined | Correct | Sec. 6.4 / Sec. 9.4: **Option A** — `optionalEvidenceKeys[]` bound and owned in `authorization.evidenceOwners[]`. |

### 0.6 Rev.6 change log (Agent B fifth review; historical)

| Finding | Verdict | Plan change |
|---------|---------|-------------|
| **M-23** authorization write before candidate/run IDs exist | Correct | Sec. 16: Step 2 = resolve role identities only; write `authorization.json` once at Phase-2 after IDs/keys exist. |
| **M-24** Phase-4 seal commit point ambiguous | Correct | Sec. 7.8 / Sec. 14: sole seal marker = sealed run-status event after three Phase-4 objects; incomplete Phase-4 => abandon + new run. |
| **M-25** FAIL -> PASS dual-actor not mappable to schema | Correct | Sec. 9.4: recommended split — evidence `executedBy*` = owner; disposition `approvedBy*` = qualification lead. |
| **m-06** G456-06 PASS / G456-05 reference predicates | Correct | Sec. 12.2 / Sec. 14.1: G456-06 PASS predicates + `distinctFromSendReadyEvidenceId` must reference active accepted G456-05 PASS. |

### 0.7 Rev.5 change log (Agent B fourth review; historical)

| Finding | Verdict | Plan change |
|---------|---------|-------------|
| **M-19** `result` vs typePayload failure states | Correct | Sec. 12.2 / Sec. 14.1: PASS/FAIL must match outcome / doctorOrReadinessSummary; contradiction => `NO_GO`. |
| **M-20** disposition state transitions incomplete | Correct | Sec. 9.2–9.5: action state table, required/forbidden fields, `restoresEventId`, RFC 8785 JCS canonicalization, full exception-disposition schema. |
| **M-21** Phase-4 write-once vs compensating events | Correct | Sec. 7.8 / Sec. 9 / Sec. 20: **Option A** — Phase-4 seals the run; further disposition requires a new `qualificationRunId`. |
| **M-22** role mapping not run-immutable | Correct | Sec. 6.3 / Sec. 7.1 / Sec. 9.4: immutable `authorization.json` keyed by `(scenarioId, variantId)`; full action matrix. |
| **m-05** G456-03 variant vs ACS lane | Correct | Sec. 6.4 / Sec. 8.3: G456-03 required variant = `acs-staging-nosend` (not `linux-docker`). |

### 0.8 Rev.4 change log (Agent B third review; historical)

| Finding | Verdict | Plan change |
|---------|---------|-------------|
| **M-15** candidateId vs binding/run identity | Correct | Sec. 6.3 / Sec. 7.1: separate `candidateId`, `bindingId`, `qualificationRunId`; evidence keyed by run. |
| **M-16** disposition event total order | Correct | Sec. 9.2: monotonic `eventSequence` + hash chain; `restore` action. |
| **M-17** Conditional exception lifecycle | Correct | Sec. 9.5 / Sec. 12.3: immutable exceptions + exception-disposition events. |
| **M-18** Staging ACS scenario predicates | Correct | Sec. 12.2 split G456-03/04 validators; Sec. 14 No-Go on mismatch. |
| **m-03** lifecycle.json vs write-once | Correct | Sec. 7.1 / Sec. 7.7: append-only lifecycle-events; versioned indexes. |
| **m-04** Qualification lead mapping | Correct | Sec. 4 / Sec. 18: role mapping required before execution; plan start condition = Rev.4+. |

### 0.9 Rev.3 change log (Agent B second review; historical)

| Finding | Verdict | Plan change |
|---------|---------|-------------|
| **B-04** G456-05 allows Production synthetic send | Correct | Sec. 8.3 / Sec. 12.2: G456-05 `sendKind=none-for-send-ready-assert`, `mailSendAttempted=false`. `typed-fixed-synthetic` only on G456-04 Staging. |
| **M-08** Issue snapshot incomplete / no freshness gate | Correct | Sec. 6.3 stores raw scenario/environment text + `fetchedAtUtc`. Sec. 14 requires live Issue body hash match before human decision. |
| **M-09** G456-16 / G456-36 variants unset | Correct | G456-16 requiredVariants fixed to `ci-auto` + `admin-integrated`. G456-36 uses `vps`. Q5 closed. |
| **M-10** Pack G still lists Gate classes | Correct | Sec. 8.8 lists execution/variant info only; no Conditional/Informational strings. |
| **M-11** append-only vs disposition mutation | Correct | Sec. 9: immutable evidence files + separate immutable disposition events; aggregator derives active set. |
| **M-12** durable timing vs write-once | Correct | Sec. 7.8 phased immutable objects; Sec. 16 order fixed; superseded candidates stay at own root. |
| **M-13** setup-release-bundle runbook unbound | Correct | Sec. 7.4 requires JA/EN setup-release-bundle digests from pin SHA. |
| **M-14** missing ACS/Production execution provenance | Correct | Sec. 12.1 execution provenance fields; G456-04/06 require `restrictedOpsRecordId`. |
| **m-02** APPROVE allowed on some NO_GO | Correct | Sec. 14: any `machineVerdict=NO_GO` => humanDecision REJECT or NOT_DECIDED only. |

### 0.10 Rev.2 change log (Agent B first review; historical)

| Finding | Verdict | Plan change |
|---------|---------|-------------|
| **B-01** handoff name/schema mismatch | Correct | Sec. 4 / Sec. 7 use `candidate-provenance.json` as sole machine-readable #455 input; cross-check `CANDIDATE-SHA256SUMS` + `image-identity.json` + per-archive manifests. Do **not** invent `setup-release-candidate-handoff.json`. |
| **B-02** Gate identity / variant cardinality | Correct | Sec. 6 binding freezes Issue snapshot + digests + `requiredVariants[]`; evidence is `scenarioId+variantId`; aggregator requires all required variants. |
| **B-03** attempt / supersession | Correct | Sec. 9 append-only evidence + disposition model; aggregator uses one `active` attempt per variant. |
| **M-01** second Gate class lists | Correct | Pack titles/tables drop Hard/Conditional/Informational labels; class only from Issue snapshot in binding. |
| **M-02** weak docs binding | Correct | Docs must come from `git archive`/`git show` of pin SHA; JA/EN/README digests **required** in binding. |
| **M-03** ACS/Admin schema gaps | Correct | Type-specific schemas; G456-05 is exact Production (not "Prod-like"); `accessProfile` required on Admin partial-failure evidence. |
| **M-04** durable bytes for #458 | Correct | Sec. 7.8 durable store contract through #458 completion. |
| **M-05** wrong script CLI | Correct | Sec. 17 matches `scan-setup-release-bundle.sh` and positional smoke args. |
| **M-06** go-no-go schema | Correct | Sec. 14 machineVerdict vs humanDecision; humans cannot override Hard NO_GO. |
| **M-07** WP ownership overlap | Correct | Sec. 15 splits fixture-owner vs evidence-owner; unique accountable owner per variant. |
| **m-01** path vocabulary | Correct | Single root `artifacts/qualification/<candidateId>/` with `superseded/` lifecycle. |

---

## 1. Purpose

Qualify one **release-candidate commit** by consuming:

1. **#455** candidate host archives + OCI layout + handoff package (`candidate-provenance.json`, `CANDIDATE-SHA256SUMS`, `CANDIDATE-HANDOFF.md`, `image-identity.json`)
2. **#457** setup-guide / README-SETUP content extracted from that same `sourceCommitSha`

...and producing **value-free** evidence that #458 can re-check, covering automated/manual E2E, fresh install, rollback, fault injection, security regression, cross-platform checks, real ACS evidence, Release Production operational verification, and an explicit **Go / No-Go**.

## 2. Non-goals

| Non-goal | Owner / note |
|----------|--------------|
| Git tag / GitHub Release / GHCR / NuGet publish | **#458** |
| Public post-promote smoke | **#458** |
| Deployment operational verification **recording** feature | Not in v1.2.0 |
| mode 5 Easy Setup automation | Manual only (see Issue #456 Informational row) |
| Full NAS / macOS formal guarantee | See Issue #456 Informational rows |
| Production bulk send | Forbidden |
| public HTTP contract / Contracts / OpenAPI changes | Forbidden (ADR 0021 D-14) |
| New DB schema **invented inside #456** qualification work | Forbidden (ADR 0021 D-14 / #445 INCLUDE clarification). Parent-approved frozen inventory `012_provider_event_inbox_details.sql` / `013_provider_queue_dead_letters.sql` on the candidate are **frozen inputs to verify**, not schema invented by #456. |
| reverse proxy auto-build / non-interactive Admin bootstrap | Forbidden |
| Treating release evidence as per-tenant "verified" status | Forbidden (ADR 0021 D-07) |
| New #455 handoff schema invented inside #456 | Forbidden — change #455 contract explicitly if needed |

## 3. Responsibility boundary

| Concern | #456 | Not #456 |
|---------|------|----------|
| Candidate packaging | consume | produce = **#455** |
| Candidate docs finalization | consume; defects -> **#457** | wording = **#457** |
| E2E / fault / security / ACS / Release OV / Go-No-Go | **own** | |
| Deployment send-ready (operator env) | exercise; evidence kind `production-acs-send-ready` | not Release OV |
| Release Production operational verification | **own** (maintainer, value-free) | never Admin/tenant OV |
| Publish / promote qualified bytes | hand off durable package | **#458** |

### State vocabulary

| Term | Evidence meaning |
|------|------------------|
| Deployment configuration applied | ACTIVE commit path in operator env |
| Deployment send-ready | Operator env meets send-ready (ADR 0021 D-07) |
| Deployment operational verification | Operator real-send — **not recorded by Easy Setup**; must not be claimed |
| Release Production operational verification | Maintainer RC normal Mailer-path Production send for **product** qualification |

---

## 4. Authority map

| Kind | Authority | Role for #456 |
|------|-----------|---------------|
| Gate Hard/Conditional/Informational | **Issue #456 required-scenario table Gate column** (sole live authority) | Classification |
| Frozen Gate snapshot for a run | `binding.issueSnapshot` (copy of table at bind time) | Mechanical judgment input |
| Design contracts | ADR 0021 | D-07/D-09/D-10/D-12 |
| Tracking | Issue #445 | Gate 3B |
| Candidate machine input | **`candidate-provenance.json`** (`CandidateProvenanceDocument`) | `candidateId` input |
| Plan document pin | plan-only commit SHA + plan file SHA-256 | `bindingId` input |
| Role mapping | immutable `authorization.json` per run (Sec. 9.4); digest in binding | Qualification lead / Conditional approver / evidence owners keyed by `(scenarioId, variantId)` |
| Candidate checksum inventory | `CANDIDATE-SHA256SUMS` | Archive byte verify |
| Candidate human handoff | `CANDIDATE-HANDOFF.md` | Operator notes only (not machine authority) |
| Image identity | `image-identity.json` | Cross-check `ociIndexDigest` / `sourceCommitSha` |
| Per-RID manifest | archive `release-bundle-manifest.json` | Cross-check digests / SHA / versions |
| Docs | Issue #457 + pin-SHA extracted setup-guide JA/EN + README-SETUP | Operator steps |
| Publish | Issue #458 | Consumer |

### Inputs #456 consumes (machine)

| Input | Path / type | Required fields used by binding |
|-------|-------------|----------------------------------|
| Provenance | `candidate-provenance.json` | `schemaVersion`, `sourceCommitSha`, `releaseVersion`, `workflowRunId`, `workflowRunAttempt`, `ociIndexDigest`, `archives[].artifactName`, `archives[].archiveFileName`, `archives[].archiveSha256`, `archives[].targetRid`, `archives[].smokeResult`, `archives[].payloadTreeSha256` |
| Checksums | `CANDIDATE-SHA256SUMS` | Each archive filename -> hex digest (must equal provenance `archiveSha256` when normalized) |
| Image identity | `image-identity.json` | `sourceCommitSha`, `imageDigest` (== provenance `ociIndexDigest`), `platforms` |
| Host archives | zip/tar.gz named in provenance | Byte identity via SHA-256 |
| OCI layout | workflow artifact `setup-release-candidate-oci` | Index digest == `ociIndexDigest` |
| Docs extracts | from `git archive <sourceCommitSha>` | Digests in binding |

### Undetermined until execution pin

| Item | Blocks |
|------|--------|
| Exact RC `sourceCommitSha` / `releaseCommitSha` | candidateId / binding; inventory **SHA** freeze (#458 Phase 1; distinct from filename list freeze) |
| Maintainer Staging/Production ACS handles | Hard ACS rows |
| Approved HTTPS reverse-proxy lab | Admin Production variants |
| Durable store concrete URI/bucket | Operationalization of Sec. 7.8 (contract fixed) |
| Role identities + `authorization.json` (see Sec. 9.4 / Sec. 18) | Disposition / Conditional / invalidate approvals |

### Execution start conditions

1. Dependencies closed: #447-#455, #457, #459 (confirmed at planning).
2. ADR 0021 Accepted.
3. **Plan Rev.12 (or later)** committed; Agent B re-review passed on that exact plan-only commit SHA (feeds B-MIG-SCOPE conditions 5-6).
4. **B-MIG-SCOPE** cleared (conditions 1-7 + frozen filename list + no-extra-migration policy). Full B-MIG / B-MIG-PIN / B-MIG-BIND are **not** required to start #458 Phase 1 version prep; they gate later phases (see Blockers).
5. Role identities resolved and approved (qualification lead, Conditional approver, intended evidence-owner assignment policy). Do **not** write `authorization.json` yet.
6. **B-MIG-PIN** cleared on `releaseCommitSha` (after version-prep merge, before #455 dispatch).
7. Fresh #455 workflow for pinned SHA; provenance + sums + archives + OCI present.
8. Phase-1 durable intake completed (Sec. 7.8) before workflow retention (14d) expires.
9. **B-MIG-BIND** cleared: new binding/run on final Issue body SHA + `planCommitSha`/`planFileSha256` + `releaseCommitSha`; Issue snapshot includes G456-42..44.
10. Empty namespace for new `qualificationRunId` (not only candidateId).
11. After `candidateId` / `bindingId` / `qualificationRunId` / required + optional evidence keys exist: write Phase-2 `binding.json` + `authorization.json` once (Sec. 9.4 / Sec. 16).
12. No publish/tag during qualification.
13. CI has no real secrets.

### Blockers (process)

| ID | Note |
|----|------|
| B-LOCAL | Local develop may lag remote; implement from fetched pin |
| B-RC | First RC pin / `releaseCommitSha` still pending (#458 Phase 1); list-freeze of migration filenames != SHA freeze of pin |
| B-ACS / B-PROXY | Real ACS / HTTPS lab outside CI |
| B-MIG | Overall clearance still **SET** until **B-MIG-SCOPE** + **B-MIG-PIN** + **B-MIG-BIND** all clear. INCLUDE decided (`migrationDecision=INCLUDE`). Do **not** treat monolithic "nine conditions" as a single pre-Phase-1 gate. |
| B-MIG-SCOPE | Conditions **1-7** + frozen migration filename list (`012_provider_event_inbox_details.sql`, `013_provider_queue_dead_letters.sql`) + **no-extra-migration** policy (no migrations beyond that frozen pair since v1.1.0 tip may enter scope without parent re-decision). **Required before #458 Phase 1 version prep.** This Rev.12 advances plan-side digest canonicalization for SCOPE condition 7 / PIN readiness; clearance of the subgate still needs Agent B APPROVE on the durable plan SHA and remaining SCOPE items. |
| B-MIG-PIN | Condition **8**: after version-prep merge, before #455 — produce normative PIN outputs (below) on `releaseCommitSha`. **FAIL** if the migrations directory has files beyond the frozen 012/013 pair since v1.1.0 tip (besides those two), or digests do not match the PIN'd tree. |
| B-MIG-BIND | Condition **9**: before #456 qualification start — new binding/run on final Issue body SHA + `planCommitSha`/`planFileSha256` + `releaseCommitSha` **and** required PIN digest fields (`migrationPinDigestSha256`, `migrationInventoryDigestSha256`, `migrationFileDigests[]`) present and matching the B-MIG-PIN output. Issue snapshot **must** include G456-42..44. **Refuse bind** if PIN fields are missing or do not match PIN evidence. |

**Normative B-MIG-PIN output (condition 8; M-R11-01; identical wording essence required in #458 Rev.7):**

```text
inventoryAlgorithm =
  "RFC8785-JCS-runner-order-migration-inventory-sha256/v1"

inventoryDocument = {
  schemaVersion: 1,
  releaseCommitSha: "<40-hex>",
  runnerOrderPaths: [
    // ALL Data/Migrations/*.sql paths in SqlMigrationRunner apply order
    // repo-relative, forward slashes, no leading ./
    // runner apply order wins if it differs from filename ordinal
  ]
}

migrationInventoryDigestSha256 =
  SHA-256( UTF-8 bytes of RFC8785 JCS(inventoryDocument) )

migrationPinWithoutDigest = {
  schemaVersion: 1,
  releaseCommitSha: "<40-hex>",
  inventoryAlgorithm: "RFC8785-JCS-runner-order-migration-inventory-sha256/v1",
  inventoryDigestSha256: "<hex>",   // == migrationInventoryDigestSha256
  files: [
    // ONLY frozen INCLUDE files 012 and 013
    // sorted by repo-relative path Ordinal ascending
    {
      path: "src/Amane.Mailer/Data/Migrations/012_provider_event_inbox_details.sql",
      sha256: "<file content sha256>",
      gitBlobSha: "<git blob sha>"
    },
    {
      path: "src/Amane.Mailer/Data/Migrations/013_provider_queue_dead_letters.sql",
      sha256: "...",
      gitBlobSha: "..."
    }
  ]
}

migrationPinDigestSha256 =
  SHA-256( UTF-8 bytes of RFC8785 JCS(migrationPinWithoutDigest) )
```

**Rules:** Digested objects **never** include their own digest fields. **Delete** `evidenceDigestSha256` from the PIN object (do not feed any post-write envelope digest into `migrationPinDigestSha256`). B-MIG-PIN produces `migrationPinWithoutDigest` + `migrationPinDigestSha256` + `migrationInventoryDigestSha256`. Binding stores `migrationPinDigestSha256`, `migrationInventoryDigestSha256`, and `migrationFileDigests[]` matching `files[]`. Binding and G456-42/43/44 evidence must carry digests that **exactly equal** these PIN values (and thus the PIN'd `releaseCommitSha` tree).

---

## 5. Dependency / readiness matrix

| Issue | State (2026-08-01) | Plan | Execute qual |
|-------|--------------------|------|--------------|
| #446-#455, #457, #459 | Closed | Yes | Yes (artifacts for pin) |
| #456 | Open | This plan Rev.12 | Self — after B-MIG-SCOPE + B-MIG-PIN + B-MIG-BIND |
| #458 | Open | Boundary | Phase 1 version prep may start after **B-MIG-SCOPE** only (not full B-MIG); owns `releaseCommitSha` pin |
| #445 B-MIG | INCLUDE ACK; clearance still SET | Phase-aware subgates below | Do not start #456 qualification until B-MIG-BIND |

**B-MIG phase-aware readiness (normative):**

```text
B-MIG-SCOPE (before Phase 1 version prep):
  conditions 1-7
  + frozen migration filename list
  + no-extra-migration policy
B-MIG-PIN (after version-prep merge, before #455):
  condition 8
  produce migrationPinWithoutDigest + migrationPinDigestSha256
    + migrationInventoryDigestSha256 on releaseCommitSha
  (RFC8785 JCS algorithms above; no evidenceDigestSha256 in PIN)
  FAIL if directory has migrations beyond frozen 012/013 since v1.1.0 tip besides those two
  or digests do not match the PIN'd releaseCommitSha tree
B-MIG-BIND (before #456 qualification start):
  condition 9
  new binding/run on final Issue body SHA + planCommitSha/planFileSha256 + releaseCommitSha
  + migrationPinDigestSha256 + migrationInventoryDigestSha256 + migrationFileDigests[]
  refuse bind if PIN fields missing or not exactly equal to B-MIG-PIN output
```

1. Frozen **inventory list** (filenames only; SCOPE): `012_provider_event_inbox_details.sql`, `013_provider_queue_dead_letters.sql`.
2. Issue #456 required-scenario snapshot **must** include rows aliased as G456-42..44 (Gate class from Issue only) before B-MIG-BIND.
3. Inventory **filename+digest freeze** to `releaseCommitSha` is **B-MIG-PIN** (after #458 Phase 1 version-prep merge; distinct from SCOPE list freeze). PIN output schema is normative (Sec. 4).
4. Issue body amendment => new `issueBodySha256` => **new binding/run** (**B-MIG-BIND** / condition #9), even if candidate bytes unchanged.
5. Overall B-MIG remains **SET** until SCOPE+PIN+BIND clear; do **not** require full clear before Phase 1 — only B-MIG-SCOPE.
6. B-MIG-BIND **refuses bind** unless binding carries PIN digests present and matching; G456-42/43/44 PASS only when evidence digests **exactly equal** those binding PIN values.

---

## 6. Gate reference scheme (no duplicate Gate authority)

### 6.1 Sole live Gate authority

Hard / Conditional / Informational live only in [Issue #456](https://github.com/kooiei-in4a/amane-mailer/issues/456).

This plan **must not**:

- Maintain a second authoritative Gate checklist
- Put Gate class labels in pack titles or scenario tables as authority
- Change Gate class mid-qualification without ADR amendment or explicit plan revision

**Mechanical judgment** uses the **frozen Issue snapshot inside binding** (Sec. 6.3), not a live GitHub fetch during aggregation.

### 6.2 Stable scenario IDs

`G456-NN` = 1-based row order in Issue #456 table **as frozen in binding.issueSnapshot** for that candidate.

| ID | Scenario alias (Issue wording) | Environment alias (Issue wording) |
|----|--------------------------------|-----------------------------------|
| G456-01 | fresh local Mailpit | Windows Docker Desktop |
| G456-02 | fresh local Mailpit | Linux Docker Engine |
| G456-03 | staging ACS no-send | Linux |
| G456-04 | staging ACS verification | maintainer-managed real ACS |
| G456-05 | production ACS send-ready | manual smoke; secret values not in evidence |
| G456-06 | Release Production operational verification | normal Mailer path; value-free evidence |
| G456-07 | Local Development Admin access | Development / loopback |
| G456-08 | Production HTTPS Admin access | approved HTTPS proxy environment |
| G456-09 | Production must not treat Secure cookie as HTTP-usable | Production |
| G456-10 | Production rejects `AMANE_ADMIN_ALLOW_HTTP=true` | Production |
| G456-11 | Admin allowed local address mismatch -> 404 | Local / proxy |
| G456-12 | No Production HTTPS path -> no Admin bootstrap; stay disabled | Production |
| G456-13 | Admin fresh bootstrap + login + setup status | Windows / Linux |
| G456-14 | Admin managed same-user reapply | Windows / Linux |
| G456-15 | Admin different username rejected | automated |
| G456-16 | Admin credential sync then subsequent failure | automated / integrated |
| G456-17 | non-interactive Admin enable rejected | Windows / Linux |
| G456-18 | apply failure -> rollback | Windows / Linux |
| G456-19 | fresh install failure | Windows / Linux |
| G456-20 | fingerprint mismatch | automated |
| G456-21 | secret swap / stale secret / bad mount | automated |
| G456-22 | stale launcher / image mismatch | automated |
| G456-23 | remote Docker Context | automated FAIL |
| G456-24 | command injection inputs | automated |
| G456-25 | path traversal | automated |
| G456-26 | symlink / reparse point | OS-specific |
| G456-27 | concurrent setup | automated |
| G456-28 | crash / cancel recovery (minimal representative) | automated |
| G456-29 | OS-specific crash recovery extras | OS-specific |
| G456-30 | token / Origin / Host / CSRF | Web integration |
| G456-31 | secret-like string leakage in outputs | automated |
| G456-32 | Admin setup status authorization | Web integration |
| G456-33 | terminal / non-interactive input boundary | Windows / Linux |
| G456-34 | Linux arm64 full E2E on real hardware | Linux arm64 |
| G456-35 | Linux arm64 artifact startup smoke | Linux arm64 |
| G456-36 | Specific VPS distribution quirks | VPS |
| G456-37 | optional non-interactive automation extras | Windows / Linux |
| G456-38 | NAS best-effort | NAS |
| G456-39 | macOS | macOS |
| G456-40 | mode 5 | Manual only |
| G456-41 | external secret manager examples | Hardened docs |
| G456-42 | DB migration fresh apply through 013 | Windows / Linux |
| G456-43 | DB migration upgrade from v1.1.0 (011 applied) through 013 | Windows / Linux |
| G456-44 | Migration 012/013 schema + PII-safe contract: 012/013 add no raw payload/provider body/recipient/secret columns (provider_event_inbox.recipient_email is 011-owned; out of 012/013 delta scope); 012 adds only status_message/occurred_at; 013 dead-letters columns/constraints/indexes match exact allowlist | 自動 |

Aliases are non-authoritative labels. Gate class for aggregation comes only from `binding.issueSnapshot.rows[N].gateClass`.

### 6.3 Identities, Issue snapshot, and rebinding (B-02, M-08, M-15)

#### Identity separation

```text
candidateId = sha256(
  provenance.sourceCommitSha || "|" ||
  provenance.workflowRunId || "|" ||
  provenance.workflowRunAttempt || "|" ||
  provenance.ociIndexDigest || "|" ||
  sorted(archives.archiveSha256 by targetRid)
)
# Identity of #455 bytes only. Never changes on Issue/plan rebind.

bindingId = sha256(
  candidateId || "|" ||
  issueBodySha256 || "|" ||
  planCommitSha || "|" ||
  planFileSha256 || "|" ||
  variantRulesVersion
)
# Identity of a frozen Gate/docs/plan binding over those bytes.

qualificationRunId = sha256(
  bindingId || "|" ||
  runAttemptNonce
)
# One qualification attempt namespace. Evidence/dispositions/decision live here.
```

`planCommitSha` = git commit that contains this plan file. `planFileSha256` = SHA-256 of `docs/agent-workflows/issue-456-release-qualification-plan.md` at that commit. `planRevision` alone is not sufficient.

Issue drift / plan change without new #455 bytes => **new `bindingId` + new `qualificationRunId`**, same `candidateId`. Never overwrite prior run trees.

#### Binding snapshot contents

At bind time, harness **must** record:

```text
bindingId
qualificationRunId
candidateId
planRevision = "12"
planCommitSha
planFileSha256
variantRulesVersion = 4
authorizationDigestSha256
issueNumber = 456
issueUpdatedAt
issueBodySha256
fetchedAtUtc
# Required B-MIG-PIN carry-forward (M-R10-01 + M-R11-01); B-MIG-BIND refuses bind if missing/mismatched:
migrationPinDigestSha256          # == SHA-256(UTF-8(RFC8785 JCS(migrationPinWithoutDigest)))
migrationInventoryDigestSha256    # == migrationPinWithoutDigest.inventoryDigestSha256
                                  # == SHA-256(UTF-8(RFC8785 JCS(inventoryDocument)))
migrationFileDigests[]            # exact equality with migrationPinWithoutDigest.files[]
                                  # ({path, sha256, gitBlobSha}; Ordinal path ascending)
rows[]:
  rowIndex
  scenarioId
  scenarioText
  environmentText
  gateClass
  scenarioTextSha256
  environmentTextSha256
  requiredVariants[]
  informationalNotRequired        # true only for Informational rows that are not Hard/Conditional-required
optionalEvidenceKeys[]:           # Informational keys that MAY be evidenced (m-07 Option A)
  scenarioId
  variantId
docs.* digests (Sec. 7.4)
```

**variantRulesVersion = 4 (no bump for Rev.12):** G456-42/43 environment text `Windows / Linux` reuses the existing default -> `win-docker` + `linux-docker`. G456-44 environment text `自動` reuses the same pattern as `automated` -> `ci-auto`. No new variantId vocabulary is introduced (mapper issue would force bump to 5; none here).

#458 must audit row meaning from binding alone without re-fetching GitHub.

**Freshness gate (before humanDecision):** re-fetch live Issue #456 body; require `currentIssueBodySha256 == binding.issueBodySha256`. Mismatch => do not APPROVE; create new binding/run and re-evaluate impact.

Aggregator refuses evidence whose `qualificationRunId` / `bindingId` / `issueBodySha256` do not match the run under decision. Aggregator also refuses evidence or disposition events whose `approvedByRole`/`approvedByIdentity`/`executedByRole`/`executedByIdentity` disagree with the sealed `authorization.json` for that run (Sec. 9.4).

### 6.4 Required variants (cardinality)

Evidence key = `(scenarioId, variantId)`. Scenario PASS only when **every** `requiredVariants[]` entry has an active PASS (or complete Conditional exception **where `gateClass=Conditional`**), where "active" is derived from disposition events (Sec. 9).

**Hard migration rows G456-42..44:** Conditional exceptions are **not** allowed. Every required variant must have **active PASS** only. `result=EXCEPTION` or an approved Conditional exception for these scenarioIds => aggregator `NO_GO` (Sec. 14).

| Environment text pattern (Issue) | Default `requiredVariants` |
|----------------------------------|----------------------------|
| Windows Docker Desktop | `win-docker` |
| Linux Docker Engine | `linux-docker` |
| staging ACS no-send (G456-03) | **`acs-staging-nosend`** (restricted ACS lane; not `linux-docker`) |
| Linux (alone) when not ACS no-send | `linux-docker` |
| Windows / Linux | `win-docker` + `linux-docker` |
| OS-specific | `win-docker` + `linux-docker` (symlink and reparse on win; symlink on linux) |
| Local / proxy | `local-dev` + `proxy-https` |
| automated / Web integration / automated FAIL (except G456-16) | `ci-auto` |
| `自動` (Issue wording; same pattern as `automated`) | `ci-auto` |
| G456-16 (`automated / integrated`) | **`ci-auto` + `admin-integrated`** (fixed; not optional) |
| Development / loopback | `admin-local-dev` |
| approved HTTPS proxy / Production (Admin HTTP rows) | `admin-prod-https` |
| maintainer-managed real ACS | `acs-staging-real` |
| G456-05 manual smoke | `acs-production` (**exact Production**, not staging) |
| Release OV row | `acs-production-release-ov` |
| Linux arm64 | `linux-arm64` |
| VPS (G456-36) | **`vps`** |
| Informational (G456-38..41) | `requiredVariants=[]` + `informationalNotRequired:true`; **and** bind `optionalEvidenceKeys` (m-07 Option A) |

`binding.variantRulesVersion = 4`. Changing rules requires plan revision.

**Rev.12 migration row variants (reuse; no rules bump; same as Rev.9/10/11):**

| scenarioId | Issue environment text | requiredVariants |
|------------|------------------------|------------------|
| G456-42 | Windows / Linux | `win-docker` + `linux-docker` |
| G456-43 | Windows / Linux | `win-docker` + `linux-docker` |
| G456-44 | 自動 | `ci-auto` |

**Informational optional keys (closes m-07; Option A):**

If an Informational row may produce qualification evidence (attempted recording), binding **must** list it under `optionalEvidenceKeys[]` with a concrete `variantId`, and `authorization.evidenceOwners[]` **must** include that `(scenarioId, variantId)` exactly once. Default optional keys:

| scenarioId | optional variantId |
|------------|--------------------|
| G456-38 | `nas` |
| G456-39 | `macos` |
| G456-40 | `mode5-manual` |
| G456-41 | `external-secret-manager-docs` |

Optional keys never block Go when missing/not-confirmed. When evidence **is** written for an optional key, the same disposition / authorization rules as other keys apply. Keys not listed in `requiredVariants[]` or `optionalEvidenceKeys[]` **must not** receive evidence under this run.

**G456-03 fixed decision (closes m-05):** required variant is `acs-staging-nosend`, not generic `linux-docker`. Staging ACS configuration (even no-send) uses the restricted ACS lane for ownership, provenance, and execution.

**G456-16 fixed decision (closes Q5):** Option B — separate automated unit/fixture evidence (`ci-auto`) from integrated follow-on-failure evidence (`admin-integrated`). Both are required Hard variants. Binding must not omit either.

### 6.5 Gate semantics

| Class (from snapshot) | Incomplete required variant | FAIL | Go impact |
|-----------------------|-----------------------------|------|-----------|
| Hard | No-Go | No-Go | No alternate-only PASS |
| Conditional | No-Go unless complete exception for that variant | Same | Exception fields mandatory |
| Informational | Optional key may be evidenced or listed not-confirmed | Record if attempted | Alone does not block Go |

---

## 7. Qualification harness

### 7.1 Storage root (m-01, M-12, M-15, m-03)

Separate candidate intake from qualification runs:

```text
artifacts/qualification/candidates/<candidateId>/
  intake/                         # Phase 1 immutable objects + Phase-1 manifest
  lifecycle-events/<eventId>.json # append-only (active|superseded|abandoned)

artifacts/qualification/runs/<qualificationRunId>/
  binding.json                    # Phase 2 immutable
  authorization.json              # Phase 2 immutable role snapshot (Sec. 9.4)
  docs-extract/
  evidence/<evidenceId>.json
  dispositions/<eventId>.json     # eventSequence hash-chained; sealed after Phase-4
  exceptions/<exceptionId>.json
  exception-dispositions/<eventId>.json
  run-status-events/<eventId>.json # append-only; includes sealed
  scans/<scanReportId>.json
  indexes/
    evidence-index-vN.json        # versioned immutable indexes (never in-place update)
  decision/                       # Phase 4 write-once; seals the run
    evidence-index.json
    go-no-go.json
  phase-manifests/
    phase-2.json
    phase-3-vN.json
    phase-4.json
```

Do **not** nest old candidates under a new candidateId. Do **not** overwrite `binding.json` or `authorization.json` for Issue/plan drift — open a new `qualificationRunId`. After Phase-4 seal, do **not** append disposition / exception-disposition events to the sealed run (Sec. 7.8 Option A).

`.gitignore` must cover `artifacts/qualification/`.

### 7.2 `candidateId` / `bindingId` / `qualificationRunId`

See Sec. 6.3. Field names for candidateId inputs match `CandidateProvenanceDocument` (`workflowRunAttempt`; `ociIndexDigest`). `image-identity.json.imageDigest` must equal `ociIndexDigest`.

### 7.3 Same-commit + cross-check rules (B-01, M-02)

| Check | Pass |
|-------|------|
| Provenance `sourceCommitSha` | Equals pin |
| `image-identity.json.sourceCommitSha` | Equals provenance |
| `image-identity.json.imageDigest` | Equals provenance `ociIndexDigest` |
| Each archive in `CANDIDATE-SHA256SUMS` | Matches provenance `archives[].archiveSha256` (normalized) |
| Each archive manifest `sourceCommitSha` | Equals provenance |
| Each manifest `imageDigest` / `ociIndexDigest` | Equals provenance `ociIndexDigest` |
| Docs digests | From pin SHA extracts (Sec. 7.4) — **required** |
| OCI layout index digest | Equals provenance `ociIndexDigest` |

Any mismatch -> stop; cannot bind; cannot Go.

### 7.4 Docs extraction (M-02, M-13)

Operator steps and packaging import contracts **must not** be read from a dirty worktree.

```text
git archive --format=tar <sourceCommitSha> \
  docs/ops/setup-guide.md \
  docs/ops/setup-guide.en.md \
  docs/ops/setup-release-bundle.md \
  docs/ops/setup-release-bundle.en.md \
  README.md README.en.md \
  | tar -x -C artifacts/qualification/runs/<qualificationRunId>/docs-extract/
```

Also extract candidate `README-SETUP.md` from the **qualified archive** (not worktree).

Binding **requires**:

```text
docs.setupGuideJaSha256
docs.setupGuideEnSha256
docs.setupReleaseBundleJaSha256
docs.setupReleaseBundleEnSha256
docs.readmeJaSha256
docs.readmeEnSha256
docs.candidateReadmeSetupSha256
docs.extractionMethod
docs.sourceCommitSha
```

### 7.5 Stale / mismatch detection

Stale launcher, wrong image, wrong archive, old/swapped/other-bundle secret, fingerprint drift — fixtures under G456-20 through G456-22 / G456-21.

### 7.6 CI vs maintainer lanes

| Lane | Secrets | Typical variants |
|------|---------|------------------|
| CI | Placeholders only | `ci-auto`, `admin-integrated` (synthetic) |
| Maintainer semi-auto | Synthetic Mailpit | `win-docker`, `linux-docker`, `admin-local-dev` |
| Maintainer restricted | Real ACS / HTTPS (never in evidence values) | `acs-staging-nosend`, `acs-staging-real`, `acs-production`, `acs-production-release-ov`, `admin-prod-https`, `vps` |

### 7.7 Candidate / run replacement (m-03)

| Change | Action |
|--------|--------|
| New #455 bytes | New `candidateId` under `candidates/`. Append lifecycle-event on old candidate: `status=superseded`, `supersededByCandidateId`. |
| Issue/plan drift (same bytes) | New `bindingId` + `qualificationRunId` under `runs/`. Prior run untouched. |
| Abandon run | Append lifecycle-event / run-status event; never mutate prior JSON objects in place |

Lifecycle uses **append-only** `lifecycle-events/<eventId>.json` (or versioned `lifecycle-vN.json`), never in-place update of a single `lifecycle.json`.

### 7.8 Durable store phases for #458 (M-04, M-12, m-03)

GitHub Actions artifacts retain **14 days**. #458 must promote qualified bytes without rebuild when possible.

**Immutability rule:** each object is write-once. Namespaces are appendable across phases via new objects / versioned indexes — never in-place rewrite.

| Phase | When | Immutable objects |
|-------|------|-------------------|
| **1 intake** | After #455 download / checksum verify; **before** bind | under `candidates/<candidateId>/intake/` + Phase-1 manifest |
| **2 binding** | After successful bind + docs extract + authorization snapshot | under `runs/<qualificationRunId>/` binding + `authorization.json` + docs + Phase-2 manifest |
| **3 evidence** | Continuously **until Phase-4 seal** | evidence, dispositions, exceptions, exception-dispositions, scans, **versioned** `evidence-index-vN.json` / `phase-3-vN.json` (new version each publish; never overwrite vN) |
| **4 decision** | After aggregation + freshness gate | `go-no-go.json`, final evidence-index, Phase-4 manifest, **run-status `sealed` event** |

**Phase-4 seal (Option A; closes M-21 / M-24 / M-26):**

Phase-4 objects are write-once and **not** a single atomic filesystem transaction. The **sole seal marker** is the final sealed run-status event. Presence of some Phase-4 files alone does **not** mean sealed.

Timestamp comparisons and cross-namespace sequencer comparisons are **not** seal authorities. #458 must re-verify from durable bytes + inventory digests, not from harness write-rejection history.

**Commit order (mandatory):**

1. Freeze Phase-3: no further evidence / disposition / exception / exception-disposition / scan writes for this seal attempt
2. Write final `decision/evidence-index.json`
3. Write `decision/go-no-go.json` (`runSealed` is an **auxiliary declaration only**, not the seal authority)
4. Write `phase-manifests/phase-4.json` containing `sealedObjectInventory` + `finalRunState` (below)
5. Verify digests of those three objects against bytes on durable store
6. **Last:** append exactly one terminal `run-status-events` object with `status=sealed` that embeds `decisionDigests` including `phase4ManifestSha256`
7. #458 and aggregators treat the run as sealed **only** when the sealed-event predicate below holds

**`phase-manifests/phase-4.json` required contents:**

```text
qualificationRunId
bindingId
candidateId
createdAtUtc
sealedObjectInventory[]:          # EVERY qualification object under this run at seal time
  path                            # relative to runs/<qualificationRunId>/
  sha256
finalRunState:
  evidenceObjectCount
  evidenceRootSha256              # Sec. 7.8.1 object-set root over evidence/* inventory entries
  dispositionLastSequence
  dispositionLastDigestSha256     # digest of max eventSequence disposition; null if none
  exceptionObjectCount
  exceptionRootSha256             # same algorithm over exceptions/*
  exceptionDispositionLastSequence
  exceptionDispositionLastDigestSha256
  scanObjectCount
  scanRootSha256                  # same algorithm over scans/*
  phase3LatestIndexSha256         # latest evidence-index-vN.json if any; else null
  finalEvidenceIndexSha256        # == decision/evidence-index.json
  goNoGoSha256                    # == decision/go-no-go.json
decisionObjectPaths:
  evidenceIndex = "decision/evidence-index.json"
  goNoGo = "decision/go-no-go.json"
rootDigestAlgorithm = "RFC8785-JCS-sorted-path-sha256/v1"
```

**Object-set root digest algorithm (closes m-08; `rootDigestAlgorithm = RFC8785-JCS-sorted-path-sha256/v1`):**

Do **not** use an ad-hoc Merkle tree. All of `evidenceRootSha256`, `exceptionRootSha256`, and `scanRootSha256` use this exact function over the corresponding inventory subset:

```text
normalizedRelativePath rules:
  - UTF-8 NFC
  - separator MUST be "/"
  - MUST be relative to runs/<qualificationRunId>/
  - MUST NOT start with "/"
  - MUST NOT contain "." or ".." path segments
  - MUST NOT contain "\\" or NUL
  - comparison / sort key = UTF-8 byte ordinal of the normalized path string

digestHex rules:
  - SHA-256 of file bytes
  - lowercase 64-char [0-9a-f] hex only

rootSha256 =
  SHA-256(
    UTF-8(
      RFC8785-JCS(
        sort_by_path([
          { "path": normalizedRelativePath, "sha256": digestHex }
          for each object in the subset
        ])
      )
    )
  )
```

Empty subset => same formula over `[]` (JCS of empty array). Implementations that diverge on NFC, separators, hex case, or sort order are invalid even if inventory path/sha256 pairs match.

Inventory **must** include: `binding.json`, `authorization.json`, all `docs-extract/**` files recorded at bind, all `evidence/*`, `dispositions/*`, `exceptions/*`, `exception-dispositions/*`, `scans/*`, `indexes/*`, `phase-manifests/phase-2.json`, `phase-manifests/phase-3-vN.json` (all versions present), `decision/evidence-index.json`, `decision/go-no-go.json`, and any other files under the run root **except** `run-status-events/*` and `phase-manifests/phase-4.json` itself (those are sealed by/after the sealed event). After writing `phase-4.json`, its own digest is covered by the sealed event's `phase4ManifestSha256`.

**Mechanical sealed predicate (#458 independent re-verify):**

```text
run is sealed iff ALL hold:
1. Exactly one run-status event exists with status=sealed
   (run-status state machine: that event is terminal; see below)
2. That event's JCS digest + previous-event hash chain are valid
3. event.decisionDigests match durable bytes of:
   - decision/evidence-index.json
   - decision/go-no-go.json
   - phase-manifests/phase-4.json
4. phase-4.json.sealedObjectInventory:
   a. every listed path exists under the run and sha256 matches
   b. no extra qualification object exists under the run outside
      inventory ∪ {phase-4.json} ∪ run-status-events/*
5. finalRunState high-water marks match recomputed values from
   disposition / exception-disposition chains and object sets
6. Replaying dispositions/exceptions for allBoundEvidenceKeys yields the same active set
   as decision/evidence-index.json (required + optional)
7. finalRunState.*RootSha256 values match recomputation via
   rootDigestAlgorithm RFC8785-JCS-sorted-path-sha256/v1
```

Harness write-rejection after seal is an operational control only; it is **not** a substitute for steps 1–7.

**Run-status terminal state machine (M-26):**

```text
(initial: zero run-status events)
  -> sealed                         # terminal
  -> abandoned-phase4-incomplete    # terminal
  -> abandoned-other                # terminal
```

Rules:

- At most **one** run-status event may exist for a run that reaches a decision/abandon outcome intended for handoff. Prefer exactly one terminal event.
- After any `sealed` or `abandoned-*` event, **any further** run-status event makes the run **invalid** / `NO_GO` (not consumable by #458).
- There is no transition from `abandoned-*` to `sealed` on the same `qualificationRunId`. Sealing after abandon requires a **new** run.
- "Active sealed event" means: the sole terminal event is `status=sealed` and no subsequent run-status event exists.

**Run-status event schema** (append-only under `run-status-events/`):

```text
eventId
qualificationRunId
bindingId
candidateId
runStatusEventSequence            # strictly increasing; for terminal-only policy, must be 1 when used as sole terminal event
previousRunStatusEventDigestSha256  # null only for sequence=1
eventDigestSha256                 # SHA-256(RFC8785-JCS(event without this field))
canonicalization: { algorithm: "RFC8785-JCS", version: 1 }
status: sealed | abandoned-phase4-incomplete | abandoned-other
sealedAtUtc                       # required when status=sealed
decisionDigests:                  # required when status=sealed
  evidenceIndexSha256
  goNoGoSha256
  phase4ManifestSha256            # covers sealedObjectInventory + finalRunState
reasonCode                        # required when abandoned-*
approvedByRole                    # qualification lead
approvedByIdentity
approvedAtUtc
```

**Incomplete Phase-4 recovery:**

- If any of steps 1–5 fail, or objects exist without a valid sealed event: mark the run `abandoned-phase4-incomplete` via a terminal run-status event (when possible) and **do not** treat it as sealed.
- Never rewrite partial Phase-4 write-once objects in place.
- Open a **new** `qualificationRunId` and re-run aggregation / Phase-4.
- After a valid sealed event exists, the harness **must reject** new evidence, disposition, exception, exception-disposition, scan, and additional run-status writes under that run.
- Corrections after seal require a **new** `qualificationRunId` (same or new `bindingId` as appropriate) and a new sealed Phase-4 decision for #458.
- #458 consumes **only** runs that satisfy the sealed predicate above.

| Item | Rule |
|------|------|
| Where | Maintainer-controlled durable store (URI operational config, not repo content) |
| Retention | Until #458 completes, or No-Go archive policy (>= 90 days recommended) |
| Access | Maintainer-only |
| Owner | Release maintainer (#458 confirms Phase-1 bytes before promote) |
| Rebuild | Lost Phase-1 bytes -> new #455 -> new candidate -> full Hard requalification |

---

## 8. Scenario packs (scheduling aids; no Gate class labels)

### 8.1 Variant keys

| variantId | Meaning |
|-----------|---------|
| `win-docker` | Windows Docker Desktop, local context |
| `linux-docker` | Linux Docker Engine, local context |
| `linux-arm64` | arm64 host |
| `ci-auto` | Automated fixtures |
| `admin-local-dev` | Development + loopback + ALLOW_HTTP |
| `admin-prod-https` | Approved HTTPS proxy lab |
| `local-dev` / `proxy-https` | G456-11 variants |
| `acs-staging-nosend` | Staging posture, no live Production send |
| `acs-staging-real` | Maintainer real Staging ACS |
| `acs-production` | **Exact Production** ACS send-ready path |
| `acs-production-release-ov` | Release Production OV (distinct evidence) |
| `admin-integrated` | G456-16 integrated follow-on-failure (required with `ci-auto`) |
| `vps` | G456-36 VPS Conditional target |

### 8.2 Pack A — Fresh Mailpit

| ID | requiredVariants | Expect | Evidence type |
|----|------------------|--------|---------------|
| G456-01 | win-docker | Mode 1 complete; identity match | `manual-smoke` or `automated-test` |
| G456-02 | linux-docker | Same | same |

### 8.3 Pack B — ACS and Release OV

| ID | requiredVariants | Expect | Evidence type | Schema notes |
|----|------------------|--------|---------------|--------------|
| G456-03 | acs-staging-nosend | Staging no-send complete | `staging-acs-verification` | Sec. 12.2 G456-03 predicates only |
| G456-04 | acs-staging-real | Typed fixed synthetic send on Staging | `staging-acs-verification` | Sec. 12.2 G456-04 predicates; `restrictedOpsRecordId` required |
| G456-05 | acs-production | **Exact Production** send-ready; **no mail send**; not staging; not "prod-like" | `production-acs-send-ready` | `sendKind=none-for-send-ready-assert`, `mailSendAttempted=false`, `liveSending=true` applied + effective/fingerprint/integrity, `testBypassUsed=false` |
| G456-06 | acs-production-release-ov | Normal Mailer path Production send for **release** | `release-production-operational-verification` | Only place for Production send completion; distinct from G456-05; `restrictedOpsRecordId` required |

### 8.4 Pack C — Admin access profiles

| ID | requiredVariants | Focus |
|----|------------------|-------|
| G456-07 | admin-local-dev | Local Development login + setup-status |
| G456-08 | admin-prod-https | Production HTTPS Secure cookie login + setup-status |
| G456-09 | admin-prod-https | Secure cookie not judged OK on HTTP |
| G456-10 | admin-prod-https | `AMANE_ADMIN_ALLOW_HTTP=true` rejected |
| G456-11 | local-dev, proxy-https | Local-address mismatch -> 404 |
| G456-12 | admin-prod-https | No HTTPS path -> no bootstrap; Admin disabled; Main may succeed |

### 8.5 Pack D — Admin bootstrap steps (`ADM-*` under parents)

| Sub-ID | Behaviour | Parent |
|--------|-----------|--------|
| ADM-01 | Fresh bootstrap | G456-13 |
| ADM-02 | Login | G456-07/08/13 |
| ADM-03 | Setup status display | G456-13, G456-32 |
| ADM-04 | Managed same-user reapply | G456-14 |
| ADM-05 | No credential rotation | G456-14 |
| ADM-06 | Different username reject | G456-15 |
| ADM-07 | Password rotation reject | G456-15 |
| ADM-08 | Manual existing Admin reject | G456-15 |
| ADM-09 | non-interactive Admin enable reject | G456-17 |
| ADM-10 | Sync then subsequent failure (`accessProfile` required) | G456-16 |
| ADM-11 | Routes not exposed after config rollback | G456-16 |
| ADM-12 | Status shows bundle + send-ready | G456-13, G456-32 |
| ADM-13 | Status does not show deployment OV confirmed | G456-13, G456-32 |

G456-13/14/17 require `win-docker` and `linux-docker`.

### 8.6 Pack E — Fault / rollback

| ID | requiredVariants | Cover |
|----|------------------|-------|
| G456-18 | win-docker, linux-docker | Apply failure -> rollback; rollback failure != success |
| G456-19 | win-docker, linux-docker | Fresh failure; not rollback-success |
| G456-20 | ci-auto | Fingerprint mismatch |
| G456-21 | ci-auto | Secret swap / old / other-bundle / bad mount |
| G456-22 | ci-auto | Stale launcher / image mismatch |
| G456-27 | ci-auto | Concurrent setup |
| G456-28 | ci-auto | Crash / cancel minimal |
| G456-16 | ci-auto, admin-integrated | Admin partial success split reporting (both variants required) |

### 8.7 Pack F — Security

| ID | requiredVariants | Focus |
|----|------------------|-------|
| G456-23 | ci-auto | Remote Docker Context FAIL |
| G456-24 | ci-auto | Command injection and env/Compose injection |
| G456-25 | ci-auto | Path traversal |
| G456-26 | win-docker, linux-docker | Symlink; Windows reparse |
| G456-30 | ci-auto | Token / Origin / Host / CSRF |
| G456-31 | ci-auto | Secret-like scan |
| G456-32 | ci-auto | Admin setup-status authz |
| G456-33 | win-docker, linux-docker | Terminal / NI secret boundaries |

### 8.8 Pack G — Cross-platform / other rows (execution info only; no Gate class labels)

| ID | requiredVariants / procedure |
|----|------------------------------|
| G456-35 | `linux-arm64` — link #455 provenance `archives[targetRid=linux-arm64].smokeResult=passed` only if same `candidateId` inputs; else re-run smoke CLI (Sec. 17) |
| G456-34 | `linux-arm64` — full E2E or complete exception record |
| G456-29 | `win-docker`, `linux-docker` — OS-specific crash extras or complete exception |
| G456-36 | `vps` — PASS or complete Conditional exception for `vps` |
| G456-37 | `win-docker`, `linux-docker` — optional NI extras or complete exception |
| G456-38 | optionalEvidenceKey `nas` — Informational recording; owner in authorization (m-07) |
| G456-39 | optionalEvidenceKey `macos` |
| G456-40 | optionalEvidenceKey `mode5-manual` |
| G456-41 | optionalEvidenceKey `external-secret-manager-docs` |

Gate class for these rows is read only from `binding.issueSnapshot` at display/aggregation time. Do not hard-code Conditional/Informational strings in this section. Informational optional keys are aggregated (Sec. 9.3); their missing/FAIL alone never blocks Go, but integrity violations on them do (Sec. 14).

### 8.9 Pack H — DB migrations (012/013) (execution info only; no Gate class labels)

Frozen INCLUDE inventory (filenames): `012_provider_event_inbox_details.sql`, `013_provider_queue_dead_letters.sql` (from #460/#461). Live Issue wording for G456-44 is migration-owned delta (`provider_event_inbox.recipient_email` is 011-owned; out of 012/013 delta scope). Validators in Sec. 12.2 enforce exact history equality, exact schema allowlists, required PII value canary, and B-MIG-PIN digest equality with binding.

| ID | requiredVariants / procedure | Evidence type |
|----|------------------------------|---------------|
| G456-42 | `win-docker`, `linux-docker` — fresh/empty DB; ApplyPending through 013; Sec. 12.2 G456-42 exact history validators | `db-migration-fresh-apply` |
| G456-43 | `win-docker`, `linux-docker` — DB previously at v1.1.0 tip (011 applied); then apply 012+013; Sec. 12.2 G456-43 exact history validators | `db-migration-upgrade` |
| G456-44 | `ci-auto` — 012/013 schema delta exact allowlist + required PII value canary (011 `recipient_email` out of delta scope); Sec. 12.2 G456-44 validators | `db-migration-schema-contract` |

**Supporting automation:** unit coverage already exists in `BounceIngestionMigrationTests` / BounceIngestionSchemaTests and may drive or corroborate G456-44 (and inform 42/43 fixtures). Supporting unit PASS alone is **not** qualification evidence — harness envelope + exact history/schema digests + PIN digest equality + required `piiValueCanaryResult` + `prohibitedContentScan` + disposition under Sec. 12 / Sec. 9 are still required for Go. Active-PASS / exception rules for these rows live only in Sec. 6.4 / 9.5 / 14 (not in this pack).

---

## 9. Evidence, disposition, and exception model (B-03, M-11, M-16, M-17, M-20, M-21, M-22)

### 9.1 Immutable evidence

```text
runs/<qualificationRunId>/evidence/<evidenceId>.json
```

Never mutated after write. Must include `qualificationRunId`, `bindingId`, `candidateId`, `planCommitSha`, `planFileSha256`, `issueBodySha256`.

Evidence may be written only while the run is **unsealed** (before Phase-4). After seal, new evidence requires a new `qualificationRunId`.

### 9.2 Immutable disposition events with total order (M-16, M-20)

```text
runs/<qualificationRunId>/dispositions/<eventId>.json
```

```text
eventId
qualificationRunId
bindingId
candidateId
eventSequence                 # strictly increasing integer per qualificationRunId, starting at 1
previousEventDigestSha256     # null only for eventSequence=1
eventDigestSha256             # sha256 of RFC 8785 JCS canonical event bytes excluding this field
canonicalization:
  algorithm = "RFC8785-JCS"
  version = 1
scenarioId
variantId
targetEvidenceId              # required except where table says forbidden
action: accept | supersede | invalidate | restore
supersededByEvidenceId        # required/forbidden per action table
restoresEventId               # required for restore; forbidden otherwise
reasonCode
approvedByRole
approvedByIdentity
approvedAtUtc
```

**Canonicalization (M-20):** `eventDigestSha256 = SHA-256(UTF-8(RFC8785-JCS(eventWithoutDigestField)))`. Implementations must use JCS (`algorithm=RFC8785-JCS`, `version=1`). Non-JCS serializers are invalid even if the hash chain links. Hash mismatch or unknown canonicalization version => `NO_GO`.

**Ordering rule:** aggregator replays **only** by ascending `eventSequence` (hash chain must verify). Never by filesystem order or `approvedAtUtc` alone. Same-second events must still have distinct sequences assigned by a single run sequencer.

**Sealed run:** after Phase-4 seal (Sec. 7.8), disposition append is rejected. Compensating corrections use a **new** `qualificationRunId`, not a higher sequence on the sealed run.

#### Per-key derived state

For each evidence key `K = (scenarioId, variantId)` the aggregator holds:

```text
activeEvidenceId: evidenceId | null
activeDispositionEventId: eventId | null   # last event that established the active set
invalidatedEvidenceIds: set
```

Initial state: all null / empty.

#### Action field matrix

| action | `targetEvidenceId` | `supersededByEvidenceId` | `restoresEventId` | Notes |
|--------|--------------------|--------------------------|-------------------|-------|
| `accept` | **required** | **forbidden** | **forbidden** | Activates `targetEvidenceId` for K |
| `supersede` | **required** (outgoing active) | **required** (incoming) | **forbidden** | Replaces active with `supersededByEvidenceId` in one event |
| `invalidate` | **required** | **forbidden** | **forbidden** | Removes target from active; records in invalidated set |
| `restore` | **forbidden** | **forbidden** | **required** | Reverts to state immediately after `restoresEventId` for K |

#### State transition table

| Current activeEvidenceId | action | Preconditions | Next activeEvidenceId |
|--------------------------|--------|---------------|------------------------|
| null | `accept` | target exists; same K; not previously invalidated unless explicitly re-accepted after new evidence | target |
| E1 | `accept` | **forbidden** while active exists | — use `supersede` |
| E1 | `supersede` | target == E1; supersededBy exists; same K; supersededBy != E1; supersededBy not invalidated | supersededBy |
| E1 | `invalidate` | target == E1 **or** target is any known evidence for K | if target==E1 then null; else active unchanged and target added to invalidated |
| any | `restore` | `restoresEventId` is a prior disposition for same K; replay prefix through that event yields a valid state | state after `restoresEventId` |
| any | * | target evidence missing, wrong `scenarioId`/`variantId`, wrong run/binding, or sealed run | **invalid transition** => `NO_GO` (even if hash chain bytes verify) |

**Semantics clarifications (M-20):**

1. `accept` does **not** silently deactivate other evidence; it is allowed only when `activeEvidenceId` is null for K (first accept), or after `invalidate`/`restore` left null.
2. `supersede` **atomically** deactivates `targetEvidenceId` and activates `supersededByEvidenceId`. A separate follow-up `accept` is **not** required and must not be used as the supersede mechanism.
3. `restore` restores the derived disposition state identified by `restoresEventId` (a prior disposition event for K), not an arbitrary evidence blob. It does not delete later events; later events remain in the chain for audit, but replay for Go uses the restore semantics above.
4. Multiple `invalidate`/`supersede` on the same K are allowed in sequence; `restore` always names the exact prior event to return to.
5. Cross-scenario / cross-variant references are invalid transitions => `NO_GO`.

### 9.3 Aggregator selection (M-27)

Bound evidence keys:

```text
requiredEvidenceKeys =
  all (scenarioId, variantId) from binding.rows[].requiredVariants[]

optionalEvidenceKeys =
  all entries from binding.optionalEvidenceKeys[]

allBoundEvidenceKeys =
  requiredEvidenceKeys ∪ optionalEvidenceKeys
```

Aggregator steps:

1. Reject if run is missing `authorization.json` or digest mismatches binding.
2. Verify disposition hash chain + JCS digests for the run (covers events for all keys).
3. Replay by `eventSequence` for **every** key in `allBoundEvidenceKeys` using Sec. 9.2 transitions.
4. Any invalid transition / unknown key / chain break => `machineVerdict=NO_GO`.
5. Derive exactly one active evidenceId (or null) per bound key.
6. Apply **global integrity rules** (Sec. 14) to **all** bound keys and their active or orphaned evidence/disposition objects — including optional keys.
7. Apply **Gate outcome rules** only to required keys (and Conditional exceptions): missing/FAIL follow Hard/Conditional semantics.
8. For optional keys: missing / `NOT_RUN` / active FAIL / not-confirmed **alone** do **not** force `NO_GO`. Still record state in final evidence index.
9. New PASS after prior FAIL: apply Sec. 9.4 FAIL -> PASS split predicates (owner on evidence `executedBy*`, lead on disposition `approvedBy*`) for **any** bound key including optional.
10. False FAIL: `invalidate` with Qualification lead; does not count as Hard FAIL once invalidated and not active.
11. Later accepted FAIL supersedes prior PASS via `supersede` (or invalidate+accept when active was cleared).

Final `decision/evidence-index.json` / `go-no-go.json.scenarioIndex` **must** include every bound key (required and optional). Optional example:

```json
{
  "scenarioId": "G456-38",
  "gateClass": "Informational",
  "variants": [
    {
      "variantId": "nas",
      "result": "NOT_CONFIRMED",
      "evidenceId": null,
      "required": false
    }
  ],
  "scenarioResult": "NOT_CONFIRMED"
}
```

When optional evidence is active-accepted, record its `result` (`PASS`/`FAIL`/…) and `evidenceId` with `"required": false`.

### 9.4 Authorization snapshot and who may append (m-04, M-22)

Phase-2 **must** write:

```text
runs/<qualificationRunId>/authorization.json
```

```text
schemaVersion = 1
qualificationRunId
bindingId
candidateId
qualificationLeadRole
qualificationLeadIdentity
conditionalApproverRole
conditionalApproverIdentity
evidenceOwners[]:
  scenarioId
  variantId
  ownerRole
  ownerIdentity
createdAtUtc
```

Rules:

- Owner map key is **`(scenarioId, variantId)`**, never `variantId` alone (WP-C and WP-D may share `linux-docker` on different scenarios).
- Every **required** `(scenarioId, variantId)` from binding **and** every `optionalEvidenceKeys[]` entry must appear exactly once in `evidenceOwners` (m-07 Option A).
- Evidence may be written only for keys present in `requiredVariants` (via rows) or `optionalEvidenceKeys`. Unknown keys => reject / `NO_GO`.
- `binding.authorizationDigestSha256 = SHA-256(RFC8785-JCS(authorization.json))`.
- `decision/go-no-go.json` must repeat `authorizationDigestSha256`.
- After Phase-2, `authorization.json` is write-once for that run.

#### Authorization matrix

| Action | Allowed role/identity |
|--------|------------------------|
| Write new evidence for K | `evidenceOwners` entry for that `(scenarioId, variantId)` via `executedByRole`/`executedByIdentity` |
| Disposition `accept` (first accept; any result including PASS or FAIL) | Evidence owner for K via `approvedBy*` |
| Disposition `supersede` PASS -> FAIL or PASS -> PASS (replacement; not FAIL -> PASS) | Evidence owner for K via `approvedBy*` |
| Disposition `accept` or `supersede` that changes **active FAIL -> active PASS** | **Split actors (M-25 recommended):** see below |
| Disposition `invalidate` | `qualificationLeadRole` + identity via `approvedBy*` |
| Disposition `restore` | `qualificationLeadRole` + identity via `approvedBy*` |
| Exception create | Evidence owner for K |
| Exception-disposition `approve` / `supersede` / `revoke` / `restore` | `conditionalApproverRole` + identity via `approvedBy*` |
| Phase-4 humanDecision APPROVE/REJECT | `qualificationLeadRole` + identity (must match authorization snapshot) |

**FAIL -> PASS dual-actor mapping (M-25; recommended; keeps single `approvedBy*` pair):**

Do **not** require co-signature arrays on the disposition event. Instead both predicates are mandatory and checked against different fields:

```text
For accept/supersede that changes active FAIL -> active PASS for key K:
1. incoming evidence (target of accept, or supersededByEvidenceId of supersede):
   executedByRole/Identity MUST match evidenceOwners[K]
2. disposition event:
   approvedByRole/Identity MUST match qualificationLeadRole/Identity
3. Both (1) and (2) are required. Either mismatch => NO_GO / reject event.
```

Interpretation: evidence owner authors the replacement PASS evidence; qualification lead alone authorizes the disposition that activates it. This is the only FAIL -> PASS authorization predicate; implementations must not substitute owner-only or lead-only checks.

Mismatch between event `approvedByRole`/`approvedByIdentity` (or evidence `executedBy*`) and the sealed snapshot, under the matrix above, => `NO_GO`.

### 9.5 Conditional exceptions (M-17, M-20)

Immutable exception objects + separate exception-disposition events:

```text
runs/<qualificationRunId>/exceptions/<exceptionId>.json
runs/<qualificationRunId>/exception-dispositions/<eventId>.json
```

Exception file (immutable):

```text
exceptionId
qualificationRunId
bindingId
candidateId
issueBodySha256
planCommitSha
planFileSha256
scenarioId
variantId
reasonNotExecutable
alternateVerification
residualRisk
impactScope
createdAtUtc
```

Exception-disposition event (separate sequence namespace `exceptionEventSequence` per run; same JCS + hash-chain rules as Sec. 9.2):

```text
eventId
qualificationRunId
bindingId
candidateId
exceptionEventSequence
previousExceptionEventDigestSha256   # null only for sequence=1
eventDigestSha256
canonicalization: { algorithm: "RFC8785-JCS", version: 1 }
scenarioId
variantId
action: approve | supersede | revoke | restore
targetExceptionId                    # required for approve|revoke; for supersede = outgoing
supersededByExceptionId              # required for supersede; forbidden otherwise
restoresExceptionEventId             # required for restore; forbidden otherwise
reasonCode
approvedByRole                       # must be conditionalApproverRole
approvedByIdentity
approvedAtUtc
```

Exception-disposition transitions mirror Sec. 9.2 with exception IDs instead of evidence IDs:

| action | Effect |
|--------|--------|
| `approve` | Active exception becomes `targetExceptionId` when none active |
| `supersede` | Replace active exception with `supersededByExceptionId` |
| `revoke` | Clear active if target is active |
| `restore` | Return to state after `restoresExceptionEventId` |

A Conditional variant is complete for Go only when the derived active exception disposition is `approve` (and fields satisfy Sec. 12.3). Issue/plan drift invalidates old exceptions because they bind to a different `qualificationRunId`/`bindingId`. After Phase-4 seal, exception-disposition append is rejected (new run required).

**Forbidden for G456-42..44:** creating or approving a Conditional exception for these Hard migration rows. Only active PASS satisfies Go. Any exception object / `approve` disposition targeting G456-42..44 => `NO_GO` (Sec. 14).

---

## 10. Test architecture

Reuse product tests as fixtures; qualification evidence lives under `artifacts/qualification/`.

Planned helpers (WP-A; must not break #455 CLIs):

```text
scripts/qualify-bind-candidate.sh
scripts/qualify-accept-evidence.sh
scripts/qualify-aggregate-go-no-go.sh
```

Do not replace or rename:

```text
scripts/scan-setup-release-bundle.sh <staged-root>
scripts/smoke-setup-release-bundle.sh <archive> <archiveSha256> <rid> <release_version>
scripts/handoff-setup-release-candidate.sh
```

---

## 11. Fault injection + Admin partial success

Fault inventory: apply mid-fail, fresh fail, rollback fail, crash/cancel, concurrent, fingerprint, secrets, stale launcher, Admin sync-then-fail.

**G456-16 / ADM-10 required fields:**

```text
configRollback
adminDatabaseState
adminExposure
accessProfile          # local-development | production-https | unknown
loginVerification
manualActionRequired
```

Assert Admin routes not served after successful config rollback to disabled.

---

## 12. Evidence schemas

### 12.1 Common envelope

```json
{
  "schemaVersion": 1,
  "kind": "release-qualification-evidence",
  "evidenceType": "...",
  "evidenceId": "...",
  "candidateId": "...",
  "sourceCommitSha": "...",
  "scenarioId": "G456-NN",
  "variantId": "...",
  "issueBodySha256": "...",
  "planRevision": "12",
  "planCommitSha": "...",
  "planFileSha256": "...",
  "bindingId": "...",
  "qualificationRunId": "...",
  "attempt": 1,
  "result": "PASS|FAIL|NOT_RUN|EXCEPTION",
  "startedAtUtc": "...",
  "finishedAtUtc": "...",
  "executedByRole": "...",
  "executedByIdentity": "...",
  "procedureId": "...",
  "procedureRevision": "...",
  "runnerClass": "...",
  "toolVersion": "...",
  "attestedAtUtc": "...",
  "identity": {},
  "prohibitedContentScan": {
    "result": "PASS|FAIL",
    "scannerId": "qualify-secret-like/1",
    "scannerVersion": "...",
    "reportDigestSha256": "..."
  },
  "typePayload": {}
}
```

Disposition is **not** stored inside this envelope (see Sec. 9.2).

`notes` are non-authoritative. Acceptance predicates live in `typePayload`.

For G456-04 and G456-06, `restrictedOpsRecordId` inside `typePayload` is **required** (opaque; never copy restricted log contents into release evidence).

### 12.2 Type-specific typePayload (M-03, B-04, M-14, M-18, M-19)

#### Result / typePayload consistency (M-19)

Common envelope `result` and typePayload failure indicators **must not contradict**. Violations are validator failures and Sec. 14 `NO_GO` even if a disposition accepted the evidence.

| Scenario | `result=PASS` only if | Failure payload => required `result` |
|----------|----------------------|--------------------------------------|
| G456-03 | `outcome=configuration-applied` **and** all Sec. 12.2 G456-03 predicates | `outcome=rejected\|failed` => `result=FAIL` |
| G456-04 | `outcome=completed` **and** all send predicates in Sec. 12.2 G456-04 | any incomplete/forbidden send state => `result=FAIL` (or evidence rejected before accept) |
| G456-05 | `doctorOrReadinessSummary=pass` **and** all Sec. 12.2 G456-05 predicates | `doctorOrReadinessSummary=fail` => `result=FAIL` |
| G456-06 | all Sec. 12.2 G456-06 predicates **and** valid active G456-05 PASS reference | any predicate/reference mismatch => `result=FAIL` (or evidence rejected before accept) |
| G456-42 | `outcome=applied` **and** all Sec. 12.2 G456-42 exact-history predicates **and** evidence migration digests exactly equal binding PIN values | apply/schema/inventory/history/PIN inequality => `result=FAIL` |
| G456-43 | `outcome=upgraded` **and** all Sec. 12.2 G456-43 exact-history predicates **and** evidence migration digests exactly equal binding PIN values | pending/apply/schema/history/PIN inequality => `result=FAIL` |
| G456-44 | `contractResult=pass` **and** exact schema allowlist PASS **and** `piiValueCanaryResult=pass` **and** `prohibitedContentScan.result=PASS` **and** evidence migration digests exactly equal binding PIN values | `contractResult=fail` **or** `piiValueCanaryResult=fail` **or** canary not run **or** canary values persisted => `result=FAIL` (or reject) |

`result=EXCEPTION` is reserved for Conditional exception paths (not G456-03/04/05 Hard PASS; **not** G456-42..44). `result=NOT_RUN` must not be active-accepted for Hard required variants. G456-42..44: Conditional exceptions forbidden — active PASS only.

#### staging-acs-verification — G456-03 (no-send) validator

```text
acsEnvironment = Staging
liveSending = false
sendKind = none
mailSendAttempted = false
testBypassUsed = false
normalMailerPath = false | true   # if present, must not imply a send completed
outcome = configuration-applied | rejected | failed
mailboxConfirmation = not-required
restrictedOpsRecordId = optional
```

**PASS consistency:** `result=PASS` **iff** `outcome=configuration-applied`.  
**FAIL consistency:** `outcome=rejected|failed` => `result=FAIL` (mandatory).  
**Forbidden for G456-03:** `typed-fixed-synthetic`, `liveSending=true`, `mailSendAttempted=true`, `outcome=completed` implying a send, `result=PASS` with `outcome!=configuration-applied`.

#### staging-acs-verification — G456-04 (typed synthetic) validator

```text
acsEnvironment = Staging
sendKind = typed-fixed-synthetic
mailSendAttempted = true
testBypassUsed = false
outcome = completed
mailboxConfirmation = not-run | observed-value-free
restrictedOpsRecordId = required
```

**PASS consistency:** `result=PASS` **iff** `outcome=completed` **and** all G456-04 send predicates above hold.  
**Forbidden for G456-04:** `sendKind=none`, missing send completion, Staging bypass / Production substitution, `result=PASS` without completed send predicates.

#### production-acs-send-ready (G456-05 only)

```text
acsEnvironment: Production
liveSending: true
sendKind: none-for-send-ready-assert
mailSendAttempted: false
testBypassUsed: false
effectiveFingerprintMatch: true
bundleIntegrityMatched: true
doctorOrReadinessSummary: pass|fail
mailboxConfirmation: not-required-for-send-ready
```

**PASS consistency:** `result=PASS` **iff** `doctorOrReadinessSummary=pass`.  
**FAIL consistency:** `doctorOrReadinessSummary=fail` => `result=FAIL` (mandatory).  
**Forbidden on G456-05:** `typed-fixed-synthetic`, any Production mail send, Staging substitution, `result=PASS` with `doctorOrReadinessSummary=fail`.

#### release-production-operational-verification (G456-06 only)

```text
acsEnvironment: Production
mailPath: normal-mailer
testBypassUsed: false
sendCompletedValueFree: true
distinctFromSendReadyEvidenceId: <G456-05 evidenceId>
tenantStatusExportForbidden: true
restrictedOpsRecordId: required opaque id
```

**PASS consistency (m-06):** `result=PASS` **iff** all G456-06 predicates above hold **and** `distinctFromSendReadyEvidenceId` references evidence that is:

1. in the **same** `qualificationRunId` and `bindingId`
2. `scenarioId=G456-05` (Production send-ready)
3. the **active accepted** evidence for its `(G456-05, variantId)` key after disposition replay
4. `result=PASS`
5. a **different** `evidenceId` than this G456-06 evidence

**Forbidden on G456-06:** dangling / wrong-run / non-active / non-PASS G456-05 references; conflating Release OV with send-ready evidence; `result=PASS` when any predicate above fails.

#### Admin partial / rollback

Includes Sec. 11 fields + `accessProfile`.

#### db-migration-fresh-apply — G456-42 validator

Machine-check **full exact** migration history (not MustContain; not ends-at / includes; not self-declared inventory alone). Canonical lists use **migration runner order** (not filename ordinal):

```text
migrationDecision = INCLUDE
migrationInventory[] = expectedPost011Inventory[] = [
  "012_provider_event_inbox_details.sql",
  "013_provider_queue_dead_letters.sql"
]   # exact equality

# Canonical expected lists (runner order at releaseCommitSha / candidate pin):
expectedFullMigrationInventory[]   # full Data/Migrations inventory in runner apply order
expectedThrough011[]               # prefix of expectedFullMigrationInventory through
                                   # "011_bounce_ingestion.sql" inclusive
expectedPost011Inventory[] == [
  "012_provider_event_inbox_details.sql",
  "013_provider_queue_dead_letters.sql"
]

# Required typePayload fields (all machine-checked):
migrationDirectoryInventoryBefore[]     # filenames under Data/Migrations at candidate pin
                                        # in runner order (== expectedFullMigrationInventory)
migrationDirectoryInventoryDigestSha256 # digest of that inventory listing
migrationFileDigests[]                  # {path, sha256, gitBlobSha} — exact equality with
                                        # binding.migrationFileDigests[] /
                                        # migrationPinWithoutDigest.files[]
                                        # (Ordinal path ascending; frozen 012/013 only)
preApplyAppliedMigrations[]             # exact ordered applied list before ApplyPending
preApplyPendingMigrations[]             # exact ordered pending list before ApplyPending
postApplyAppliedMigrations[]            # exact ordered applied list after ApplyPending
postApplyPendingMigrations[]            # exact ordered pending list after ApplyPending
lastAppliedBefore
lastAppliedAfter

startingSchema = empty-or-new-db
applyPendingThrough = "013_provider_queue_dead_letters.sql"

# Full exact equality (M-R10-02; not ends-at / must-include):
preApplyAppliedMigrations[] == []
preApplyPendingMigrations[] == expectedFullMigrationInventory[]
postApplyAppliedMigrations[] == expectedFullMigrationInventory[]
postApplyPendingMigrations[] == []
lastAppliedAfter == "013_provider_queue_dead_letters.sql"

# Directory inventory digest consistency with Sec. 4 M-R11-01 algorithms:
migrationDirectoryInventoryDigestSha256 == binding.migrationInventoryDigestSha256
                                        == migrationPinWithoutDigest.inventoryDigestSha256
                                        == SHA-256(UTF-8(RFC8785 JCS(inventoryDocument)))
migrationFileDigests[] == binding.migrationFileDigests[]   # exact
# also require binding.migrationPinDigestSha256
#   == SHA-256(UTF-8(RFC8785 JCS(migrationPinWithoutDigest)))
# Digested objects never include their own digest fields; no evidenceDigestSha256 in PIN.

tablesPresent[] exact presence check may cross-check provider_event_inbox,
                provider_queue_dead_letters
# Schema delta allowlists verified under G456-44; fresh-apply may cross-check presence.
outcome = applied | failed
```

**B-MIG-PIN / directory gate:** if `migrationDirectoryInventoryBefore[]` contains any migration filename beyond the frozen 012/013 pair since v1.1.0 tip (besides those two), FAIL (and B-MIG-PIN fails for the pin). Evidence migration digests that are not **exactly equal** to binding PIN values => FAIL.

**PASS consistency:** `result=PASS` **iff** `outcome=applied` **and** all full exact-equality predicates above hold **and** `migrationDecision=INCLUDE` with exact `migrationInventory[]` **and** evidence migration digests **exactly equal** binding PIN values.  
**FAIL consistency:** any inequality, apply stop before 013, extra post-011 migration in directory, missing digest fields, or PIN mismatch => `result=FAIL`.  
**Required:** `prohibitedContentScan.result=PASS`. Contradiction between `result` and typePayload => `NO_GO` (Sec. 14 / M-19).

#### db-migration-upgrade — G456-43 validator

```text
migrationDecision = INCLUDE
migrationInventory[] = expectedPost011Inventory[] = [
  "012_provider_event_inbox_details.sql",
  "013_provider_queue_dead_letters.sql"
]   # exact equality

# Same canonical expected lists as G456-42 (runner order, not filename ordinal):
expectedFullMigrationInventory[]
expectedThrough011[]
expectedPost011Inventory[] == [
  "012_provider_event_inbox_details.sql",
  "013_provider_queue_dead_letters.sql"
]

# Same required typePayload history + digest fields as G456-42:
migrationDirectoryInventoryBefore[]
migrationDirectoryInventoryDigestSha256
migrationFileDigests[]                  # exact equality with binding PIN digests
preApplyAppliedMigrations[]
preApplyPendingMigrations[]
postApplyAppliedMigrations[]
postApplyPendingMigrations[]
lastAppliedBefore
lastAppliedAfter

startingSchema = v1.1.0-tip-011-applied
applyPendingThrough = "013_provider_queue_dead_letters.sql"

# Full exact equality (M-R10-02; not ends-at / must-include):
lastAppliedBefore == "011_bounce_ingestion.sql"
preApplyAppliedMigrations[] == expectedThrough011[]
preApplyPendingMigrations[] == expectedPost011Inventory[]   # == [012, 013]
postApplyAppliedMigrations[] == expectedThrough011[] + expectedPost011Inventory[]
postApplyPendingMigrations[] == []
lastAppliedAfter == "013_provider_queue_dead_letters.sql"

# Directory inventory digest consistency with the canonical lists / binding PIN (M-R11-01):
migrationDirectoryInventoryDigestSha256 == binding.migrationInventoryDigestSha256
                                        == migrationPinWithoutDigest.inventoryDigestSha256
migrationFileDigests[] == binding.migrationFileDigests[]
# binding.migrationPinDigestSha256 == SHA-256(UTF-8(RFC8785 JCS(migrationPinWithoutDigest)))
# no evidenceDigestSha256 in PIN

tablesPresent[] exact presence check may cross-check provider_event_inbox,
                provider_queue_dead_letters
outcome = upgraded | failed
```

**B-MIG-PIN / directory gate:** same as G456-42 — extra migrations beyond frozen 012/013 since v1.1.0 tip => FAIL; PIN digest inequality => FAIL.

**PASS consistency:** `result=PASS` **iff** `outcome=upgraded` **and** all full exact-equality predicates above hold **and** evidence migration digests **exactly equal** binding PIN values.  
**FAIL consistency:** pending/applied list inequality, apply fails, directory extras, missing digests, or PIN mismatch => `result=FAIL`.  
**Required:** `prohibitedContentScan.result=PASS`.

#### db-migration-schema-contract — G456-44 validator

Align with live Issue wording (migration-owned delta) and `BounceIngestionMigrationTests` / BounceIngestionSchemaTests (supporting automation; harness envelope still required). Prefer **exact allowlists** over partial deny-lists. PII **value** canary is **required** (B-R10-01).

```text
migrationDecision = INCLUDE
migrationInventory[] = [
  "012_provider_event_inbox_details.sql",
  "013_provider_queue_dead_letters.sql"
]   # exact equality

# PIN digest equality (M-R10-01 + M-R11-01) — same as G456-42/43:
migrationDirectoryInventoryDigestSha256 == binding.migrationInventoryDigestSha256
                                        == migrationPinWithoutDigest.inventoryDigestSha256
migrationFileDigests[] == binding.migrationFileDigests[]   # exact
# binding.migrationPinDigestSha256 must equal
#   SHA-256(UTF-8(RFC8785 JCS(migrationPinWithoutDigest)))
# no evidenceDigestSha256 in PIN / digested objects

# 012 delta only (exact):
addedColumnsByMigration.012 == [
  "status_message TEXT NULL",
  "occurred_at TEXT NULL"
] on table provider_event_inbox
# No other columns added by 012.

# 013 created table exact allowlist (from 013 SQL):
createdTableByMigration.013.table = provider_queue_dead_letters
createdTableByMigration.013.columns[] == [
  "id TEXT NOT NULL PRIMARY KEY",
  "provider TEXT NOT NULL",
  "queue_message_id TEXT NOT NULL",
  "failure_stage TEXT NOT NULL",
  "last_error_code TEXT NOT NULL",
  "dequeue_count INTEGER NOT NULL",
  "created_at TEXT NOT NULL",
  "updated_at TEXT NOT NULL"
]
createdTableByMigration.013.constraints[] == [
  "CHECK (failure_stage IN ('decode', 'parse'))",
  "CHECK (dequeue_count >= 0)",
  "UNIQUE (provider, queue_message_id)"
]
createdTableByMigration.013.indexes[] == [
  "idx_provider_queue_dead_letters_created ON provider_queue_dead_letters (created_at)"
]

# Scope note (Issue): provider_event_inbox.recipient_email is 011-owned;
# out of 012/013 delta scope — do not treat its presence as a 012/013 FAIL,
# and do not claim 012/013 "added" it.

# Required PII value canary (B-R10-01; no not-run):
# Prove raw body / recipient / secret / connection-string / provider-raw-error
# canary VALUES are NOT persisted into provider_queue_dead_letters (013 table),
# including into allowlisted columns such as queue_message_id / last_error_code /
# failure_stage.
piiValueCanaryResult = pass | fail     # required; no not-run

contractResult = pass | fail
```

**PASS consistency:** `result=PASS` **iff** `contractResult=pass` **and** exact schema allowlist PASS (`addedColumnsByMigration.012` + `createdTableByMigration.013` columns/constraints/indexes) **and** `piiValueCanaryResult=pass` **and** `prohibitedContentScan.result=PASS` **and** `migrationDecision=INCLUDE` with exact `migrationInventory[]` **and** evidence migration digests **exactly equal** binding PIN values.  
**FAIL / reject:** canary not run; `piiValueCanaryResult=fail`; raw body / recipient / secret / connection-string / provider-raw-error canary values persisted into the 013 table; `contractResult=fail`; schema allowlist inequality; PIN digest mismatch.  
**Required:** `prohibitedContentScan.result=PASS`.  
**Forbidden:** treating 011-owned `recipient_email` as in-scope 012/013 delta evidence; recording `not-run` for the PII value canary.

### 12.3 Conditional exception

See Sec. 9.5 for immutable exception + exception-disposition lifecycle. Payload fields remain:

```text
reasonNotExecutable
alternateVerification
residualRisk
approverRole / approverIdentity   # on approve disposition event
impactScope
approvedAtUtc                     # on approve disposition event
```

### 12.4 #458 reuse header

`scope=release-qualification-only; not-for-tenant-operational-status`

---

## 13. Rerun policy

| Change | Action |
|--------|--------|
| New candidate bytes / provenance | New `candidateId`; all Hard required variants |
| Docs content / new SHA | New pin -> new #455 -> new candidate |
| Harness fix | Invalidate false FAILs; rerun affected variants (Sec. 9) |
| Security fixture change | Rerun affected security variants |
| OS procedure change | That OS variants |
| #458 tagged artifact drift | #458 differential requal; identity mismatch -> new candidate |

---

## 14. Go / No-Go schema (M-06, M-08, m-02, M-18, M-19, M-20, M-21, M-22, M-24, M-25, M-26, M-27, m-06, m-07, m-08)

### 14.1 Machine rules (all bound keys + Gate outcomes)

Optional evidence missing / FAIL alone does **not** force `NO_GO`. Global integrity, authorization, secret/PII, schema, identity, and seal violations on **required or optional** evidence **do** force `NO_GO`.

| Rule | machineVerdict | Applies to |
|------|----------------|------------|
| Any Hard **required** variant FAIL | `NO_GO` | required only |
| Any Hard **required** variant missing active evidence | `NO_GO` | required only |
| Conditional incomplete exception | `NO_GO` | required Conditional |
| Optional key missing / NOT_RUN / active FAIL / NOT_CONFIRMED | may remain `GO_ELIGIBLE` | optional only (Gate) |
| candidateId / issueBodySha256 / docs digest mismatch | `NO_GO` | global |
| `prohibitedContentScan FAIL` on **any** bound evidence (required or optional) | `NO_GO` | all bound |
| Responsibility-boundary violation | `NO_GO` | global |
| G456-05 evidence with mailSendAttempted=true or synthetic sendKind | `NO_GO` | required ACS |
| G456-03 predicate mismatch (Sec. 12.2) | `NO_GO` | required ACS |
| G456-04 predicate mismatch (Sec. 12.2) | `NO_GO` | required ACS |
| G456-06 predicate mismatch **or** invalid `distinctFromSendReadyEvidenceId` (Sec. 12.2 m-06) | `NO_GO` | required ACS |
| G456-42/43/44 migration predicate mismatch **or** `migrationInventory` / `migrationDecision` / full exact history / exact schema-allowlist / PIN digest mismatch (Sec. 12.2) | `NO_GO` | required migration |
| G456-44 `piiValueCanaryResult` missing / not-run / fail **or** raw body/recipient/secret/connection-string/provider-raw-error canary values persisted into 013 table (Sec. 12.2 B-R10-01) | `NO_GO` | required migration |
| Binding missing/mismatched `migrationPinDigestSha256` / `migrationInventoryDigestSha256` / `migrationFileDigests[]` vs B-MIG-PIN (B-MIG-BIND refuse; Sec. 4 / 6.3) | `NO_GO` | required migration |
| Conditional exception created or approved for G456-42..44 (Hard; active PASS only) | `NO_GO` | required migration |
| `result` / typePayload contradiction (Sec. 12.2 M-19) on any bound evidence (incl. G456-44 canary / contractResult) | `NO_GO` | all bound |
| Disposition / exception / run-status hash-chain invalid **or** JCS canonicalization mismatch | `NO_GO` | all bound |
| Disposition / exception **invalid state transition** (Sec. 9.2 / 9.5) | `NO_GO` | all bound |
| FAIL -> PASS actor predicates violated (Sec. 9.4 M-25) | `NO_GO` | all bound |
| Authorization snapshot missing / digest mismatch / role-identity mismatch on events | `NO_GO` | all bound |
| Evidence or disposition written after sealed run-status event on this run | `NO_GO` | global |
| Phase-4 objects present but sealed predicate false (incomplete / abandoned Phase-4) | `NO_GO` for handoff; run not consumable by #458 | global |
| Sealed inventory mismatch / extra objects under run / high-water mark / rootDigestAlgorithm mismatch (Sec. 7.8) | `NO_GO`; #458 must reject | global |
| Additional run-status event after terminal `sealed` or `abandoned-*` | `NO_GO`; run invalid | global |
| Evidence `qualificationRunId` / `bindingId` mismatch vs decision run | `NO_GO` | all bound |
| Evidence for a key not in requiredVariants or optionalEvidenceKeys | `NO_GO` | global |
| Only Informational incomplete (optional keys not confirmed) | May be `GO_ELIGIBLE` if listed | optional |

### 14.2 Freshness + human decision

Before `humanDecision` may be set:

1. Re-fetch live Issue #456; require `currentIssueBodySha256 == binding.issueBodySha256`.
2. If mismatch => force `machineVerdict=NO_GO` (or block decision until rebind); do not APPROVE.
3. Run must still be **unsealed** (no sealed run-status event yet) while writing Phase-4 objects; the sealed event is written **last** (Sec. 7.8).

**Human override ban (m-02):**

```text
if machineVerdict == NO_GO:
  humanDecision MUST be REJECT or NOT_DECIDED
```

Validator rejects any `humanDecision=APPROVE` when `machineVerdict=NO_GO` (all causes, not only Hard FAIL).

Final Go for #458 requires `machineVerdict=GO_ELIGIBLE` and `humanDecision=APPROVE` on a run that satisfies the **sealed predicate including inventory re-verify** (Sec. 7.8). `go-no-go.json.runSealed` alone is insufficient.

### 14.3 decision/go-no-go.json

```json
{
  "schemaVersion": 1,
  "candidateId": "...",
  "bindingId": "...",
  "qualificationRunId": "...",
  "bindingDigestSha256": "...",
  "authorizationDigestSha256": "...",
  "planCommitSha": "...",
  "planFileSha256": "...",
  "evidenceIndexDigestSha256": "...",
  "runSealed": true,
  "issueFreshnessCheck": {
    "checkedAtUtc": "...",
    "currentIssueBodySha256": "...",
    "matchedBinding": true
  },
  "machineVerdict": "GO_ELIGIBLE|NO_GO",
  "machineReasons": ["..."],
  "scenarioIndex": [
    {
      "scenarioId": "G456-01",
      "gateClass": "Hard",
      "variants": [
        {"variantId": "win-docker", "result": "PASS", "evidenceId": "...", "required": true}
      ],
      "scenarioResult": "PASS"
    },
    {
      "scenarioId": "G456-38",
      "gateClass": "Informational",
      "variants": [
        {"variantId": "nas", "result": "NOT_CONFIRMED", "evidenceId": null, "required": false}
      ],
      "scenarioResult": "NOT_CONFIRMED"
    }
  ],
  "informationalNotConfirmed": ["G456-38"],
  "humanDecision": "APPROVE|REJECT|NOT_DECIDED",
  "humanDecisionScope": "release-qualification-v1.2.0",
  "approverRole": "...",
  "approverIdentity": "...",
  "decidedAtUtc": "..."
}
```

`gateClass` in `scenarioIndex` is copied from binding snapshot at aggregation time (not from plan Pack tables). `approverRole` / `approverIdentity` must match `authorization.json` qualification lead. `runSealed: true` is an **auxiliary declaration** copied into the decision object before the sealed run-status event is written; sole seal authority remains the sealed run-status event (Sec. 7.8).

---

## 15. Work packages (M-07 ownership)

**Fixture owner** = automated tests in repo.  
**Evidence owner** = accountable for active qualification evidence (via evidence + disposition accept), keyed by `(scenarioId, variantId)` in `authorization.json`.

| WP | Fixture owner | Evidence owner (variants) |
|----|---------------|---------------------------|
| WP-A | Binding, schemas, aggregator, durable phases, JA/EN runbook | N/A (infra) |
| WP-B | CI fixtures G456-15,16,20-28,30-32,23-26 | `ci-auto` for those IDs; **and** G456-16 `admin-integrated` |
| WP-C | Semi-auto Mailpit drivers | G456-01 `win-docker`, G456-02 `linux-docker` |
| WP-D | Admin profile / bootstrap drivers | G456-07-14,17 (`win-docker`/`linux-docker`/`admin-*`) |
| WP-E | ACS runbook adapters | G456-03 `acs-staging-nosend`; G456-04-06 ACS variants |
| WP-F | Exception + decision UX | G456-29,34,36,37 exceptions; G456-38-41 **optional** Informational keys (`nas`/`macos`/`mode5-manual`/`external-secret-manager-docs`); final go-no-go.json |
| WP-G | DB migration fresh/upgrade drivers + schema-contract fixtures | G456-42/43 (`win-docker`/`linux-docker`); G456-44 (`ci-auto`) |

| ID | Evidence owner |
|----|----------------|
| G456-15 | WP-B (`ci-auto`) |
| G456-16 | WP-B (`ci-auto` **and** `admin-integrated`) — both required |
| G456-32 | WP-B (`ci-auto`) |
| G456-03 | WP-E (`acs-staging-nosend`) |
| G456-07-14,17 | WP-D |
| G456-36 | WP-F (`vps`) |
| G456-42 | WP-G (`win-docker` and `linux-docker`) |
| G456-43 | WP-G (`win-docker` and `linux-docker`) |
| G456-44 | WP-G (`ci-auto`) |

No two WPs may append a disposition `accept`/`supersede` for the same `(scenarioId, variantId)` without a higher-`eventSequence` event authorized per Sec. 9.4.

### Issue complete when

All Hard required variants PASS (derived active for the chosen **sealed** `qualificationRunId`; for G456-42..44 this means active PASS only — Conditional exceptions forbidden); Conditional exceptions approved via exception-disposition only where `gateClass=Conditional`; Informational listed; Issue freshness gate passed; go-no-go APPROVE; Phase-1 candidate intake + Phase-2..4 run objects present for #458; JA/EN qualification runbook merged.

---

## 16. Execution order

```text
0. Fetch develop @ pin; clean worktree
1. Commit plan Rev.12; pass Agent B re-review on that exact plan-only SHA (B-MIG-SCOPE conditions 5-6)
2. Clear **B-MIG-SCOPE** (conditions 1-7 + frozen filename list + no-extra-migration policy)
   before any #458 Phase 1 version prep. Do NOT require B-MIG-PIN / B-MIG-BIND / full nine-condition clear yet.
3. Resolve and approve role identities and the intended evidence-owner assignment policy
   (qualification lead, Conditional approver, owners per required + optionalEvidenceKeys).
   Do NOT write authorization.json yet.
4. After version-prep merge: clear **B-MIG-PIN** (condition 8) on releaseCommitSha
   (migrationPinWithoutDigest + migrationPinDigestSha256 + migrationInventoryDigestSha256;
    RFC8785 JCS algorithms; FAIL on extra migrations beyond frozen 012/013 or digest mismatch;
    no evidenceDigestSha256 in PIN).
5. Dispatch #455 for pin -> download handoff package
6. Phase-1 durable intake under candidates/<candidateId>/
7. Clear **B-MIG-BIND** (condition 9): final Issue body SHA + planCommitSha/planFileSha256
   + releaseCommitSha + migrationPinDigestSha256 + migrationInventoryDigestSha256
   + migrationFileDigests[] (must match PIN); refuse bind if PIN fields missing/mismatched;
   snapshot includes G456-42..44; compute bindingId / qualificationRunId;
   materialize required + optional evidence keys
8. Write authorization.json and binding.json once as Phase-2 objects
   (plus docs extract + Phase-2 manifest) under runs/<qualificationRunId>/
9. WP-B CI fixtures (includes G456-16 both variants)
10. WP-C Mailpit variants
11. WP-D Admin variants
12. WP-E ACS / Release OV (G456-03 acs-staging-nosend; G456-04; G456-05 no-send; G456-06 send)
13. WP-G DB migrations G456-42..44 (fresh / upgrade / schema-contract; active PASS only)
14. WP-F exceptions + optional Informational evidence + recording
    (G456-42..44 must NOT use Conditional exceptions)
15. Issue freshness check; freeze Phase-3; write Phase-4 objects with sealedObjectInventory; append terminal sealed run-status event last
16. Hand off sealed run to #458 (no publish in #456); #458 re-verifies inventory
```

---

## 17. Validation commands (aligned to #455)

```powershell
dotnet restore Amane.Mailer.slnx --locked-mode
dotnet format whitespace Amane.Mailer.slnx --verify-no-changes
dotnet build Amane.Mailer.slnx -c Release --no-restore
dotnet test Amane.Mailer.slnx -c Release --no-build --verbosity minimal
```

Candidate scripts (exact interfaces; exploration base `9d6c556...`; re-confirm on planning base `3f2b640...` / pin):

```bash
bash scripts/scan-setup-release-bundle.sh <staged-root>

bash scripts/smoke-setup-release-bundle.sh \
  <archive> <archiveSha256> <rid> <release_version>

bash scripts/handoff-setup-release-candidate.sh <out-root>
```

Do **not** document `scan-setup-release-bundle.mjs` or flag-style smoke CLI.

---

## 18. Blockers / open questions

| ID | Item | Status |
|----|------|--------|
| Q1 | First RC pin SHA | Open (execution) |
| Q2 | Durable store concrete URI | Open (ops); phased contract in Sec. 7.8 |
| Q3 | Production HTTPS lab topology | Open (execution) |
| Q4 | Conditional `conditionalApproverRole` / identity | Open until execution start; **resolve before #455** (Sec. 16 Step 2); **materialize in Phase-2 `authorization.json`** after run IDs exist (M-23) |
| Q5 | G456-16 integrated variant | Closed — `ci-auto` + `admin-integrated` |
| Q6 | `qualificationLeadRole` / identity | Open until execution start; **resolve before #455**; **materialize in Phase-2 `authorization.json`** (M-22/M-23) |
| Q7 | Evidence-owner identity map per `(scenarioId, variantId)` | Open until execution start; **resolve policy before #455**; **materialize once in Phase-2 `authorization.json`**; not variantId-alone (M-22/M-23) |
| Q8 | G456-03 required variant | Closed — `acs-staging-nosend` (m-05) |
| Q9 | Phase-4 seal marker | Closed — sealed run-status event is sole authority (M-24) |
| Q10 | FAIL -> PASS dual-actor mapping | Closed — owner `executedBy*` + lead `approvedBy*` (M-25) |
| Q11 | Seal inventory / #458 re-verify | Closed — `sealedObjectInventory` + `finalRunState` in phase-4.json (M-26) |
| Q12 | Informational evidence authorization | Closed — Option A `optionalEvidenceKeys` + owners (m-07) |
| Q13 | Optional evidence aggregation | Closed — allBoundEvidenceKeys replay + integrity vs Gate split (M-27) |
| Q14 | Object-set root digest algorithm | Closed — `RFC8785-JCS-sorted-path-sha256/v1` (m-08) |
| Q15 | B-MIG INCLUDE 012/013 | Decided INCLUDE (`migrationDecision=INCLUDE`); `dSeqAck=true`; `attestMode=EXTERNAL_PROVENANCE`. Clearance still SET until **B-MIG-SCOPE** + **B-MIG-PIN** + **B-MIG-BIND** clear. Phase 1 version prep needs **B-MIG-SCOPE only** (conditions 1-7 + frozen filenames + no-extra-migration policy) — not full nine-condition clear. B-MIG-PIN = condition 8 `migrationPinWithoutDigest` + `migrationPinDigestSha256` + `migrationInventoryDigestSha256` on `releaseCommitSha` (RFC8785 JCS; no `evidenceDigestSha256` in PIN; FAIL on extras / digest mismatch). B-MIG-BIND = condition 9 new binding/run with PIN digest fields present and matching (refuse otherwise). This Rev.12 = REVISE response to Agent B Rev.11 **M-R11-01**; does not alone clear SCOPE/PIN/BIND. |

---

## 19. Completion criteria

- [ ] Provenance-based `candidateId`; separate `bindingId` / `qualificationRunId`
- [ ] Full Issue snapshot + planCommitSha/planFileSha256; all Hard variants active PASS
- [ ] Issue freshness gate before human APPROVE
- [ ] Disposition hash-chain + JCS; state-machine transitions; exception immutable lifecycle
- [ ] G456-03/04/05/06 scenario predicates + result/typePayload consistency enforced as No-Go rules
- [ ] G456-05 no Production send / no synthetic sendKind; G456-06 references active G456-05 PASS
- [ ] Durable Phases 1-4; seal = sealed run-status event + inventory re-verify; incomplete Phase-4 abandoned
- [ ] Docs digests including setup-release-bundle JA/EN
- [ ] Roles resolved before #455; authorization.json written once at Phase-2 after IDs exist
- [ ] FAIL -> PASS uses owner executedBy + lead approvedBy split predicates
- [ ] Unique evidence owners keyed by `(scenarioId, variantId)` including optional Informational keys
- [ ] Aggregator replays allBoundEvidenceKeys; optional Gate miss ≠ NO_GO; optional integrity fail = NO_GO
- [ ] Object-set roots use `RFC8785-JCS-sorted-path-sha256/v1`
- [ ] G456-03 uses `acs-staging-nosend`
- [ ] G456-42..44 present in Issue snapshot before bind; migrationInventory exact; full exact history + schema allowlist + required piiValueCanary + PIN digest equality enforced; Conditional exceptions forbidden for these rows (Sec. 6.4 / 9.5 / 14)
- [ ] B-MIG phase-aware: SCOPE before Phase 1; PIN after version-prep before #455; BIND before qualification; no Go/publish authorized by plan-only rev

---

## 20. Rollback / recovery (process)

| Event | Action |
|-------|--------|
| Hard FAIL (valid) | No-Go; product fix; new evidence + disposition under Sec. 9 **before seal** |
| False FAIL | Disposition invalidate + Qualification lead; rerun **before seal** |
| Lost Phase-1 durable bytes | New candidate; full Hard |
| Doc / Issue / plan drift | New bindingId + qualificationRunId; impact/full Hard as required |
| Bad disposition event (unsealed run) | Compensating higher-sequence event (`restore` / `supersede` / new accept); never edit history |
| Incomplete Phase-4 (objects without sealed event) | `abandoned-phase4-incomplete`; **new** `qualificationRunId`; never rewrite partial Phase-4 objects |
| Bad disposition / decision after sealed event | **New** `qualificationRunId` (Option A); never mutate sealed decision objects |
| Doc defect in setup guide | #457 -> new SHA -> may need new candidate if packaging changes |

---

## 21. Residual risk

1. Privileged host seal+secret rewrite (ADR out of scope).
2. Single maintainer Production OV environment != all customer topologies.
3. arm64 full E2E may remain excepted depending on snapshot class.
4. OCI import path differences.
5. Value-free evidence vs restricted ops logs — keep separate (`restrictedOpsRecordId` opaque).
6. Manual misclassification mitigated by typePayload enums, not eliminated.
7. Freshness check-to-APPROVE race window remains (minimize operationally).
8. Disposition/exception approver operational mistakes mitigated by hash-chain + authorization snapshot audit, not eliminated.
9. Phase-4 sealed event + sealedObjectInventory enable #458 independent re-verify; partial Phase-4 still requires abandon + new run.
10. FAIL -> PASS split-actor mapping relies on operators setting executedBy/approvedBy correctly; matrix validates but does not invent missing actors.
11. Optional Informational evidence still depends on operators actually attempting recording; absence alone does not block Go.
12. Optional evidence with secret/PII or authz failures correctly blocks Go only if scanners/aggregators actually evaluate those objects (now mandatory for all bound keys).
13. B-MIG INCLUDE expands Hard required variants (G456-42..44); Issue body amend without rebind would incorrectly reuse a stale snapshot — freshness gate + B-MIG-BIND new binding/run required.
14. Phase-aware B-MIG split prevents SCOPE/PIN circularity with #458 Phase 1; operators must still clear PIN/BIND before #455/#456 start respectively.

---

## 22. Plan self-review (Rev.12)

| # | Check | Result |
|---|-------|--------|
| 1 | Migration scope honesty (INCLUDE inventory only; no silent EXCLUDE/`none`) | Pass — frozen filenames + G456-42..44 |
| 2 | No Gate class labels in Pack H (procedure/variant/evidenceType only) | Pass — m-R10-01; active-PASS rules in Sec. 6.4 / 9.5 / 14 |
| 3 | Aliases match live Issue wording exactly (incl. G456-44 migration-owned delta) | Pass — Sec. 6.2 |
| 4 | Variants reuse rules (no `variantRulesVersion` bump) | Pass — keep 4; Windows/Linux + 自動 patterns |
| 5 | Full exact migration history equality (runner order; no ends-at/includes) | Pass — Sec. 12.2 G456-42/43 (M-R10-02) |
| 6 | Exact schema allowlists + required PII value canary (no not-run) | Pass — Sec. 12.2 G456-44 (B-R10-01) |
| 7 | B-MIG-PIN digests in binding + evidence exact equality | Pass — Sec. 4 / 6.3 / 12.2 (M-R10-01) |
| 8 | Digest canonicalization / no PIN circularity (`evidenceDigestSha256` deleted) | Pass — Sec. 4 M-R11-01 RFC8785 JCS algorithms; digested objects exclude own digests |
| 9 | Supporting unit tests != qualification envelope | Pass — Pack H note |
| 10 | 011-owned recipient_email out of 012/013 delta scope | Pass — Sec. 6.2 / 12.2 |
| 11 | Hard G456-42..44: Conditional exceptions forbidden; active PASS only | Pass — Sec. 6.4 / 9.5 / 14 (not Pack H) |
| 12 | Phase-aware B-MIG-SCOPE / PIN / BIND | Pass — Sec. 4 / 5 / Q15 / 16 |
| 13 | Phase 1 needs SCOPE only; not full nine-condition clear | Pass |
| 14 | Dual base SHA header | Pass — `9d6c556...` + `3f2b640...` |
| 15 | Sec. 2 non-goal: no schema invented inside #456; frozen 012/013 are verify inputs | Pass |
| 16 | Rev.8 sealed-store / aggregator / FAIL -> PASS / optional-key rules not weakened | Pass |
| 17 | No Go / publish / Phase 1 authorization from this doc alone; B-MIG still SET | Pass |
| 18 | Same RC commit + plan pin fields | Pass — planCommitSha/planFileSha256 |
| 19 | #455/#457/#458 boundaries; dSeqAck / EXTERNAL_PROVENANCE recorded | Pass |
| 20 | Sec. 14 NO_GO / M-19 synced for canary + PIN digest mismatch | Pass |
| 21 | Sister #458 Rev.7 digest-rule essence sync-ready | Pass — identical algorithm wording in Sec. 4 |

**Self-review verdict:** Ready for **Agent B review of Rev.12** (review-only). **Not** ready for Go execution, publish, or #458 Phase 1 authorization from this document alone. B-MIG clearance remains **SET** (SCOPE/PIN/BIND incomplete).

---

## 23. Explicitly not done in this change

- No product/test code
- No qualification execution / Go decision
- No Issue/PR/tag/deploy operations (Issue #456 body already amended on GitHub for B-02; this file aligns)
- Plan document only (`docs/agent-workflows/issue-456-release-qualification-plan.md`)
- Does **not** clear overall B-MIG (SCOPE/PIN/BIND incomplete)
- Does **not** authorize Phase 1 / publish

### Next steps (plan-only)

1. **Agent B** review Rev.12 (APPROVE / REVISE). Independent review of this plan revision feeds **B-MIG-SCOPE** conditions 5-6 / 7.
2. After APPROVE: complete remaining **B-MIG-SCOPE** items (conditions 1-7 durable); then #458 may start Phase 1 version prep — still no #456 Go / publish from this doc.
3. After version-prep merge: clear **B-MIG-PIN** (condition 8) on `releaseCommitSha` (`migrationPinWithoutDigest` + digests per Sec. 4; FAIL on extras / digest mismatch; no `evidenceDigestSha256` in PIN) before #455.
4. Before #456 qualification: clear **B-MIG-BIND** (condition 9) with new binding/run on final Issue body SHA + plan digests + `releaseCommitSha` + PIN digest fields (refuse bind if missing/mismatched).
5. Do **not** start Phase 1 / publish / qualification from this revision alone.
6. Keep digest-rule wording synchronized with sister **#458 Rev.7** (M-R11-02 / m-R11-01 owned primarily there).