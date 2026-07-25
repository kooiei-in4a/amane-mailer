# Code quality gates (formatter / staged analyzers)

[日本語](code-quality-gates.md)

Align formatter and staged .NET analyzer gates between CI and local runs
([#359](https://github.com/kooiei-in4a/amane-mailer/issues/359)).
Automate only mechanically decidable problems; leave design judgment to review.

## Policy

- Treat the existing repository style as the source of truth; no bulk reformat
- Do not force `charset` in `.editorconfig` (avoids bulk re-encoding of existing files)
- The formatter gate checks whitespace (indentation / newline drift)
- Introduce analyzers in stages. Do not enable `AnalysisMode=All` or flip every
  rule to error at once
- Do not add blanket `NoWarn` entries. Suppressions must be targeted and justified
- Native AOT / trimming warnings (`IL2026` / `IL3050` / `IL2104`,
  `IlcTreatWarningsAsErrors`) remain a separate preserved gate

## Phase 1 rules (`src/**` only as errors)

| Rule | Why |
|------|-----|
| `CA2000` | Catch disposable ownership leaks in new production code |
| `CA1001` | Types that own disposable fields must implement `IDisposable` |
| `CA2213` | Disposable fields must be disposed |
| `CA2016` | Forward `CancellationToken` where required |
| `CS4014` | Unobserved async calls |
| `IDE0051` | Unused private members |
| `CA1823` | Unused private fields |

Scope:

- **error**: `src/**/*.cs` (Mailer runtime + Contracts)
- **deferred**: `tests/**` (especially high `CA2000` fixture noise)
- **generated**: `**/obj/**` and `**/*.g.cs` set `generated_code = true`

## Explicitly deferred

| Rule / setting | Why |
|----------------|-----|
| `CA2007` (ConfigureAwait) | Not needed for ASP.NET Core server code; dominant All-mode noise |
| `CA1849` | SQLite reader sync APIs and similar; out of this stage |
| `AnalysisMode=All` | Thousands of existing findings; conflicts with staged rollout |
| Test `CA2000` as error | Fixture / logger-provider noise; later stage |

## Local commands

Same formatter check as CI:

```powershell
dotnet format whitespace Amane.Mailer.slnx --verify-no-changes
```

Staged analyzers participate in the normal Release build via
`Directory.Build.props` (`EnableNETAnalyzers` + `EnforceCodeStyleInBuild`) and
`.editorconfig` severities:

```powershell
dotnet restore Amane.Mailer.slnx --locked-mode
dotnet build Amane.Mailer.slnx -c Release --no-restore
```

## CI

| Where | When |
|-------|------|
| `.github/workflows/ci.yml` (`Restore, build, and test`) | After restore: whitespace formatter verify, then build (includes analyzer severities) |

Timing notes (Agent A, 2026-07-25 local Windows / SDK 10.0.302):

- `dotnet format whitespace --verify-no-changes`: ~6s
- Release build with staged rules: comparable to prior; no excessive increase

## Remediation

1. Formatter: run `dotnet format whitespace Amane.Mailer.slnx`, then re-verify
2. Analyzer: fix real leaks / ownership. For justified ownership transfer, reshape
   the code so the analyzer can see dispose paths, or use a minimal justified
   suppression
3. Do not green the build with mass suppressions on generated or test code

## Later stages (follow-up)

- Expand disposable / unused rules into test projects
- Add further async rules after measuring real findings
- Optionally add a separate `dotnet format style` gate
