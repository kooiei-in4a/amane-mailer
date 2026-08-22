#!/usr/bin/env python3
"""Focused positive/negative tests for the v1.3.1 scope overlay."""

from __future__ import annotations

import copy
import importlib.util
import json
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent
RUNNER_PATH = ROOT / "scripts" / "qualification-runner.py"
OLD_SCOPE_PATH = ROOT / "docs" / "qualification" / "v1.3.0-scope.json"
NEW_SCOPE_PATH = ROOT / "docs" / "qualification" / "v1.3.1-scope.json"


def load_runner():
    spec = importlib.util.spec_from_file_location("qualification_runner_scope_revision", RUNNER_PATH)
    if spec is None or spec.loader is None:
        raise RuntimeError("qualification runner cannot be loaded")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


runner = load_runner()


def hard_keys(profile: dict) -> set[tuple[str, str]]:
    return {
        (row["scenarioId"], variant)
        for row in profile["scenarioRows"]
        if row["gateClass"] == "Hard"
        for variant in row["requiredVariants"]
    }


def expect_rejection(label: str, callback) -> None:
    try:
        callback()
    except runner.RunnerError:
        return
    raise AssertionError(f"negative case unexpectedly passed: {label}")


old = runner.load_scope_manifest(OLD_SCOPE_PATH)
new = runner.load_scope_manifest(NEW_SCOPE_PATH)
expected_removed = {
    tuple(item.split("/", 1)) for item in runner.V131_REMOVED_VARIANT_KEYS
}
old_hard = hard_keys(old)
new_hard = hard_keys(new)

assert old["scopeId"] == runner.V13_SCOPE_ID
assert new["scopeId"] == runner.V131_SCOPE_ID
assert len(old_hard) == 47
assert len(new_hard) == 39
assert old_hard - new_hard == expected_removed
assert new_hard - old_hard == set()
assert {
    (row["scenarioId"], variant)
    for row in new["scenarioRows"]
    if row["scenarioId"].startswith("G583")
    for variant in row["requiredVariants"]
} == {
    ("G583-MIG-01", "win-docker"),
    ("G583-MIG-01", "linux-docker"),
    ("G583-MIG-02", "win-docker"),
    ("G583-MIG-02", "linux-docker"),
    ("G583-MIG-03", "ci-auto"),
}

runner.validate_scope_release_compatibility("1.3.0", old)
runner.validate_scope_release_compatibility("1.3.1", new)
for label, release, profile in (
    ("v1.3.1 with historical scope", "1.3.1", old),
    ("v1.3.0 with revised scope", "1.3.0", new),
    ("v1.3.2 with historical scope", "1.3.2", old),
    ("v1.3.2 with revised scope", "1.3.2", new),
    ("v1.3.1 without scope", "1.3.1", None),
):
    expect_rejection(label, lambda release=release, profile=profile: runner.validate_scope_release_compatibility(release, profile))

with tempfile.TemporaryDirectory(prefix="qualification-scope-revision-") as temporary:
    root = Path(temporary)
    old_copy = root / "v1.3.0-scope.json"
    new_copy = root / "v1.3.1-scope.json"
    old_copy.write_bytes(OLD_SCOPE_PATH.read_bytes())
    manifest = json.loads(NEW_SCOPE_PATH.read_text(encoding="utf-8"))

    def reject_overlay(label: str, removed: list[str]) -> None:
        candidate = copy.deepcopy(manifest)
        candidate["variantOverlay"]["removedRequiredVariants"] = removed
        new_copy.write_text(json.dumps(candidate, indent=2) + "\n", encoding="utf-8")
        expect_rejection(label, lambda: runner.load_scope_manifest(new_copy))

    reject_overlay("reintroduced G456 Windows variant", list(runner.V131_REMOVED_VARIANT_KEYS[:-1]))
    reject_overlay("removed Linux counterpart", list(runner.V131_REMOVED_VARIANT_KEYS) + ["G456-13/linux-docker"])
    reject_overlay("removed unrelated CI lane", list(runner.V131_REMOVED_VARIANT_KEYS) + ["G456-20/ci-auto"])
    reject_overlay("removed G583 Windows route", list(runner.V131_REMOVED_VARIANT_KEYS) + ["G583-MIG-01/win-docker"])

print(json.dumps({
    "result": "PASS",
    "oldScope": old["scopeId"],
    "newScope": new["scopeId"],
    "historicalHard": len(old_hard),
    "revisedHard": len(new_hard),
    "removed": len(expected_removed),
    "negativeCases": 9,
    "g583RoutesPreserved": 5,
}, sort_keys=True))
