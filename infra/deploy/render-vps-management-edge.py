#!/usr/bin/env python3
"""Render the VPS Caddy edge from operator-provided GeoLite2 Country CSV files.

The renderer is deliberately stdlib-only and offline.  It consumes already downloaded
GeoLite2 CSV files and an already generated bcrypt hash; it never contacts MaxMind and
never accepts a plaintext password.  Only the derived Japan CIDRs and the bcrypt hash
are written to the generated Caddyfile.

Typical invocation::

    python3 infra/deploy/render-vps-management-edge.py \
        --ipv4-blocks GeoLite2-Country-Blocks-IPv4.csv \
        --ipv6-blocks GeoLite2-Country-Blocks-IPv6.csv \
        --locations GeoLite2-Country-Locations-en.csv \
        --basic-auth-username caddy-admin \
        --basic-auth-hash-file /run/operator-secrets/caddy-admin.bcrypt \
        --template infra/deploy/Caddyfile.vps-dogfood.example \
        --output infra/deploy/Caddyfile.vps-dogfood

Use ``--self-test`` for the offline renderer contract tests.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import ipaddress
import os
import re
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Iterable, Mapping, Sequence


class RenderError(ValueError):
    """A safe, value-free renderer validation error."""


_BCRYPT_RE = re.compile(r"\$2[aby]\$(?:0[4-9]|[12][0-9]|3[01])\$[./A-Za-z0-9]{53}\Z")
_BASIC_AUTH_USERNAME_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,63}\Z")
_COUNTRY_CODE_RE = re.compile(r"[A-Z]{2}\Z")
_UNKNOWN_GEONAME_VALUES = frozenset({"", "unknown", "null", "none", "-"})

_IPV4_MARKER = "{{JP_IPV4_CIDRS}}"
_IPV6_MARKER = "{{JP_IPV6_CIDRS}}"
_BASIC_USERNAME_MARKER = "{{CADDY_BASIC_AUTH_USERNAME}}"
_BASIC_HASH_MARKER = "{{CADDY_BASIC_AUTH_BCRYPT_HASH}}"
_REQUIRED_MARKERS = (
    _IPV4_MARKER,
    _IPV6_MARKER,
    _BASIC_USERNAME_MARKER,
    _BASIC_HASH_MARKER,
)


@dataclass(frozen=True)
class RenderedEdge:
    """Rendered content and value-free summary information."""

    content: str
    ipv4_count: int
    ipv6_count: int

    @property
    def content_bytes(self) -> bytes:
        return self.content.encode("utf-8")

    @property
    def byte_count(self) -> int:
        return len(self.content_bytes)

    @property
    def sha256(self) -> str:
        return hashlib.sha256(self.content_bytes).hexdigest()


def _read_csv_rows(
    path: Path,
    required_columns: Iterable[str],
    description: str,
) -> Iterable[tuple[int, Mapping[str, str]]]:
    """Yield normalized CSV rows without ever including row contents in errors."""

    try:
        handle = path.open("r", encoding="utf-8-sig", newline="")
    except OSError as exc:
        raise RenderError(f"unable to read {description} CSV") from exc

    with handle:
        try:
            reader = csv.DictReader(handle, strict=True)
            raw_fieldnames = reader.fieldnames
        except (csv.Error, UnicodeError) as exc:
            raise RenderError(f"malformed {description} CSV header") from exc

        if not raw_fieldnames:
            raise RenderError(f"{description} CSV header is missing")

        normalized_fieldnames = [
            field.strip() if field is not None else "" for field in raw_fieldnames
        ]
        if any(not field for field in normalized_fieldnames):
            raise RenderError(f"{description} CSV header contains an empty column")
        if len(set(normalized_fieldnames)) != len(normalized_fieldnames):
            raise RenderError(f"{description} CSV header contains duplicate columns")

        required = frozenset(required_columns)
        missing = sorted(required.difference(normalized_fieldnames))
        if missing:
            raise RenderError(f"{description} CSV is missing a required column")

        try:
            for row_number, raw_row in enumerate(reader, start=2):
                if None in raw_row:
                    raise RenderError(f"malformed {description} CSV row")
                if any(
                    normalized in required and raw_row.get(original) is None
                    for original, normalized in zip(raw_fieldnames, normalized_fieldnames)
                ):
                    raise RenderError(f"malformed {description} CSV row")
                row = {
                    normalized: (raw_row.get(original) or "").strip()
                    for original, normalized in zip(raw_fieldnames, normalized_fieldnames)
                }
                yield row_number, row
        except (csv.Error, UnicodeError) as exc:
            raise RenderError(f"malformed {description} CSV row") from exc


def _parse_location_id(value: str) -> int:
    if not re.fullmatch(r"[0-9]+", value):
        raise RenderError("malformed locations geoname mapping")
    try:
        parsed = int(value, 10)
    except ValueError as exc:
        raise RenderError("malformed locations geoname mapping") from exc
    if parsed < 0:
        raise RenderError("malformed locations geoname mapping")
    return parsed


def _parse_block_geoname_id(value: str) -> int | None:
    """Return a usable block ID; empty/unknown block IDs are deliberately excluded."""

    if value.casefold() in _UNKNOWN_GEONAME_VALUES or not re.fullmatch(r"[0-9]+", value):
        return None
    try:
        parsed = int(value, 10)
    except ValueError:
        return None
    return parsed if parsed >= 0 else None


def load_country_mapping(path: Path | str) -> dict[int, str | None]:
    """Load geoname_id -> country_iso_code, rejecting ambiguous mappings."""

    mapping: dict[int, str | None] = {}
    for _, row in _read_csv_rows(
        Path(path),
        ("geoname_id", "country_iso_code"),
        "locations",
    ):
        geoname_id = _parse_location_id(row["geoname_id"])
        country = row["country_iso_code"]
        # GeoLite locations also contains continent/other aggregate rows with no country.
        # Such rows are known but not JP; they remain fail-closed for block matching.
        if country and not _COUNTRY_CODE_RE.fullmatch(country):
            raise RenderError("malformed locations country mapping")

        if geoname_id in mapping and mapping[geoname_id] != (country or None):
            raise RenderError("conflicting geoname mapping")
        mapping[geoname_id] = country or None
    return mapping


def _read_japan_networks(
    path: Path | str,
    expected_version: int,
    country_mapping: Mapping[int, str | None],
) -> list[ipaddress.IPv4Network | ipaddress.IPv6Network]:
    networks: list[ipaddress.IPv4Network | ipaddress.IPv6Network] = []
    description = f"IPv{expected_version} blocks"
    for _, row in _read_csv_rows(Path(path), ("network", "geoname_id"), description):
        network_text = row["network"]
        if (
            network_text.count("/") != 1
            or not re.fullmatch(r"[0-9]+", network_text.rsplit("/", 1)[1])
        ):
            raise RenderError(f"invalid IPv{expected_version} CIDR")
        try:
            network = ipaddress.ip_network(network_text, strict=True)
        except ValueError as exc:
            raise RenderError(f"invalid IPv{expected_version} CIDR") from exc
        if network.version != expected_version:
            raise RenderError(f"IPv{expected_version} blocks contain the wrong address family")
        if network.prefixlen == 0:
            raise RenderError(f"IPv{expected_version} default route is not allowed")

        geoname_id = _parse_block_geoname_id(row["geoname_id"])
        if geoname_id is not None and country_mapping.get(geoname_id) == "JP":
            networks.append(network)
    return networks


def _collapse_networks(
    networks: Sequence[ipaddress.IPv4Network | ipaddress.IPv6Network],
    expected_version: int,
) -> tuple[str, ...]:
    collapsed = tuple(ipaddress.collapse_addresses(networks))
    if any(network.version != expected_version or network.prefixlen == 0 for network in collapsed):
        raise RenderError(f"IPv{expected_version} rendered CIDR is not allowed")
    ordered = sorted(
        collapsed,
        key=lambda network: (int(network.network_address), network.prefixlen),
    )
    return tuple(str(network) for network in ordered)


def validate_bcrypt_hash(value: str) -> str:
    """Validate and return an already-generated bcrypt hash without exposing it."""

    normalized = value.strip()
    if not _BCRYPT_RE.fullmatch(normalized):
        raise RenderError("Basic Auth bcrypt hash is missing or invalid")
    return normalized


def validate_basic_auth_username(value: str) -> str:
    """Validate a non-secret username that is safe as one Caddyfile token."""

    if not _BASIC_AUTH_USERNAME_RE.fullmatch(value):
        raise RenderError("Basic Auth username is missing or invalid")
    return value


def _read_bcrypt_hash_file(path: Path | str) -> str:
    try:
        raw = Path(path).read_text(encoding="utf-8")
    except (OSError, UnicodeError) as exc:
        raise RenderError("unable to read Basic Auth bcrypt hash file") from exc
    return validate_bcrypt_hash(raw)


def _cidr_marker_replacement(template_text: str, marker: str, cidrs: Sequence[str]) -> str:
    """Render one marker as bounded remote_ip directives when it occupies a whole line."""

    marker_index = template_text.index(marker)
    line_start = template_text.rfind("\n", 0, marker_index) + 1
    line_end = template_text.find("\n", marker_index)
    if line_end < 0:
        line_end = len(template_text)
    line = template_text[line_start:line_end]
    if line.strip() != marker:
        # Also accept a compact custom template using `remote_ip {{...}}`; this keeps the
        # renderer useful for small fixtures while the checked-in template uses one directive
        # per range to avoid an unbounded Caddyfile line.
        if "remote_ip" not in line:
            raise RenderError("Caddy CIDR marker is not attached to remote_ip")
        return " ".join(cidrs)

    indentation = line[: len(line) - len(line.lstrip())]
    return "\n".join(f"{indentation}remote_ip {cidr}" for cidr in cidrs)


def render_caddyfile(
    ipv4_blocks: Path | str,
    ipv6_blocks: Path | str,
    locations: Path | str,
    template: Path | str,
    basic_auth_username: str,
    basic_auth_hash: str,
) -> RenderedEdge:
    """Render a Caddy template from GeoLite-derived JP CIDRs and a bcrypt hash."""

    validated_username = validate_basic_auth_username(basic_auth_username)
    validated_hash = validate_bcrypt_hash(basic_auth_hash)
    country_mapping = load_country_mapping(locations)
    ipv4_networks = _read_japan_networks(ipv4_blocks, 4, country_mapping)
    ipv6_networks = _read_japan_networks(ipv6_blocks, 6, country_mapping)
    ipv4_cidrs = _collapse_networks(ipv4_networks, 4)
    ipv6_cidrs = _collapse_networks(ipv6_networks, 6)
    if not ipv4_cidrs and not ipv6_cidrs:
        raise RenderError("no JP CIDRs were found")

    try:
        template_text = Path(template).read_text(encoding="utf-8")
    except (OSError, UnicodeError) as exc:
        raise RenderError("unable to read Caddy template") from exc

    for marker in _REQUIRED_MARKERS:
        if template_text.count(marker) != 1:
            raise RenderError("Caddy template is missing or duplicates a renderer marker")

    replacements = {
        _IPV4_MARKER: _cidr_marker_replacement(template_text, _IPV4_MARKER, ipv4_cidrs),
        _IPV6_MARKER: _cidr_marker_replacement(template_text, _IPV6_MARKER, ipv6_cidrs),
        _BASIC_USERNAME_MARKER: validated_username,
        _BASIC_HASH_MARKER: validated_hash,
    }
    rendered = template_text
    for marker, value in replacements.items():
        rendered = rendered.replace(marker, value)

    if any(marker in rendered for marker in _REQUIRED_MARKERS):
        raise RenderError("Caddy template renderer markers remain after rendering")
    return RenderedEdge(rendered, len(ipv4_cidrs), len(ipv6_cidrs))


def write_rendered_edge(output: Path | str, rendered: RenderedEdge) -> None:
    """Atomically write the ignored runtime artifact with owner-only permissions."""

    destination = Path(output)
    try:
        destination.parent.mkdir(parents=True, exist_ok=True)
        fd, temporary_name = tempfile.mkstemp(
            prefix=f".{destination.name}.",
            suffix=".tmp",
            dir=destination.parent,
        )
        temporary_path = Path(temporary_name)
        try:
            os.fchmod(fd, 0o600)
            with os.fdopen(fd, "w", encoding="utf-8", newline="") as handle:
                handle.write(rendered.content)
            os.replace(temporary_path, destination)
        except BaseException:
            try:
                os.close(fd)
            except OSError:
                pass
            try:
                temporary_path.unlink()
            except OSError:
                pass
            raise
    except OSError as exc:
        raise RenderError("unable to write generated Caddyfile") from exc


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--ipv4-blocks",
        "--blocks-ipv4",
        "--ipv4-csv",
        "--ipv4-blocks-csv",
        dest="ipv4_blocks",
        help="GeoLite2 Country IPv4 blocks CSV",
    )
    parser.add_argument(
        "--ipv6-blocks",
        "--blocks-ipv6",
        "--ipv6-csv",
        "--ipv6-blocks-csv",
        dest="ipv6_blocks",
        help="GeoLite2 Country IPv6 blocks CSV",
    )
    parser.add_argument(
        "--locations",
        "--locations-csv",
        "--locations-en-csv",
        dest="locations",
        help="GeoLite2 Country locations-en CSV",
    )
    hash_group = parser.add_mutually_exclusive_group()
    hash_group.add_argument(
        "--basic-auth-hash-file",
        "--basic-hash-file",
        "--bcrypt-hash-file",
        dest="basic_auth_hash_file",
        help="file containing an already-generated bcrypt hash",
    )
    hash_group.add_argument(
        "--basic-auth-hash",
        "--basic-auth-bcrypt-hash",
        dest="basic_auth_hash",
        help="already-generated bcrypt hash (a hash file is preferred)",
    )
    parser.add_argument(
        "--basic-auth-username",
        "--basic-username",
        "--caddy-basic-auth-username",
        dest="basic_auth_username",
        help="non-secret username for the Caddy Basic Auth entry",
    )
    parser.add_argument(
        "--template",
        "--caddy-template",
        "--caddyfile-template",
        dest="template",
        help="Caddyfile template containing renderer markers",
    )
    parser.add_argument(
        "--output",
        "--caddyfile",
        "--output-caddyfile",
        dest="output",
        help="ignored generated Caddyfile path",
    )
    parser.add_argument(
        "--self-test",
        action="store_true",
        help="run offline renderer self-tests",
    )
    parser.add_argument(
        "--self-test-output",
        metavar="PATH",
        help="write the self-test's generated Caddyfile to PATH (only with --self-test)",
    )
    return parser


def _write_fixture(path: Path, content: str) -> None:
    path.write_text(content, encoding="utf-8", newline="")


def _expect_render_error(label: str, operation: Callable[[], object]) -> None:
    try:
        operation()
    except RenderError:
        return
    raise AssertionError(f"expected failure: {label}")


def run_self_test(generated_output: Path | str | None = None) -> None:
    """Exercise the positive and fail-closed renderer contract using local fixtures."""

    basic_auth_username = "caddy-test-admin"
    bcrypt_hash = "$2b$12$" + "A" * 53
    plaintext_secret = "self-test-plaintext-secret-never-rendered"
    template_text = """{$MAILER_PUBLIC_HOSTNAME} {
    @browser_jp {
        path /admin /admin/* /setup /setup/*
        {{JP_IPV4_CIDRS}}
        {{JP_IPV6_CIDRS}}
    }
    handle @browser_jp {
        basic_auth {
            {{CADDY_BASIC_AUTH_USERNAME}} {{CADDY_BASIC_AUTH_BCRYPT_HASH}}
        }
        reverse_proxy mailer:8080 {
            header_up -Authorization
        }
    }
}
"""

    with tempfile.TemporaryDirectory(prefix="render-vps-management-edge-self-test-") as root:
        directory = Path(root)
        ipv4 = directory / "GeoLite2-Country-Blocks-IPv4.csv"
        ipv6 = directory / "GeoLite2-Country-Blocks-IPv6.csv"
        locations = directory / "GeoLite2-Country-Locations-en.csv"
        template = directory / "Caddyfile.template"
        hash_file = directory / "caddy-admin.bcrypt"

        _write_fixture(
            locations,
            "geoname_id,country_iso_code\n"
            "1,JP\n"
            "2,US\n"
            "3,JP\n",
        )
        ipv4_content = (
            "network,geoname_id\n"
            "198.51.100.0/25,1\n"
            "198.51.100.0/25,1\n"
            "198.51.100.128/25,1\n"
            "203.0.113.0/24,2\n"
            "203.0.114.0/24,999999\n"
        )
        ipv6_content = (
            "network,geoname_id\n"
            "2001:db8:1::/65,1\n"
            "2001:db8:1:0:8000::/65,1\n"
            "2001:db8:2::/64,3\n"
        )
        _write_fixture(ipv4, ipv4_content)
        _write_fixture(ipv6, ipv6_content)
        _write_fixture(template, template_text)
        _write_fixture(hash_file, bcrypt_hash + "\n")
        assert _read_bcrypt_hash_file(hash_file) == bcrypt_hash, "bcrypt hash file read failed"

        first = render_caddyfile(
            ipv4,
            ipv6,
            locations,
            template,
            basic_auth_username,
            bcrypt_hash,
        )
        _write_fixture(ipv4, "network,geoname_id\n" + "".join(reversed(ipv4_content.splitlines(True)[1:])))
        _write_fixture(ipv6, "network,geoname_id\n" + "".join(reversed(ipv6_content.splitlines(True)[1:])))
        second = render_caddyfile(
            ipv4,
            ipv6,
            locations,
            template,
            basic_auth_username,
            bcrypt_hash,
        )

        assert first.content == second.content, "output is not deterministic"
        assert first.ipv4_count == 1, "duplicate/collapse did not produce one IPv4 CIDR"
        assert first.ipv6_count == 2, "IPv6 JP CIDR count is incorrect"
        assert "198.51.100.0/24" in first.content, "IPv4 collapse is missing"
        assert "203.0.113.0/24" not in first.content, "non-JP range was included"
        assert "203.0.114.0/24" not in first.content, "unknown geoname range was included"
        assert bcrypt_hash in first.content, "bcrypt hash is missing from output"
        assert plaintext_secret not in first.content, "plaintext fixture secret was rendered"
        assert "geoname_id" not in first.content, "raw GeoLite column was rendered"
        assert "country_iso_code" not in first.content, "raw GeoLite column was rendered"

        fallback_blocks = directory / "fallback-blocks.csv"
        _write_fixture(
            fallback_blocks,
            "network,geoname_id,registered_country_geoname_id,represented_country_geoname_id\n"
            "203.0.115.0/24,,1,1\n",
        )
        fallback_rendered = render_caddyfile(
            fallback_blocks,
            ipv6,
            locations,
            template,
            basic_auth_username,
            bcrypt_hash,
        )
        assert "203.0.115.0/24" not in fallback_rendered.content, "fallback geonames were used"

        repository_template = Path(__file__).with_name("Caddyfile.vps-dogfood.example")
        repository_rendered = render_caddyfile(
            ipv4,
            ipv6,
            locations,
            repository_template,
            basic_auth_username,
            bcrypt_hash,
        )
        assert "remote_ip 198.51.100.0/24" in repository_rendered.content
        assert "remote_ip 2001:db8:1::/64" in repository_rendered.content
        assert "remote_ip 2001:db8:2::/64" in repository_rendered.content
        assert "header_up -Authorization" in repository_rendered.content
        assert basic_auth_username in repository_rendered.content
        assert basic_auth_username + " " + bcrypt_hash in repository_rendered.content
        assert "{{JP_IPV4_CIDRS}}" not in repository_rendered.content
        assert "{{JP_IPV6_CIDRS}}" not in repository_rendered.content
        assert "{{CADDY_BASIC_AUTH_USERNAME}}" not in repository_rendered.content
        assert "{{CADDY_BASIC_AUTH_BCRYPT_HASH}}" not in repository_rendered.content

        for invalid_username in ("", " ", "caddy admin", "caddy\nadmin", "caddy{admin}"):
            _expect_render_error(
                "invalid Basic Auth username",
                lambda invalid_username=invalid_username: render_caddyfile(
                    ipv4,
                    ipv6,
                    locations,
                    template,
                    invalid_username,
                    bcrypt_hash,
                ),
            )

        locations_us = directory / "locations-us.csv"
        _write_fixture(locations_us, "geoname_id,country_iso_code\n2,US\n")
        _expect_render_error(
            "zero JP ranges",
            lambda: render_caddyfile(
                ipv4,
                ipv6,
                locations_us,
                template,
                basic_auth_username,
                bcrypt_hash,
            ),
        )

        invalid_cidr = directory / "invalid-cidr.csv"
        _write_fixture(invalid_cidr, "network,geoname_id\nnot-a-cidr,1\n")
        _expect_render_error(
            "invalid CIDR",
            lambda: render_caddyfile(
                invalid_cidr,
                ipv6,
                locations,
                template,
                basic_auth_username,
                bcrypt_hash,
            ),
        )
        default_ipv4 = directory / "default-ipv4.csv"
        _write_fixture(default_ipv4, "network,geoname_id\n0.0.0.0/0,1\n")
        _expect_render_error(
            "IPv4 default route",
            lambda: render_caddyfile(
                default_ipv4,
                ipv6,
                locations,
                template,
                basic_auth_username,
                bcrypt_hash,
            ),
        )
        default_ipv6 = directory / "default-ipv6.csv"
        _write_fixture(default_ipv6, "network,geoname_id\n::/0,1\n")
        _expect_render_error(
            "IPv6 default route",
            lambda: render_caddyfile(
                ipv4,
                default_ipv6,
                locations,
                template,
                basic_auth_username,
                bcrypt_hash,
            ),
        )

        missing_blocks_column = directory / "missing-block-column.csv"
        _write_fixture(missing_blocks_column, "network\n198.51.100.0/24\n")
        _expect_render_error(
            "missing blocks column",
            lambda: render_caddyfile(
                missing_blocks_column,
                ipv6,
                locations,
                template,
                basic_auth_username,
                bcrypt_hash,
            ),
        )
        missing_locations_column = directory / "missing-locations-column.csv"
        _write_fixture(missing_locations_column, "geoname_id\n1\n")
        _expect_render_error(
            "missing locations column",
            lambda: render_caddyfile(
                ipv4,
                ipv6,
                missing_locations_column,
                template,
                basic_auth_username,
                bcrypt_hash,
            ),
        )
        _expect_render_error(
            "missing Basic Auth hash",
            lambda: render_caddyfile(
                ipv4,
                ipv6,
                locations,
                template,
                basic_auth_username,
                "",
            ),
        )
        _expect_render_error(
            "invalid Basic Auth hash",
            lambda: render_caddyfile(
                ipv4,
                ipv6,
                locations,
                template,
                basic_auth_username,
                "plain-password",
            ),
        )

        malformed_locations = directory / "malformed-locations.csv"
        _write_fixture(malformed_locations, "geoname_id,country_iso_code\nnot-an-id,JP\n")
        _expect_render_error(
            "malformed locations mapping",
            lambda: render_caddyfile(
                ipv4,
                ipv6,
                malformed_locations,
                template,
                basic_auth_username,
                bcrypt_hash,
            ),
        )
        conflicting_locations = directory / "conflicting-locations.csv"
        _write_fixture(conflicting_locations, "geoname_id,country_iso_code\n1,JP\n1,US\n")
        _expect_render_error(
            "conflicting geoname mapping",
            lambda: render_caddyfile(
                ipv4,
                ipv6,
                conflicting_locations,
                template,
                basic_auth_username,
                bcrypt_hash,
            ),
        )

        output = directory / "nested" / "Caddyfile.vps-dogfood"
        write_rendered_edge(output, first)
        assert output.read_text(encoding="utf-8") == first.content, "output write failed"
        assert (output.stat().st_mode & 0o777) == 0o600, "output permissions are too broad"

        import contextlib
        import io

        cli_output = directory / "cli" / "Caddyfile.vps-dogfood"
        summary = io.StringIO()
        with contextlib.redirect_stdout(summary):
            exit_code = _run(
                [
                    "--ipv4-blocks",
                    str(ipv4),
                    "--ipv6-blocks",
                    str(ipv6),
                    "--locations",
                    str(locations),
                    "--basic-auth-username",
                    basic_auth_username,
                    "--basic-auth-hash-file",
                    str(hash_file),
                    "--template",
                    str(repository_template),
                    "--output",
                    str(cli_output),
                ]
            )
        assert exit_code == 0, "CLI render failed"
        assert bcrypt_hash not in summary.getvalue(), "CLI summary leaked the bcrypt hash"
        assert plaintext_secret not in summary.getvalue(), "CLI summary leaked the plaintext fixture"
        assert "IPv4 CIDR count:" in summary.getvalue(), "CLI summary omitted IPv4 count"
        assert "IPv6 CIDR count:" in summary.getvalue(), "CLI summary omitted IPv6 count"
        assert "output bytes:" in summary.getvalue(), "CLI summary omitted output size"
        assert "SHA-256:" in summary.getvalue(), "CLI summary omitted output digest"

        if generated_output is not None:
            write_rendered_edge(generated_output, repository_rendered)


def _run(argv: Sequence[str] | None = None) -> int:
    parser = _build_parser()
    args = parser.parse_args(argv)
    if args.self_test:
        run_self_test(args.self_test_output)
        print("render-vps-management-edge self-test: PASS")
        return 0
    if args.self_test_output:
        parser.error("--self-test-output requires --self-test")

    required = {
        "--ipv4-blocks": args.ipv4_blocks,
        "--ipv6-blocks": args.ipv6_blocks,
        "--locations": args.locations,
        "--basic-auth-username": args.basic_auth_username,
        "--template": args.template,
        "--output": args.output,
    }
    missing = [name for name, value in required.items() if not value]
    if not args.basic_auth_hash_file and not args.basic_auth_hash:
        missing.append("--basic-auth-hash-file or --basic-auth-hash")
    if missing:
        parser.error("missing required argument(s): " + ", ".join(missing))

    basic_hash = (
        _read_bcrypt_hash_file(args.basic_auth_hash_file)
        if args.basic_auth_hash_file
        else validate_bcrypt_hash(args.basic_auth_hash)
    )
    rendered = render_caddyfile(
        args.ipv4_blocks,
        args.ipv6_blocks,
        args.locations,
        args.template,
        args.basic_auth_username,
        basic_hash,
    )
    write_rendered_edge(args.output, rendered)
    print(f"IPv4 CIDR count: {rendered.ipv4_count}")
    print(f"IPv6 CIDR count: {rendered.ipv6_count}")
    print(f"output bytes: {rendered.byte_count}")
    print(f"SHA-256: {rendered.sha256}")
    return 0


def main(argv: Sequence[str] | None = None) -> int:
    try:
        return _run(argv)
    except RenderError as exc:
        print(f"render-vps-management-edge: FAIL: {exc}", file=sys.stderr)
        return 1
    except AssertionError as exc:
        print(f"render-vps-management-edge self-test: FAIL: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
