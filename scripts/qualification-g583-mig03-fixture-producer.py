#!/usr/bin/env python3
"""Produce the isolated, value-free .NET fixture result for G583-MIG-03.

The producer never accepts a platform selector and never creates qualification
state.  It only runs the fixed schema/privacy test from the exact checked-out
source commit and returns its structured fixture result.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parent.parent
ADAPTER_PATH = Path(__file__).with_name("qualification-g583-mig03-ci-auto-adapter.py")
PROJECT = ROOT / "tests" / "Amane.Mailer.Tests" / "Amane.Mailer.Tests.csproj"


class ProducerError(Exception):
    """Expected fail-closed producer error."""


def fail(message: str) -> None:
    raise ProducerError(message)


def load_adapter() -> Any:
    spec = importlib.util.spec_from_file_location("qualification_g583_mig03_adapter_for_producer", ADAPTER_PATH)
    if spec is None or spec.loader is None:
        fail("MIG03 adapter cannot be loaded")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def git_output(*arguments: str) -> str:
    try:
        result = subprocess.run(
            ["git", "-C", str(ROOT), *arguments],
            capture_output=True,
            check=True,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
    except (OSError, subprocess.CalledProcessError):
        fail("source identity query failed")
    return result.stdout.strip()


def require_source_identity(source_commit: str) -> None:
    if not source_commit or len(source_commit) != 40 or any(character not in "0123456789abcdef" for character in source_commit):
        fail("source commit must be lowercase 40-hex")
    if git_output("rev-parse", "HEAD") != source_commit:
        fail("checked-out source commit does not match the requested source commit")
    relevant = [
        "scripts/qualification-g583-mig03-ci-auto-adapter.py",
        "scripts/qualification-g583-mig03-fixture-producer.py",
        "scripts/qualification-g583-mig03-adapter-manifest.json",
        "tests/Amane.Mailer.Tests/Qualification/G583MigrationSchemaContractFixtureTests.cs",
    ]
    if git_output("status", "--porcelain", "--", *relevant):
        fail("MIG03 fixture source files are dirty")


def fixture_template() -> dict[str, Any]:
    adapter = load_adapter()
    manifest = adapter.load_manifest()
    return {
        "schemaVersion": 1,
        "kind": "qualification-fixture-result",
        "fixtureId": manifest["fixture"]["fixtureId"],
        "fixtureRevision": manifest["fixture"]["fixtureRevision"],
        "scenarioId": adapter.SCENARIO,
        "variantId": adapter.VARIANT,
        "sourceTestId": manifest["fixture"]["sourceTestId"],
        "result": "PASS",
        "operationExitCode": 0,
        "observations": dict(adapter.FIXTURE_OBSERVATIONS),
    }


def validate_fixture_result(value: Any) -> dict[str, Any]:
    adapter = load_adapter()
    try:
        return adapter.validate_fixture_result(value, adapter.load_manifest())
    except adapter.AdapterError as exc:
        raise ProducerError("fixture result was rejected by the MIG03 adapter") from exc


def run_fixture(source_commit: str) -> dict[str, Any]:
    require_source_identity(source_commit)
    if not PROJECT.is_file() or PROJECT.is_symlink():
        fail("MIG03 test project is unavailable")
    dotnet = shutil.which("dotnet")
    if dotnet is None:
        fail("dotnet is unavailable")
    adapter = load_adapter()
    manifest = adapter.load_manifest()
    with tempfile.TemporaryDirectory(prefix="g583-mig03-fixture-") as temp:
        result_path = Path(temp) / "fixture-result.json"
        environment = os.environ.copy()
        environment["AMANE_QUALIFICATION_FIXTURE_RESULT_PATH"] = str(result_path)
        command = [
            dotnet, "test", str(PROJECT), "-c", "Release", "--nologo",
            "--no-build", "--no-restore",
            "--filter", manifest["fixture"]["testSelector"],
        ]
        try:
            result = subprocess.run(
                command,
                cwd=ROOT,
                env=environment,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                timeout=900,
                check=False,
            )
        except (OSError, subprocess.SubprocessError):
            fail("MIG03 fixture process could not be executed")
        if result.returncode != 0 or not result_path.is_file() or result_path.is_symlink():
            fail("MIG03 fixture did not pass exactly")
        try:
            fixture_result = json.loads(result_path.read_text(encoding="utf-8"))
        except (OSError, UnicodeError, json.JSONDecodeError):
            fail("MIG03 fixture did not write valid JSON")
        validate_fixture_result(fixture_result)
        require_source_identity(source_commit)
        return fixture_result


def command_self_test(_: argparse.Namespace) -> int:
    fixture = fixture_template()
    validate_fixture_result(fixture)
    fixture["observations"] = {"piiValueCanaryResult": "fail"}
    try:
        validate_fixture_result(fixture)
    except ProducerError:
        print(json.dumps({"result": "PASS", "scenarioId": "G583-MIG-03", "variantId": "ci-auto"}, sort_keys=True))
        return 0
    raise AssertionError("privacy-negative fixture result was accepted")


def command_run(args: argparse.Namespace) -> int:
    result = run_fixture(args.source_commit)
    output = json.dumps(result, sort_keys=True, separators=(",", ":")) + "\n"
    if args.output:
        path = Path(args.output)
        if path.exists() or path.is_symlink():
            fail("output already exists")
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(output, encoding="utf-8", newline="\n")
    else:
        sys.stdout.write(output)
    return 0


def command_manifest(_: argparse.Namespace) -> int:
    adapter = load_adapter()
    manifest = adapter.load_manifest()
    print(json.dumps({
        "contractVersion": manifest["contractVersion"],
        "dockerDependency": False,
        "fixtureId": manifest["fixture"]["fixtureId"],
        "result": "PASS",
    }, sort_keys=True))
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    manifest = sub.add_parser("manifest")
    manifest.set_defaults(func=command_manifest)
    self_test = sub.add_parser("self-test")
    self_test.set_defaults(func=command_self_test)
    run = sub.add_parser("run")
    run.add_argument("--source-commit", required=True)
    run.add_argument("--output")
    run.set_defaults(func=command_run)
    return parser


def main(argv: list[str] | None = None) -> int:
    try:
        args = build_parser().parse_args(argv)
        return args.func(args)
    except ProducerError as exc:
        print(f"[error] {exc}", file=sys.stderr)
        return 1
    except (OSError, ValueError, TypeError) as exc:
        print(f"[error] operation failed: {type(exc).__name__}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
