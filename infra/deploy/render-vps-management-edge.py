#!/usr/bin/env python3
"""Render the VPS Caddy edge from operator-provided IPdeny JP zone files.

The renderer is deliberately stdlib-only and offline. It consumes already downloaded
IPv4/IPv6 zone files and an already generated bcrypt hash; it never downloads the zones
and never accepts a plaintext password. Only normalized JP CIDRs and the bcrypt hash are
written to the generated Caddyfile.

Typical invocation::

    python3 infra/deploy/render-vps-management-edge.py \
        --ipv4-zone jp-aggregated-ipv4.zone \
        --ipv6-zone jp-aggregated-ipv6.zone \
        --basic-auth-username caddy-admin \
        --basic-auth-hash-file /run/operator-secrets/caddy-admin.bcrypt \
        --template infra/deploy/Caddyfile.vps-dogfood.example \
        --output infra/deploy/Caddyfile.vps-dogfood

Use ``--self-test`` for the offline renderer contract tests.
"""

from __future__ import annotations

import argparse
import hashlib
import ipaddress
import os
import re
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Sequence


class RenderError(ValueError):
    """A safe, value-free renderer validation error."""


_BCRYPT_RE = re.compile(r"\$2[aby]\$(?:0[4-9]|[12][0-9]|3[01])\$[./A-Za-z0-9]{53}\Z")
_BASIC_AUTH_USERNAME_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,63}\Z")

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


def _read_zone_networks(
    path: Path | str,
    expected_version: int,
) -> list[ipaddress.IPv4Network | ipaddress.IPv6Network]:
    """Read a strict one-CIDR-per-line zone file for one address family."""

    try:
        contents = Path(path).read_text(encoding="utf-8-sig")
    except (OSError, UnicodeError) as exc:
        raise RenderError(f"unable to read IPv{expected_version} zone file") from exc

    networks: list[ipaddress.IPv4Network | ipaddress.IPv6Network] = []
    for line in contents.splitlines():
        stripped = line.strip()
        if not stripped:
            continue

        fields = stripped.split()
        if len(fields) != 1:
            raise RenderError(f"malformed IPv{expected_version} zone line")
        network_text = fields[0]
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
            raise RenderError(f"IPv{expected_version} zone contains the wrong address family")
        if network.prefixlen == 0:
            raise RenderError(f"IPv{expected_version} default route is not allowed")
        networks.append(network)

    if not networks:
        raise RenderError(f"IPv{expected_version} zone contains no CIDRs")
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
    ipv4_zone: Path | str,
    ipv6_zone: Path | str,
    template: Path | str,
    basic_auth_username: str,
    basic_auth_hash: str,
) -> RenderedEdge:
    """Render a Caddy template from IPdeny-derived JP CIDRs and a bcrypt hash."""

    validated_username = validate_basic_auth_username(basic_auth_username)
    validated_hash = validate_bcrypt_hash(basic_auth_hash)
    ipv4_networks = _read_zone_networks(ipv4_zone, 4)
    ipv6_networks = _read_zone_networks(ipv6_zone, 6)
    ipv4_cidrs = _collapse_networks(ipv4_networks, 4)
    ipv6_cidrs = _collapse_networks(ipv6_networks, 6)
    if not ipv4_cidrs:
        raise RenderError("no IPv4 CIDRs were found")
    if not ipv6_cidrs:
        raise RenderError("no IPv6 CIDRs were found")

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

    if "{{" in rendered or "}}" in rendered:
        raise RenderError("Caddy template contains an unresolved marker")
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
        "--ipv4-zone",
        "--ipdeny-ipv4-zone",
        dest="ipv4_zone",
        help="IPdeny aggregated IPv4 zone file (one CIDR per line)",
    )
    parser.add_argument(
        "--ipv6-zone",
        "--ipdeny-ipv6-zone",
        dest="ipv6_zone",
        help="IPdeny aggregated IPv6 zone file (one CIDR per line)",
    )
    parser.add_argument(
        "--basic-auth-hash-file",
        dest="basic_auth_hash_file",
        help="file containing an already-generated bcrypt hash",
    )
    parser.add_argument(
        "--basic-auth-username",
        dest="basic_auth_username",
        help="non-secret username for the Caddy Basic Auth entry",
    )
    parser.add_argument(
        "--template",
        dest="template",
        help="Caddyfile template containing renderer markers",
    )
    parser.add_argument(
        "--output",
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
        ipv4 = directory / "jp-ipv4.zone"
        ipv6 = directory / "jp-ipv6.zone"
        template = directory / "Caddyfile.template"
        hash_file = directory / "caddy-admin.bcrypt"

        ipv4_content = (
            "\n"
            "  \n"
            "203.0.113.128/25\n"
            "198.51.100.128/25\n"
            "198.51.100.0/25\n"
            "198.51.100.0/25\n"
            "203.0.113.0/25\n"
        )
        ipv6_content = (
            "\n"
            "2001:db8:2::/64\n"
            "2001:db8:1:0:8000::/65\n"
            "2001:db8:1::/65\n"
            "2001:db8:1::/65\n"
        )
        _write_fixture(ipv4, ipv4_content)
        _write_fixture(ipv6, ipv6_content)
        _write_fixture(template, template_text)
        _write_fixture(hash_file, bcrypt_hash + "\n")
        assert _read_bcrypt_hash_file(hash_file) == bcrypt_hash, "bcrypt hash file read failed"

        first = render_caddyfile(ipv4, ipv6, template, basic_auth_username, bcrypt_hash)
        _write_fixture(ipv4, "\n".join(reversed(ipv4_content.splitlines())) + "\n")
        _write_fixture(ipv6, "\n".join(reversed(ipv6_content.splitlines())) + "\n")
        second = render_caddyfile(ipv4, ipv6, template, basic_auth_username, bcrypt_hash)

        assert first.content == second.content, "output is not deterministic"
        assert first.ipv4_count == 2, "duplicate/adjacent collapse did not produce two IPv4 CIDRs"
        assert first.ipv6_count == 2, "IPv6 duplicate/adjacent collapse did not produce two CIDRs"
        assert first.content.count("198.51.100.0/24") == 1, "IPv4 duplicate was retained"
        assert first.content.count("2001:db8:1::/64") == 1, "IPv6 duplicate was retained"
        assert "203.0.113.0/24" in first.content, "IPv4 adjacent collapse is missing"
        assert "2001:db8:2::/64" in first.content, "IPv6 CIDR is missing"
        assert first.content.index("198.51.100.0/24") < first.content.index("203.0.113.0/24")
        assert bcrypt_hash in first.content, "bcrypt hash is missing from output"
        assert plaintext_secret not in first.content, "plaintext fixture secret was rendered"
        assert "{{" not in first.content and "}}" not in first.content

        repository_template = Path(__file__).with_name("Caddyfile.vps-dogfood.example")
        repository_rendered = render_caddyfile(
            ipv4,
            ipv6,
            repository_template,
            basic_auth_username,
            bcrypt_hash,
        )
        assert "remote_ip 198.51.100.0/24" in repository_rendered.content
        assert "remote_ip 2001:db8:1::/64" in repository_rendered.content
        assert "remote_ip 2001:db8:2::/64" in repository_rendered.content
        assert "header_up -Authorization" in repository_rendered.content
        assert basic_auth_username + " " + bcrypt_hash in repository_rendered.content
        assert "{{" not in repository_rendered.content and "}}" not in repository_rendered.content

        # The single Japan allow-list is one IP-only matcher shared by the browser management
        # surface and /api. Both markers land inside it and nowhere else.
        assert repository_rendered.content.count("remote_ip 198.51.100.0/24") == 1
        jp_open = repository_rendered.content.index("\t@jp {")
        jp_close = repository_rendered.content.index("\n\t}", jp_open)
        jp_block = repository_rendered.content[jp_open:jp_close]
        assert "remote_ip 198.51.100.0/24" in jp_block
        assert "remote_ip 2001:db8:1::/64" in jp_block
        assert "path" not in jp_block

        # /api is Japan-only, has no Caddy Basic Auth, and does not strip the client Authorization
        # header. A non-JP source falls to the sibling 404 handler, never the upstream.
        api_open = repository_rendered.content.index("handle @api_family {")
        api_block = repository_rendered.content[api_open:]
        api_block = api_block[: api_block.index("\n\t\thandle @metrics_operator {")]
        assert "handle @jp {" in api_block
        assert "reverse_proxy mailer:8080" in api_block
        assert "respond 404" in api_block
        assert "basic_auth" not in api_block
        assert "-Authorization" not in api_block
        api_jp_open = api_block.index("handle @jp {")
        api_jp_close = api_block.index("\n\t\t\t}", api_jp_open)
        assert "reverse_proxy mailer:8080" not in api_block[api_jp_close:]

        # Liveness/readiness stay country-unrestricted; /api is no longer a public path.
        assert "@public path /healthz /readyz" in repository_rendered.content
        assert "/api" not in repository_rendered.content[
            repository_rendered.content.index("@public path") :
        ].split("\n", 1)[0]

        for invalid_username in ("", " ", "caddy admin", "caddy\nadmin", "caddy{admin}"):
            _expect_render_error(
                "invalid Basic Auth username",
                lambda invalid_username=invalid_username: render_caddyfile(
                    ipv4,
                    ipv6,
                    template,
                    invalid_username,
                    bcrypt_hash,
                ),
            )

        missing_zone = directory / "missing.zone"
        _expect_render_error(
            "missing IPv4 zone",
            lambda: render_caddyfile(
                missing_zone,
                ipv6,
                template,
                basic_auth_username,
                bcrypt_hash,
            ),
        )
        invalid_cidr = directory / "invalid-cidr.zone"
        _write_fixture(invalid_cidr, "not-a-cidr\n")
        _expect_render_error(
            "invalid CIDR",
            lambda: render_caddyfile(
                invalid_cidr,
                ipv6,
                template,
                basic_auth_username,
                bcrypt_hash,
            ),
        )
        default_ipv4 = directory / "default-ipv4.zone"
        _write_fixture(default_ipv4, "0.0.0.0/0\n")
        _expect_render_error(
            "IPv4 default route",
            lambda: render_caddyfile(
                default_ipv4,
                ipv6,
                template,
                basic_auth_username,
                bcrypt_hash,
            ),
        )
        default_ipv6 = directory / "default-ipv6.zone"
        _write_fixture(default_ipv6, "::/0\n")
        _expect_render_error(
            "IPv6 default route",
            lambda: render_caddyfile(
                ipv4,
                default_ipv6,
                template,
                basic_auth_username,
                bcrypt_hash,
            ),
        )

        empty_ipv4 = directory / "empty-ipv4.zone"
        _write_fixture(empty_ipv4, "\n \t\n")
        _expect_render_error(
            "empty IPv4 zone",
            lambda: render_caddyfile(
                empty_ipv4,
                ipv6,
                template,
                basic_auth_username,
                bcrypt_hash,
            ),
        )
        empty_ipv6 = directory / "empty-ipv6.zone"
        _write_fixture(empty_ipv6, "\n \t\n")
        _expect_render_error(
            "empty IPv6 zone",
            lambda: render_caddyfile(
                ipv4,
                empty_ipv6,
                template,
                basic_auth_username,
                bcrypt_hash,
            ),
        )
        wrong_family_ipv4 = directory / "wrong-family-ipv4.zone"
        _write_fixture(wrong_family_ipv4, "2001:db8::/64\n")
        _expect_render_error(
            "wrong IPv4 family",
            lambda: render_caddyfile(
                wrong_family_ipv4,
                ipv6,
                template,
                basic_auth_username,
                bcrypt_hash,
            ),
        )
        wrong_family_ipv6 = directory / "wrong-family-ipv6.zone"
        _write_fixture(wrong_family_ipv6, "198.51.100.0/24\n")
        _expect_render_error(
            "wrong IPv6 family",
            lambda: render_caddyfile(
                ipv4,
                wrong_family_ipv6,
                template,
                basic_auth_username,
                bcrypt_hash,
            ),
        )
        extra_token = directory / "extra-token.zone"
        _write_fixture(extra_token, "198.51.100.0/24 # comment\n")
        _expect_render_error(
            "extra token or comment",
            lambda: render_caddyfile(
                extra_token,
                ipv6,
                template,
                basic_auth_username,
                bcrypt_hash,
            ),
        )

        missing_marker_template = directory / "missing-marker.template"
        _write_fixture(missing_marker_template, template_text.replace(_IPV4_MARKER, ""))
        _expect_render_error(
            "missing template marker",
            lambda: render_caddyfile(
                ipv4,
                ipv6,
                missing_marker_template,
                basic_auth_username,
                bcrypt_hash,
            ),
        )
        duplicate_marker_template = directory / "duplicate-marker.template"
        _write_fixture(
            duplicate_marker_template,
            template_text.replace(_IPV4_MARKER, _IPV4_MARKER + "\n        " + _IPV4_MARKER),
        )
        _expect_render_error(
            "duplicate template marker",
            lambda: render_caddyfile(
                ipv4,
                ipv6,
                duplicate_marker_template,
                basic_auth_username,
                bcrypt_hash,
            ),
        )
        unresolved_marker_template = directory / "unresolved-marker.template"
        _write_fixture(unresolved_marker_template, template_text + "\n{{UNRESOLVED_MARKER}}\n")
        _expect_render_error(
            "unresolved template marker",
            lambda: render_caddyfile(
                ipv4,
                ipv6,
                unresolved_marker_template,
                basic_auth_username,
                bcrypt_hash,
            ),
        )
        _expect_render_error(
            "missing Basic Auth hash",
            lambda: render_caddyfile(ipv4, ipv6, template, basic_auth_username, ""),
        )
        _expect_render_error(
            "invalid Basic Auth hash",
            lambda: render_caddyfile(
                ipv4,
                ipv6,
                template,
                basic_auth_username,
                "plain-password",
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
                    "--ipv4-zone",
                    str(ipv4),
                    "--ipv6-zone",
                    str(ipv6),
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
        "--ipv4-zone": args.ipv4_zone,
        "--ipv6-zone": args.ipv6_zone,
        "--basic-auth-hash-file": args.basic_auth_hash_file,
        "--basic-auth-username": args.basic_auth_username,
        "--template": args.template,
        "--output": args.output,
    }
    missing = [name for name, value in required.items() if not value]
    if missing:
        parser.error("missing required argument(s): " + ", ".join(missing))

    basic_hash = _read_bcrypt_hash_file(args.basic_auth_hash_file)
    rendered = render_caddyfile(
        args.ipv4_zone,
        args.ipv6_zone,
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
