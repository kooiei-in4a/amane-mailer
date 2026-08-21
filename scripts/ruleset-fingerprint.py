#!/usr/bin/env python3
"""Create a stable fingerprint for the release-promotion ruleset boundary."""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path
from typing import Any


def fail(field: str, message: str) -> "NoReturn":
    print(f"[error] {field}: {message}", file=sys.stderr)
    raise SystemExit(1)


def load_json(path: Path, field: str) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        fail(field, "missing or invalid JSON")


def canonical_json(value: Any) -> bytes:
    return json.dumps(
        value,
        ensure_ascii=False,
        separators=(",", ":"),
        sort_keys=True,
    ).encode("utf-8")


def sorted_unique_strings(value: Any) -> list[str]:
    if not isinstance(value, list) or any(not isinstance(item, str) for item in value):
        return []
    return sorted(set(value))


def normalize_status_checks(value: Any) -> list[dict[str, Any]]:
    if not isinstance(value, list):
        return []
    checks = []
    for item in value:
        if not isinstance(item, dict):
            continue
        checks.append(
            {
                "context": item.get("context"),
                "integration_id": item.get("integration_id"),
            }
        )
    return sorted(
        checks,
        key=lambda item: (str(item.get("context")), str(item.get("integration_id"))),
    )


def normalize_rule(rule: Any) -> dict[str, Any]:
    if not isinstance(rule, dict):
        return {"type": None}
    normalized = {key: value for key, value in rule.items() if key not in {
        "ruleset_id", "ruleset_source", "ruleset_source_type"
    }}
    parameters = normalized.get("parameters")
    if isinstance(parameters, dict):
        parameters = dict(parameters)
        if "allowed_merge_methods" in parameters:
            parameters["allowed_merge_methods"] = sorted_unique_strings(
                parameters["allowed_merge_methods"]
            )
        if "required_status_checks" in parameters:
            parameters["required_status_checks"] = normalize_status_checks(
                parameters["required_status_checks"]
            )
        normalized["parameters"] = parameters
    return normalized


def normalize_rules(value: Any) -> list[dict[str, Any]]:
    if not isinstance(value, list):
        return []
    rules = [normalize_rule(rule) for rule in value]
    return sorted(rules, key=lambda item: (str(item.get("type")), canonical_json(item)))


def normalize_actors(value: Any) -> list[dict[str, Any]]:
    if not isinstance(value, list):
        return []
    actors = []
    for actor in value:
        if not isinstance(actor, dict):
            continue
        actors.append(
            {
                "actor_id": actor.get("actor_id"),
                "actor_type": actor.get("actor_type"),
                "bypass_mode": actor.get("bypass_mode"),
            }
        )
    return sorted(
        actors,
        key=lambda item: (
            str(item.get("actor_type")),
            str(item.get("actor_id")),
            str(item.get("bypass_mode")),
        ),
    )


def normalize_conditions(value: Any) -> dict[str, Any]:
    if not isinstance(value, dict):
        return {}
    result = dict(value)
    ref_name = result.get("ref_name")
    if isinstance(ref_name, dict):
        result["ref_name"] = {
            **ref_name,
            "include": sorted_unique_strings(ref_name.get("include")),
            "exclude": sorted_unique_strings(ref_name.get("exclude")),
        }
    return result


def fingerprints(ruleset: dict[str, Any], effective_rules: Any) -> dict[str, Any]:
    if not isinstance(ruleset, dict):
        fail("ruleset", "must be an object")
    projection = {
        "schemaVersion": 1,
        "id": ruleset.get("id"),
        "name": ruleset.get("name"),
        "target": ruleset.get("target"),
        "source_type": ruleset.get("source_type"),
        "source": ruleset.get("source"),
        "enforcement": ruleset.get("enforcement"),
        "conditions": normalize_conditions(ruleset.get("conditions")),
        "rules": normalize_rules(ruleset.get("rules")),
        "bypass_actors": normalize_actors(ruleset.get("bypass_actors")),
        "effective_rules": normalize_rules(effective_rules),
    }
    policy_projection = {
        "schemaVersion": 1,
        "target": projection["target"],
        "enforcement": projection["enforcement"],
        "rules": projection["rules"],
        "bypass_actors": projection["bypass_actors"],
    }
    return {
        "fingerprint": hashlib.sha256(canonical_json(projection)).hexdigest(),
        "policyFingerprint": hashlib.sha256(
            canonical_json(policy_projection)
        ).hexdigest(),
        "projection": projection,
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--ruleset", required=True, type=Path)
    parser.add_argument("--effective-rules", required=True, type=Path)
    parser.add_argument("--expect-fingerprint")
    parser.add_argument("--expect-policy-fingerprint")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    result = fingerprints(
        load_json(args.ruleset, "ruleset"),
        load_json(args.effective_rules, "effectiveRules"),
    )
    if args.expect_fingerprint and result["fingerprint"] != args.expect_fingerprint:
        fail("rulesetFingerprint", "mismatch")
    if (
        args.expect_policy_fingerprint
        and result["policyFingerprint"] != args.expect_policy_fingerprint
    ):
        fail("rulesetPolicyFingerprint", "mismatch")

    rendered = json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True) + "\n"
    if args.output:
        args.output.write_text(rendered, encoding="utf-8")
    else:
        print(rendered, end="")


if __name__ == "__main__":
    main()
