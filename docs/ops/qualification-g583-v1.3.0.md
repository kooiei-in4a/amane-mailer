# v1.3.0 G583 formal qualification routes

This note documents only the five G583 routes required for the v1.3.0 release. It does not change G456 semantics or the qualification lifecycle.

## Required routes

- `G583-MIG-01 / win-docker`
- `G583-MIG-01 / linux-docker`
- `G583-MIG-02 / win-docker`
- `G583-MIG-02 / linux-docker`
- `G583-MIG-03 / ci-auto`

The source of route authority remains `scripts/qualification-g583-dispatch-manifest.json`. The formal bridge is `scripts/qualification-g583-formal-adapter.py`; the existing `scripts/qualification-runner.py` remains the durable evidence writer and validator.

## MIG01 / MIG02

The formal bridge requires an artifact-authority JSON document bound to the current candidate. It must contain the current `candidateId`, `releaseCommitSha`, `ociIndexDigest`, selected OCI manifest digests, and a digest-pinned candidate image reference. MIG02 also requires the authoritative v1.2.0 baseline image reference.

Run one route:

```text
python scripts/qualification-g583-formal-adapter.py run \
  --run-root <fresh-v1.3.0-run-root> \
  --scenario-id G583-MIG-01 \
  --variant-id linux-docker \
  --artifact-authority <artifact-authority.json> \
  --output <staged-evidence-envelope.json>
```

The adapter executes the dedicated Docker fixture, revalidates platform/candidate/migration identity, and produces a full runner-compatible evidence envelope. `NOT_RUN_ENVIRONMENT` is not a PASS.

## MIG03 / ci-auto

When the exact release source is available in the execution environment, run:

```text
python scripts/qualification-g583-formal-adapter.py run \
  --run-root <fresh-v1.3.0-run-root> \
  --scenario-id G583-MIG-03 \
  --variant-id ci-auto \
  --output <staged-evidence-envelope.json>
```

For GitHub Actions execution, the checked-in MIG03 adapter may first create bound prequalification observations. Those observations may be transported to the qualification operator and supplied with:

```text
--mig03-observations <bound-mig03-observations.json>
```

The formal bridge rejects observations whose candidate, source SHA, OCI index, migration PIN/inventory authority, owner, route, or schema allowlist do not match the local fresh binding.

## Durable evidence append

The formal bridge does not mutate the qualification store. After it succeeds, append the produced full envelope using the existing runner `evidence` command and then accept it using the existing `disposition` command. The CLI identity/result/actor arguments must exactly match the envelope and the bound authorization owner.

Do not reuse RC11 evidence or candidate state. The v1.3.0 release attempt starts with a new candidate, new binding, new authorization, new nonces, a new qualification run, and `0/47` Hard PASS.
