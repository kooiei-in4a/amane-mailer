# ACS secret / platform-owned sender registration CLI runbook

> Target: `admin provider register-acs` / `admin provider check-acs-preflight`
> action ID: `MAILER-ACS-INPUT-01`

## 1. Purpose

This is a non-public, one-shot CLI for safely registering the ACS connection string and the
sender identity (email, display name) for System Admin platform-owned mail, using interactive
input only.

- The ACS connection string is stored only in a deploy-time secret file
  (`acs_connection_string`). It is never written to tenant JSON, the database, or any amane-flow
  secret path.
- The platform-owned sender identity is stored in a new, tenant-independent
  `platform-sender.json`. It is never assigned to an existing tenant, and no fake tenant is
  created.
- This command alone does not complete System Admin confirmation mail delivery. Wiring
  `platform-sender.json` into a runtime send decision is the responsibility of the formal
  platform-owned mail request contract (MAIL-PLATFORM-01).

## 2. Deploy host preparation

1. Create the host directories referenced by `MAILER_ACS_SECRET_HOST_PATH` (default
   `./secrets/acs`) and `MAILER_PLATFORM_SENDER_HOST_PATH` (default `./config/platform-sender`).
2. Resolve the Mailer runtime image's actual non-root execution UID/GID (chiseled image). Use
   `docker inspect <image> --format '{{.Config.User}}'` — do not transcribe a guessed value into
   this runbook, since it depends on the actual built image.
3. Create both directories owned by that UID/GID, mode `0700`, with no group/other permissions
   at all.
4. Never transcribe the secret value itself into this runbook or any approval record.

## 3. Running the command

```bash
docker compose --env-file .env -f compose.yml --profile acs-admin run --rm mailer-acs-admin
```

Do not pass `-T`. Non-echo secret input requires a real TTY; running with `-T` (no-TTY) is
rejected with `REJECTED_INPUT_REDIRECTED`.

To run only the non-interactive preflight (no secret is ever requested, safe to run repeatedly):

```bash
docker compose --env-file .env -f compose.yml --profile acs-admin run --rm mailer-acs-admin admin provider check-acs-preflight
```

## 4. Interactive steps

1. Target environment confirmation: only the exact literal `Staging` is accepted (`staging`,
   `STAGING`, and any other spelling are rejected).
2. Intent confirmation: the fixed phrase `MAILER-ACS-REGISTER` is required.
3. ACS connection string: non-echo input, entered twice. Nothing is written if the two do not
   match.
4. Sender email: only a bare email address is accepted.
5. Sender display name: 1-200 characters, no control characters.

On success only `SUCCESS` is printed; on rejection only a canonical result code (e.g.
`REJECTED_ALREADY_REGISTERED`) is printed. Secret values and raw exceptions are never printed.

## 5. Mutual exclusion

From the moment preflight passes until both files finish committing, the command holds an
exclusive lock (`.register-acs.lock`) in the ACS secret directory. A second concurrent invocation
is rejected immediately with `REJECTED_CONCURRENT_EXECUTION`.

The lock is an OS-level advisory lock (`FileShare.None`), keyed to whether a process actually
holds the file open — not to whether the lock file exists on disk. If the process dies abnormally,
the OS releases the lock automatically, so a leftover lock file never blocks the next run.

## 6. Recovering from a partial write

The two files (ACS secret, `platform-sender.json`) are written prepare-then-commit: commit A,
then commit B. If commit B fails, commit A is automatically rolled back (deleted), and the
command reports `REJECTED_PARTIAL_WRITE_ROLLED_BACK`.

In the extremely narrow window where the rollback itself fails (e.g. a filesystem fault), the
next invocation's preflight detects the "only one of the two is registered" state as
`REJECTED_PARTIAL_STATE` and stops without attempting to auto-heal. In that case:

1. Check only whether each of the two files exists, without displaying any secret value.
2. If you determine a file is an unintended leftover, delete it manually before retrying.
3. If in doubt, record the operator, timestamp, and observed canonical result code, and confirm
   with an approver before proceeding.

## 7. Real-PTY CLI verification (development / CI)

`scripts/pty-smoke-register-acs.py` uses synthetic values only and drives the CLI through a real
pseudo-terminal to confirm success, re-run rejection, partial-state rejection, and that secrets
never appear in terminal output. Run on Linux:

```bash
dotnet build src/Amane.Mailer/Amane.Mailer.csproj
python3 scripts/pty-smoke-register-acs.py
```

`Console.ReadKey(intercept: true)` non-echo secret input is a real-terminal behavior that a unit
test with a fake console cannot verify; this script checks it separately.

## 8. What may be recorded as sanitized evidence

Safe to record:

- Command name, target environment, canonical result code.
- Whether the run succeeded or was rejected.

Never record:

- The actual ACS connection string or sender email value.
- Raw exceptions, stack traces, or the content of interactive input.
