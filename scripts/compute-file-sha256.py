#!/usr/bin/env python3
"""Compute sha256:<64 lowercase hex> for a file without parsing sha256sum output.

Windows/MSYS paths with backslashes make GNU sha256sum emit a leading \\ escape
marker on the digest field. Candidate attempt 3 recorded archiveSha256=sha256:\\070f...
because awk took that escaped first field. Always hash file bytes directly.
"""
from __future__ import annotations

import argparse
import hashlib
import re
import sys
import tempfile
from pathlib import Path

DIGEST_RE = re.compile(r"^sha256:[a-f0-9]{64}$")
CHUNK = 1024 * 1024


def file_digest(path: Path) -> str:
    if not path.is_file():
        raise FileNotFoundError(f"not a regular file: {path}")
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while True:
            chunk = handle.read(CHUNK)
            if not chunk:
                break
            digest.update(chunk)
    return f"sha256:{digest.hexdigest()}"


def require_digest(value: str) -> str:
    if not DIGEST_RE.fullmatch(value):
        raise ValueError(f"malformed digest: {value!r}")
    return value


def self_test() -> None:
    with tempfile.TemporaryDirectory() as tmp:
        base = Path(tmp)
        target = base / "sample.bin"
        target.write_bytes(b"abc")
        expected = "sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        got = require_digest(file_digest(target))
        assert got == expected, got

        spaced = base / "has space.bin"
        spaced.write_bytes(b"abc")
        assert file_digest(spaced) == expected

        nested = base / "dir with space" / "nested.bin"
        nested.parent.mkdir(parents=True)
        nested.write_bytes(b"abc")
        assert file_digest(nested) == expected

        changed = base / "changed.bin"
        changed.write_bytes(b"abcd")
        assert file_digest(changed) != expected

        other = base / "other.bin"
        other.write_bytes(b"abc")
        assert file_digest(target) == file_digest(other)

        try:
            require_digest(
                "sha256:\\ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
            )
        except ValueError:
            pass
        else:
            raise AssertionError("leading backslash digest must be rejected")

        try:
            require_digest(
                "sha256:BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"
            )
        except ValueError:
            pass
        else:
            raise AssertionError("uppercase digest must be rejected")

    print("compute-file-sha256 self-test: ok")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", nargs="?", help="file to hash")
    parser.add_argument(
        "--expect",
        help="optional expected sha256:<hex>; exit 1 on mismatch",
    )
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args(argv)

    if args.self_test:
        self_test()
        return 0

    if not args.path:
        parser.error("path is required unless --self-test")

    digest = require_digest(file_digest(Path(args.path)))
    if args.expect is not None:
        expect = require_digest(args.expect)
        if digest != expect:
            print(
                f"[error] digest mismatch: got {digest} expected {expect}",
                file=sys.stderr,
            )
            return 1
    print(digest)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
