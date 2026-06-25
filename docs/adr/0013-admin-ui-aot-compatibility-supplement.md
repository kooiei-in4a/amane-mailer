# ADR 0013 Supplement: Admin UI Native AOT Compatibility

- **Status:** Accepted
- **Date:** 2026-06-24
- **Supplements:** [ADR 0013: Admin threat model, exposure scope, and PII policy](0013-admin-threat-model-and-pii-policy.md)

## Decision

Amane.Mailer admin UI will use **Minimal API + StaticFiles with static HTML/JS/CSS**.

Server-side responsibilities stay limited to:

- admin enable/disable gating
- cookie sign-in / sign-out endpoints
- CSRF token issue and validation
- authenticated Admin API endpoints
- authenticated static asset serving for `/admin` resources

Razor Pages is not selected for the Mailer admin UI because `AddRazorPages()` emits
IL2026 under the repository's current trimming/AOT settings:

```text
Razor Pages does not currently support trimming or native AOT.
```

The repository keeps `IlcTreatWarningsAsErrors=true`, so allowing this warning would
weaken the Native AOT gate that the Mailer service relies on.

## Validation

PoC location:

- [spike/Amane.Mailer.AotSpike](../../spike/Amane.Mailer.AotSpike)

The PoC adds:

- `AddAuthentication().AddCookie()`
- `AddAntiforgery()`
- `UseStaticFiles()` for `/admin/assets`
- `/admin/login`
- `/admin/api/login`
- `/admin/api/logout`
- authenticated `/admin` Hello Admin page
- `admin hash-password --password <password>` PBKDF2 CLI

Validation results on 2026-06-24:

| Check | Result | Notes |
|---|---|---|
| Cookie auth registration and sign-in | PASS | Native AOT Docker image signs in with demo `admin` / `password` credentials |
| AntiForgery registration and form validation | PASS | Login POST validates `__RequestVerificationToken` |
| StaticFiles | PASS | `/admin/assets/admin-poc.css` returns 404 without auth; authenticated `/admin` renders CSS link |
| Hello Admin PoC | PASS | Native AOT Docker image returns authenticated `Hello Admin` page |
| PBKDF2 hash CLI | PASS | Native AOT Docker image emits `pbkdf2:sha256:600000:...` |
| Razor Pages registration | FAIL | `AddRazorPages()` fails build with IL2026 when warnings are errors |

Commands used:

```powershell
dotnet build spike\Amane.Mailer.AotSpike\Amane.Mailer.AotSpike.csproj -c Release
docker build -f spike\Amane.Mailer.AotSpike\Dockerfile -t amane-mailer-aot-spike:admin-poc .
docker run --rm -d -p 18080:8080 --name amane-admin-aot-poc amane-mailer-aot-spike:admin-poc
docker run --rm amane-mailer-aot-spike:admin-poc admin hash-password --password test-password
```

## Alternatives

| Option | AOT result | Decision |
|---|---|---|
| Minimal API + StaticFiles | PASS | Selected. Smallest server-side surface and matches ADR 0013 fail-closed goals |
| Minimal API + Razor Pages | FAIL | Rejected because `AddRazorPages()` is marked incompatible with trimming/Native AOT |
| Minimal API + Blazor WASM static assets | Likely AOT-compatible for the Mailer host | Not selected for the first admin foundation because it adds a larger client build surface |
| Blazor Server colocated with Mailer | Not selected | Rejected for the Native AOT Mailer process and operational simplicity |
| Separate admin container | AOT-compatible for Mailer | Reserved as fallback if static UI becomes too costly |
| Disable AOT / self-contained JIT | Would avoid AOT issue | Rejected; weakens the Mailer deployment constraint |

## Consequences for #175

#175 should implement the admin foundation using the selected static UI shape:

- keep `AMANE_ADMIN_ENABLED=false` as the default fail-closed mode
- map `/admin` and `/admin/api/*` only when admin is enabled
- protect state-changing Admin API endpoints with CSRF validation
- put all PII and operational data behind authenticated Admin API endpoints
- serve static assets only after the admin authentication gate
- provide `admin hash-password` for PBKDF2 hash bootstrap
- require an explicitly configured `AMANE_ADMIN_PASSWORD_HASH`; do not carry over
  the PoC's demo password fallback
- use secure production cookie settings, including `CookieSecurePolicy.Always`
  behind HTTPS-capable deployment
- implement ADR 0013 session lifetime requirements, including absolute expiry and
  idle timeout

Razor Pages should not be introduced into `src/Amane.Mailer` while Native AOT remains
a release requirement.

## PoC Boundaries

The spike intentionally keeps a few development-only shortcuts so that the native
container can be exercised over local HTTP:

- `CookieSecurePolicy.SameAsRequest` is used only so the Docker PoC works at
  `http://127.0.0.1:18080`. #175 should use secure cookies for real deployments.
- the fallback `admin` / `password` credential exists only for the Hello Admin PoC.
  #175 must fail closed when the admin password hash is missing.
- login and logout call `.DisableAntiforgery()` only to avoid the middleware's
  automatic 400 response; both handlers still call `ValidateRequestAsync()`
  manually before changing authentication state.
- the root `.dockerignore` allows the spike directory so the existing spike
  Dockerfile can build from the repository root. If the spike is removed later,
  this exception should be removed with it.
