#!/usr/bin/env python3
"""Real PTY smoke test for `admin provider test-acs-send`.

Drives the built Amane.Mailer.dll through a pseudo-terminal so
Console.IsInputRedirected is false and Console.ReadKey(intercept: true) exercises a real
raw-mode keystroke read. Unit tests with a fake console cannot verify non-echo secret/PII input.

This smoke never contacts real ACS. It uses a synthetic connection string and stops before a
successful live send (invalid handoff path after PII prompts, or earlier rejects).

Linux only (uses the `pty` module). Run after building:

    dotnet build src/Amane.Mailer/Amane.Mailer.csproj
    python3 scripts/pty-smoke-test-acs-send.py
"""
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
SYNTHETIC_RECIPIENT = "smoke-recipient@example.com"
INVALID_CONN = "not-a-real-acs-connection-string"
INVALID_HANDOFF = "relative/handoff.txt"

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
SCRATCH_ROOT = os.path.join("/tmp", "amane-mailer-pty-smoke-test-acs-send")


def fresh_root(tag):
    root = os.path.join(SCRATCH_ROOT, tag)
    shutil.rmtree(root, ignore_errors=True)
    os.makedirs(root, mode=0o700)
    return root


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
    for key in list(env.keys()):
        if key.startswith("ACS_") or key.startswith("MAILER_ACS_"):
            env.pop(key, None)
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
                os.write(master, send_text.encode() + b"\n")

        exit_code, output = _drain_until_exit(master, proc, output, timeout)
    finally:
        try:
            os.close(master)
        except OSError:
            pass
    return exit_code, output


def run_redirected_stdin():
    env = dict(os.environ)
    for key in list(env.keys()):
        if key.startswith("ACS_") or key.startswith("MAILER_ACS_"):
            env.pop(key, None)
    proc = subprocess.Popen(
        [DOTNET, DLL, "admin", "provider", "test-acs-send"],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        env=env,
        text=True,
    )
    stdout, stderr = proc.communicate(input="Staging\n", timeout=20)
    return proc.returncode, stdout + stderr


def check(label, condition, detail=""):
    status = "PASS" if condition else "FAIL"
    print(f"[{status}] {label} {detail}")
    if not condition:
        FAILURES.append(label)


def assert_no_leak(text, label_prefix):
    check(f"{label_prefix}: secret never echoed", SYNTHETIC_CONN not in text)
    check(f"{label_prefix}: invalid secret never echoed", INVALID_CONN not in text)
    check(f"{label_prefix}: sender email never echoed", SYNTHETIC_EMAIL not in text)
    check(f"{label_prefix}: recipient email never echoed", SYNTHETIC_RECIPIENT not in text)
    check(
        f"{label_prefix}: synthetic subject never echoed",
        "Amane Mailer ACS test-send verification" not in text,
    )


def scenario_wrong_environment():
    print("\n=== scenario: wrong environment rejects before secret/send ===")
    steps = [("Confirm target environment", "staging")]
    exit_code, output = run_interactive(["admin", "provider", "test-acs-send"], {}, steps)
    text = output.decode(errors="replace")
    check("wrongenv: exit code is 2", exit_code == 2, f"(got {exit_code})")
    check("wrongenv: REJECTED_ENVIRONMENT_MISMATCH", "REJECTED_ENVIRONMENT_MISMATCH" in text)
    check("wrongenv: never prompted for connection string", "ACS connection string:" not in text)
    assert_no_leak(text, "wrongenv")


def scenario_wrong_intent():
    print("\n=== scenario: wrong intent rejects before secret/send ===")
    steps = [
        ("Confirm target environment", "Staging"),
        ("Type MAILER-ACS-TEST-SEND to confirm intent", "WRONG-PHRASE"),
    ]
    exit_code, output = run_interactive(["admin", "provider", "test-acs-send"], {}, steps)
    text = output.decode(errors="replace")
    check("wrongintent: exit code is 2", exit_code == 2, f"(got {exit_code})")
    check("wrongintent: REJECTED_INTENT_MISMATCH", "REJECTED_INTENT_MISMATCH" in text)
    check("wrongintent: never prompted for connection string", "ACS connection string:" not in text)
    assert_no_leak(text, "wrongintent")


def scenario_tty_secret_mismatch_and_no_echo():
    print("\n=== scenario: TTY secret mismatch rejects without echoing ===")
    steps = [
        ("Confirm target environment", "Staging"),
        ("Type MAILER-ACS-TEST-SEND to confirm intent", "MAILER-ACS-TEST-SEND"),
        ("ACS connection string:", SYNTHETIC_CONN),
        ("Re-enter ACS connection string:", INVALID_CONN),
    ]
    exit_code, output = run_interactive(["admin", "provider", "test-acs-send"], {}, steps)
    text = output.decode(errors="replace")
    check("secretmismatch: exit code is 2", exit_code == 2, f"(got {exit_code})")
    check("secretmismatch: REJECTED_SECRET_MISMATCH", "REJECTED_SECRET_MISMATCH" in text)
    assert_no_leak(text, "secretmismatch")


def scenario_pii_prompts_then_invalid_handoff_before_send():
    print("\n=== scenario: PII prompts + invalid handoff reject before ACS send ===")
    steps = [
        ("Confirm target environment", "Staging"),
        ("Type MAILER-ACS-TEST-SEND to confirm intent", "MAILER-ACS-TEST-SEND"),
        ("ACS connection string:", SYNTHETIC_CONN),
        ("Re-enter ACS connection string:", SYNTHETIC_CONN),
        ("Sender email:", SYNTHETIC_EMAIL),
        ("Recipient email:", SYNTHETIC_RECIPIENT),
        ("Absolute path for message ID handoff file:", INVALID_HANDOFF),
    ]
    exit_code, output = run_interactive(["admin", "provider", "test-acs-send"], {}, steps)
    text = output.decode(errors="replace")
    check("pii-handoff: exit code is 2", exit_code == 2, f"(got {exit_code})")
    check(
        "pii-handoff: REJECTED_MESSAGE_ID_HANDOFF_PATH_INVALID",
        "REJECTED_MESSAGE_ID_HANDOFF_PATH_INVALID" in text,
    )
    check("pii-handoff: sender prompt reached", "Sender email:" in text)
    check("pii-handoff: recipient prompt reached", "Recipient email:" in text)
    assert_no_leak(text, "pii-handoff")


def scenario_ctrl_c_during_environment_prompt():
    print("\n=== scenario: Ctrl+C during environment prompt exits 2 ===")
    exit_code, output = run_ctrl_c_at_prompt(
        ["admin", "provider", "test-acs-send"],
        {},
        "Confirm target environment",
    )
    text = output.decode(errors="replace")
    check("ctrlc-env: exit code is 2", exit_code == 2, f"(got {exit_code})")
    check("ctrlc-env: REJECTED_CANCELLED", "REJECTED_CANCELLED" in text)
    assert_no_leak(text, "ctrlc-env")


def scenario_ctrl_c_during_secret_prompt():
    print("\n=== scenario: Ctrl+C during secret prompt exits 2 ===")
    steps_before = [
        ("Confirm target environment", "Staging"),
        ("Type MAILER-ACS-TEST-SEND to confirm intent", "MAILER-ACS-TEST-SEND"),
    ]
    exit_code, output = run_ctrl_c_after_steps(
        ["admin", "provider", "test-acs-send"],
        {},
        steps_before,
        "ACS connection string:",
    )
    text = output.decode(errors="replace")
    check("ctrlc-secret: exit code is 2", exit_code == 2, f"(got {exit_code})")
    check("ctrlc-secret: REJECTED_CANCELLED", "REJECTED_CANCELLED" in text)
    assert_no_leak(text, "ctrlc-secret")


def scenario_ctrl_c_during_sender_prompt():
    print("\n=== scenario: Ctrl+C during sender PII prompt exits 2 ===")
    steps_before = [
        ("Confirm target environment", "Staging"),
        ("Type MAILER-ACS-TEST-SEND to confirm intent", "MAILER-ACS-TEST-SEND"),
        ("ACS connection string:", SYNTHETIC_CONN),
        ("Re-enter ACS connection string:", SYNTHETIC_CONN),
    ]
    exit_code, output = run_ctrl_c_after_steps(
        ["admin", "provider", "test-acs-send"],
        {},
        steps_before,
        "Sender email:",
    )
    text = output.decode(errors="replace")
    check("ctrlc-sender: exit code is 2", exit_code == 2, f"(got {exit_code})")
    check("ctrlc-sender: REJECTED_CANCELLED", "REJECTED_CANCELLED" in text)
    assert_no_leak(text, "ctrlc-sender")


def run_ctrl_c_at_prompt(command_args, env_overrides, expect_regex, timeout=20):
    return run_ctrl_c_after_steps(command_args, env_overrides, [], expect_regex, timeout)


def run_ctrl_c_after_steps(command_args, env_overrides, steps, expect_regex, timeout=20):
    master, slave = pty.openpty()
    env = dict(os.environ)
    for key in list(env.keys()):
        if key.startswith("ACS_") or key.startswith("MAILER_ACS_"):
            env.pop(key, None)
    env.update(env_overrides)
    proc = subprocess.Popen(
        [DOTNET, DLL] + command_args,
        stdin=slave, stdout=slave, stderr=slave,
        env=env, close_fds=True,
    )
    os.close(slave)
    output = b""
    try:
        for step_regex, send_text in steps:
            pattern = re.compile(step_regex.encode())
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
                print(f"TIMEOUT waiting for pattern: {step_regex!r}")
                proc.kill()
                proc.wait(timeout=5)
                return -1, output
            if send_text is not None:
                os.write(master, send_text.encode() + b"\n")

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
            print(f"TIMEOUT waiting for Ctrl+C target prompt: {expect_regex!r}")
            proc.kill()
            proc.wait(timeout=5)
            return -1, output

        # ETX — Console.ReadKey(intercept: true) maps this to Ctrl+C.
        os.write(master, b"\x03")
        exit_code, output = _drain_until_exit(master, proc, output, timeout)
    finally:
        try:
            os.close(master)
        except OSError:
            pass
    return exit_code, output


def scenario_redirected_stdin_rejected():
    print("\n=== scenario: redirected stdin is rejected ===")
    exit_code, text = run_redirected_stdin()
    check("redirected: exit code is 2", exit_code == 2, f"(got {exit_code})")
    check("redirected: REJECTED_INPUT_REDIRECTED", "REJECTED_INPUT_REDIRECTED" in text)
    assert_no_leak(text, "redirected")


def main():
    if sys.platform != "linux":
        print("This smoke test requires a real Linux PTY; skipping on non-Linux platforms.")
        sys.exit(0)

    fresh_root("base")
    scenario_wrong_environment()
    scenario_wrong_intent()
    scenario_tty_secret_mismatch_and_no_echo()
    scenario_pii_prompts_then_invalid_handoff_before_send()
    scenario_ctrl_c_during_environment_prompt()
    scenario_ctrl_c_during_secret_prompt()
    scenario_ctrl_c_during_sender_prompt()
    scenario_redirected_stdin_rejected()

    print("\n=== summary ===")
    if FAILURES:
        print(f"FAILED ({len(FAILURES)}): " + ", ".join(FAILURES))
        sys.exit(1)
    print("ALL PTY SMOKE CHECKS PASSED (no real ACS send)")
    sys.exit(0)


if __name__ == "__main__":
    main()
