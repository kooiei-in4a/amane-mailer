#!/usr/bin/env python3
"""Real PTY smoke test for `admin provider register-acs` / `check-acs-preflight`.

Drives the built Amane.Mailer.dll through a pseudo-terminal (not a pipe), so
Console.IsInputRedirected is false and Console.ReadKey(intercept: true) exercises a real raw-mode
keystroke read, the same as an operator's terminal. Unit tests with a fake console cannot verify
this: only a real TTY proves that secret input is never echoed. Synthetic values only; this
script never touches a real ACS connection string.

Linux only (uses the `pty` module). Run after building the Debug or Release configuration:

    dotnet build src/Amane.Mailer/Amane.Mailer.csproj
    python3 scripts/pty-smoke-register-acs.py

Exit code 0 means every check passed; non-zero means at least one failed (see printed detail).
"""
import json
import os
import pty
import re
import select
import shutil
import subprocess
import sys
import time

SYNTHETIC_CONN = (
    "Endpoint=https://synthetic-smoke.example.communication.azure.com/;"
    "AccessKey=SYNTHETIC-NOT-REAL-0000000000"
)
SYNTHETIC_EMAIL = "smoke-sender@example.com"
SYNTHETIC_DISPLAY_NAME = "Smoke Test Sender"

FAILURES = []


def find_repo_root():
    directory = os.path.dirname(os.path.abspath(__file__))
    while directory != os.path.dirname(directory):
        if os.path.exists(os.path.join(directory, "Amane.Mailer.slnx")):
            return directory
        directory = os.path.dirname(directory)
    raise SystemExit("Could not find repository root containing Amane.Mailer.slnx")


def find_dll(repo_root):
    override = os.environ.get("PTY_SMOKE_DLL_PATH")
    if override:
        return override
    bin_dir = os.path.join(repo_root, "src", "Amane.Mailer", "bin")
    for configuration in ("Release", "Debug"):
        candidate = os.path.join(bin_dir, configuration, "net10.0", "Amane.Mailer.dll")
        if os.path.exists(candidate):
            return candidate
    raise SystemExit(
        "Amane.Mailer.dll not found under src/Amane.Mailer/bin/{Release,Debug}/net10.0. "
        "Build the project first, or set PTY_SMOKE_DLL_PATH."
    )


REPO_ROOT = find_repo_root()
DOTNET = shutil.which("dotnet") or "dotnet"
DLL = find_dll(REPO_ROOT)
SCRATCH_ROOT = os.path.join("/tmp", "amane-mailer-pty-smoke")


def fresh_dirs(tag):
    root = os.path.join(SCRATCH_ROOT, tag)
    shutil.rmtree(root, ignore_errors=True)
    acs_dir = os.path.join(root, "secrets", "acs")
    sender_dir = os.path.join(root, "config", "platform-sender")
    os.makedirs(acs_dir, mode=0o700)
    os.makedirs(sender_dir, mode=0o700)
    os.chmod(acs_dir, 0o700)
    os.chmod(sender_dir, 0o700)
    return acs_dir, sender_dir


def _drain_until_exit(master, proc, output, timeout):
    deadline = time.time() + timeout
    while time.time() < deadline and proc.poll() is None:
        ready, _, _ = select.select([master], [], [], 0.5)
        if master in ready:
            try:
                chunk = os.read(master, 4096)
            except OSError:
                break
            if not chunk:
                break
            output += chunk
    for _ in range(10):
        ready, _, _ = select.select([master], [], [], 0.2)
        if master not in ready:
            break
        try:
            chunk = os.read(master, 4096)
        except OSError:
            break
        if not chunk:
            break
        output += chunk
    try:
        exit_code = proc.wait(timeout=10)
    except subprocess.TimeoutExpired:
        print(f"DID NOT EXIT within timeout. output so far: {output.decode(errors='replace')!r}")
        proc.kill()
        exit_code = proc.wait(timeout=5)
    return exit_code, output


def run_interactive(command_args, env_overrides, steps, timeout=20):
    master, slave = pty.openpty()
    env = dict(os.environ)
    env.update(env_overrides)
    proc = subprocess.Popen(
        [DOTNET, DLL] + command_args,
        stdin=slave, stdout=slave, stderr=slave,
        env=env, close_fds=True,
    )
    os.close(slave)
    output = b""
    try:
        for expect_regex, send_text in steps:
            pattern = re.compile(expect_regex.encode())
            buf = b""
            deadline = time.time() + timeout
            matched = False
            while time.time() < deadline:
                ready, _, _ = select.select([master], [], [], 0.5)
                if master in ready:
                    try:
                        chunk = os.read(master, 4096)
                    except OSError:
                        break
                    if not chunk:
                        break
                    buf += chunk
                    output += chunk
                    if pattern.search(buf):
                        matched = True
                        break
            if not matched:
                print(f"TIMEOUT waiting for pattern: {expect_regex!r}")
                print(f"buffer so far: {buf!r}")
                proc.kill()
                proc.wait(timeout=5)
                return -1, output
            if send_text is not None:
                # A bare LF is used deliberately: sending CRLF into a canonical-mode PTY can be
                # translated (ICRNL) into two line-terminated reads (one from the CR, one from the
                # trailing LF), so the *next* Console.ReadLine() silently consumes a stale empty
                # line before this script's next scripted input is even sent.
                os.write(master, send_text.encode() + b"\n")

        exit_code, output = _drain_until_exit(master, proc, output, timeout)
    finally:
        try:
            os.close(master)
        except OSError:
            pass
    return exit_code, output


def run_noninteractive(command_args, env_overrides, timeout=20):
    master, slave = pty.openpty()
    env = dict(os.environ)
    env.update(env_overrides)
    proc = subprocess.Popen(
        [DOTNET, DLL] + command_args,
        stdin=slave, stdout=slave, stderr=slave,
        env=env, close_fds=True,
    )
    os.close(slave)
    output = b""
    try:
        exit_code, output = _drain_until_exit(master, proc, output, timeout)
    finally:
        try:
            os.close(master)
        except OSError:
            pass
    return exit_code, output


def check(label, condition, detail=""):
    status = "PASS" if condition else "FAIL"
    print(f"[{status}] {label} {detail}")
    if not condition:
        FAILURES.append(label)


def scenario_success():
    print("\n=== scenario: fresh success ===")
    acs_dir, sender_dir = fresh_dirs("success")
    env = {
        "MAILER_ACS_SECRET_DIRECTORY": acs_dir,
        "MAILER_PLATFORM_SENDER_DIRECTORY": sender_dir,
    }
    steps = [
        ("Confirm target environment", "Staging"),
        ("Type MAILER-ACS-REGISTER to confirm intent", "MAILER-ACS-REGISTER"),
        ("ACS connection string:", SYNTHETIC_CONN),
        ("Re-enter ACS connection string:", SYNTHETIC_CONN),
        ("Sender email:", SYNTHETIC_EMAIL),
        ("Sender display name:", SYNTHETIC_DISPLAY_NAME),
    ]
    exit_code, output = run_interactive(["admin", "provider", "register-acs"], env, steps)
    text = output.decode(errors="replace")
    check("success: exit code is 0", exit_code == 0, f"(got {exit_code})")
    check("success: stdout reports SUCCESS", "SUCCESS" in text)
    check("success: secret connection string never echoed to terminal", SYNTHETIC_CONN not in text)

    acs_file = os.path.join(acs_dir, "acs_connection_string")
    sender_file = os.path.join(sender_dir, "platform-sender.json")
    acs_ok = os.path.exists(acs_file) and open(acs_file).read() == SYNTHETIC_CONN
    check("success: acs secret file written with exact content", acs_ok)
    if os.path.exists(sender_file):
        data = json.loads(open(sender_file).read())
        check(
            "success: platform-sender.json has expected sender email",
            data.get("sender", {}).get("email") == SYNTHETIC_EMAIL,
        )
        check("success: platform-sender.json live_sending is false", data.get("live_sending") is False)
    else:
        check("success: platform-sender.json written", False)
    return acs_dir, sender_dir


def scenario_rerun_rejected(acs_dir, sender_dir):
    print("\n=== scenario: re-run against already-registered directories is rejected ===")
    env = {
        "MAILER_ACS_SECRET_DIRECTORY": acs_dir,
        "MAILER_PLATFORM_SENDER_DIRECTORY": sender_dir,
    }
    exit_code, output = run_noninteractive(["admin", "provider", "register-acs"], env)
    text = output.decode(errors="replace")
    check("rerun: exit code is 2", exit_code == 2, f"(got {exit_code})")
    check("rerun: rejected with REJECTED_ALREADY_REGISTERED", "REJECTED_ALREADY_REGISTERED" in text)
    check("rerun: no prompt text appears (rejected before any prompt)", "Confirm target environment" not in text)
    check("rerun: secret connection string never appears", SYNTHETIC_CONN not in text)


def scenario_partial_state_rejected():
    print("\n=== scenario: partial state (only ACS secret present) is rejected ===")
    acs_dir, sender_dir = fresh_dirs("partial")
    with open(os.path.join(acs_dir, "acs_connection_string"), "w") as f:
        f.write(SYNTHETIC_CONN)
    env = {
        "MAILER_ACS_SECRET_DIRECTORY": acs_dir,
        "MAILER_PLATFORM_SENDER_DIRECTORY": sender_dir,
    }
    exit_code, output = run_noninteractive(["admin", "provider", "register-acs"], env)
    text = output.decode(errors="replace")
    check("partial: exit code is 2", exit_code == 2, f"(got {exit_code})")
    check("partial: rejected with REJECTED_PARTIAL_STATE", "REJECTED_PARTIAL_STATE" in text)
    check("partial: no prompt text appears (rejected before any prompt)", "Confirm target environment" not in text)
    check("partial: secret connection string never appears", SYNTHETIC_CONN not in text)


def scenario_wrong_environment_rejects_before_secret():
    print("\n=== scenario: wrong environment confirmation rejects before any secret is requested ===")
    acs_dir, sender_dir = fresh_dirs("wrongenv")
    env = {
        "MAILER_ACS_SECRET_DIRECTORY": acs_dir,
        "MAILER_PLATFORM_SENDER_DIRECTORY": sender_dir,
    }
    steps = [("Confirm target environment", "staging")]
    exit_code, output = run_interactive(["admin", "provider", "register-acs"], env, steps)
    text = output.decode(errors="replace")
    check("wrongenv: exit code is 2", exit_code == 2, f"(got {exit_code})")
    check("wrongenv: rejected with REJECTED_ENVIRONMENT_MISMATCH", "REJECTED_ENVIRONMENT_MISMATCH" in text)
    check("wrongenv: never prompted for connection string", "ACS connection string:" not in text)


def scenario_preflight_only_does_not_prompt():
    print("\n=== scenario: check-acs-preflight never prompts, reports SUCCESS on a clean directory ===")
    acs_dir, sender_dir = fresh_dirs("preflight")
    env = {
        "MAILER_ACS_SECRET_DIRECTORY": acs_dir,
        "MAILER_PLATFORM_SENDER_DIRECTORY": sender_dir,
    }
    exit_code, output = run_noninteractive(["admin", "provider", "check-acs-preflight"], env)
    text = output.decode(errors="replace")
    check("preflight: exit code is 0", exit_code == 0, f"(got {exit_code})")
    check("preflight: reports SUCCESS", "SUCCESS" in text)
    check("preflight: no prompt text appears", "Confirm target environment" not in text)


def main():
    if sys.platform != "linux":
        print("This smoke test requires a real Linux PTY; skipping on non-Linux platforms.")
        sys.exit(0)

    acs_dir, sender_dir = scenario_success()
    scenario_rerun_rejected(acs_dir, sender_dir)
    scenario_partial_state_rejected()
    scenario_wrong_environment_rejects_before_secret()
    scenario_preflight_only_does_not_prompt()

    print("\n=== summary ===")
    if FAILURES:
        print(f"FAILED ({len(FAILURES)}): " + ", ".join(FAILURES))
        sys.exit(1)
    print("ALL PTY SMOKE CHECKS PASSED")
    sys.exit(0)


if __name__ == "__main__":
    main()
