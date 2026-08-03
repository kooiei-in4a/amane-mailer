# Issue #458 Release Execution Plan (v1.2.0)

> **Completion (historical plan):** v1.2.0 was published. Tag target
> `c173db1d03725e754c4432d02b7c43ceed98c3c0`; GitHub Release
> <https://github.com/kooiei-in4a/amane-mailer/releases/tag/v1.2.0>;
> release record [docs/releases/v1.2.0.md](../releases/v1.2.0.md).
> Issues #445 / #456 / #458 are **closed**. Do **not** treat the plan-only /
> PENDING / Phase-forbidden wording below as the current product status.
> Follow-ups #504–#507 are separate next work, not v1.2.0 incompleteness.

| Field | Value |
|-------|-------|
| **Status** | **plan-only** (planning round). Release ops are **not** authorized by this document alone. |
| **Issue** | [#458](https://github.com/kooiei-in4a/amane-mailer/issues/458) — `[P1] release: v1.2.0 version prep・公開・post-promote syncを完了する` |
| **Parent tracking** | [#445](https://github.com/kooiei-in4a/amane-mailer/issues/445) |
| **Design authority** | [ADR 0021](../adr/0021-easy-setup-boundaries.md) ([#446](https://github.com/kooiei-in4a/amane-mailer/issues/446), Accepted) |
| **Target release** | `v1.2.0` |
| **Base branch** | `develop` |
| **Plan revision** | **8** / **2026-08-01** |
| **Supersedes** | **Rev.7** / 2026-08-01 (and Rev.6 / Rev.5 / Rev.4 / Rev.3 / Rev.2 / Rev.1) |
| **Encoding** | UTF-8 (no BOM); prefer ASCII arrows (`->`) over rare Unicode punctuation |
| **This round** | Plan document revision only. **Does not execute** version prep, promote, tag, publish, public smoke, sync, commit, push, Issue/PR mutation. **Step 0.0 COMPLETE on develop for Rev.4 plan text** (`issue458PlanCommitSha` / `baseDevelopSha` = `3f2b640c08294502a6796c2634de5fdf03ce776f`) — still durable for that file revision. **This Rev.8 plan text is not yet durable** until Agent B APPROVE + merge (new plan-only durability). Phase 1 remains **forbidden** until **B-MIG-SCOPE cleared** + explicit maintainer authorization. |

This document is the execution plan for future release rounds of #458. It does **not** close #456 or #458, and it does **not** authorize irreversible release operations.

---

## 0. Rev.8 changelog（Agent B M-R12-01 REVISE 対応; sister #456 Rev.12 APPROVE）

| Finding | Severity | Plan change in Rev.8 |
|---------|----------|----------------------|
| **M-R12-01** | Major | Fix **CV-MIG-PIN-01** field path: replace non-existent `migrationPinWithoutDigest.inventoryDigestSha256` with normative `migrationPinWithoutDigest.inventoryDigestSha256` (changelog M-R11-02 note, Step 2.3 predicate table, procedure step 5, Appendix). Clarify Step 2.1 inputs/verification as cleared PIN outputs: `migrationPinWithoutDigest` + `migrationPinDigestSha256` + `migrationInventoryDigestSha256`. **#456 Rev.12 unchanged** (already APPROVE). |

**Historical Rev.7 (M-R11) retained as prior revision content:**

| Finding | Severity | Plan change in Rev.7 |
|---------|----------|----------------------|
| **M-R11-01** | Major | Sync **migrationPin digest canonicalization** with sister **#456 plan Rev.12**. Normative algorithms: `inventoryAlgorithm = "RFC8785-JCS-runner-order-migration-inventory-sha256/v1"`; `inventoryDocument = { schemaVersion:1, releaseCommitSha, runnerOrderPaths:[...] }` (ALL `Data/Migrations/*.sql` in SqlMigrationRunner apply order); `migrationInventoryDigestSha256 = SHA-256(UTF-8(RFC8785-JCS(inventoryDocument)))`; `migrationPinWithoutDigest = { schemaVersion:1, releaseCommitSha, inventoryAlgorithm, inventoryDigestSha256, files:[012,013] sorted Ordinal by path }`; `migrationPinDigestSha256 = SHA-256(UTF-8(RFC8785-JCS(migrationPinWithoutDigest)))`. **Remove** `evidenceDigestSha256` from PIN object. Digested objects exclude their own digest fields. Update §5, §6.1, Steps 1b.0 / 2.1 / 2.3, Appendix B. |
| **M-R11-02** | Major | Strengthen **CV-MIG-PIN-01**: recompute full inventory from `releaseCommitSha` tree; equality of recomputed value with **all** of `migrationPinWithoutDigest.inventoryDigestSha256`, `binding.migrationInventoryDigestSha256`, and G456-42/43/44 evidence inventory digests (path corrected in Rev.8). Keep **CV-MIG-PIN-02**. Sealed-package integrity predicates, **not** a second Hard product-scenario list. |
| **m-R11-01** | Minor | Scrub local absolute path; use `LOCAL_SHELL_TEMP_PATH_ACCESS_DENIED`. |
| **B-01 retained** | Blocker (closed in Rev.5 text) | Keep **phase-aware** order: **B-MIG-SCOPE** -> Phase 1 -> **B-MIG-PIN** before #455 -> **B-MIG-BIND** before #456. |

**Sister plan status:** #456 Qualification Plan **Rev.12** = Agent B **APPROVE** (content). Durability merge of PR #481 is separate and does not require Rev.12 re-review.

**Historical Rev.6 (M-R10-01) retained as prior revision content:** PIN digests -> binding + **CV-MIG-PIN-01/02** consumer equality; sister alignment previously #456 Rev.11 (now **Rev.12** for digest algorithms). Rev.6 `evidenceDigestSha256` field in PIN is **superseded / removed** by Rev.7 M-R11-01.

**Historical Rev.5 (B-01) retained as prior revision content:** phase-aware SCOPE/PIN/BIND split; Issue #458 AC migration Hard = active PASS only.

**Maintainer decisions already recorded (execution chat / Issue notes; not invented here):** `dSeqAck=true`; `attestMode=EXTERNAL_PROVENANCE` (方式2); `migrationDecision=INCLUDE` for frozen `012_provider_event_inbox_details.sql` / `013_provider_queue_dead_letters.sql`. **B-MIG clearance remains SET** until phase-aware subgates clear. Phase 1 still forbidden until **B-MIG-SCOPE cleared** + explicit maintainer authorization.

**Step 0.0 status (honest):** COMPLETE on `develop` for the **Rev.4** durable plan file (`issue458PlanCommitSha`=`baseDevelopSha`=`3f2b640c08294502a6796c2634de5fdf03ce776f`). Do **not** re-open Step 0.0 as future-only without acknowledging COMPLETE. **This Rev.8 text supersedes Rev.7 content and is not yet durable** — after Agent B APPROVE of Rev.8, a **new** plan-only durability (Step 0.0 re-run for Rev.8 / APPROVE+merge) is required before treating this revision as binding. Do **not** claim Phase 0 complete.

**Rev.6 / Rev.5 / Rev.4 strengths retained (do not regress):** M-R10-01 PIN digests into binding + CV-MIG-PIN consumer checks (algorithms now Rev.12/Rev.8); B-01 phase-aware B-MIG-SCOPE/PIN/BIND; B-DISPATCH / RC ref; Step 0.0 durability pattern; attempt unity (attempt==1 only); CV-ACTIVE/SCENARIO; merge-commit-only promotion; Option A single #455 workflow run; B-PUB/B-OCI-HANDOFF before 1b.2/#456; exact promotion head == `releaseCommitSha`; D-SEQ; D-ATTEST decided EXTERNAL_PROVENANCE; CV-* sealed integrity; B-VAL; completion PR on main after smoke; no UNDECIDED success outputs; Hard sole authority = Issue #456 table; archive manifest immutability; P-OCI-PROMOTE; B-MIG INCLUDE nine conditions (phase-aware).

**Rev.3 / Rev.2 strengths retained:** as listed in Rev.4 §0 (Option A tag target; phase-aware B-456/B-EVID/B-GO block promotion start not Phase 1; publication readiness; etc.).

---

## 1. 請求項の分離（Separation of claims）

| Claim class | Meaning | Examples in this document |
|-------------|---------|---------------------------|
| **Fact (explored)** | Confirmed via GitHub API / raw tree / local file read during planning | `origin/develop` SHA, Issue states, migrations 012/013 present, versions still `1.1.0`, #455 job graph |
| **Issue requirement** | Required by Issue #458 / ADR 0021 / #456 Gate authority | Hard sole authority = #456 required-scenario table; promote qualified bytes |
| **Future action (plan)** | To be done only in a later explicitly authorized execution round | Create version-prep PR, promote, tag, publish; Step 0.0 plan durability |
| **Unconfirmed** | Could not be verified this round (often due to broken local Shell) | Local worktree dirty/clean; whether promote tooling already exists beyond rebuild path |
| **Assumption** | Working hypothesis; must be re-checked before irreversible gates | Contracts/OpenAPI HTTP **content** unchanged; only version string sync to `1.2.0` |
| **Required decision** | Maintainer must acknowledge before irreversible ops; plan records it; this round does **not** edit Issue bodies | **D-SEQ**, **D-ATTEST**, **B-MIG INCLUDE/EXCLUDE** |
| **Blocker** | Prevents starting or continuing a named phase | B-MIG (umbrella) / **B-MIG-SCOPE** / **B-MIG-PIN** / **B-MIG-BIND**, D-SEQ, D-ATTEST, B-RC, **B-DISPATCH**, B-PUB/B-OCI-HANDOFF, B-VAL, B-456, B-EVID, B-GO |
| **Residual risk** | Accepted risk **after** Conditional exception recording; **never** waives Hard FAIL/missing | Documented Conditional rows only |

**Honesty rule:** Never describe an unexecuted future action as completed. This round status for all Phase steps is **計画のみ / 未実行**.

---

## 2. 探索事実（Exploration facts）

Recorded at plan time (2026-08-01). Re-verify before any irreversible gate. SHAs and ahead_by from earlier exploration are restated; re-fetch at execution.

| Item | Value | Claim class |
|------|-------|-------------|
| Remote | `https://github.com/kooiei-in4a/amane-mailer` | Fact |
| Local branch (at planning) | `develop` | Fact |
| Local `develop` SHA | `26976726c0571469c9b8c32f4aa0edfa3ce5ec65` (**behind** `origin/develop`) | Fact |
| `origin/develop` (Rev.4 Step 0.0 COMPLETE) | `3f2b640c08294502a6796c2634de5fdf03ce776f` (= `issue458PlanCommitSha` / `baseDevelopSha` for durable **Rev.4** plan text) | Fact |
| Prior exploration `origin/develop` | `b623bb818689f6841d1e0cc3fef14c9671333d76` (merge PR #479: #456 qualification plan Rev.8 handoff; superseded as tip by Step 0.0) | Fact (historical) |
| `origin/main` | `e61a1f26e7e57ba6217d91e0cef8bf19e2acc163` (v1.1.0 post-promote sync record) | Fact |
| Maintainer decisions (chat / Issue notes) | `dSeqAck=true`; `attestMode=EXTERNAL_PROVENANCE`; `migrationDecision=INCLUDE` (012/013 frozen inventory). **B-MIG clearance still SET** (phase-aware subgates). | Fact / Required decision recorded |
| Compare `main...develop` | `status=ahead`, **ahead_by 488**, **behind_by 0**, total_commits 488 | Fact |
| Worktree dirty/clean | **UNCONFIRMED** at earlier exploration (`LOCAL_SHELL_TEMP_PATH_ACCESS_DENIED`) | Unconfirmed |
| Latest public release | **v1.1.0** (`published_at` ~2026-07-27; GitHub Release assets list empty in API snapshot; Docker/NuGet recorded in `docs/releases/v1.1.0.md`) | Fact |
| #456 plan path | `docs/agent-workflows/issue-456-release-qualification-plan.md` (Rev.8 durable on develop; sister **Rev.12** PIN digest canonicalization + phase-aware B-MIG in progress) | Fact |
| #456 Go / No-Go | **NOT executed** | Fact |
| #456 `sourceCommitSha` / first RC pin | **undetermined** until version-prep merge freezes it | Fact / Plan |
| `easy-setup` status | `partial` in `docs/implementation-status.json` | Fact |
| Product versions observed | Contracts `<Version>` = `1.1.0`; OpenAPI `info.version` = `"1.1.0"`; CHANGELOG latest section `1.1.0` | Fact |
| Migration `012_*` | **`012_provider_event_inbox_details.sql` EXISTS on develop** | Fact |
| Migration `013_*` | **`013_provider_queue_dead_letters.sql` EXISTS on develop** | Fact |
| Migration provenance | From #460 / #461 (**closed**); positioned as **v1.1.1 candidates**, not Easy Setup children | Fact |
| `implementation-status.json` | Already cites these migration files under bounce feature evidence | Fact |
| Rev.1 claim “no 012 observed” / expected `dbMigrationStatement=none` | **Incorrect** | Fact (plan error; corrected in Rev.2; retained) |
| `workflow_dispatch` default-branch requirement | GitHub Actions `workflow_dispatch` requires the workflow file to exist on the **default branch** (`main`). Dispatch `--ref` selects the **git ref checked out for the run**, but the workflow definition itself must already be on `main`. | Fact / Issue requirement |
| `generate-setup-release-candidate.yml` on `main` | **May be absent on `origin/main` today** (workflow lives on `develop` / release path). Until a **workflow-only / release-infra bootstrap PR** lands it on `main`, `workflow_dispatch` **cannot** be used. This is Blocker **B-DISPATCH**. | Fact / Blocker |
| `workflow_dispatch @ raw SHA` | **Does not work** as a substitute for an existing branch/tag ref for this release procedure. Do **not** plan `gh workflow run ... --ref <raw-sha>` as the normative path. | Fact / Plan invariant |

### 2.1 Release mechanics (observed)

| Topic | Observation |
|-------|-------------|
| Image publish | `.github/workflows/publish-image.yml` — `workflow_dispatch` on **release tag ref**; `environment: release`; validates tag SemVer, Contracts/OpenAPI versions match package version; rejects unpublished-tag collision; multi-arch GHCR. **Rebuilds** via `docker/build-push-action`; does **not** ingest a pre-qualified OCI layout; does **not** pass `SOURCE_COMMIT` / `MAILER_VERSION` build-args. |
| Root Dockerfile (`origin/develop`) | `infra/docker/Dockerfile` defines `ARG SOURCE_COMMIT=unknown` and `ARG MAILER_VERSION=0.0.0`, and passes `/p:Version` + `/p:InformationalVersion` into Native AOT publish. Candidate path (`scripts/build-candidate-oci-image.sh`) supplies both build-args. Public rebuild without those args can embed binary `0.0.0+unknown` even when outer OCI labels look correct. |
| Contracts publish | `.github/workflows/publish-contracts.yml` — tag-ref NuGet publish for `Amane.Mailer.Contracts` |
| CI | `.github/workflows/ci.yml` — PR/push validation (restore locked-mode, build, test, OpenAPI, etc.) |
| Candidate packaging | `.github/workflows/generate-setup-release-candidate.yml` (#455) — RC only; **no** GHCR push of public version tags |
| #455 job graph (normative for Option A) | `validate-inputs` -> `build-oci` -> (`package-linux-x64` \|\| `package-linux-arm64` \|\| `package-win-x64`) -> `assemble-handoff` |
| #455 cannot ingest prebuilt OCI | Package jobs download the **same-run** `setup-release-candidate-oci` artifact (`image-identity.json` + layout from `build-oci`). There is **no** workflow input or step to supply an externally prebuilt OCI layout. **Option B (separate prebuilt OCI)** is out-of-policy unless #455 is later extended and this plan is revised. |
| Candidate OCI labels / version | `build-candidate-oci-image.sh` sets `org.opencontainers.image.version=${MAILER_VERSION}` where `MAILER_VERSION` is **major.minor.patch only** (e.g. `1.2.0`, **no `v`**). Workflow input `release_version` is validated `^[0-9]+\.[0-9]+\.[0-9]+$`. |
| Candidate attestations | Current `build-oci` / `build-candidate-oci-image.sh` produce a local OCI layout + `image-identity.json`; they do **not** attach registry attestation manifests / SBOM to the index today. |
| Release record format | `docs/releases/vX.Y.Z.md` — Source / Positioning / Included changes / Docker digests / NuGet / smoke tables / value-free evidence (see `docs/releases/v1.1.0.md`) |
| Historical OCI version label | Prior public releases often recorded `org.opencontainers.image.version=vX.Y.Z` (with `v`). **v1.2.0 candidate/promote path intentionally uses `1.2.0` without `v`** to match the candidate script; public verifier expectations must match that choice. |
| Setup bundle manifest | `release-bundle-manifest.json`, **schemaVersion 1** (additive). Required packaging fields include `packagingKind`, `artifactId`, `sourceCommitSha`, `mailerVersion`, `imageRepository`/`imageTag`/`imageDigest`, `ociIndexDigest`, compose digests, schema min/max, `mailpitImageReference`, `payloadTreeSha256`, etc. `archiveSha256` lives in outer `CANDIDATE-SHA256SUMS` / `candidate-provenance.json`, not inside host manifest |
| Promote contract (#455/#458) | **Qualified host archives are published byte-identical.** `imageDigest` / `ociIndexDigest` inside the archive must already equal the **final public** image digest at archive generation. Rebuild of host archives = new candidate. |
| `implementation-status` | Feature id `easy-setup` remains `partial` until #458 confirms **all** release gates **and** public smoke; then `implemented` **only** in the Phase 7 completion PR on `main` (ADR 0021 D-13) |

### 2.2 Historical note (v1.1.0 vs v1.2.0 tag target)

v1.1.0 used a **squash merge commit** as the annotated tag target. For v1.2.0, that practice is **intentionally not repeated**: #458 AC requires qualification evidence to bind the same commit identity used by tag / Docker / NuGet / bundles. See §7.2 Option A. Rev.3 forbade squash/rebase as the promotion merge method so the promotion head can remain exactly `releaseCommitSha`. **Rev.4 further restricts the promotion PR path to merge commit only** (fast-forward out-of-policy for v1.2.0) while the annotated tag remains on exact `releaseCommitSha`.

---

## 3. #456 Hard gate 正本（sole authority）

| Rule | Statement |
|------|-----------|
| **Sole Hard authority** | **Issue #456 required-scenario table** (Gate column). Live Issue body is classification authority; a run freezes a snapshot into qualification binding. |
| **Plan authority for execution of #456** | `docs/agent-workflows/issue-456-release-qualification-plan.md` — sister **Rev.12** (M-R11-01 PIN digest canonicalization + phase-aware B-MIG-SCOPE/PIN/BIND; supersedes Rev.11 / Rev.10 / Rev.9 draft / Rev.8 durable on develop). Digest algorithms **must match** this Rev.8 plan. Re-pin after sister APPROVE + durability. |
| **Do not** | Create an alternate / shortened Hard checklist inside #458 |
| **Do not** | Waive Hard missing/FAIL via residual-risk narrative alone |
| **Do not** | Re-implement full #456 qualification inside #458 |
| **Go status (this round)** | **Go NOT executed** |
| **`sourceCommitSha`** | Frozen as the **version-prep merge SHA** on develop, then bound by #456 on that same SHA |

#458 **consumer-validates** #456 sealed evidence for the **same** `releaseCommitSha` that becomes the tag target, using the **version-pinned `#456 consumer validator`** (predicate IDs in Step 2.3). Those `CV-*` checks are **#458 sealed-package integrity** predicates — **not** a second Hard gate for product scenarios. If the release commit or qualified bytes change after qualification, follow Phase 2.7 requalification matrix.

**#458 plan binding-equivalent tracking (not a Hard gate):** after Step 0.0, record `issue458PlanCommitSha` + `issue458PlanFileSha256` in release working evidence (mirrors #456 `planCommitSha` / `planFileSha256` idea without inventing a second Hard gate).

---

## 4. 依存関係表（Dependency table）

| Issue | Role | State (explored) | #458 start impact |
|-------|------|------------------|-------------------|
| #446 | ADR / design authority | **closed** | Ready |
| #447 | Effective inspection | **closed** | Ready |
| #448 | Setup Core / Managed bundle | **closed** | Ready |
| #449 | Host Docker adapter | **closed** | Ready |
| #450 | Apply / verify / rollback | **closed** | Ready |
| #451 | Typed ACS workflow | **closed** | Ready |
| #452 | Localhost Web Assistant | **closed** | Ready |
| #453 | Terminal / non-interactive path | **closed** | Ready |
| #454 | Admin setup-status (read-only) | **closed** | Ready |
| #455 | RC Windows/Linux bundles + OCI handoff | **closed** | Ready as tooling/runbook; **candidate for this release** generated in Phase 1b/1c via **one** workflow run after SHA pin + **B-DISPATCH** clearance |
| #456 | Qualification E2E / Go-No-Go | **open** | **BLOCKER for promotion start (Phase 3.5+)**, not for Phase 1 version prep |
| #457 | Docs single entry (setup-guide) | **closed** | Ready |
| #459 | Optional Admin bootstrap | **closed** | Ready |
| #460 / #461 | Migrations 012 / 013 (v1.1.1 candidates) | **closed** | **B-MIG**: INCLUDE decided; phase-aware **B-MIG-SCOPE** / **B-MIG-PIN** / **B-MIG-BIND** still SET |
| #445 | Parent tracking | **open** | Remains open until #456+#458 complete; owns **D-SEQ** + **B-MIG** (+ acknowledges **D-ATTEST**) |
| #458 | This issue (publish) | **open** | Plan-only this round (Rev.8 authorship; Phase 1 forbidden) |

Dependency shape (Rev.8 — **non-circular**; phase-aware B-MIG + PIN/BIND digests; single #455 run; RC ref + attempt==1):

```text
#446 -> #447/#448/#449 -> #450 -> #451 & #459 -> #452/#453/#454
                                              |
                                    #455 tooling ready; #457 closed
                                              |
Agent B APPROVE of this plan revision (Rev.8)
  -> Step 0.0 plan durability on develop for APPROVED Rev.8 text
     (Rev.4 Step 0.0 already COMPLETE @ 3f2b640...; Rev.8 needs new durability)
  -> Phase 0 (scope + B-MIG-SCOPE track + D-SEQ + D-ATTEST ack + worktree)
  -> Clear B-MIG-SCOPE (conds 1-7 + frozen filenames + no-extra policy)
  -> Phase 1 version prep PR merge to develop (explicit auth required)
  -> freeze releaseCommitSha / RC sourceCommitSha
  -> Clear B-MIG-PIN (cond 8: migrationPin output exact on releaseCommitSha)
  -> create immutable release/v1.2.0-rc @ releaseCommitSha (Step 1.6.1)
  -> Clear B-DISPATCH (workflow on main + RC ref) + B-PUB/D-ATTEST (Step 1b.0 / 1b.1)
  -> Dispatch #455 --ref release/v1.2.0-rc (attempt 1 only):
       build-oci (=1b.2) then package-* + assemble-handoff (=1c.1)
  -> Staging/dry-run digest-preserving promote proof on that OCI
  -> Clear B-MIG-BIND (cond 9: binding carries PIN digests) before #456 start
  -> Phase 2 #456 qualify that candidate; #458 consumer-validate
       (B-VAL + CV-* incl ACTIVE + CV-MIG-PIN-01/02)
  -> Phase 3 gates + publication readiness
  -> Phase 4 PR from release/v1.2.0-rc exact head; merge commit only
  -> Phase 5 tag exact releaseCommitSha; promote same OCI+archives; Release assets
  -> Phase 6 public smoke
  -> Phase 7 completion PR on main
  -> Phase 8 sync
  -> close #445 when children done
```

### 4.1 Parent #445 Gate 3C interpretation（Decision D-SEQ）

Parent Gate 3C language that “publish after #456” remains normative for **promote / tag / publish**.

**Clarification for this release (retained from Rev.2):**

- Version prep + candidate generation that **establish the release commit that #456 qualifies** are owned by #458 and **precede** #456.
- A literal reading of “all of #458 after #456” is **incorrect** for this sequencing; it would recreate the circular order Agent B flagged (B-01 / Rev.1).
- Execution must **not** start until the maintainer acknowledges this clarification.

| Decision ID | Required content | This round |
|-------------|------------------|------------|
| **D-SEQ** | Parent note / maintainer ACK: Gate 3C applies to promote/tag/publish; version prep + single #455 candidate run precede #456 for v1.2.0 | **ACK recorded** (`dSeqAck=true`) — still required evidence before irreversible ops |
| **D-ATTEST** | Maintainer ACK: 方式1 (REGISTRY_ATTEST) vs 方式2 (EXTERNAL_PROVENANCE); see §7.4. Default recommendation = 方式2 for v1.2.0 | **Decided** `attestMode=EXTERNAL_PROVENANCE` (方式2) — still required before Step 1b.2 |

---

## 5. Blockers

| ID | Blocker | Blocks from | Why | Clearance criterion |
|----|---------|-------------|-----|---------------------|
| **B-MIG** (umbrella; aka **B-SCOPE**) | INCLUDE decided; **phase-aware clearance still SET** until SCOPE/PIN/BIND clear | **Phased** — see B-MIG-SCOPE / B-MIG-PIN / B-MIG-BIND | develop has 012+013 from #460/#461. INCLUDE needs nine conditions overall, but clearing them as one pre-Phase-1 mega-gate creates circularity with conds 8–9 | Parent INCLUDE recorded (`migrationDecision=INCLUDE`); clear subgates per §6.1; success records concrete `dbMigrationStatement` (never UNDECIDED / false `none`) |
| **B-MIG-SCOPE** | INCLUDE conditions 1–7 + frozen filename list + no-extra-migration policy incomplete | **Phase 1 version prep start** | Scope/authority must be honest before version strings land; does **not** require `releaseCommitSha` | Conds 1–7 satisfied; frozen filenames = `012_provider_event_inbox_details.sql`, `013_provider_queue_dead_letters.sql`; any migration outside inventory needs parent re-decision |
| **B-MIG-PIN** | Condition 8 incomplete: normative `migrationPin` output not proven exact on `releaseCommitSha` | **After version-prep merge; before #455 / Step 1b.2** | Cond 8 needs the pin that Phase 1 creates — cannot clear before Phase 1 | Produce normative **B-MIG-PIN output** (§6.1); inventory + per-file digests match `releaseCommitSha` tree; **FAIL** if tree has migrations beyond frozen 012/013, or digests mismatch tree |
| **B-MIG-BIND** | Condition 9 incomplete: new binding/run not created with PIN digests | **Before #456 qualification start** | Cond 9 needs final Issue/plan/`releaseCommitSha` identities after pin; digests must flow into #456 binding | New binding/run on final Issue body SHA + planCommitSha/planFileSha256 + `releaseCommitSha` **and** required PIN handoff fields (`migrationPinDigestSha256`, `migrationInventoryDigestSha256`, `migrationFileDigests[]`); **refuse BIND** without PIN outputs |
| **D-SEQ** | Gate 3C sequencing clarification unacknowledged | **Any irreversible / execution start** (after Step 0.0 durability) | Circular-order fix must be explicitly accepted | Maintainer ACK recorded (`dSeqAck=true`) |
| **D-ATTEST** | Attestation / provenance mode undecided | **Step 1b.2 / single #455 run** | Index digest identity depends on whether attestations are in the final OCI graph | Maintainer ACK; decided `EXTERNAL_PROVENANCE` (方式2) for v1.2.0 |
| **B-RC** | `releaseCommitSha` / RC `sourceCommitSha` not yet pinned | **Phase 1b/1c (#455) and Phase 2 (#456)** | After version-prep merge, that merge SHA is frozen; required before candidate generate / qualify | Version-prep PR merged; SHA recorded as `releaseCommitSha` == future #456 `sourceCommitSha` |
| **B-DISPATCH** (aka **B-RC-REF**) | #455 cannot be dispatched at exact `releaseCommitSha` with verifiable ref/SHA identity | **Before Step 1b.2** | GitHub requires workflow file on default branch `main` for `workflow_dispatch`; dispatch must use immutable RC branch ref (not raw SHA); run must verify `GITHUB_SHA` / job HEAD == `releaseCommitSha` | (1) `generate-setup-release-candidate.yml` (+ promote tooling required by B-PUB) on `main`; `infraBootstrapMainSha` recorded; (2) `release/v1.2.0-rc` exists and tip == `releaseCommitSha`; (3) dispatch `--ref release/v1.2.0-rc`; (4) machine-verify ref/SHA/job HEAD equality (Step 1b.0 / §7.6) |
| **B-PUB** / **B-OCI-HANDOFF** | No promote-capable path **and/or** no digest-preserving promote proof tooling readiness | **Before Step 1b.2 / 1c.1 / #456** (not “by Phase 5”) | Current `publish-image.yml` rebuilds and cannot ingest candidate OCI. Under Option A, the OCI from `build-oci` **is** the final public OCI to promote. Starting #456 on un-promotable bytes is forbidden | (1) Promote-capable tooling exists for chosen method; (2) digest-preserving dry-run or staging-namespace push proof recorded for that method on the chosen path; failure => stop |
| **B-VAL** | Version-pinned `#456 consumer validator` (implementing `CV-*` incl ACTIVE/SCENARIO + **CV-MIG-PIN-01/02** on INCLUDE path) missing | **Phase 2.3+** (consumer validation / promotion that depends on it) | Sealed-package integrity must be reported at predicate level | Validator binary/script exists at pinned version implementing listed `CV-*` IDs (incl migration PIN equality on INCLUDE), **or** maintainers accept an explicitly enumerated checklist that is still sealed-package integrity (not a second Hard gate for product scenarios) |
| **B-456** | Issue #456 still open / qualification incomplete | **Promotion start (Phase 3.5+)** — **not** Phase 1 | Hard authority and sealed evidence do not yet exist for the pinned RC | #456 produces durable sealed Go package for pinned `sourceCommitSha` |
| **B-EVID** | Qualification evidence / Go package not yet available to #458 | **Phase 2 consumer validation / Phase 3.5+** | Cannot map Hard PASS / Conditional / Informational / digests | Durable store per #456 Rev.8+ available; value-free; inventory complete |
| **B-GO** | Human+machine Go not executed | **Phase 3.5+** | Publish must not proceed without Go | `machineVerdict` GO_ELIGIBLE + human APPROVE per #456 rules; Hard NO_GO cannot be overridden |

Additional pre-flight (not separate Hard authority): local worktree cleanliness **UNCONFIRMED** at early exploration — re-verify before version-prep PR / promote. Rev.4 Step 0.0 durability is COMPLETE; **Rev.8 plan text** still needs Agent B APPROVE + new Step 0.0 / merge before this revision is binding. Phase 0 is **not** complete while **B-MIG-SCOPE** remains SET.

**Rev.8 order reminder (B-01 retained):** Phase 1 start requires **B-MIG-SCOPE cleared** (not full B-MIG). After Step 1.6 pin, require **B-MIG-PIN** (normative `migrationPin` output) before 1b.2/#455. Before Phase 2/#456 start require **B-MIG-BIND** (binding carries PIN digests; refuse without PIN). B-456, B-EVID, and B-GO are **not** blockers for starting Phase 1 version prep. B-RC becomes active after Phase 1 merge (pin). **B-DISPATCH**, **B-PUB/B-OCI-HANDOFF**, and **D-ATTEST** must clear **before** the single #455 run (Step 1b.2). **B-VAL** (incl **CV-MIG-PIN-01/02** on INCLUDE) must clear before Phase 2.3+.

Planning-time undecided / SET states (allowed **only** in this Blockers sense until cleared): B-MIG-SCOPE, B-MIG-PIN, B-MIG-BIND (umbrella B-MIG SET), B-DISPATCH, B-PUB/B-OCI-HANDOFF, B-VAL, B-456, B-EVID, B-GO. D-SEQ / D-ATTEST / migrationDecision already decided but must remain recorded. Successful Step outputs must use concrete enums (see §7.5) — never `UNDECIDED`.

---

## 6. Minor release gate（v1.2.0）

v1.2.0 is a **backward-compatible operator-facing** minor release. If any of the following becomes necessary **without** parent amendment, **stop**, report to parent #445, and do **not** silently fold into #458:

| Gate item | Action if needed |
|-----------|------------------|
| Breaking public HTTP request/response change | Report; separate release / scope change |
| Breaking Consumer-facing Contracts change | Report |
| DB migration (012/013 or any new since v1.1.0) | **B-MIG**: parent INCLUDE vs EXCLUDE; do not silently ship; do not claim “none” while undecided |
| Mail request state-machine change | Report |
| Runtime-wide config precedence change | Report |
| Mode 5 Azure Queue auto-configuration | Out of scope (manual only) |
| New externally reachable HTTP endpoint | Report |
| Settings change that breaks existing 1.1.x operators | Report |
| Consumer bounced Webhook **#307** | Explicitly **not** in v1.2.0 (v1.5.0+ candidate) |
| Reverse proxy auto-build | Forbidden |
| Non-interactive Admin bootstrap | Forbidden |

### 6.1 B-MIG INCLUDE / EXCLUDE authority（M-R2-03 retained; Rev.5 phase-aware; Rev.6/Rev.7 PIN/BIND digests）

#### INCLUDE path — ALL of the following are mandatory (overall)

1. Parent #445 **scope decision** to INCLUDE migrations for v1.2.0. (**Done:** INCLUDE.)
2. Formal change to Issue #458 **minor gate** + migration AC (truthful INCLUDE inventory; no `none` / UNDECIDED).
3. Issue #456 body **required-scenario table** add/include migration rows **OR** explicit containment decision mapping migrations to existing rows.
4. Explicit **Gate class** per migration row (Hard / Conditional / Informational as appropriate). Migration Hard rows G456-42..44 require **active PASS only** (Conditional-exception language removed from Issue AC).
5. #456 qualification **plan revision** update reflecting migration scope (sister **Rev.12** PIN digest canonicalization + phase-aware split).
6. **Independent review** of that plan revision.
7. Migration qualification **evidence schema** defined and usable.
8. Migration inventory **frozen** to `releaseCommitSha` via normative **B-MIG-PIN output** (exact filenames + digests on that tree).
9. **New binding/run** that carries PIN digests into #456 binding (Issue body / plan / pin identities change when bodies are amended).

#### Phase-aware clearance (normative; breaks Rev.4 circularity — Agent B B-01; order unchanged in Rev.8)

```text
B-MIG-SCOPE (conds 1-7 + frozen filename list + no-extra-migration policy)
  -> required before Phase 1 version prep
B-MIG-PIN (cond 8: migrationPin output exact on releaseCommitSha)
  -> required after version-prep merge, before #455 / Step 1b.2
B-MIG-BIND (cond 9: new binding/run carrying PIN digests)
  -> required before #456 qualification start
```

INCLUDE still needs the nine conditions **overall**, but they clear **in phases** — **not** as one pre-Phase-1 mega-gate. Until **B-MIG-SCOPE** clears, Phase 1 is forbidden. Until **B-MIG-PIN** clears, #455 / 1b.2 is forbidden. Until **B-MIG-BIND** clears, #456 qualification start is forbidden. Umbrella **B-MIG** remains SET until all three subgates clear.

Frozen INCLUDE inventory (no-extra-migration policy): `012_provider_event_inbox_details.sql`, `013_provider_queue_dead_letters.sql`. Any migration outside this list requires parent re-decision.

#### Normative B-MIG-PIN output（same as #456 Rev.12 / M-R11-01 — must match）

On PIN clearance, produce digests with these **identical** algorithms (sister #456 Rev.12):

```text
inventoryAlgorithm = "RFC8785-JCS-runner-order-migration-inventory-sha256/v1"

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

**Rules:** Digested objects **never** include their own digest fields. **Delete** `evidenceDigestSha256` from the PIN object (do not feed any post-write envelope digest into `migrationPinDigestSha256`). B-MIG-PIN produces `migrationPinWithoutDigest` + `migrationPinDigestSha256` + `migrationInventoryDigestSha256`.

**PIN FAIL** if:

- `releaseCommitSha` tree contains migrations beyond the frozen 012/013 list (since v1.1.0 tip / no-extra policy), **or**
- any inventory / per-file SHA-256 / `gitBlobSha` mismatches the `releaseCommitSha` tree, **or**
- digests were computed with a different algorithm / including self-digest fields / including `evidenceDigestSha256`.

#### Normative B-MIG-BIND handoff（same as #456 Rev.12 / M-R11-01）

BIND must carry PIN digests into the #456 `binding.json` required fields:

```text
migrationPinDigestSha256          # == SHA-256(UTF-8(RFC8785 JCS(migrationPinWithoutDigest)))
migrationInventoryDigestSha256    # == migrationPinWithoutDigest.inventoryDigestSha256
migrationFileDigests[]            # exact equality with migrationPinWithoutDigest.files[]
```

**Refuse BIND** if PIN outputs are missing, incomplete, or digests do not match the cleared PIN outputs. Sister #456 Hard rows G456-42..44 PASS only when evidence migration digests exactly equal these binding PIN values (and thus the PIN'd `releaseCommitSha` tree). #458 consumer-validates that equality via **CV-MIG-PIN-01** / **CV-MIG-PIN-02** (sealed-package integrity — **not** a second Hard product-scenario list).

#### EXCLUDE path

- Parent chooses EXCLUDE and a **constructible** release commit / method without shipping 012/013 (honest assessment: may be impossible without history rewrite or a separate release branch). **Not selected for v1.2.0** (`migrationDecision=INCLUDE`).
- When a constructible branch/method is chosen, **re-review Phase 1.6+ branch and promotion steps** before execution (exact-head / merge-method invariants may need a plan revision).

#### Success outputs (after Steps 0.2 / 1.4 / PIN / BIND as applicable)

On success (never `UNDECIDED`):

- `migrationDecision=INCLUDE|EXCLUDE` (v1.2.0: `INCLUDE`)
- `dbMigrationStatement=<concrete decided text>` (INCLUDE inventory + operator impact; never `none`)
- Subgate flags: `bMigScopeCleared`, `bMigPinCleared`, `bMigBindCleared` when each phase clears
- On PIN clear: `migrationPinWithoutDigest` + `migrationPinDigestSha256` + `migrationInventoryDigestSha256` (algorithms above; no `evidenceDigestSha256`)
- On BIND clear: binding carries `migrationPinDigestSha256`, `migrationInventoryDigestSha256`, `migrationFileDigests[]`

---

## 7. 設計不変条件（must appear in every execution round）

1. **Do not create an alternate Hard list.** Sole Hard authority = Issue #456 required-scenario table.
2. **Do not waive Hard** missing/FAIL via residual risk alone.
3. **Prefer promote qualified archive bytes**; **rebuild of host archives = new candidate** (new `archiveSha256` / provenance).
4. **Never mutate archive-internal manifests at publish.** `imageDigest` / `ociIndexDigest` must already equal final public digests at #455 generation time. **Forbid** “documented promote map” as acceptance of manifest≠published digest.
5. **OCI handoff = Option A single #455 workflow run.** Step 1b.2 and 1c.1 are phases of **one** run — not two independent OCI builds. Option B (external prebuilt OCI) is out-of-policy for current #455.
6. **Tag / Docker / NuGet / bundle `sourceCommitSha` use exact `releaseCommitSha` equality only** (Option A). **Forbid** “equivalent tree” as tag-target policy.
7. **Promotion PR head SHA == `releaseCommitSha`** (complete equality). Merge method = **merge commit only** for v1.2.0; **forbid squash merge, rebase merge, and fast-forward** as the promotion PR path. Prefer reusing `release/v1.2.0-rc` as the promotion head so equality is natural.
8. **Publish method decided and promote-path proof recorded before the #455 run / #456.** Primary: **P-OCI-PROMOTE**. Current rebuild-as-publish path is **B-PUB** until replaced/extended or P-REBUILD meets discouraged parity rules.
9. **D-ATTEST decided before Step 1b.2.** Default recommendation: 方式2 (`EXTERNAL_PROVENANCE`) for v1.2.0.
10. **Canonical version strings:** git tag `v1.2.0`; OCI `org.opencontainers.image.version` = `1.2.0` (no `v`); Contracts/NuGet/OpenAPI/mailerVersion = `1.2.0`.
11. **`implementation-status` -> `implemented` ONLY** in Phase 7 completion PR on `main` **after** public smoke. Never in version-prep PR.
12. **Contracts/OpenAPI:** expect HTTP **content** unchanged for Easy Setup release; **version string sync to `1.2.0`** is still required for tag validation. Record the change-or-no-content-change **decision** in the release record.
13. When **candidate == published** (byte-identical archives + same OCI graph that was qualified), **skip** re-running: real ACS Staging verification; Release Production operational verification; Local Development / Production HTTPS Admin access E2E; rollback / fault injection; full Windows/Linux fresh install; Admin DB partial-failure; non-interactive Admin rejection scenarios.
14. **Irreversible gates** requiring explicit stop/go: **Step 0.0 plan durability** (Rev.8 re-run after APPROVE), **D-SEQ ACK**, **D-ATTEST ACK**, **B-MIG-SCOPE** (before Phase 1), **B-MIG-PIN** (before 1b.2/#455), **B-MIG-BIND** (before #456), **B-DISPATCH clearance**, **3.5**, **4.2 merge-method check**, **4.4**, **5.0**, **5.1–5.5**, **7.3 merge**, **8.2**.
15. **#455 attempt unity (v1.2.0):** accept only `workflowRunAttempt == 1` with all required jobs `runAttempt == 1`; forbid partial job re-runs / attempt mix (§7.7).
16. **B-DISPATCH / RC ref:** dispatch #455 only via immutable `release/v1.2.0-rc` at exact `releaseCommitSha` after workflow exists on `main`; never imply raw-SHA dispatch works (§7.6).
17. **This round does not execute release ops** — only revises this plan document.

### 7.1 Canonical order (Rev.8)

```text
Agent B APPROVE of Rev.8
  -> Step 0.0 plan durability on develop for Rev.8 text
     (Rev.4 Step 0.0 COMPLETE @ 3f2b640...; do not pretend Rev.8 is already durable)
  -> Phase 0 (B-MIG-SCOPE track, D-SEQ, D-ATTEST ack, worktree, ...)
  -> Clear B-MIG-SCOPE (conds 1-7 + frozen filenames + no-extra policy)
  -> Phase 1 version prep (explicit auth) -> freeze releaseCommitSha
  -> Clear B-MIG-PIN (cond 8: migrationPin output on releaseCommitSha)
  -> create immutable release/v1.2.0-rc @ that SHA
  -> Clear B-DISPATCH (workflow on main + RC ref) + B-PUB/D-ATTEST
  -> Dispatch #455 --ref release/v1.2.0-rc (attempt 1 only)
  -> promote-path proof
  -> Clear B-MIG-BIND (cond 9: binding carries PIN digests)
       -> #456 -> CV-* incl ACTIVE + CV-MIG-PIN-01/02
  -> Phase 4 PR from release/v1.2.0-rc exact head; merge commit only
  -> tag releaseCommitSha -> publish same bytes -> smoke
  -> completion PR -> sync
```

### 7.2 Commit identity policy — Option A (preferred; retained + Rev.4 merge-method tighten)

| Rule | Statement |
|------|-----------|
| **Chosen policy** | **Option A (preferred)** |
| After promotion | Verify `main` **contains** `releaseCommitSha` as an **ancestor** (`git merge-base --is-ancestor releaseCommitSha origin/main`) |
| Promotion head | Prefer **reuse** of immutable `release/v1.2.0-rc` (exact `releaseCommitSha`); PR **head SHA == `releaseCommitSha`** (complete equality; ancestor-only insufficient) |
| Do not include | develop commits after qualification / after the frozen release pin |
| Merge method | **merge commit only** for v1.2.0 (`mergeMethodAllowed=merge`, `mergeMethodUsed=merge`); **forbid squash merge, rebase merge, and fast-forward** as the promotion PR path (FF marked out-of-policy to avoid direct-push ambiguity) |
| Tag target | Create annotated tag `v1.2.0` on **`releaseCommitSha` itself** (the merge commit on `main` is **not** the tag target) |
| Artifact labels | Docker/NuGet/bundle `sourceCommitSha` / revision labels all use that **same** SHA |
| Forbidden | “Equivalent tree”, tree-OID equality, tagging a different merge/squash commit, discovering squash/rebase/FF-only incompatibility first at Step 4.4 |
| v1.1.0 note | Prior release tagged a squash merge commit; v1.2.0 intentionally differs |

### 7.3 Publish method + OCI handoff policy（B-R2-01 / B-PUB retained）

#### OCI handoff — Option A: single #455 workflow run (normative)

| Plan step | Workflow job (same `workflowRunId`, **attempt == 1**) |
|-----------|-----------------------------------------------|
| Step 1b.2 | `build-oci` |
| Step 1c.1 | `package-linux-x64` / `package-linux-arm64` / `package-win-x64` + `assemble-handoff` |

Rules:

- One `workflowRunId` with **`workflowRunAttempt == 1`** freezes OCI layout + `image-identity.json` + host archives + provenance together.
- The OCI layout produced by that run’s `build-oci` job **is** the final public OCI to promote under **P-OCI-PROMOTE**.
- Do **not** describe a separate prebuilt OCI that current #455 cannot ingest.
- **Option B** remains out-of-policy unless #455 is later extended and this plan is revised.
- Before starting #456 (and before trusting 1c outputs for qualification): require **promote-path proof** — dry-run or staging-namespace push of that same OCI layout proving **index digest is preserved** end-to-end. Failure => stop; do not start #456 on un-promotable bytes.

#### Primary: P-OCI-PROMOTE (recommended)

- Push the **qualified** OCI layout (runtime manifests / configs / layers — and attestation graph if 方式1) to GHCR.
- Attach tags `v1.2.0` and `sha-<releaseCommitSha>` to the **same** index that was qualified.
- Do **not** use current `publish-image.yml` rebuild-as-publish path for v1.2.0 unless replaced/extended first (**B-PUB**).

#### Discouraged alternative: P-REBUILD

Only if maintainer explicitly retains rebuild **and** clears B-PUB via parity proof:

- Exact parity of build args / labels / BuildKit with the candidate path (`SOURCE_COMMIT`, `MAILER_VERSION`, OCI version label `1.2.0`).
- Build final OCI in **non-public staging** first.
- That final OCI **MUST** be what #456 qualifies (not a different post-tag rebuild).
- Compare runtime platform manifests, configs, **all layers**, and **embedded binary version** before creating public version tags.
- Class C discovery **after** public version tags is **forbidden**.
- Note: P-REBUILD does not restore Option B prebuilt-OCI handoff into current #455; archives still come from the same run that built the OCI under Option A unless #455 is extended.

#### Archive manifest immutability

- Qualified host archives are published **byte-identical**.
- Forbid updating `release-bundle-manifest.json` inside archives at publish time.
- Forbid accepting manifest≠published digest via “documented promote map”.

### 7.4 Attestation / SBOM / provenance（D-ATTEST / M-R2-04 retained）

Required decision **before Step 1b.2**:

| Mode | `attestMode` value | Meaning |
|------|--------------------|---------|
| **方式1** | `REGISTRY_ATTEST` | Attestation-inclusive final OCI: `build-oci` must produce attestation-inclusive graph **before** packaging; that index digest is what manifests embed; #456 qualifies that graph; promote identical graph |
| **方式2** | `EXTERNAL_PROVENANCE` | Runtime index fixed **without** registry attestation; publish `candidate-provenance.json`, SBOM (if produced), checksums on GitHub Release; record SHA-256 in release record; state in release notes that registry attestation is **not** attached for v1.2.0 |

**Default recommendation for v1.2.0** under Option A single-run + current candidate script (no attestations today): **方式2**. Maintainer ACK still required (**D-ATTEST**).

### 7.5 Success output enums（m-R2-01 retained）

Successful Step outputs use concrete values only:

| Key | Allowed success values |
|-----|------------------------|
| `migrationDecision` | `INCLUDE` \| `EXCLUDE` |
| `dbMigrationStatement` | `<concrete decided text>` |
| `publishMethod` | `P-OCI-PROMOTE` \| `P-REBUILD` |
| `identityClass` | `A` \| `C` |
| `ociHandoffMode` | `SINGLE_WF_RUN_OPTION_A` |
| `attestMode` | `REGISTRY_ATTEST` \| `EXTERNAL_PROVENANCE` |
| `mergeMethodAllowed` | `merge` |
| `mergeMethodUsed` | `merge` |
| `workflowRunAttempt` | `1` (v1.2.0 candidate acceptance) |
| `dispatchRef` | `release/v1.2.0-rc` |

`UNDECIDED` is allowed **only** in planning-time Blocker / Required-decision tables, never as a successful Step output.

### 7.6 B-DISPATCH / B-RC-REF — dispatch at exact releaseCommitSha（B-R3-01）

Normative procedure that **blocks before Step 1b.2**:

1. **Release-infrastructure bootstrap (separate from product promotion):** After Agent B APPROVE of this plan revision **and** plan durability (Step 0.0), ensure `generate-setup-release-candidate.yml` (and any promote tooling required by B-PUB) exists on **default branch `main`** via a **workflow-only / release-infra bootstrap PR** to `main` (not the v1.2.0 product promotion). Record `infraBootstrapMainSha`. Until the workflow is on the default branch, `workflow_dispatch` cannot be used as GitHub requires.

2. After version-prep merge freezes `releaseCommitSha`, create **immutable RC branch**:
   ```text
   release/v1.2.0-rc  -> exact releaseCommitSha
   ```
   Do **not** move this branch until #458 completes (or abandon + restart).

3. Dispatch #455 with:
   ```text
   gh workflow run generate-setup-release-candidate.yml --ref release/v1.2.0-rc
   ```
   (or equivalent) with inputs `release_version=1.2.0` and mailpit pin.

4. Machine-verify before accepting the run:
   ```text
   refs/heads/release/v1.2.0-rc == releaseCommitSha
   GITHUB_REF == refs/heads/release/v1.2.0-rc
   GITHUB_SHA == releaseCommitSha
   each job git rev-parse HEAD == releaseCommitSha
   ```

5. Evidence: all jobs same HEAD SHA; RC branch tip unchanged across the run.

6. If RC branch moves after dispatch start: abandon run + qualification; new full #455 from current policy.

7. **Reuse** `release/v1.2.0-rc` as Phase 4 promotion PR head so head SHA equality is natural.

**Forbidden implication:** `workflow_dispatch @ raw SHA` is **not** a supported / planned path for v1.2.0.

### 7.7 Attempt unity — no partial workflow re-run（M-R3-02）

Normative for v1.2.0 candidates:

```text
Any required job FAIL/CANCEL
  -> abandon that workflow run
  -> forbid GitHub "Re-run failed jobs" / "Re-run jobs" / "Re-run specific job"
  -> new workflow_dispatch
  -> new workflowRunId; all jobs from scratch
```

Success gates for candidate acceptance:

```text
workflowRunAttempt == 1
all required jobs runAttempt == 1
all jobs headSha == releaseCommitSha
all artifacts from same workflowRunId
```

Record attempt fields in provenance verification (Steps 1c.1 / 2.1). If future policy allows attempt>1, require schema extension — **out of scope for v1.2.0**; v1.2.0 = attempt==1 only.

---

## 8. Phases 0–8（実行計画）

Common column legend for every Step table:

| Column | Meaning |
|--------|---------|
| Phase/Step | Order |
| Purpose | What the step guarantees |
| Preconditions | Must be true before starting |
| Inputs | Issues, SHAs, artifacts, evidence |
| Actions | Future-round operations only |
| Verification | Success checks |
| Evidence | Value-free artifacts to retain |
| Stop | Must not continue |
| Re-run | When requalification / redo is required |
| Outputs | Hand-off to next step |
| Owner | Owning Issue |
| This-round status | Always **計画のみ / 未実行** in Rev.8 for Phase 1+; Step 0.0 Rev.4 COMPLETE acknowledged separately |

---

### Step 0.0 — Reviewed plan durability（M-R3-01; before Phase 0）

| Item | Content |
|------|---------|
| Phase/Step | **0.0** (before Phase 0) |
| Purpose | Persist the Agent B–APPROVED plan text on `develop` before any Phase 0+ execution; fix binding-equivalent plan identity |
| Preconditions | Agent B **APPROVE** of **this plan revision (Rev.8)**; maintainer authorizes the plan-only durability PR/commit |
| Inputs | Reviewed plan text (this file, Rev.8); `origin/develop` tip |
| Actions | (1) Agent B APPROVE of this plan revision. (2) Open a **plan-only** PR / commit containing **only** the APPROVED plan text (no unrelated product edits). (3) Merge to `develop`. (4) Fix `issue458PlanCommitSha` = that merge/commit SHA. (5) Record plan file blob SHA and `issue458PlanFileSha256` (SHA-256 of the committed plan file bytes). (6) `git fetch origin/develop`; refresh `baseDevelopSha`. (7) **Only then** begin Phase 0 under this revision. |
| Verification | Committed plan text == reviewed APPROVED text (byte/content equality for the plan file); SHAs recorded; `baseDevelopSha` refreshed |
| Evidence | PR URL; `issue458PlanCommitSha`; plan blob SHA; `issue458PlanFileSha256`; `baseDevelopSha` |
| Stop | Committed text ≠ reviewed text; substantive plan change without re-review; attempting Phase 0 under a non-durable Rev.8 text |
| Re-run | Any substantive plan change => new revision + Agent B re-review + new Step 0.0 |
| Outputs | `issue458PlanCommitSha`; `issue458PlanFileSha256`; `baseDevelopSha` (refreshed) |
| Owner | #458 |
| This-round status | **Rev.4 Step 0.0 COMPLETE** on develop: `issue458PlanCommitSha`=`baseDevelopSha`=`3f2b640c08294502a6796c2634de5fdf03ce776f` (durable **Rev.4** plan file). **This Rev.8 plan text is not yet durable** — after Agent B APPROVE of Rev.8, re-run Step 0.0 for Rev.8 (APPROVE+merge). Do not describe Rev.4 COMPLETE as absent/future-only. **This authorship round does not commit.** |

---

### Phase 0：開始条件と Release freeze

#### Step 0.1 — Dependency and ADR freeze

| Item | Content |
|------|---------|
| Phase/Step | 0.1 |
| Purpose | Confirm all implementation deps closed; ADR 0021 Accepted; no unfinished product work smuggled into release |
| Preconditions | Step 0.0 complete; network access to GitHub; execution round authorization |
| Inputs | Issues #445–#461; ADR 0021; `implementation-status.json` |
| Actions | Re-fetch Issue states; confirm #446–#455/#457/#459 closed; #456/#458/#445 open expected until done; confirm ADR Accepted; note #460/#461 closed but migrations present |
| Verification | Dependency table matches live GitHub; no open implementation child that owns unfinished Easy Setup product AC |
| Evidence | Dated dependency snapshot (Issue number, state, closed_at) — no secrets |
| Stop | Any required implementation Issue reopened or unfinished product AC found |
| Re-run | If deps change after freeze, restart Phase 0 |
| Outputs | `depsFrozenAt`, dependency snapshot |
| Owner | #458 (reads #445 children) |
| This-round status | 計画のみ / 未実行（探索時点の表は §4 に記録済み） |

#### Step 0.2 — Scope and minor-release gate freeze (+ B-MIG-SCOPE)

| Item | Content |
|------|---------|
| Phase/Step | 0.2 |
| Purpose | Freeze v1.2.0 scope; clear **B-MIG-SCOPE** (not full B-MIG); detect minor-gate violations early |
| Preconditions | Durable plan for the active revision (Rev.8 Step 0.0 after APPROVE); 0.1 pass |
| Inputs | Issue #458 minor gate list; migration inventory since v1.1.0; CHANGELOG draft intent; diff `main...develop` summary; §6.1 authority requirements |
| Actions | Inventory migrations since v1.1.0 (at least `012_*`, `013_*`). Confirm parent **INCLUDE** (`migrationDecision=INCLUDE`). Track INCLUDE conditions **1–7**, freeze filename list (`012_provider_event_inbox_details.sql`, `013_provider_queue_dead_letters.sql`), and no-extra-migration policy. Do **not** require conds 8–9 here (those are B-MIG-PIN / B-MIG-BIND). Review develop tip vs last release for Contracts schema diffs, new public endpoints. |
| Verification | **B-MIG-SCOPE** cleared with written decision meeting §6.1 SCOPE criteria; no silent breaking HTTP/Contracts/mode-5 automation/#307; migration statement concrete; umbrella B-MIG still SET until PIN+BIND later |
| Evidence | Scope checklist (pass/fail per minor-gate row); B-MIG-SCOPE clearance record; `migrationDecision=INCLUDE` |
| Stop | INCLUDE undecided; **B-MIG-SCOPE** incomplete; any minor-gate trip without parent amendment; attempting to demand full nine-condition clear before Phase 1 (circular) |
| Re-run | After scope change decision |
| Outputs | `scopeFrozen=true`; `migrationDecision=INCLUDE`; `dbMigrationStatement=<concrete decided text>`; `bMigScopeCleared=true` when SCOPE done |
| Owner | #458 / parent #445 |
| This-round status | 計画のみ / 未実行 — **migrationDecision=INCLUDE recorded; B-MIG-SCOPE currently SET; umbrella B-MIG SET; Phase 1 forbidden** |

#### Step 0.3 — D-SEQ + D-ATTEST + release-commit policy

| Item | Content |
|------|---------|
| Phase/Step | 0.3 |
| Purpose | Lock Gate 3C interpretation, attestation mode, and commit-identity policy before execution |
| Preconditions | 0.0–0.2 progressing; maintainer available |
| Inputs | §4.1 D-SEQ; §7.2 Option A; §7.4 D-ATTEST; §7.6 B-DISPATCH; this plan Rev.8 |
| Actions | Confirm maintainer ACK for D-SEQ (`dSeqAck=true`) and D-ATTEST (`attestMode=EXTERNAL_PROVENANCE`). Record policy: after Phase 1 merge, that SHA **is** `releaseCommitSha` / RC `sourceCommitSha`; create immutable `release/v1.2.0-rc`; later #456 binds the same SHA; tag targets that exact SHA (Option A); promotion head equality + **merge commit only**. |
| Verification | Written ACKs; `attestMode` recorded; no floating “latest develop tip” publish policy |
| Evidence | D-SEQ ACK reference; D-ATTEST ACK; policy text |
| Stop | Attempt to execute without D-SEQ ACK; attempt 1b.2 without D-ATTEST; attempt to publish from floating branch tip |
| Re-run | If policy changes, revise plan before ops |
| Outputs | `dSeqAck=true`; `attestMode=EXTERNAL_PROVENANCE`; `releaseCommitPolicy=OptionA`; `mergeMethodAllowed=merge` |
| Owner | #445 / #458 |
| This-round status | 計画のみ / 未実行 — **D-SEQ ACK and D-ATTEST=EXTERNAL_PROVENANCE already decided; still record before irreversible ops / 1b.2** |

#### Step 0.4 — Blocker evaluation (phase-aware)

| Item | Content |
|------|---------|
| Phase/Step | 0.4 |
| Purpose | Evaluate blockers by phase; do **not** incorrectly block Phase 1 on B-456/B-EVID/B-GO **or** on full nine-condition B-MIG (circular with PIN/BIND) |
| Preconditions | Live #456 / #458 / tooling status; durable plan for active revision |
| Inputs | Blockers table §5; §6.1 phase-aware split |
| Actions | For **Phase 1 start**: require **B-MIG-SCOPE cleared**, D-SEQ ACK, worktree preflight path ready, explicit maintainer authorization. For **after 1.6 / before 1b.2/#455**: require B-RC after merge, **B-MIG-PIN cleared**, RC branch tip pin, D-ATTEST ACK, **B-DISPATCH cleared**, **B-PUB/B-OCI-HANDOFF cleared**. For **Phase 2 / #456 start**: require **B-MIG-BIND cleared** + 1c.2 promote-path proof. For **Phase 2.3+**: require B-VAL. For **Phase 3.5+**: require B-456/B-EVID/B-GO. |
| Verification | Phase-aware clearance matrix written (SCOPE/PIN/BIND distinct) |
| Evidence | Blocker evaluation record |
| Stop | Starting a phase while its blockers remain SET; treating umbrella B-MIG as a single pre-Phase-1 mega-gate |
| Re-run | After each clearance |
| Outputs | `blockersByPhase` map |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 — B-MIG-SCOPE/PIN/BIND SET (umbrella B-MIG SET); D-SEQ/D-ATTEST decided; B-DISPATCH/B-PUB/B-VAL/B-456/B-EVID/B-GO SET; B-RC pending pin; **Phase 0 not complete; Phase 1 forbidden** |

#### Step 0.5 — Worktree / remote sync preflight

| Item | Content |
|------|---------|
| Phase/Step | 0.5 |
| Purpose | Ensure clean, up-to-date `develop` before version prep |
| Preconditions | Shell/git usable in execution round; Step 0.0 refreshed `baseDevelopSha` available |
| Inputs | `git status`, `git fetch`, `origin/develop` |
| Actions | Confirm clean worktree; fast-forward local develop to origin; refuse dirty tree |
| Verification | Clean; local SHA == `origin/develop` (or intentional release-prep branch from that tip) |
| Evidence | SHA pair + clean flag |
| Stop | Dirty worktree; divergent history; secrets in tree |
| Re-run | After cleanup |
| Outputs | `baseDevelopSha` (reconfirmed) |
| Owner | #458 |
| This-round status | 計画のみ / 未実行（dirty = UNCONFIRMED this round） |

---

### Phase 1：Version preparation（precedes #456）

#### Step 1.1 — Version touch list

| Item | Content |
|------|---------|
| Phase/Step | 1.1 |
| Purpose | Enumerate every version surface for `1.2.0` / tag `v1.2.0` |
| Preconditions | Phase 0 scope freeze (**B-MIG-SCOPE cleared**); D-SEQ ACK; worktree OK; durable Rev.8 plan (Step 0.0). Explicit maintainer authorization for Phase 1. **Not** blocked by B-456/B-EVID/B-GO; **not** blocked by B-MIG-PIN/BIND (those clear later) |
| Inputs | csproj files; OpenAPI; Docker labels; bundle scripts; CHANGELOG; release record path |
| Actions | Prepare edits (future round) for: service/Docker/`MAILER_VERSION` (=`1.2.0` no `v`), Contracts `<Version>`, OpenAPI `info.version`, setup bundle `mailerVersion`/`setupLauncherVersion`, CHANGELOG `[1.2.0]`, `docs/releases/v1.2.0.md` draft skeleton, workflow inputs; document public verifier expectation that OCI label is `1.2.0` |
| Verification | Checklist covers all tag-validation surfaces used by publish workflows |
| Evidence | Version inventory table |
| Stop | Unknown version surface discovered late |
| Re-run | If new packaging field added |
| Outputs | `versionTouchList` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

#### Step 1.2 — CHANGELOG + release record draft

| Item | Content |
|------|---------|
| Phase/Step | 1.2 |
| Purpose | Value-free operator-facing release narrative (draft; public identities filled later) |
| Preconditions | 1.1 |
| Inputs | Merged Easy Setup PRs; ADR 0021; B-MIG decision; D-ATTEST intent; v1.1.0 record as template |
| Actions | Author `CHANGELOG.md` 1.2.0 section; create `docs/releases/v1.2.0.md` skeleton (Source placeholders until tag). Reflect INCLUDE/EXCLUDE migrations honestly. Note intended `attestMode` consequences (especially 方式2: no registry attestation for v1.2.0). |
| Verification | No secrets/PII/private paths/raw provider errors; positions Easy Setup; states non-goals; migration statement matches B-MIG |
| Evidence | Draft files in version-prep PR |
| Stop | PII/secret found; false “no migration” claim while INCLUDE |
| Re-run | After redaction / B-MIG alignment |
| Outputs | Draft CHANGELOG + release record skeleton |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

#### Step 1.3 — Contracts / OpenAPI decision record

| Item | Content |
|------|---------|
| Phase/Step | 1.3 |
| Purpose | Explicitly decide content change vs version-string-only sync |
| Preconditions | 1.1 |
| Inputs | Diff Contracts DTOs/constants vs v1.1.0 tag; OpenAPI diff |
| Actions | **Assumption to validate:** HTTP content unchanged for Easy Setup; **still set version strings to `1.2.0`** for publish validation. Record decision text for release record. If content changed unexpectedly, stop (minor gate / contract process). |
| Verification | Decision recorded; if content unchanged, only version fields differ; OpenAPI validate script planned |
| Evidence | “Contracts/OpenAPI decision” subsection ready for release record |
| Stop | Undocumented contract content drift |
| Re-run | After contract PR process if drift real |
| Outputs | `contractsOpenApiDecision` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

#### Step 1.4 — DB migration inventory（B-MIG-SCOPE contribution）

| Item | Content |
|------|---------|
| Phase/Step | 1.4 |
| Purpose | Inventory migrations since v1.1.0; apply parent INCLUDE; never invent `none` by default; contribute to B-MIG-SCOPE (not PIN/BIND) |
| Preconditions | 0.2 B-MIG decision = INCLUDE; B-MIG-SCOPE in progress or cleared |
| Inputs | `src/**/Migrations/*.sql`; compare to v1.1.0; known `012_*`, `013_*`; §6.1 |
| Actions | List all migrations newer than v1.1.0. For INCLUDE: document upgrade/downgrade boundaries for #456 scope and release notes; confirm frozen filename list + no-extra policy (SCOPE). Do **not** claim cond 8 (PIN) or cond 9 (BIND) complete before `releaseCommitSha` / final binding exist. |
| Verification | Inventory matches develop truth; statement matches decision; SCOPE criteria met or EXCLUDE constructible |
| Evidence | Migration inventory note + SCOPE authority checklist |
| Stop | Shipping without concrete statement; claiming full B-MIG clear before PIN/BIND; extra migrations beyond frozen list without parent re-decision |
| Re-run | After parent decision change |
| Outputs | `migrationDecision=INCLUDE`; `dbMigrationStatement=<concrete decided text>` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行（exploration: 012+013 exist; INCLUDE decided; B-MIG-SCOPE/PIN/BIND SET） |

#### Step 1.5 — `implementation-status` staging policy

| Item | Content |
|------|---------|
| Phase/Step | 1.5 |
| Purpose | Prevent premature `implemented` |
| Preconditions | — |
| Inputs | `docs/implementation-status.json` `easy-setup` entry |
| Actions | In version-prep PR, **keep** status `partial` (or update notes only). Schedule `implemented` exclusively in **Phase 7 completion PR on main** after public smoke |
| Verification | Version-prep PR diff does not set `implemented` |
| Evidence | Diff review note |
| Stop | Accidental `implemented` before Phase 6/7 |
| Re-run | Revert status |
| Outputs | Status remains `partial` through promote/publish/smoke |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

#### Step 1.6 — Version-prep PR on develop + freeze releaseCommitSha

| Item | Content |
|------|---------|
| Phase/Step | 1.6 |
| Purpose | Land version alignment on develop; **freeze** merge SHA as release/RC identity |
| Preconditions | 1.1–1.5; **B-MIG-SCOPE cleared**; D-SEQ ACK; worktree clean; durable Rev.8 plan; explicit Phase 1 authorization. Full B-MIG (PIN/BIND) **not** required here |
| Inputs | Touch list; base develop SHA |
| Actions | Branch from frozen develop; open PR; CI green; merge to develop; record **merge result commit** as `releaseCommitSha` / RC `sourceCommitSha`. Immediately after pin, run **B-MIG-PIN** verification (emit normative `migrationPin` per §6.1; feeds PIN before 1b.2). |
| Verification | All version strings `1.2.0`; CI pass; no product feature drive-by; SHA recorded |
| Evidence | PR URL, merge SHA |
| Stop | CI fail; scope creep; B-MIG-SCOPE still SET; attempting Phase 1 under full-nine-condition mega-gate reading |
| Re-run | Fix-forward PR; new merge SHA becomes the pin (invalidates prior candidate/qual if any) |
| Outputs | `releaseCommitSha` (frozen); clears **B-RC** for subsequent #455/#456 (RC branch still required — Step 1.6.1); enables **B-MIG-PIN** (`migrationPin` clearance before 1b.2) |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 — **Phase 1 forbidden until B-MIG-SCOPE cleared + explicit auth** |

#### Step 1.6.1 — Create immutable RC branch at releaseCommitSha（B-R3-01）

| Item | Content |
|------|---------|
| Phase/Step | **1.6.1** |
| Purpose | Create immutable `release/v1.2.0-rc` pointing at exact `releaseCommitSha` for dispatch + promotion head reuse |
| Preconditions | Step 1.6 complete; `releaseCommitSha` frozen; B-RC cleared for pin identity |
| Inputs | `releaseCommitSha` |
| Actions | Create branch `release/v1.2.0-rc` at **exact** `releaseCommitSha`. Push to origin. Do **not** move this branch until #458 completes (or abandon + restart). Prefer this same branch as Phase 4 promotion PR head. |
| Verification | `refs/heads/release/v1.2.0-rc` tip == `releaseCommitSha` |
| Evidence | Branch tip SHA record |
| Stop | Branch tip drift; branch created from wrong SHA; later force-move without abandon+restart |
| Re-run | If tip moves after pin: abandon prior #455/qual; recreate/reset branch to new policy SHA and restart candidate path |
| Outputs | `dispatchRef=release/v1.2.0-rc`; RC tip == `releaseCommitSha` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

---

### Phase 1b / 1c：Single #455 workflow run（OCI + packages + handoff）

> **Normative (B-R2-01 retained):** Phase 1b and 1c are **not** two independent OCI builds. They are ordered jobs inside **one** `generate-setup-release-candidate.yml` run. `ociHandoffMode=SINGLE_WF_RUN_OPTION_A`.
>
> **Normative (B-R3-01 / M-R3-02):** Dispatch only `--ref release/v1.2.0-rc` after B-DISPATCH clearance; accept only **attempt == 1** (no partial job re-runs).

#### Step 1b.0 — B-DISPATCH clearance + B-MIG-PIN check（before 1b.2）

| Item | Content |
|------|---------|
| Phase/Step | **1b.0** |
| Purpose | Clear **B-DISPATCH** and confirm **B-MIG-PIN** before starting the #455 run; prove workflow is on `main` and RC ref identity is correct |
| Preconditions | Steps 1.6 + 1.6.1 complete; `releaseCommitSha` frozen; Agent B APPROVE + durable plan already done earlier |
| Inputs | `origin/main`; `generate-setup-release-candidate.yml`; promote tooling required by B-PUB; `release/v1.2.0-rc`; `releaseCommitSha`; frozen migration inventory |
| Actions | (1) Confirm release-infra bootstrap: workflow (+ promote tooling required by B-PUB) exists on default branch `main`; record `infraBootstrapMainSha` (bootstrap may have landed earlier after Step 0.0 — verify still present). (2) Confirm `refs/heads/release/v1.2.0-rc` tip == `releaseCommitSha`. (3) Confirm dispatch will use `--ref release/v1.2.0-rc` (not raw SHA). (4) Confirm attempt-unity policy understood (§7.7). (5) **B-MIG-PIN:** produce/verify normative `migrationPin` output (§6.1) against `releaseCommitSha` tree (RFC8785-JCS inventoryAlgorithm / inventoryDocument / migrationPinWithoutDigest / migrationPinDigestSha256 per §6.1; no evidenceDigestSha256); **FAIL** if tree has migrations beyond frozen 012/013, digests mismatch tree, or algorithm/self-digest rules violated. |
| Verification | Workflow present on `main`; RC tip equality; no raw-SHA dispatch plan; **B-MIG-PIN cleared** with `migrationPinWithoutDigest` + `migrationPinDigestSha256` + `migrationInventoryDigestSha256` per §6.1 |
| Evidence | `infraBootstrapMainSha`; RC tip SHA; B-DISPATCH clearance note; B-MIG-PIN PIN outputs / digests |
| Stop | Workflow missing on `main`; RC tip ≠ `releaseCommitSha`; plan to dispatch @ raw SHA; **B-MIG-PIN FAIL** (extra migrations, digest mismatch, or non-canonical algorithm / self-digest / `evidenceDigestSha256`) |
| Re-run | After infra bootstrap PR / RC branch fix / inventory repair (may need new version-prep pin) |
| Outputs | `bDispatchCleared=true`; `infraBootstrapMainSha`; `dispatchRef=release/v1.2.0-rc`; `bMigPinCleared=true`; `migrationPinWithoutDigest`; `migrationPinDigestSha256`; `migrationInventoryDigestSha256` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 — **B-DISPATCH currently SET**; **B-MIG-PIN SET** until after 1.6 |

#### Step 1b.1 — Choose publish method + clear B-PUB / B-OCI-HANDOFF + confirm D-ATTEST

| Item | Content |
|------|---------|
| Phase/Step | 1b.1 |
| Purpose | Decide P-OCI-PROMOTE vs P-REBUILD; ensure promote-capable tooling + digest-preserving proof readiness **before** starting the #455 run; confirm attestation mode |
| Preconditions | `releaseCommitSha` frozen; RC branch exists; D-ATTEST ACK; Step 1b.0 progressing / clearable |
| Inputs | §7.3–7.4; `publish-image.yml`; `generate-setup-release-candidate.yml`; candidate Dockerfile/script path on develop / RC tip |
| Actions | Record `publishMethod`. For P-OCI-PROMOTE: ensure promote tooling can push the **same** OCI layout from `build-oci` without rebuild. Prepare dry-run/staging proof procedure. Confirm Option A single-run mapping. Confirm `attestMode` consequences (方式1 requires attestation-inclusive `build-oci`; 方式2 does not attach registry attestation). Confirm OCI label will be `1.2.0` (no `v`). |
| Verification | Method recorded; B-PUB/B-OCI-HANDOFF clearance path identified **before 1b.2**; no assumption that current rebuild workflow is promote; D-ATTEST recorded |
| Evidence | Publish-method + promote-proof readiness note; `attestMode` |
| Stop | Proceeding to 1b.2 without promote tooling readiness; Option B prebuilt-OCI narrative; D-ATTEST missing |
| Re-run | After workflow/tooling extension |
| Outputs | `publishMethod=P-OCI-PROMOTE\|P-REBUILD`; `ociHandoffMode=SINGLE_WF_RUN_OPTION_A`; `attestMode=REGISTRY_ATTEST\|EXTERNAL_PROVENANCE`; `bPubClearancePlan` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 — **B-PUB/B-OCI-HANDOFF currently SET** |

#### Step 1b.2 — `build-oci` (same #455 run) — produce final OCI graph

| Item | Content |
|------|---------|
| Phase/Step | 1b.2 |
| Purpose | Produce the OCI layout that **is** the final public OCI to promote (under P-OCI-PROMOTE), inside the single #455 run at exact RC ref |
| Preconditions | 1b.0 B-DISPATCH cleared; **B-MIG-PIN cleared**; 1b.1; B-RC cleared; B-PUB/B-OCI-HANDOFF tooling readiness cleared; D-ATTEST ACK |
| Inputs | `release/v1.2.0-rc` @ `releaseCommitSha`; `MAILER_VERSION=1.2.0`; `attestMode` |
| Actions | Dispatch `generate-setup-release-candidate.yml` with `--ref release/v1.2.0-rc`, inputs `release_version=1.2.0` and mailpit pin. Job `build-oci` runs `scripts/build-candidate-oci-image.sh` with `SOURCE_COMMIT` + `MAILER_VERSION`. If 方式1: attestation-inclusive graph must be produced here before packaging. If 方式2: runtime index without registry attestation is expected. Record `workflowRunId` and **`workflowRunAttempt` (must be 1)**. Machine-verify GITHUB_REF / GITHUB_SHA / job HEAD == `releaseCommitSha`. Upload OCI layout + `image-identity.json`. **Do not** re-run failed jobs; on FAIL/CANCEL abandon and new dispatch (§7.7). |
| Verification | Digests stable and recorded; embedded binary version == `1.2.0`; OCI label `org.opencontainers.image.version=1.2.0`; no public `v1.2.0` tag created yet; same `workflowRunId` will continue into 1c.1; `workflowRunAttempt==1`; all job HEAD == `releaseCommitSha`; RC tip unchanged |
| Evidence | `image-identity.json`; `oci-index.digest`; `workflowRunId`; `workflowRunAttempt=1`; per-job HEAD SHAs |
| Stop | Version mismatch; public tags created early; digest unknown; attempting separate prebuilt OCI ingestion; 方式1 without attestation-inclusive graph; attempt>1 / partial re-run; GITHUB_SHA ≠ `releaseCommitSha`; RC tip moved |
| Re-run | New full `workflow_dispatch` (new `workflowRunId`, attempt 1); invalidates prior packages |
| Outputs | `workflowRunId`; `workflowRunAttempt=1`; `finalImageDigest`; `finalOciIndexDigest`; `stagingOciLayoutRef` (artifact from this run) |
| Owner | #458 / #455 handoff |
| This-round status | 計画のみ / 未実行 |

#### Step 1c.1 — `package-*` + `assemble-handoff` (same #455 run)

| Item | Content |
|------|---------|
| Phase/Step | 1c.1 |
| Purpose | Produce Win x64 / Linux x64 / Linux arm64 candidate archives + provenance from the **same** run’s `build-oci` identity |
| Preconditions | 1b.2 succeeded in the **same** `workflowRunId` with **`workflowRunAttempt == 1`** |
| Inputs | Same-run OCI artifact / `image-identity.json`; `releaseCommitSha`; attempt fields |
| Actions | Jobs `package-linux-x64`, `package-linux-arm64`, `package-win-x64` download same-run OCI identity and generate archives. Job `assemble-handoff` writes `CANDIDATE-SHA256SUMS`, `candidate-provenance.json`, `CANDIDATE-HANDOFF.md`. Do not start a second OCI build. Record per-job `runAttempt==1` and `headSha==releaseCommitSha`. On any required job FAIL/CANCEL: abandon run; forbid job re-run; new full dispatch. |
| Verification | Per-RID manifests: `sourceCommitSha` == `releaseCommitSha`; `mailerVersion` == `1.2.0`; `imageDigest`/`ociIndexDigest` == this run’s `build-oci` finals; three RIDs; Mailpit digest pinned (no `:tag` form); provenance references same run; **all artifacts from same `workflowRunId`**; attempt fields == 1 |
| Evidence | Provenance + sums + per-RID identity fields + `workflowRunId` + attempt fields |
| Stop | Digest placeholders; wrong SHA; missing RID; packages from a different run than OCI; attempt mix / re-run jobs |
| Re-run | New full #455 workflow run (new freeze; attempt 1 only) |
| Outputs | `candidateArtifactSet`; `ociHandoffMode=SINGLE_WF_RUN_OPTION_A`; `workflowRunAttempt=1` |
| Owner | #455 tooling / #458 gate |
| This-round status | 計画のみ / 未実行 |

#### Step 1c.2 — Promote-path proof（digest-preserving） before #456

| Item | Content |
|------|---------|
| Phase/Step | 1c.2 |
| Purpose | Prove the same OCI layout can be promoted without changing index digest; clear remaining B-PUB/B-OCI-HANDOFF for qualification start |
| Preconditions | 1c.1 complete; promote tooling available; attempt-unity gates passed |
| Inputs | Same-run OCI layout; chosen `publishMethod` |
| Actions | Dry-run or staging-namespace push of that OCI layout. Compare source index digest to destination index digest. Record method, tooling version, proof run id. Failure => stop; do **not** start #456. |
| Verification | Index digest preserved end-to-end; proof recorded value-free |
| Evidence | Promote-path proof record (digests, method, tool ids) |
| Stop | Digest change; tooling cannot promote layout; proof skipped |
| Re-run | Fix tooling / method; may require new #455 run if layout must change (esp. 方式1) |
| Outputs | `promotePathProofPass=true`; B-PUB/B-OCI-HANDOFF cleared for proceeding to #456 |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

---

### Phase 2：#456 qualification + #458 consumer validation

#### Step 2.1 — Execute / fetch #456 sealed Go package

| Item | Content |
|------|---------|
| Phase/Step | 2.1 |
| Purpose | Run or obtain #456 qualification against candidate at `releaseCommitSha` from the frozen #455 run |
| Preconditions | Phase 1c.2 promote-path proof pass; B-RC cleared; attempt-unity recorded; **B-MIG-BIND cleared** (cond 9: new binding/run on final Issue/plan/`releaseCommitSha` **carrying PIN digests**) |
| Inputs | Issue #456 body table; plan Rev.12 (sister; PIN digest canonicalization + phase-aware B-MIG); candidate artifacts; OCI identity; `workflowRunId`; `workflowRunAttempt`; cleared PIN outputs: `migrationPinWithoutDigest` + `migrationPinDigestSha256` + `migrationInventoryDigestSha256` |
| Actions | Confirm **B-MIG-BIND** before start: binding must include `migrationPinDigestSha256`, `migrationInventoryDigestSha256`, `migrationFileDigests[]` from PIN; **refuse BIND** without those PIN outputs. Execute #456 per sister plan (separate authorization) **or** fetch completed sealed package; load binding snapshot; load evidence inventory; load go-no-go decision objects; **verify provenance attempt fields** (`workflowRunAttempt==1`, job attempts == 1, headSha equality, same `workflowRunId`) |
| Verification | Sealed package exists for this SHA/candidateIds; value-free; binds same OCI/archives from `workflowRunId` attempt 1; binding identities match final Issue/plan/pin; PIN handoff fields present and match cleared PIN outputs (`migrationPinWithoutDigest` + `migrationPinDigestSha256` + `migrationInventoryDigestSha256`) |
| Evidence | Evidence index digests (not raw logs); attempt-field verification record; B-MIG-BIND clearance + PIN handoff digests |
| Stop | Missing Hard rows; PII in evidence; incomplete inventory; wrong SHA; qualifying a different OCI than 1b.2; attempt>1 / mixed attempts; **B-MIG-BIND still SET**; BIND attempted without PIN digests |
| Re-run | New #456 run (after new #455 if candidate identity invalid) |
| Outputs | `qualificationPackageRef`; progresses B-456/B-EVID/B-GO clearance; `bMigBindCleared=true`; binding PIN fields recorded |
| Owner | #456 (execution) / #458 (consume) |
| This-round status | 計画のみ / 未実行 — **B-MIG-BIND SET until before Phase 2 start** |

#### Step 2.2 — Hard all-PASS confirmation（no alternate list）

| Item | Content |
|------|---------|
| Phase/Step | 2.2 |
| Purpose | Confirm every Hard row PASS using **only** #456 table |
| Preconditions | 2.1 |
| Inputs | Required-scenario table; active evidence per scenarioId+variantId |
| Actions | Mechanically map each Hard row -> active PASS evidence; **do not** build a second Hard checklist |
| Verification | Zero Hard missing/FAIL/NOT_RUN |
| Evidence | Mapping table (scenarioId, variantId, evidenceId, result) |
| Stop | Any Hard not PASS; attempt to substitute alternate checks |
| Re-run | #456 re-run |
| Outputs | `hardAllPass=true` |
| Owner | #458 / #456 |
| This-round status | 計画のみ / 未実行 |

#### Step 2.3 — Version-pinned `#456 consumer validator`（M-R2-02 + M-R3-03）

| Item | Content |
|------|---------|
| Phase/Step | 2.3 |
| Purpose | #458 consumer-validates sealed evidence with explicit Rev.8 **predicate IDs**; version-pinned validator is the authority |
| Preconditions | 2.1–2.2; **B-VAL cleared** |
| Inputs | Sealed run package; Phase-4 manifest; decision object; inventories; roots; disposition chain; authz; active evidence set; validator at pinned version |
| Actions | Run version-pinned `#456 consumer validator` implementing all `CV-*` IDs below (including **CV-ACTIVE-*** / **CV-SCENARIO-01** and, on the migration INCLUDE path, **CV-MIG-PIN-01** / **CV-MIG-PIN-02**). Produce predicate-level report rows with required fields. This is sealed-package integrity — **not** a second Hard product-scenario gate. |
| Verification | Every required predicate PASS (or documented Conditional path only where #456 allows — never for Hard). Report fields complete. |
| Evidence | Consumer validation report (value-free) |
| Stop | Any required predicate FAIL; optional evidence PII/schema/authz violations; validator missing (B-VAL) |
| Re-run | New sealed run / disposition fix per #456; or land validator then re-run |
| Outputs | `consumerValidationPass=true`; `validatorId`; `validatorVersion` |
| Owner | #458 consumes #456 |
| This-round status | 計画のみ / 未実行 — **B-VAL currently SET if validator absent** |

**Required report fields (each predicate row):**

`validatorId`, `validatorVersion`, `qualificationRunId`, `bindingId`, `candidateId`, `predicateId`, `expected`, `actual/result`, `recomputed digest/root/high-water`, `final verdict`.

**Additional required report fields for ACTIVE / SCENARIO predicates:**

`scenarioId`, `variantId`, `derivedActiveEvidenceId`, `indexedEvidenceId`, `derivedExceptionId`, `indexedExceptionState`, `match`.

**Mandatory predicate groups / IDs (`CV-*`) — validator MUST implement:**

| Predicate ID | Expected |
|--------------|----------|
| **CV-SEAL-01** | Single terminal `sealed` run-status |
| **CV-SEAL-02** | No run-status events after terminal sealed |
| **CV-SEAL-03** | Sealed event `eventSequence` / previous digest / event digest / JCS canonicalization valid |
| **CV-P4-01** | Phase-4 manifest ↔ decision object digest match |
| **CV-INV-01** | `sealedObjectInventory` complete |
| **CV-INV-02** | No extra objects beyond inventory |
| **CV-ROOT-01** | Evidence / exception / scan roots recompute and match |
| **CV-ROOT-02** | `rootDigestAlgorithm` == `RFC8785-JCS-sorted-path-sha256/v1` (or exact Rev.8 name) |
| **CV-HW-01** | Disposition high-water + hash chain integrity |
| **CV-HW-02** | Exception-disposition sequence / hash chain / high-water integrity |
| **CV-ST-01** | Disposition / exception-disposition state-transition validity |
| **CV-FRS-01** | `finalRunState` object count / root / last sequence / last digest recompute |
| **CV-IDX-01** | `phase3LatestIndexSha256`, `finalEvidenceIndexSha256`, `goNoGoSha256` match sealed package |
| **CV-ID-01** | Evidence / disposition / exception `qualificationRunId` / `bindingId` / `candidateId` match |
| **CV-BIND-01** | `planCommitSha` / `planFileSha256` / `issueBodySha256` / docs digests match binding |
| **CV-BIND-02** | No evidence keys outside required ∪ optional binding |
| **CV-AUTH-01** | Authorization digest + role identities valid |
| **CV-AUTH-02** | FAIL->PASS requires `executedBy` / `approvedBy` per Rev.8 |
| **CV-EV-01** | Result vs `typePayload` consistency |
| **CV-ACS-01** | G456-03 scenario-specific predicates |
| **CV-ACS-02** | G456-04 scenario-specific predicates |
| **CV-ACS-03** | G456-05 scenario-specific predicates |
| **CV-ACS-04** | G456-06 scenario-specific predicates |
| **CV-ACS-05** | G456-06 references active G456-05 PASS |
| **CV-FRESH-01** | Issue freshness check result recorded / pass |
| **CV-GO-01** | `GO_ELIGIBLE` + human APPROVE; Hard NO_GO cannot be overridden |
| **CV-OPT-01** | Optional evidence PII / schema / authz violations => NO_GO |
| **CV-ACTIVE-01** | Replay disposition events for every required∪optional key; derived `activeEvidenceId` == `decision/evidence-index.json` exactly |
| **CV-ACTIVE-02** | Replay exception-disposition for every Conditional key; derived active exception == exception state used by `go-no-go.json` |
| **CV-SCENARIO-01** | Derived per-variant results reproduce `go-no-go.json` `scenarioIndex` including `informationalNotConfirmed` |
| **CV-MIG-PIN-01** | Recompute full inventory from `releaseCommitSha` tree (see procedure below); equality of recomputed `migrationInventoryDigestSha256` with **all** of `migrationPinWithoutDigest.inventoryDigestSha256`, `binding.migrationInventoryDigestSha256`, and G456-42/43/44 evidence inventory digests |
| **CV-MIG-PIN-02** | Per-file SHA-256 / `gitBlobSha` == `releaseCommitSha` tree **and** `binding.migrationFileDigests[]` |

**CV-MIG-PIN-01 procedure (M-R11-02; sealed-package integrity — not a second Hard product-scenario list):**

```text
CV-MIG-PIN-01:
  1. Read releaseCommitSha git tree (checkout or git cat-file/tree)
  2. Enumerate ALL Data/Migrations/*.sql in runner apply order
  3. Assert expectedPost011Inventory == [012, 013] only (no 014+ / other post-011 migrations)
  4. Recompute migrationInventoryDigestSha256 with fixed inventoryAlgorithm + inventoryDocument
  5. Equality of recomputed value with ALL of:
     - migrationPinWithoutDigest.inventoryDigestSha256
     - binding.migrationInventoryDigestSha256
     - G456-42/43/44 evidence inventory digests
```

**CV-MIG-PIN-02** remains per-file sha256/`gitBlobSha` vs `releaseCommitSha` tree + `binding.migrationFileDigests[]`.

**Migration INCLUDE path:** **B-VAL** / Step 2.3 **must** cover **CV-MIG-PIN-01** and **CV-MIG-PIN-02**. These are sealed-package integrity predicates that prove PIN digests flowed into binding and independently re-verify against the release tree — **not** a second Hard product-scenario list (Hard sole authority remains Issue #456 G456-42..44).

If validator binary/script does not yet exist in repo: **B-VAL** blocks Phase 2.3+ until a version-pinned validator implementing these IDs exists (or maintainers accept an explicitly enumerated checklist that remains sealed-package integrity — still not a second Hard gate for product scenarios).

#### Step 2.4 — Conditional / Informational / Go checks

| Item | Content |
|------|---------|
| Phase/Step | 2.4 |
| Purpose | Validate Conditional exceptions and Informational honesty; confirm Go |
| Preconditions | 2.3 |
| Inputs | Conditional exception + disposition events; Informational list; machineVerdict + humanDecision |
| Actions | For each Conditional exception verify: reason, alternate confirmation, residual risk, approver, impact scope. Confirm Informational unchecked items are explicitly listed. Confirm Go per #456 rules (human cannot APPROVE Hard NO_GO). Align with CV-GO-01 / CV-AUTH-* / CV-OPT-01 / CV-ACTIVE-* / CV-SCENARIO-01 outcomes. |
| Verification | Schema-complete exceptions; Go present; Issue snapshot freshness gate satisfied if required by #456 |
| Evidence | Exception summary + Go decision digests |
| Stop | Incomplete Conditional; Go missing; Hard NO_GO overridden |
| Re-run | #456 disposition / new run |
| Outputs | `goConfirmed=true` (clears **B-GO** when combined with sealed package) |
| Owner | #458 / #456 |
| This-round status | 計画のみ / 未実行 |

#### Step 2.5 — Release commit identity match

| Item | Content |
|------|---------|
| Phase/Step | 2.5 |
| Purpose | Prove qualification `sourceCommitSha` == frozen `releaseCommitSha` (exact equality only) |
| Preconditions | 2.4; Phase 1.6 SHA known |
| Inputs | Binding `sourceCommitSha`; `releaseCommitSha`; RC branch tip |
| Actions | Compare SHAs with **exact equality**. Confirm `release/v1.2.0-rc` tip still equals pin. If any content-changing commit landed after pin, apply Phase 2.7 (new candidate + full requal). **No** “equivalent tree” escape hatch. |
| Verification | Exact match **or** completed requal for new SHA |
| Evidence | SHA equality record |
| Stop | Mismatch without requal; “equivalent tree” proposed as pass; RC tip drift |
| Re-run | Phase 2.7 |
| Outputs | `releaseCommitSha` confirmed as tag-target intent |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

#### Step 2.6 — Candidate artifact identity freeze

| Item | Content |
|------|---------|
| Phase/Step | 2.6 |
| Purpose | Freeze candidate digests/checksums for later promote comparison (byte-identical expectation) |
| Preconditions | 2.5 |
| Inputs | `candidate-provenance.json`, `CANDIDATE-SHA256SUMS`, `image-identity.json`, per-RID `payloadTreeSha256`, OCI index digest, `workflowRunId`, attempt fields |
| Actions | Copy identity fields into release working evidence; confirm manifests already hold final public digests from the same #455 run (attempt 1) |
| Verification | All three RIDs + OCI identity present; Mailpit digest pinned; archive digests frozen; run id + attempt bound |
| Evidence | Frozen identity JSON (value-free) |
| Stop | Missing RID; digest mismatch inside handoff; packages/OCI from different runs; attempt ≠ 1 |
| Re-run | New #455 candidate + #456 |
| Outputs | `frozenCandidateIdentity`; `workflowRunId`; `workflowRunAttempt=1` |
| Owner | #458 / #455 / #456 |
| This-round status | 計画のみ / 未実行 |

#### Step 2.7 — Requalification matrix（release commit / artifact change）

| Change detected | Requal scope | Skip allowed? |
|-----------------|--------------|---------------|
| Hard evidence FAIL/missing | Full #456 run; no publish | No |
| `sourceCommitSha` / `releaseCommitSha` changed (product/code/compose/Dockerfile/version) | New #455 single run + full #456 | No |
| Final OCI digest changed after archives generated | **New full #455 run** (new archives + checksums) + full #456 **before** public tags | No |
| Docs-only commit explicitly classified by maintainer as non-runtime | Follow #456 plan rules; default **conservative full requal** unless #456 binding allows docs delta | Only if #456 plan explicitly permits |
| Candidate archive rebuilt | **New candidate** — do not promote old smoke to new bytes | Full #456 on new archives |
| OCI layers rebuilt | New full #455 run | Full image-related scenarios |
| Attempt to mutate archive-internal manifest to match a new public digest | **Forbidden** — treat as new candidate path | N/A |
| Attempt Option B separate prebuilt OCI into current #455 | **Forbidden** / out-of-policy | N/A |
| Partial job re-run / attempt mix on #455 | **Forbidden** — abandon; new full dispatch attempt 1 | N/A |
| RC branch tip moved after dispatch | Abandon run + qual; new full #455 from current policy | N/A |
| Floating `latest` / wrong version mix attempted | Stop; fix references | N/A |
| Class C discovered after public version tags | **Forbidden** under publish policy | N/A |
| Promote-path proof fails (digest not preserved) | Stop before #456; fix tooling or new run | N/A |

| Item | Content |
|------|---------|
| Phase/Step | 2.7 |
| Purpose | Define requal boundaries without alternate Hard lists; enforce single-run handoff + manifest immutability + attempt unity |
| Preconditions | Identity mismatch or commit/digest move |
| Inputs | Diff class; frozen identities |
| Actions | Select row from matrix; execute owned #456/#455 work as required; return to 2.1 |
| Verification | Matrix row cited in evidence |
| Evidence | Requal decision record |
| Stop | Publishing despite matrix requiring requal; manifest mutation shortcut; Option B; attempt mix |
| Re-run | Until match |
| Outputs | Updated `releaseCommitSha` / identities / `workflowRunId` |
| Owner | #456 (execution) / #458 (gate) |
| This-round status | 計画のみ / 未実行 |

---

### Phase 3：Release gate（promotion 前）

#### Step 3.1 — Release-gate CI on release commit

| Item | Content |
|------|---------|
| Phase/Step | 3.1 |
| Purpose | CI green on frozen SHA |
| Preconditions | Phase 2 consumer validation complete; B-456/B-EVID/B-GO cleared |
| Inputs | `releaseCommitSha` / `release/v1.2.0-rc` |
| Actions | Ensure CI completed on that commit/PR |
| Verification | Required checks success |
| Evidence | Check run IDs / conclusion |
| Stop | Any required check fail |
| Re-run | Fix-forward + possible 2.7 |
| Outputs | `ciGreen=true` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

#### Step 3.2 — Version / Contracts / OpenAPI congruence

| Item | Content |
|------|---------|
| Phase/Step | 3.2 |
| Purpose | All version strings `1.2.0`; contract decision applied; OCI label expectation documented |
| Preconditions | 3.1 |
| Inputs | csproj, openapi.yaml, decision from 1.3; candidate identity |
| Actions | Re-read tree at SHA; run `node scripts/validate-openapi.mjs`; contract drift scripts as in publish workflow; confirm OCI version expectation `1.2.0` (no `v`) |
| Verification | Versions equal `1.2.0`; validate pass |
| Evidence | Command results (value-free) |
| Stop | Mismatch |
| Re-run | Version-prep fix (+ 2.7 if SHA moves) |
| Outputs | `versionsAligned=true` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

#### Step 3.3 — Bundle manifest vs image digest congruence（candidate == final）

| Item | Content |
|------|---------|
| Phase/Step | 3.3 |
| Purpose | setup `release-bundle-manifest.json` imageDigest/ociIndexDigest match frozen **final** OCI identity from the same #455 run |
| Preconditions | 2.6 |
| Inputs | Per-RID manifests; `image-identity.json` / 1b.2 digests; `workflowRunId` |
| Actions | Compare fields; ensure schemaVersion 1; packagingKind correct for stage; **no** planned post-publish manifest rewrite |
| Verification | Digests equal finals; RID trio present; same run binding |
| Evidence | Comparison table |
| Stop | Mismatch; plan to “fix digests at publish” |
| Re-run | New candidate (full #455 run) + requal |
| Outputs | `candidateManifestAligned=true` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

#### Step 3.4 — Secret / PII / private path scan + platform presence

| Item | Content |
|------|---------|
| Phase/Step | 3.4 |
| Purpose | Candidate archives safe; Win x64 / Linux x64 / Linux arm64 present |
| Preconditions | Frozen archives available |
| Inputs | Archives; `scripts/scan-setup-release-bundle.sh` |
| Actions | Scan each extracted RID; confirm three artifacts; reject `latest` references |
| Verification | Scans PASS; three RIDs; no secret/PII/private path |
| Evidence | Scan summaries |
| Stop | Scan fail; missing RID |
| Re-run | Repackage (#455 single run) + requal |
| Outputs | `candidateScanPass=true` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

#### Step 3.4b — External publication readiness

| Item | Content |
|------|---------|
| Phase/Step | 3.4b |
| Purpose | Confirm publication surface is empty for v1.2.0 and durable store / credentials are ready **before** irreversible tag/publish |
| Preconditions | 3.1–3.4 |
| Inputs | GitHub tags/releases; GHCR tags; nuget.org; local/durable candidate store; `release` environment; `attestMode` |
| Actions | Confirm **absent**: git tag `v1.2.0`; GHCR `v1.2.0` & `sha-<releaseCommitSha>`; NuGet `1.2.0`; GitHub Release `v1.2.0`. Confirm durable store has all bundles + checksums + provenance (+ SBOM/external provenance if 方式2). Confirm release environment / credentials available. Review partial-publish resume/abort procedures (§11.1). |
| Verification | Absence checks pass; store complete; credentials available; recovery procedures briefed |
| Evidence | Readiness checklist (value-free) |
| Stop | Any public identity already present on wrong bytes; missing durable artifacts; credentials unavailable |
| Re-run | After cleanup / restore |
| Outputs | `publicationReady=true` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

#### Step 3.5 — Pre-promotion final Go/No-Go（irreversible gate）

| Item | Content |
|------|---------|
| Phase/Step | **3.5 (IRREVERSIBLE GATE)** |
| Purpose | Last chance to stop before promotion PR |
| Preconditions | 3.1–3.4b; Phase 2 Go + consumer validation confirmed; B-456/B-EVID/B-GO cleared |
| Inputs | All prior outputs; Blockers |
| Actions | Maintainer records pre-promote APPROVE/REJECT; verify immutable refs only (no `latest`); confirm Option A tag intent; confirm exact-head + **merge-commit-only** promotion plan using `release/v1.2.0-rc` |
| Verification | Written APPROVE referencing #456 Go + `releaseCommitSha` |
| Evidence | Pre-promote decision note |
| Stop | Any doubt; blocker; Hard incomplete; consumer validation incomplete |
| Re-run | After fixes |
| Outputs | `prePromoteGo=true` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

---

### Phase 4：develop -> main promotion（exact head；merge commit only）

#### Step 4.1 — Promotion PR creation conditions

| Item | Content |
|------|---------|
| Phase/Step | 4.1 |
| Purpose | Open promote PR whose **head SHA == `releaseCommitSha`** exactly |
| Preconditions | 3.5 APPROVE |
| Inputs | `releaseCommitSha`; `release/v1.2.0-rc`; `origin/main` |
| Actions | Prefer **reuse** of immutable `release/v1.2.0-rc` (already at exact `releaseCommitSha`) as promotion head. Open PR to `main`. Do **not** include later develop commits. **This planning round: do not create.** |
| Verification | PR **head SHA == `releaseCommitSha`** (complete equality; ancestor-only insufficient) |
| Evidence | PR URL + head SHA + `releaseCommitSha` |
| Stop | Head drift; head is descendant with extra commits; head only “contains” release commit as ancestor but differs |
| Re-run | Reset head to exact `releaseCommitSha` (RC branch) |
| Outputs | `promotionPrNumber`; `promotionHeadSha` (== `releaseCommitSha`) |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

#### Step 4.2 — Merge-before gates + merge-method precondition（irreversible-adjacent）

| Item | Content |
|------|---------|
| Phase/Step | **4.2 (IRREVERSIBLE GATE adjacent)** |
| Purpose | PR checks green; **confirm merge method availability before merge** |
| Preconditions | 4.1 |
| Inputs | PR checks; repo merge settings |
| Actions | Wait for required checks; no force-merge on red. **Before merge:** confirm repository allows **merge commit**, and that **squash merge, rebase merge, and fast-forward will not be used** for this PR. If only squash/rebase/FF are available, **stop** and change settings / process — do not discover incompatibility at 4.4. Record `mergeMethodAllowed=merge`. |
| Verification | All required green; merge-method precondition recorded (`merge` available; squash/rebase/FF forbidden for this PR) |
| Evidence | Check conclusions; merge-method precondition note |
| Stop | Red CI; only squash/rebase/FF available; intent to squash/FF |
| Re-run | Fix on develop + 2.7 if needed; fix merge settings |
| Outputs | `promotionPrReady=true`; `mergeMethodAllowed=merge` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

#### Step 4.3 — Merge (merge commit only) and main SHA capture

| Item | Content |
|------|---------|
| Phase/Step | 4.3 |
| Purpose | Merge using merge commit only; capture resulting `main` tip; keep tag target = `releaseCommitSha` |
| Preconditions | 4.2 merge-method precondition pass |
| Inputs | Repo merge policy; Option A |
| Actions | Merge PR via **merge commit only**. Record `mainTipSha` and `mergeMethodUsed=merge`. Do **not** squash, rebase-merge, or fast-forward. The merge commit on `main` is **not** the tag target. |
| Verification | `main` contains `releaseCommitSha` as ancestor; release tree reachable; `mergeMethodUsed=merge` |
| Evidence | Merge tip SHA + ancestor check + merge method used |
| Stop | Squash/rebase/FF used; unexpected rewrite that loses `releaseCommitSha` from history |
| Re-run | Abort release; re-pin |
| Outputs | `mainTipSha`; `mergeMethodUsed=merge` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

#### Step 4.4 — Post-merge identity gate（irreversible gate）

| Item | Content |
|------|---------|
| Phase/Step | **4.4 (IRREVERSIBLE GATE)** |
| Purpose | Final ancestor/tag-target check (not the first place squash/rebase/FF incompatibility is discovered) |
| Preconditions | 4.3; 4.2 already confirmed merge method |
| Inputs | `releaseCommitSha`, `mainTipSha`, qualification pin |
| Actions | Ancestor check; set `tagTargetSha = releaseCommitSha` (**exact**). Reject equivalent-tree proposals. Confirm merge method was merge commit (not squash/rebase/FF). Confirm tag will **not** target `mainTipSha` unless it equals `releaseCommitSha` (it will not, after a merge commit). |
| Verification | Ancestor true; tag-target intent == #456 `sourceCommitSha` |
| Evidence | Comparison record |
| Stop | Mismatch; proposal to tag merge commit instead of `releaseCommitSha`; discovery that squash/FF was used |
| Re-run | 2.7 / abort |
| Outputs | `tagTargetSha` (== `releaseCommitSha`) |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

---

### Phase 5：Tag と Publish（SAME qualified bytes）

#### Step 5.0 — Pre-tag publication readiness reconfirm

| Item | Content |
|------|---------|
| Phase/Step | **5.0 (IRREVERSIBLE GATE adjacent)** |
| Purpose | Reconfirm 3.4b immediately before tag; lock publish + attest modes; brief partial-failure recovery |
| Preconditions | 4.4 pass; B-PUB/B-OCI-HANDOFF previously cleared with proof |
| Inputs | 3.4b checklist; `publishMethod`; `attestMode`; durable store; promote-path proof |
| Actions | Re-run absence checks; confirm promote tooling still ready for the **same** qualified OCI; confirm operators know resume/abort matrix (§11.1); confirm Release asset list includes provenance per 方式2 if selected |
| Verification | Still absent public v1.2.0 identities; store intact; method ready |
| Evidence | Reconfirm note |
| Stop | Identity collision; missing artifacts; promote tooling regression |
| Re-run | After remediation |
| Outputs | `preTagReady=true` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

#### Step 5.1 — Annotated tag `v1.2.0`（irreversible）

| Item | Content |
|------|---------|
| Phase/Step | **5.1 (IRREVERSIBLE)** |
| Purpose | Create annotated tag on **exact** `releaseCommitSha` |
| Preconditions | 5.0 pass |
| Inputs | `tagTargetSha` (== `releaseCommitSha`) |
| Actions | `git tag -a v1.2.0` on exact SHA; push tag; **planning round: do not** |
| Verification | `v1.2.0^{commit}` == `releaseCommitSha` |
| Evidence | Tag object SHA + target SHA |
| Stop | Wrong target; tag already exists on different commit; tagging `mainTipSha` merge commit by mistake |
| Re-run | Only with maintainer recovery process (not casual retag) |
| Outputs | `tagName=v1.2.0` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

#### Step 5.2 — Docker image publish（irreversible; P-OCI-PROMOTE）

| Item | Content |
|------|---------|
| Phase/Step | **5.2 (IRREVERSIBLE)** |
| Purpose | Publish GHCR multi-arch image as the **same** qualified OCI index from the #455 `build-oci` job |
| Preconditions | 5.1; `release` environment approvals; B-PUB cleared; `publishMethod` + `attestMode` recorded |
| Inputs | Qualified OCI layout from frozen `workflowRunId` (attempt 1); promote tooling |
| Actions | **P-OCI-PROMOTE:** push qualified OCI; attach `v1.2.0` and `sha-<releaseCommitSha>` to **same** index. Preserve index digest proven in 1c.2. **Do not** use current rebuild `publish-image.yml` unless replaced/extended. **P-REBUILD (discouraged):** only if final staging OCI was what #456 qualified and parity proven. For 方式1: promote attestation-inclusive graph. For 方式2: runtime index without registry attestation; do not claim attestation manifests exist. |
| Verification | Version tag digest == immutable `sha-<commit>` tag digest == Phase 1b.2 / qualification digest; labels revision == `releaseCommitSha`; OCI `org.opencontainers.image.version` == `1.2.0`; embedded binary version core == `1.2.0` |
| Evidence | Tool/workflow run ID; index + per-arch digests |
| Stop | Rebuild of unqualified bytes; digest mismatch; B-PUB path used without clearance; wrong OCI version label (`v1.2.0` with `v`) |
| Re-run | Maintainer-approved recovery; if OCI changes -> new #455 run + requal **before** retagging public version |
| Outputs | `publishedImageIdentity` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

#### Step 5.3 — NuGet publish from tag ref（irreversible）

| Item | Content |
|------|---------|
| Phase/Step | **5.3 (IRREVERSIBLE)** |
| Purpose | Publish `Amane.Mailer.Contracts` 1.2.0 |
| Preconditions | 5.1 |
| Inputs | `publish-contracts.yml` |
| Actions | Dispatch on tag; **download** published package and verify version + repository commit (+ symbols policy). **NuGet success ≠ `--skip-duplicate` alone.** |
| Verification | nuget.org lists 1.2.0; downloaded package version/commit match `releaseCommitSha`; symbols policy satisfied |
| Evidence | Package URL; workflow run; download verification note |
| Stop | Version/commit mismatch; relying only on skip-duplicate |
| Re-run | Maintainer recovery per §11.1 |
| Outputs | `nugetIdentity` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

#### Step 5.4 — Setup bundle publish（Win x64 / Linux x64 / Linux arm64）（irreversible）

| Item | Content |
|------|---------|
| Phase/Step | **5.4 (IRREVERSIBLE)** |
| Purpose | Publish three host archives by **promoting qualified bytes byte-identical** |
| Preconditions | 5.1; frozen candidate archives from same `workflowRunId` (attempt 1) |
| Inputs | Qualified zip/tar.gz + checksums; manifests (already hold final digests) |
| Actions | Attach/publish **same bytes**; **do not** rewrite `release-bundle-manifest.json`; never casually rebuild |
| Verification | Published `archiveSha256` == frozen candidate for each RID; three RIDs; embedded `imageDigest`/`ociIndexDigest` == published image digests |
| Evidence | Checksums; manifest digests |
| Stop | Any in-archive mutation; rebuild without new candidate qualification; RID missing; digest mismatch |
| Re-run | New candidate + 2.7 if rebuild required |
| Outputs | `publishedBundleIdentity` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

#### Step 5.5 — GitHub Release + checksums / provenance / external attestation artifacts（irreversible）

| Item | Content |
|------|---------|
| Phase/Step | **5.5 (IRREVERSIBLE)** |
| Purpose | Public Release with artifacts, checksums, and provenance; record digests in release record |
| Preconditions | 5.2–5.4 (or coordinated partial resume per §11.1) |
| Inputs | Digests; archives; notes; `attestMode`; `candidate-provenance.json`; optional SBOM |
| Actions | Create/update GitHub Release `v1.2.0`. **Must attach:** host archives (three RIDs), SHA256SUMS / `CANDIDATE-SHA256SUMS`, `candidate-provenance.json`. If 方式2: also attach SBOM/external provenance artifacts if produced; record their SHA-256 in the release record; state in release notes that registry attestation is **not** attached for v1.2.0. If 方式1: record attestation manifest digests as part of the promoted graph (no false “missing attestation” claim). No secrets. |
| Verification | Assets present; checksums match; notes include migration statement per B-MIG and attestMode disclosure |
| Evidence | Release URL; asset list; sums; provenance digests in release record |
| Stop | Missing required asset; checksum mismatch; 方式2 without provenance disclosure |
| Re-run | Edit release assets carefully per recovery matrix |
| Outputs | `githubReleaseUrl` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

---

### Phase 6：公開後の最小 smoke

#### Step 6.1 — Public runtime image pull + minimal start + embedded version

| Item | Content |
|------|---------|
| Phase/Step | 6.1 |
| Purpose | Prove public GHCR image runs and **embedded binary version core == `1.2.0`**; OCI label == `1.2.0` |
| Preconditions | 5.2 complete |
| Inputs | `ghcr.io/kooiei-in4a/amane-mailer:v1.2.0` by digest; `scripts/release-smoke.ps1` / runbook |
| Actions | Clean-state pull; minimal health/ready/send smoke for amd64 and arm64 as in v1.1.0 practice; probe embedded version (not only OCI labels / `--help` text if labels can drift); confirm OCI label `org.opencontainers.image.version=1.2.0` |
| Verification | Smoke PASS matrix; embedded version core == `1.2.0`; OCI label matches canonical choice |
| Evidence | Value-free smoke table in release record |
| Stop | Smoke FAIL; version core mismatch; unexpected `v1.2.0` OCI label when plan required `1.2.0` |
| Re-run | Diagnose; may require yank + new candidate path (class C after public tags is forbidden as a planned outcome) |
| Outputs | `publicImageSmokePass=true` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

#### Step 6.2 — Public setup bundle fetch / checksum / extract / `--help`

| Item | Content |
|------|---------|
| Phase/Step | 6.2 |
| Purpose | Prove public bundles usable |
| Preconditions | 5.4–5.5 |
| Inputs | Release assets; published checksums |
| Actions | Download each RID; verify checksum; extract; `--help` / assistant help; Linux exec bit without chmod; startup possibility minimal check per smoke script |
| Verification | All RID smokes PASS; checksums == frozen candidate |
| Evidence | Per-RID smoke summary |
| Stop | Checksum fail; exec fail |
| Re-run | Fix assets via recovery matrix |
| Outputs | `publicBundleSmokePass=true` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

#### Step 6.3 — Public artifact secret/PII/private path scan

| Item | Content |
|------|---------|
| Phase/Step | 6.3 |
| Purpose | Scans succeed on **published** bytes |
| Preconditions | 6.2 |
| Inputs | Extracted public archives; scan script |
| Actions | Re-scan published artifacts |
| Verification | PASS |
| Evidence | Scan summary |
| Stop | FAIL |
| Re-run | Yank/replace per maintainer process |
| Outputs | `publicScanPass=true` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

#### Step 6.4 — candidate vs published identity compare

| Item | Content |
|------|---------|
| Phase/Step | 6.4 |
| Purpose | Determine equality class; expected class **A** |
| Preconditions | 6.1–6.3; frozenCandidateIdentity |
| Inputs | Candidate vs published checksums/digests/payloadTree; image INDEX digests |
| Actions | Classify: **(A)** byte-identical archives + same qualified OCI graph; **(C)** mismatch. Planned class **B** (re-attest INDEX change with same archives / manifest rewrite) remains **removed**. |
| Verification | Classification recorded with hashes; prefer A |
| Evidence | Compare table |
| Stop | Unclassified mismatch; discovering planned B/C after public tags |
| Re-run | 6.5 |
| Outputs | `identityClass=A\|C` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

#### Step 6.5 — Conditional requal on mismatch only

| Item | Content |
|------|---------|
| Phase/Step | 6.5 |
| Purpose | Re-run only impacted #456 scenarios when needed; skip when safe |
| Preconditions | 6.4 |
| Inputs | identityClass; Phase 2.7 matrix |
| Actions | **If A (candidate==published):** skip ACS Staging, Release OV, Admin access E2E, rollback/fault, full fresh install, Admin DB partial-failure, non-interactive Admin rejection. **If C:** stop release completion; run impacted requal / new candidate as matrix dictates; do not set `implemented`. |
| Verification | Skip/requal decision cites matrix |
| Evidence | Decision record |
| Stop | Proceeding to `implemented` under class C |
| Re-run | Until A |
| Outputs | `postPublishQualificationState` |
| Owner | #458 / #456 |
| This-round status | 計画のみ / 未実行 |

---

### Phase 7：Release 完了記録（completion PR on main）

#### Step 7.1 — Branch from latest main for completion PR

| Item | Content |
|------|---------|
| Phase/Step | 7.1 |
| Purpose | Start completion documentation/status PR from **latest main SHA** after smoke |
| Preconditions | Phase 6 pass (class A) |
| Inputs | `origin/main` tip after promotion; public identities; smoke results |
| Actions | `git fetch`; branch from latest `main` SHA (not from develop tip alone) |
| Verification | Branch base == current main tip intended for completion |
| Evidence | Base SHA record |
| Stop | Branching from develop for `implemented` status |
| Re-run | Rebase onto latest main if main moved |
| Outputs | `completionBranch` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

#### Step 7.2 — Fill release record + set `implemented` in ONE PR

| Item | Content |
|------|---------|
| Phase/Step | 7.2 |
| Purpose | Durable value-free public evidence + status flip in a single main-targeted PR |
| Preconditions | 7.1 |
| Inputs | Tag, digests, NuGet, bundles, smoke, decisions, B-MIG statement, D-ATTEST / attestMode, provenance digests, plan binding SHAs |
| Actions | Update `docs/releases/v1.2.0.md` with Source/Docker/NuGet/smoke; record Contracts/OpenAPI decision; record **migration statement** (`dbMigrationStatement`); record OCI label `1.2.0` and attestMode disclosure; setup vs upgrade notes; Admin access profiles; Admin disabled without Production HTTPS path; non-interactive Admin bootstrap not performed; Release OV is product qualification not tenant verification; **#307 not in v1.2.0**. In the **same** PR: set `easy-setup` -> `implemented` and refresh evidence notes/links. |
| Verification | All AC disclosure bullets present; no PII; status not set in earlier version-prep history |
| Evidence | PR diff |
| Stop | Missing required disclosure; splitting status to version-prep PR |
| Re-run | Edit PR |
| Outputs | Completion PR ready for CI |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

#### Step 7.3 — CI / checker / review / merge to main（irreversible）

| Item | Content |
|------|---------|
| Phase/Step | **7.3 (IRREVERSIBLE GATE)** |
| Purpose | Land completion PR on main; capture `completionMainSha` |
| Preconditions | 7.2; CI + `node scripts/check-implementation-status.mjs`; review |
| Inputs | Completion PR |
| Actions | Merge to main; record `completionMainSha` |
| Verification | Manifest validates; `easy-setup=implemented` on main |
| Evidence | Merge SHA; checker output |
| Stop | Any Phase 6 failure outstanding; checker fail |
| Re-run | Keep `partial` until fixed |
| Outputs | `completionMainSha`; `easy-setup=implemented` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

---

### Phase 8：Post-promote sync

#### Step 8.1 — Sync completionMainSha -> develop

| Item | Content |
|------|---------|
| Phase/Step | 8.1 |
| Purpose | Bring completion PR commits from main back to develop |
| Preconditions | 7.3 merged; `completionMainSha` known |
| Inputs | `completionMainSha`, develop tip |
| Actions | Open sync PR `completionMainSha` (main) -> develop; resolve conflicts carefully; **planning round: do not** |
| Verification | Intended diffs only; versions remain consistent; `implemented` present on develop after merge |
| Evidence | Sync PR URL |
| Stop | Conflict that drops release evidence |
| Re-run | Manual resolve |
| Outputs | `syncPrNumber` |
| Owner | #458 |
| This-round status | 計画のみ / 未実行 |

#### Step 8.2 — Parent #445 completion check（irreversible toward close）

| Item | Content |
|------|---------|
| Phase/Step | **8.2 (IRREVERSIBLE toward parent close)** |
| Purpose | Confirm parent tracking can close |
| Preconditions | #456 closed with Go; #458 AC done; sync merged |
| Inputs | #445 completion criteria; #456/#458 states |
| Actions | Verify children done; close #445 only in authorized execution round (**not this round**) |
| Verification | No open blocking child |
| Evidence | Checklist |
| Stop | #456 or #458 incomplete |
| Re-run | After remaining work |
| Outputs | Parent ready-to-close flag |
| Owner | #445 / #458 |
| This-round status | 計画のみ / 未実行 |

---

## 9. Phase 2.7 再掲（requalification matrix summary）

See Step 2.7 table. Normative rules:

- Hard authority never replaced by #458 shortcuts.
- Single #455 workflow run freezes OCI + archives + provenance (`ociHandoffMode=SINGLE_WF_RUN_OPTION_A`).
- Rebuild of host archives => new candidate (new full #455 run).
- Final OCI digest change after archive generation => new full #455 run + re-qual **before** public version tags.
- Archive-internal manifest mutation at publish is **forbidden**.
- Option B separate prebuilt OCI is **out-of-policy** for current #455.
- Promote-path digest-preserving proof required before #456.
- Heavy #456 scenarios skipped **only** when candidate==published (class A).
- Planned class B (re-attest INDEX / promote-map) remains **removed**.
- Partial job re-run / attempt mix is **forbidden**; v1.2.0 accepts attempt==1 only.
- RC branch tip move after dispatch => abandon + new full #455.

---

## 10. Acceptance criteria 対応表（Issue #458 全件）

| 受入条件（Issue #458） | Phase/Step | 確認方法 | 証拠 | 停止条件 | Rev.8 note |
|------------------------|------------|----------|------|----------|------------|
| #456必須シナリオ表のHard項目が全件PASSでGo判定されている | 2.2, 2.4, 3.5 | Map each Hard row to active PASS; confirm Go objects | Mapping + Go digests | Any Hard not PASS or Go missing | Unchanged; ACTIVE replay strengthens integrity |
| Hard gateを別の短縮リストから判定せず、#456必須シナリオ表を正本として確認している | 2.1–2.2, §3, §7 | Review plan/execution uses Issue table only | Explicit sole-authority statement in evidence | Alternate Hard list detected | `CV-*` are sealed-package integrity, not a second Hard list |
| Hard項目未実施またはFAILを残存リスク記録だけで免除していない | 2.2, 2.4, §7 | Reject residual-only Hard waiver | Exception log shows no Hard waiver | Hard waived via residual risk | Unchanged |
| Conditional例外には理由、代替確認、residual risk、承認者、影響範囲がある | 2.4 | Schema check each Conditional | Exception summaries | Incomplete Conditional | CV-ACTIVE-02 |
| Informational未確認項目が明記されている | 2.4 | List present in Go package / release notes as needed | Informational list | Hidden unchecked Informational | CV-SCENARIO-01 |
| #456のqualification evidenceがtag targetと同じrelease commitに対応する | 1.6, 1.6.1, 2.5, 4.1, 4.4, 5.1 | Exact SHA compare pin vs tag target; promotion head equality; RC ref | SHA equality record | Mismatch | **B-R3-01** RC branch + **M-R2-01** exact head |
| tag、tag target SHA、Docker image、NuGet、setup bundleが同一v1.2.0 release commitへ対応する | 5.1–5.4, 7.2 | Cross-check labels/SourceLink/manifest sourceCommitSha | Release record identities | Divergent commit identities | Option A retained; merge commit ≠ tag |
| Docker version tagとimmutable SHA tagのdigestが一致する | 5.2, 6.4 | imagetools inspect both tags | Digest pair | Mismatch | Same qualified index from single #455 run |
| setup release bundle manifestのimage digestが公開imageと一致する | 1b.2, 1c.1, 1c.2, 3.3, 5.4, 6.4 | Compare manifest imageDigest to published INDEX; digest finalized in same-run `build-oci` before packaging; promote preserves digest | Compare table + promote-path proof | Mismatch | Single-run + promote proof retained |
| setup bundle checksumがGitHub Releaseへ記録される | 5.5, 6.2 | Release assets include sums; verify download | SHA256SUMS on Release | Missing/mismatched sums | Plus `candidate-provenance.json` required |
| Windows x64 / Linux x64 / Linux arm64 artifactが公開される | 5.4, 5.5, 6.2 | Three assets present | Asset list | Missing RID | Unchanged |
| public runtime imageをpullして最小smokeが成功する | 6.1 | release-smoke + embedded version core == `1.2.0` + OCI label `1.2.0` | Smoke table | Smoke FAIL / version mismatch | Label canonicalization retained |
| public setup bundleを取得・展開し、checksum、`--help`、起動可能性を確認する | 6.2 | smoke-setup-release-bundle | Per-RID summaries | FAIL | Unchanged |
| candidateとpublished artifactの内容一致を確認する | 6.4 | Hash/digest compare (expect class A) | Compare table | Class C | Class B promote-map path removed |
| 一致する場合、real ACS / Release Production OV / Admin access E2E / rollbackを重複実行しない | 6.5, §7 | Skip list applied only for class A | Skip decision record | Unsafe skip on class C | Unchanged |
| 不一致がある場合、影響範囲を明示して再実行する | 2.7, 6.5 | Matrix row + scoped #456 | Requal record | Publish completion on class C | Unchanged intent |
| secret / PII / private path scanが公開artifactでも成功する | 3.4, 6.3 | scan scripts | Scan summaries | Scan FAIL | Unchanged |
| release時に`latest`や任意ref / versionの組合せを使用しない | 0.3, 1b.0, 3.5, 5.x | Ref audit; RC branch only | Ref audit note | `latest`/mixed versions / raw-SHA dispatch used | **B-DISPATCH** |
| OpenAPI / Contractsの変更内容または変更なし判断をrelease recordへ明記する | 1.3, 7.2 | Decision text in record | Release record subsection | Missing decision | Unchanged |
| DB migration 012/013 を INCLUDE したことを具体文で明記する（`none` / UNDECIDED 禁止） | 0.2, 1.4, 7.2 | Inventory + concrete `dbMigrationStatement`; B-MIG-SCOPE/PIN/BIND | Release notes sentence | Migration present undocumented **or** false `none` | **B-01** phase-aware; success outputs never UNDECIDED |
| migration inventory 凍結 + G456-42～44 全 required variant が active PASS（Conditional 例外は gateClass=Conditional の行にのみ可） | 1.4, 1b.0 PIN, 2.1–2.2 | Exact inventory on `releaseCommitSha`; map Hard migration rows -> active PASS only | PIN proof + Hard mapping | Hard migration missing/FAIL/EXCEPTION; PIN mismatch | Issue AC Conditional-exception language removed |
| setupとupgradeの違いをrelease notesへ明記する | 7.2 | Notes review | Release notes | Missing | Includes migration upgrade notes if INCLUDE |
| Local Development / Production HTTPS Admin access profileをrelease notesまたはsetup guideへ反映している | 7.2 | Docs review | Notes/guide pointers | Missing profiles | Unchanged |
| Production HTTPS経路がない環境ではAdmin disabledを維持することを明記する | 7.2 | Notes review | Explicit sentence | Missing | Unchanged |
| non-interactiveではAdmin bootstrapを行わないことを明記する | 7.2 | Notes review | Explicit sentence | Missing | Unchanged |
| Release Production operational verificationは製品artifactのqualificationであり、利用者環境のverificationではないことを必要に応じて明記する | 7.2 | Notes review | Boundary sentence | Misleading tenant “verified” claim | Unchanged |
| #307がv1.2.0に含まれずv1.5.0以降候補であることを必要に応じて明記する | 7.2, §6 | Notes review | Explicit non-goal | #307 silently included | Unchanged |
| `docs/implementation-status.json`が`implemented`になりevidenceが更新される | 7.2–7.3 | JSON status + notes on **main** completion PR | Diff + checker | `implemented` before public smoke or in version-prep PR | Completion PR on main retained |
| main / developがpost-promote sync後に意図どおり整合する | 8.1–8.2 | Sync `completionMainSha` -> develop | Sync PR | Divergent unintended drift | Unchanged |

---

## 11. リスクと停止条件（Risks / stops）

| Risk | Severity | Stop / mitigation |
|------|----------|-------------------|
| Circular “#456 before any #458” reading recreates B-01 (Rev.1) | Blocker | D-SEQ ACK; Rev.8 phase-aware order (SCOPE -> Phase 1 -> PIN -> BIND) |
| Circular “full B-MIG before Phase 1” vs conds 8–9 needing pin/binding (Agent B B-01) | Blocker | **B-MIG-SCOPE / PIN / BIND** phase-aware split; §5 / §6.1 / Steps 0.4 / 1.6 / 1b.0 / 2.1 |
| Treating 1b and 1c as two independent OCI builds / Option B prebuilt OCI | Blocker | **B-R2-01**; `ociHandoffMode=SINGLE_WF_RUN_OPTION_A` only |
| Dispatching #455 @ raw SHA / missing workflow on main | Blocker | **B-DISPATCH** / §7.6; Steps 1.6.1 + 1b.0 |
| Partial job re-run / attempt mix on #455 | Blocker | **M-R3-02** / §7.7; abandon + new dispatch |
| Starting #456 on un-promotable OCI | Blocker | **B-PUB/B-OCI-HANDOFF** before 1b.2/1c/#456; Step 1c.2 proof |
| Publishing without #456 Go | Blocker | B-GO / Step 3.5 (not Phase 1) |
| Alternate Hard checklist drift | Blocker | Forbidden by §3 / §7; `CV-*` ≠ Hard |
| Consumer validator incomplete / missing (incl ACTIVE) | Blocker | **B-VAL** before 2.3+; **M-R3-03** |
| Hard waiver via residual risk | Blocker | Forbidden |
| Rebuild mistaken for same candidate | Major | Treat as new candidate; 2.7 |
| Using current `publish-image.yml` rebuild-as-publish | Blocker | **B-PUB**; prefer P-OCI-PROMOTE |
| Mutating archive-internal manifest at publish | Blocker | Forbidden |
| Squash/rebase/FF promotion merge | Blocker | **m-R3-01**; Step 4.2 precondition (`merge` only) |
| Promotion head ≠ `releaseCommitSha` | Blocker | Step 4.1 exact equality; reuse RC branch |
| False `dbMigrationStatement=none` / UNDECIDED success output | Blocker | **B-MIG** / §6.1 / §7.5 |
| Attestation mode undecided at build time | Blocker | **D-ATTEST** before 1b.2 |
| Plan text drift vs reviewed APPROVE | Blocker | **M-R3-01** Step 0.0; stop if committed ≠ reviewed |
| Premature `implemented` in version-prep | Major | Steps 1.5 / 7.2–7.3 |
| Contract content drift unnoticed | Major | Step 1.3 + minor gate |
| Secret/PII in release evidence | Blocker | Scans 3.4/6.3; redaction stop |
| Tag on squash/merge commit != releaseCommitSha | Blocker | Steps 4.2–4.4 + 5.1 Option A |
| OCI label `v1.2.0` vs expected `1.2.0` | Major | Canonicalization retained |
| Using `latest` or mixed versions | Blocker | Ref audit |
| Partial publish without recovery plan | Major | Steps 3.4b / 5.0 + §11.1 |
| NuGet “success” via `--skip-duplicate` only | Major | Download+verify |
| Local dirty tree / behind develop | Major | Step 0.5 (dirty UNCONFIRMED this round) |
| Shell unavailable in planner environment | Minor (plan round) | Prefer Write/HTTP; execution round needs working git/ci |

### 11.1 Partial publish resume / abort

| Scenario | Resume | Abort / recovery |
|----------|--------|------------------|
| Docker OK / NuGet fail | Keep image tags only if digests match qualified OCI; fix NuGet; download-verify package before continuing | If wrong package identity published, maintainer yank/unlist process; do not proceed to `implemented` |
| Docker+NuGet OK / bundle fail | Re-publish **same** qualified archive bytes; do not rebuild; checksum must match freeze | If wrong bytes uploaded, replace assets carefully; never mutate internals to “fix” digests |
| Artifacts OK / GitHub Release fail | Create/update Release with correct assets + sums + provenance | Draft cleanup; avoid duplicate conflicting assets |
| Tag created / publish aborted mid-flight | Use 3.4b-style inventory of what exists; resume only remaining ops with same identities | If wrong tag target, maintainer recovery (not casual retag) |
| Any class C after public version tags | **Stop completion**; do not claim AC pass | Yank / new candidate / re-qual path — forbidden as a planned outcome |

**Irreversible gates (must have explicit APPROVE immediately before):** Step 0.0 plan durability (Rev.8 re-run after APPROVE), D-SEQ ACK, D-ATTEST ACK, **B-MIG-SCOPE**, **B-MIG-PIN**, **B-MIG-BIND**, B-DISPATCH clearance, **3.5**, **4.2 merge-method check**, **4.4**, **5.0**, **5.1–5.5**, **7.3**, **8.2**.

---

## 12. 明示的に未実施（Explicitly not done this round）

This planning round (**Rev.8 authorship**) **did not**:

- Step 0.0 durability for **this Rev.8** text (Rev.4 Step 0.0 is COMPLETE @ `3f2b640…`; Rev.8 needs new durability after APPROVE+merge)
- Clear **B-MIG-SCOPE** / **B-MIG-PIN** / **B-MIG-BIND** (umbrella B-MIG remains SET)
- Claim Phase 0 complete or authorize Phase 1
- Version prep / version number edits
- CHANGELOG or `docs/releases/v1.2.0.md` product updates (except this plan file)
- release-bundle-manifest generation
- Docker / NuGet / setup bundle build or publish
- develop -> main promotion PR create/merge
- release-infra bootstrap PR to `main`
- `release/v1.2.0-rc` branch creation
- annotated tag creation
- GitHub Release create/update
- public artifact smoke
- main -> develop sync
- commit / push of this Rev.8 authorship
- Issue comment / close (Issue bodies already updated on GitHub separately where noted)
- PR review submission
- Parent #445 close
- #456 qualification execution / Go decision
- Consumer validator implementation (B-VAL remains a planning blocker)
- Any alternate Hard list
- Clearing B-PUB by implementing promote workflow
- Clearing B-DISPATCH by landing workflow on `main`
- Executing promote-path proof

Only this file path is in scope for plan authorship: `docs/agent-workflows/issue-458-release-execution-plan.md`.

**This round still does not execute release ops; only plan doc revision. Status remains plan-only. Phase 1 still forbidden.**

---

## 13. セルフレビュー（1–17）— Rev.8

| # | Check | Result |
|---|-------|--------|
| 1 | Issue #458全受入条件が計画に対応 | **Pass** (§10; INCLUDE AC + active PASS for migration Hard; phase-aware B-MIG + PIN/BIND digests) |
| 2 | #456必須シナリオ表を唯一のHard gate正本 | **Pass** (§3, 2.2) |
| 3 | 独自Hard一覧を作っていない | **Pass** (`CV-*` = sealed-package integrity under B-VAL; ACTIVE/SCENARIO + CV-MIG-PIN-01/02; not a second Hard list) |
| 4 | Hard未実施/FAILを免除していない | **Pass** (migration Hard = active PASS only) |
| 5 | qualification evidenceとrelease commitの同一性を確認できる | **Pass** (1.6, 1.6.1, 2.5, 4.1 exact head via RC, 4.4, 5.1 Option A; CV-MIG-PIN-02 tree equality) |
| 6 | candidateとpublished差異判定が明確 | **Pass** (6.4–6.5; class B removed) |
| 7 | artifact一致時にqualificationを不要重複実行しない | **Pass** (6.5 class A only) |
| 8 | 不一致時の再qualification範囲を決定できる | **Pass** (2.7; attempt unity + RC tip move rows) |
| 9 | irreversible操作の直前に停止gateがある | **Pass** (0.0, D-SEQ, D-ATTEST, B-MIG-SCOPE/PIN/BIND, B-DISPATCH, 3.5, 4.2, 4.4, 5.0, 5.1–5.5, 7.3, 8.2) |
| 10 | tag/Docker/NuGet/bundleが同一commitへ対応 | **Pass** (Phase 5 + Option A + exact head; merge commit ≠ tag) |
| 11 | `latest`または任意refに依存しない | **Pass** (RC branch + B-DISPATCH; no raw-SHA dispatch) |
| 12 | minor release gate超過を混入させない | **Pass** (§6, 0.2, B-MIG-SCOPE/PIN/BIND) |
| 13 | secret/PII/raw log/private pathを証拠へ残さない | **Pass** |
| 14 | `implementation-status`更新順序が正しい | **Pass** (1.5 -> Phase 7 completion PR on main after smoke) |
| 15 | post-promote syncまで計画 | **Pass** (Phase 8) |
| 16 | 今回禁止操作を実行していない | **Pass** (§12) |
| 17 | 現在の事実と将来の計画を混同していない | **Pass** (§1; Rev.4 Step 0.0 COMPLETE acknowledged; Rev.8 text not yet durable; Phase 0 not complete; Phase 1 forbidden) |

### 13.1 Agent B R2 finding closure table（retained）

| Finding | Severity | Closed in Rev.3? | Where |
|---------|----------|------------------|-------|
| **B-R2-01** | Blocker | **Yes** | §0 (Rev.3), §2.1, §5 B-PUB/B-OCI-HANDOFF, §7.1/7.3, Phase 1b/1c (same run), Step 1c.2 promote-path proof |
| **M-R2-01** | Major | **Yes** (merge/FF tightened further in Rev.4 to merge-only) | §7.2, Steps 4.1–4.4 |
| **M-R2-02** | Major | **Yes** | §3, §5 B-VAL, Step 2.3 `CV-*` + report fields |
| **M-R2-03** | Major | **Yes** (phase-aware in Rev.5; PIN/BIND digests in Rev.6) | §6.1 nine INCLUDE conditions + SCOPE/PIN/BIND; Steps 0.2/1.4/1b.0/2.1/2.3 |
| **M-R2-04** | Major | **Yes** | §4.1/§7.4 D-ATTEST; OCI label `1.2.0`; Step 5.5 attachments |
| **m-R2-01** | Minor | **Yes** | §7.5; success outputs — no UNDECIDED on success |

### 13.2 Agent B R3 finding closure table（retained）

| Finding | Severity | Closed in Rev.4? | Where |
|---------|----------|------------------|-------|
| **B-R3-01** | Blocker | **Yes** | §0, §2 (dispatch facts), §5 **B-DISPATCH**, §7.6, Steps **1.6.1** / **1b.0** / 1b.2, Appendix C `--ref release/v1.2.0-rc` |
| **M-R3-01** | Major | **Yes** | §0, Step **0.0**, §3 binding-equivalent tracking, Next steps |
| **M-R3-02** | Major | **Yes** | §0, §7.7, Steps 1b.2 / 1c.1 / 2.1, §7.5 attempt enums, 2.7 matrix rows |
| **M-R3-03** | Major | **Yes** | Step 2.3 **CV-ACTIVE-01/02** + **CV-SCENARIO-01** + report fields |
| **m-R3-01** | Minor | **Yes** | §7.2, Steps 4.2–4.3 (`mergeMethodAllowed/Used=merge`); FF out-of-policy |

### 13.3 Agent B B-01 (Rev.5) finding closure table（retained）

| Finding | Severity | Closed in Rev.5? | Where |
|---------|----------|------------------|-------|
| **B-01** (B-MIG vs Phase 1 circularity) | Blocker | **Addressed in plan text** (phase-aware order retained in Rev.6/Rev.7) | §0, §5, §6.1, §7.1, Steps 0.2 / 0.4 / 1.4 / 1.6 / 1b.0 / 2.1, Next steps |

### 13.4 Agent B M-R10-01 (Rev.6) finding closure table（historical）

| Finding | Severity | Closed in Rev.6? | Where |
|---------|----------|------------------|-------|
| **M-R10-01** (PIN digests -> binding + consumer equality) | Major | **Yes in Rev.6 plan text** (algorithms superseded by Rev.7 M-R11-01) | §5 B-MIG-PIN/BIND/B-VAL, §6.1, Steps 1b.0 / 2.1 / 2.3 **CV-MIG-PIN-01/02**; sister was #456 Rev.11 |

### 13.5 Agent B M-R11 (Rev.7) finding closure table

| Finding | Severity | Closed in Rev.7? | Where |
|---------|----------|------------------|-------|
| **M-R11-01** (PIN digest canonicalization / remove `evidenceDigestSha256` circularity) | Major | **Addressed in plan text — needs Agent B re-review** | §0, §6.1 normative RFC8785-JCS algorithms (must match #456 Rev.12), Steps 1b.0 / 2.1 / 2.3, Appendix B |
| **M-R11-02** (CV-MIG-PIN-01 must recompute full inventory from releaseCommitSha tree) | Major | **Addressed in plan text — needs Agent B re-review** | Step 2.3 **CV-MIG-PIN-01** procedure (enumerate ALL migrations; assert post-011 == [012,013]; recompute digest; equality vs PIN + binding + G456-42/43/44); **CV-MIG-PIN-02** retained |
| **m-R11-01** (local absolute path in exploration facts) | Minor | **Yes** | §2 Worktree dirty/clean = value-free `LOCAL_SHELL_TEMP_PATH_ACCESS_DENIED` |

**Self-review verdict:** **Pass for authorship completeness of the M-R11 sync** (17/17 checks as plan-text). **Not** an execution APPROVE. **Rev.7 needs independent Agent B re-review** (APPROVE / REVISE). Residual: **B-MIG-SCOPE/PIN/BIND SET** (umbrella B-MIG SET); **B-DISPATCH**, **B-PUB/B-OCI-HANDOFF**, **B-VAL** (must cover CV-MIG-PIN-* with tree recompute), plus later **B-RC** / **B-456** / **B-EVID** / **B-GO**. Rev.4 Step 0.0 outputs remain valid for the **Rev.4** durable file only (`3f2b640…`); **this Rev.8 plan text is not yet durable** until APPROVE+merge / new plan-only durability. Do **not** claim Phase 0 complete. **Phase 1 still forbidden.**

---

## 14. Next steps + Agent B re-review note

1. Independent **Agent B** re-review (APPROVE / REVISE) against Issue #458 + this **Rev.8** document + #456 **Rev.12** (sister PIN digest canonicalization) + ADR 0021 + current `generate-setup-release-candidate.yml`. Agent B must not modify the repo. Focus: **M-R12-01** field path (`migrationPinWithoutDigest.inventoryDigestSha256`); confirm prior **M-R11-01** RFC8785-JCS algorithms (must match sister), **M-R11-02** CV-MIG-PIN-01 tree recompute, **m-R11-01** path scrub; confirm B-01 phase-aware order retained (SCOPE -> Phase 1 -> PIN before #455 -> BIND before #456).
2. After Agent B **APPROVE**: execute **Step 0.0 for Rev.8** (plan-only PR/commit of APPROVED Rev.8 text -> merge to develop -> fix new `issue458PlanCommitSha` / `issue458PlanFileSha256` -> refresh `baseDevelopSha`). Rev.4 Step 0.0 COMPLETE @ `3f2b640…` remains historical fact for that file revision. **This authorship round still does not commit.** Rev.7 is **not durable** until APPROVE+merge.
3. Maintainer decisions already recorded: **D-SEQ** ACK, **D-ATTEST**=`EXTERNAL_PROVENANCE`, **B-MIG** decision=INCLUDE — keep durable evidence; do not re-litigate unless changed.
4. Clear **B-MIG-SCOPE** (conds 1–7 + frozen filenames + no-extra policy) before any Phase 1 start.
5. Clear **B-DISPATCH** via workflow-only / release-infra bootstrap PR to `main` (record `infraBootstrapMainSha`) before any #455 dispatch.
6. Clear **B-PUB/B-OCI-HANDOFF** by implementing/adopting digest-preserving P-OCI-PROMOTE tooling **and** recording proof readiness before the #455 run.
7. Clear **B-VAL** by landing a version-pinned `#456 consumer validator` implementing `CV-*` including ACTIVE/SCENARIO **and CV-MIG-PIN-01/02** (with tree recompute) on the INCLUDE path (or an explicitly accepted enumerated equivalent for sealed-package integrity).
8. Re-verify local worktree clean + fast-forward `develop` to `origin/develop` (tip at execution time).
9. Only after Agent B APPROVE **and** Rev.8 Step 0.0 **and** **B-MIG-SCOPE cleared** + D-SEQ/D-ATTEST recorded + **explicit maintainer authorization**: begin Phase 1 version prep (**B-MIG-PIN/BIND and B-456/B-EVID/B-GO not required to start Phase 1**). **Phase 1 forbidden until then.**
10. After version-prep merge: freeze SHA (**B-RC**), clear **B-MIG-PIN** (emit `migrationPinWithoutDigest` + digests per §6.1), create `release/v1.2.0-rc`, clear B-DISPATCH + B-PUB/B-OCI-HANDOFF, single #455 run `--ref release/v1.2.0-rc` (**attempt 1 only**), promote-path proof (1c.2), clear **B-MIG-BIND** (binding carries PIN digests; refuse without PIN), then #456, then #458 consumer validation (B-VAL + CV-ACTIVE + **CV-MIG-PIN-***), then promote (**merge commit only**) / tag on `releaseCommitSha` / publish.
11. Keep `easy-setup` at `partial` until Phase 6 public smoke + Phase 7 completion PR on main.

**Canonical next-step order:**

```text
Agent B APPROVE (Rev.8)
  -> Step 0.0 durability for Rev.8
  -> Clear B-MIG-SCOPE
  -> Phase 1 version prep (explicit auth)
  -> Clear B-MIG-PIN (migrationPinWithoutDigest + digests)
  -> #455 (after B-DISPATCH / B-PUB / D-ATTEST)
  -> Clear B-MIG-BIND (binding carries PIN digests)
  -> #456 ...
  -> CV-* incl CV-MIG-PIN-01/02
```

**No release execution in this round. Phase 1 still forbidden. Rev.8 not durable until APPROVE+merge.**

---

## Appendix A — Workflow / artifact quick reference

| Artifact | Owner path |
|----------|------------|
| Qualification plan | `docs/agent-workflows/issue-456-release-qualification-plan.md` (Rev.12 sister; PIN digest canonicalization + phase-aware B-MIG) |
| This execution plan | `docs/agent-workflows/issue-458-release-execution-plan.md` (**Rev.8**) |
| Release record template | `docs/releases/v1.1.0.md` -> future `v1.2.0.md` |
| Bundle runbook | `docs/ops/setup-release-bundle.md` / `.en.md` |
| Image publish (current; rebuild) | `.github/workflows/publish-image.yml` — **not** promote-capable for v1.2.0 until extended (**B-PUB**) |
| Contracts publish | `.github/workflows/publish-contracts.yml` |
| RC generate (Option A) | `.github/workflows/generate-setup-release-candidate.yml` — must exist on **`main`** for dispatch (**B-DISPATCH**); jobs: `build-oci` (=1b.2), `package-*` + `assemble-handoff` (=1c.1); dispatch `--ref release/v1.2.0-rc` |
| Candidate OCI script | `scripts/build-candidate-oci-image.sh` (`MAILER_VERSION` major.minor.patch; OCI label without `v`) |
| Status manifest | `docs/implementation-status.json` (`easy-setup`) |
| Migrations (develop) | `012_provider_event_inbox_details.sql`, `013_provider_queue_dead_letters.sql` |

## Appendix B — Glossary

| Term | Meaning |
|------|---------|
| Hard | Missing/FAIL => No-Go; no alternate-only PASS |
| Conditional | Go only with reason/alternate/residual/approver/scope |
| Informational | Does not block release if listed |
| Promote qualified bytes | Publish the exact archives #456 smoked (byte-identical) |
| P-OCI-PROMOTE | Push the qualified OCI layout; tag the same index |
| P-REBUILD | Discouraged; final staging OCI must be what #456 qualifies |
| Rebuild (archives) | Always a new candidate (new full #455 run) |
| Option A (OCI handoff) | Single #455 workflow run freezes OCI + packages + provenance |
| Option B (OCI handoff) | Separate prebuilt OCI — **out-of-policy** for current #455 |
| Option A (tag) | Tag exact `releaseCommitSha`; main must contain it as ancestor |
| D-SEQ | Gate 3C clarification: prep/candidate precede #456; promote/tag/publish follow #456 |
| D-ATTEST | 方式1 REGISTRY_ATTEST vs 方式2 EXTERNAL_PROVENANCE (v1.2.0 decided EXTERNAL_PROVENANCE) |
| B-MIG | Umbrella INCLUDE/EXCLUDE + nine INCLUDE conditions; clearance phased via SCOPE/PIN/BIND |
| B-MIG-SCOPE | Conds 1-7 + frozen filenames + no-extra policy; before Phase 1 |
| B-MIG-PIN | Cond 8 normative PIN outputs on releaseCommitSha (`migrationPinWithoutDigest` + digests; RFC8785-JCS algorithms matching #456 Rev.12); after 1.6, before #455 / 1b.2; FAIL on extra migrations / digest mismatch / non-canonical algorithm |
| B-MIG-BIND | Cond 9 new binding/run carrying PIN digests (`migrationPinDigestSha256`, `migrationInventoryDigestSha256`, `migrationFileDigests[]`); refuse without PIN; before #456 start |
| B-DISPATCH / B-RC-REF | Workflow on `main` + immutable `release/v1.2.0-rc` dispatch at exact SHA |
| B-OCI-HANDOFF | Promote-capable tooling + digest-preserving proof before 1b.2/1c/#456 |
| B-VAL | Version-pinned consumer validator implementing `CV-*` (incl CV-MIG-PIN-01/02 on INCLUDE) |
| `CV-*` | #458 sealed-package integrity predicates (not a second Hard gate) |
| CV-MIG-PIN-01 | Recompute full inventory digest from releaseCommitSha tree; equality vs `migrationPinWithoutDigest.inventoryDigestSha256` + binding + G456-42/43/44 (sealed integrity, not second Hard list) |
| CV-MIG-PIN-02 | Per-file SHA-256/blob SHA == releaseCommitSha tree and binding.migrationFileDigests |
| Attempt unity | v1.2.0 accepts only workflow/job attempt == 1; no partial re-runs |
| Release OV | Maintainer product qualification send — not tenant verification |

## Appendix C — Normative #455 job mapping (Rev.8)

```text
# Preconditions (B-DISPATCH + B-MIG-PIN):
#   generate-setup-release-candidate.yml exists on default branch main
#   release/v1.2.0-rc tip == releaseCommitSha
#   B-MIG-PIN cleared (cond 8: migrationPin output exact on releaseCommitSha)
#   DO NOT use workflow_dispatch @ raw SHA

gh workflow run generate-setup-release-candidate.yml --ref release/v1.2.0-rc
  # inputs: release_version=1.2.0, mailpit pin
  validate-inputs
  build-oci                  <-- Plan Step 1b.2
  package-linux-x64
  package-linux-arm64        <-- Plan Step 1c.1 (with assemble-handoff)
  package-win-x64
  assemble-handoff

Machine-verify before accept:
  refs/heads/release/v1.2.0-rc == releaseCommitSha
  GITHUB_REF == refs/heads/release/v1.2.0-rc
  GITHUB_SHA == releaseCommitSha
  each job HEAD == releaseCommitSha
  workflowRunAttempt == 1; all required jobs runAttempt == 1
  RC branch tip unchanged across run

-> one workflowRunId (attempt 1 only) freezes:
   OCI layout + image-identity.json + host archives + candidate-provenance.json
-> Step 1c.2 promote-path proof (digest preserved)
-> Clear B-MIG-BIND (cond 9: binding carries PIN digests) before Phase 2 #456
-> Phase 2 #456 + consumer validator (CV-* incl ACTIVE/SCENARIO + CV-MIG-PIN-01/02)
-> Phase 4: reuse release/v1.2.0-rc as PR head; merge commit only
-> Phase 5: tag releaseCommitSha (not the merge commit on main)
```

---

End of Issue #458 Release Execution Plan **Rev.8** (2026-08-01). Supersedes Rev.7, Rev.6, Rev.5, Rev.4, Rev.3, Rev.2, and Rev.1.
