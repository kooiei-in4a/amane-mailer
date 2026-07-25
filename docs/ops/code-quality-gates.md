# Code quality gates（formatter / staged analyzers）

[English](code-quality-gates.en.md)

formatter と段階導入した .NET analyzer を CI とローカルで揃えます
（[#359](https://github.com/kooiei-in4a/amane-mailer/issues/359)）。
機械判定できる問題だけを自動化し、設計判断はレビューに残します。

## 方針

- 既存のリポジトリ style を正本とし、大規模な一括 reformat はしない
- `.editorconfig` に `charset` を強制しない（既存ファイルの一括再エンコードを避ける）
- formatter gate は whitespace（改行・インデント崩れ）を検出する
- analyzer は段階導入する。`AnalysisMode=All` や全 rule の一括 error 化はしない
- 無差別な `NoWarn` 追加は禁止する。suppress は個別・理由付きのみ
- Native AOT / trimming warning（`IL2026` / `IL3050` / `IL2104`、
  `IlcTreatWarningsAsErrors`）は別ゲートとして維持する

## Phase 1 で有効化した rule（`src/**` のみ error）

| Rule | 理由 |
|------|------|
| `CA2000` | disposable の所有権漏れを新規コードで止める |
| `CA1001` | disposable フィールドを持つ型の `IDisposable` 欠落 |
| `CA2213` | disposable フィールドの未 Dispose |
| `CA2016` | `CancellationToken` の未転送 |
| `CS4014` | 未待機の async 呼び出し |
| `IDE0051` | 未使用 private member |
| `CA1823` | 未使用 private field |

適用範囲:

- **error**: `src/**/*.cs`（Mailer runtime + Contracts）
- **deferred**: `tests/**`（特に `CA2000` の fixture 由来 noise が大きい）
- **generated**: `**/obj/**` と `**/*.g.cs` は `generated_code = true`（不要 warning を出さない）

## 明示的に今は上げない rule

| Rule / 設定 | 理由 |
|-------------|------|
| `CA2007`（ConfigureAwait） | ASP.NET Core サーバでは不要。`AnalysisMode=All` 時の主 noise |
| `CA1849` | SQLite reader の sync API など、今回の段階対象外 |
| `AnalysisMode=All` | 既存数千件の noise。段階導入の前提に反する |
| test 向け `CA2000` error | fixture / logger provider 由来が多く、別段階で扱う |

## ローカル再現

CI と同じ formatter 検証:

```powershell
dotnet format whitespace Amane.Mailer.slnx --verify-no-changes
```

staged analyzer は `Directory.Build.props`（`EnableNETAnalyzers` +
`EnforceCodeStyleInBuild`）と `.editorconfig` の severity により、通常の
Release build に含まれます。

```powershell
dotnet restore Amane.Mailer.slnx --locked-mode
dotnet build Amane.Mailer.slnx -c Release --no-restore
```

## CI

| 場所 | タイミング |
|------|------------|
| `.github/workflows/ci.yml`（`Restore, build, and test`） | restore 後に whitespace formatter verify。続けて build（analyzer severity 含む） |

計測メモ（Agent A、2026-07-25 ローカル Windows / SDK 10.0.302）:

- `dotnet format whitespace --verify-no-changes`: 約 6s
- Release build（analyzer 込み）: 既存と同程度（段階 rule のみのため過度な増加なし）

## 失敗時の対応

1. formatter: `dotnet format whitespace Amane.Mailer.slnx` で直し、再 verify する
2. analyzer: 実リークなら dispose / ownership を修正する。所有権移転など正当なケースは
   パターンで analyzer に見える形へ直すか、最小限の理由付き suppress にする
3. generated / test 側の大量 suppress で green にしない

## 次段階（本 issue の後続）

- test プロジェクトへの disposable / unused 規則の段階拡大
- async 関連の追加 rule（実測後に個別 error 化）
- 必要なら style formatter（`dotnet format style`）の別 gate
