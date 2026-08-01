#!/usr/bin/env python3
"""Create a POSIX-entry ZIP for Easy Setup win-x64 host archives (#458).

Compress-Archive on Windows runners stores backslash separators. Git Bash unzip
then warns "appears to use backslashes as path separators" and exits 1
(Candidate attempt 3). Emit forward-slash entry names via zipfile instead.
"""
from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path, PurePosixPath

REQUIRED_ROOT = "win-x64"


def _is_unsafe_path(path: Path) -> bool:
    if path.is_symlink():
        return True
    try:
        if path.is_junction():  # type: ignore[attr-defined]
            return True
    except (AttributeError, NotImplementedError, OSError):
        pass
    return False


def validate_archive_name(archive_name: str, root_name: str = REQUIRED_ROOT) -> None:
    if not archive_name:
        raise RuntimeError("empty archive entry name")
    if "\\" in archive_name:
        raise RuntimeError(f"backslash entry is not allowed: {archive_name!r}")
    if archive_name.startswith("/") or archive_name.startswith("\\"):
        raise RuntimeError(f"absolute entry is not allowed: {archive_name!r}")
    if any(ord(ch) < 32 for ch in archive_name):
        raise RuntimeError(f"control character in entry name: {archive_name!r}")

    posix = PurePosixPath(archive_name)
    parts = posix.parts
    if not parts:
        raise RuntimeError(f"empty archive entry parts: {archive_name!r}")
    if parts[0] != root_name:
        raise RuntimeError(
            f"top-level directory must be {root_name}/, got {archive_name!r}"
        )
    if any(part in ("", ".", "..") for part in parts):
        raise RuntimeError(f"traversal or empty segment in entry: {archive_name!r}")
    if any(":" in part for part in parts):
        raise RuntimeError(f"drive letter or colon in entry: {archive_name!r}")


def create_posix_zip(source_dir: Path, destination: Path) -> None:
    source = source_dir.resolve()
    dest = destination
    if not source.is_dir():
        raise RuntimeError(f"source is not a directory: {source}")
    if source.name != REQUIRED_ROOT:
        raise RuntimeError(
            f"source directory must be named {REQUIRED_ROOT}, got {source.name!r}"
        )
    if _is_unsafe_path(source):
        raise RuntimeError(f"symlink/reparse source is not allowed: {source}")

    dest.parent.mkdir(parents=True, exist_ok=True)
    if dest.exists():
        dest.unlink()

    members: list[tuple[Path, str]] = []
    for path in sorted(source.rglob("*")):
        if _is_unsafe_path(path):
            raise RuntimeError(f"symlink/reparse is not allowed: {path}")
        try:
            relative = path.relative_to(source)
        except ValueError as exc:
            raise RuntimeError(f"path escapes source tree: {path}") from exc

        archive_name = PurePosixPath(REQUIRED_ROOT, *relative.parts).as_posix()
        validate_archive_name(archive_name)

        if path.is_dir():
            continue
        if not path.is_file():
            raise RuntimeError(f"unsupported path type: {path}")
        members.append((path, archive_name))

    if not members:
        raise RuntimeError(f"source has no files to archive: {source}")

    with zipfile.ZipFile(dest, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for path, archive_name in members:
            archive.write(path, arcname=archive_name)

    with zipfile.ZipFile(dest, "r") as archive:
        bad = archive.testzip()
        if bad is not None:
            raise RuntimeError(f"zip testzip failed for entry: {bad}")
        names = archive.namelist()
        if not names:
            raise RuntimeError("zip has no entries")
        for name in names:
            validate_archive_name(name)
            if name.endswith("/"):
                continue
            info = archive.getinfo(name)
            if info.is_dir():
                continue
            # Ensure stored payload matches source bytes for every file entry.
            relative = PurePosixPath(name).relative_to(REQUIRED_ROOT)
            source_file = source.joinpath(*relative.parts)
            if not source_file.is_file():
                raise RuntimeError(f"zip entry has no source file: {name}")
            if archive.read(name) != source_file.read_bytes():
                raise RuntimeError(f"zip payload mismatch: {name}")


def _extract_tree_bytes(root: Path) -> dict[str, bytes]:
    out: dict[str, bytes] = {}
    for path in sorted(root.rglob("*")):
        if path.is_dir():
            continue
        rel = path.relative_to(root).as_posix()
        out[rel] = path.read_bytes()
    return out


def self_test() -> None:
    with tempfile.TemporaryDirectory() as tmp:
        base = Path(tmp)
        source = base / REQUIRED_ROOT
        nested = source / "docs" / "nested"
        nested.mkdir(parents=True)
        (source / "Amane.Mailer.exe").write_bytes(b"MZ-binary-payload")
        (nested / "readme.txt").write_bytes(b"hello\n")
        (source / "empty-dir").mkdir()

        dest = base / "out.zip"
        create_posix_zip(source, dest)

        with zipfile.ZipFile(dest, "r") as archive:
            names = archive.namelist()
            assert all("\\" not in name for name in names), names
            assert all(name.startswith(f"{REQUIRED_ROOT}/") for name in names), names
            assert all(not name.startswith("/") for name in names), names
            assert ".." not in PurePosixPath(names[0]).parts
            assert archive.getinfo(f"{REQUIRED_ROOT}/Amane.Mailer.exe").file_size == len(
                b"MZ-binary-payload"
            )
            assert archive.read(f"{REQUIRED_ROOT}/docs/nested/readme.txt") == b"hello\n"

        unzip = shutil.which("unzip")
        if unzip:
            probe = subprocess.run(
                [unzip, "-t", str(dest)],
                capture_output=True,
                text=True,
                check=False,
            )
            assert probe.returncode == 0, probe.stdout + probe.stderr

            extract_dir = base / "extracted"
            extract_dir.mkdir()
            probe = subprocess.run(
                [unzip, "-q", str(dest), "-d", str(extract_dir)],
                capture_output=True,
                text=True,
                check=False,
            )
            assert probe.returncode == 0, probe.stdout + probe.stderr
            assert _extract_tree_bytes(extract_dir / REQUIRED_ROOT) == _extract_tree_bytes(
                source
            )
        else:
            # Fallback extract via zipfile when unzip is unavailable locally.
            extract_dir = base / "extracted-zf"
            extract_dir.mkdir()
            with zipfile.ZipFile(dest, "r") as archive:
                archive.extractall(extract_dir)
            assert _extract_tree_bytes(extract_dir / REQUIRED_ROOT) == _extract_tree_bytes(
                source
            )

        # Reject empty source
        empty = base / "empty" / REQUIRED_ROOT
        empty.mkdir(parents=True)
        try:
            create_posix_zip(empty, base / "empty.zip")
        except RuntimeError:
            pass
        else:
            raise AssertionError("empty source must be rejected")

        # Reject wrong root name
        wrong = base / "linux-x64"
        wrong.mkdir()
        (wrong / "bin").write_bytes(b"x")
        try:
            create_posix_zip(wrong, base / "wrong.zip")
        except RuntimeError:
            pass
        else:
            raise AssertionError("non-win-x64 root must be rejected")

        # Reject symlink when the platform can create one without elevation.
        if hasattr(os, "symlink"):
            link_root = base / "link-src" / REQUIRED_ROOT
            link_root.mkdir(parents=True)
            target = link_root / "real.bin"
            target.write_bytes(b"real")
            link = link_root / "link.bin"
            try:
                os.symlink(target.name, link)
            except (OSError, NotImplementedError):
                # Do not pretend PASS: skip only when creation is impossible.
                print("create-setup-release-zip self-test: symlink create skipped")
            else:
                try:
                    create_posix_zip(link_root, base / "link.zip")
                except RuntimeError:
                    pass
                else:
                    raise AssertionError("symlink member must be rejected")

        # validate_archive_name contract unit checks
        try:
            validate_archive_name(r"win-x64\bad.txt")
        except RuntimeError:
            pass
        else:
            raise AssertionError("backslash name must be rejected")

        try:
            validate_archive_name("/win-x64/abs.txt")
        except RuntimeError:
            pass
        else:
            raise AssertionError("absolute name must be rejected")

        try:
            validate_archive_name("win-x64/../escape.txt")
        except RuntimeError:
            pass
        else:
            raise AssertionError("traversal name must be rejected")

        try:
            validate_archive_name("C:/win-x64/x.txt")
        except RuntimeError:
            pass
        else:
            raise AssertionError("drive-style name must be rejected")

    print("create-setup-release-zip self-test: ok")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source_dir", nargs="?", help="staged win-x64 directory")
    parser.add_argument("destination", nargs="?", help="output .zip path")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args(argv)

    if args.self_test:
        self_test()
        return 0

    if not args.source_dir or not args.destination:
        parser.error("source_dir and destination are required unless --self-test")

    create_posix_zip(Path(args.source_dir), Path(args.destination))
    print(f"[info] Wrote POSIX ZIP {args.destination}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
