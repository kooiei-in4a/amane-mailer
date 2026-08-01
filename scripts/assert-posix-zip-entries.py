#!/usr/bin/env python3
"""Assert Easy Setup win-x64 ZIP entries use portable POSIX names (#458)."""
from __future__ import annotations

import argparse
import sys
import zipfile
from pathlib import PurePosixPath


def assert_posix_win_x64_zip(path: str, rid: str = "win-x64") -> None:
    with zipfile.ZipFile(path, "r") as zf:
        bad = zf.testzip()
        if bad is not None:
            raise SystemExit(f"zip testzip failed: {bad}")
        names = zf.namelist()
        if not names:
            raise SystemExit("zip has no entries")
        for name in names:
            if chr(92) in name:
                raise SystemExit(f"backslash entry: {name!r}")
            if name.startswith("/") or name.startswith(chr(92)):
                raise SystemExit(f"absolute entry: {name!r}")
            parts = PurePosixPath(name).parts
            if not parts or parts[0] != rid:
                raise SystemExit(f"top-level must be {rid}/: {name!r}")
            if any(p in ("", ".", "..") for p in parts):
                raise SystemExit(f"traversal entry: {name!r}")
            if any(":" in p for p in parts):
                raise SystemExit(f"drive/colon entry: {name!r}")
            if "oci" in parts:
                raise SystemExit(f"oci layout must not appear in host zip: {name!r}")
    print("zip-entries-ok")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("zip_path")
    parser.add_argument("--rid", default="win-x64")
    args = parser.parse_args(argv)
    assert_posix_win_x64_zip(args.zip_path, rid=args.rid)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
