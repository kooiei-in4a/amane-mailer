# Issue #532: 添付MVP上限の実Docker/cgroup total-memory qualification

## 目的

Issue #523で確定したMVP添付上限(1ファイルdecoded binary最大2 MiB、1通のdecoded attachment合計最大5 MiB、添付最大5件、件名・本文・宛先・添付を含むACS provider送信データ全体最大8 MiB)が、実Docker/cgroupのprocess全体total-memory制限(256 MiB / 512 MiB)下で成立するかを検証する。

Issue #526 / PR #531で確認済みの`DOTNET_GCHeapHardLimit`によるmanaged GC heap pressure測定は、process全体を制限する実container total-memory qualificationではない。本Evidenceはその不足を、実Docker containerのcgroup total-memory制限を用いて埋める。

## 参照情報

| 項目 | 値 |
|---|---|
| Reviewed develop SHA | `843c160de8798a18a9d3bee4791418c98deae310` |
| Qualification commit SHA(実装: probe拡張・fixture・script) | `9a140422e0a3da1f27ff793eb279ffd8ad48ccd8` |
| Branch | `spike/532-docker-memory-qualification` |
| Docker Client / Server | 29.6.1 / Docker Desktop 4.81.0 (Engine 29.6.1) |
| Docker Compose | v5.2.0(本qualificationでは未使用。単発`docker run`相当の`docker create`/`docker start`で十分なため) |
| Host platform | Windows 11 Pro 10.0.26200、Docker Desktop Linuxコンテナバックエンド |
| Container image | `mcr.microsoft.com/dotnet/runtime:10.0`(`Microsoft.NETCore.App 10.0.10`) |
| Cgroup version | v2(`Cgroup Driver: cgroupfs`, `Cgroup Version: 2`) |
| Provider | OFFLINE FAKE ONLY(offline fake `HttpClientTransport`、live ACSなし) |

## 再利用したPR #531資産(無変更)

- `Spike526FixtureFactory`(`CreateSyntheticBytes` / `CreateAttachment` / `CreateRecipients` / `DeterministicGuid`)
- `Spike526TokenBufferProcessor`(Candidate A: bounded token-buffer decode、per-file/total limit判定、strict UTF-8、digest/length照合、cancellation-safe cleanup)
- `Spike526AcsEnvelopeCapture`(offline fake `HttpClientTransport`によるACS SDK 1.1.0 exact capture、および15ケースでunderestimate 0件の qualified estimator `EstimateUpperBound`)
- `Spike526TempStore` / scoped cleanup経路
- `Spike526JsonContext`(source-generated JSON、value-free出力)

## 変更内容

| ファイル | 内容 |
|---|---|
| `tests/Amane.Mailer.Spike526.Probe/Spike532Fixtures.cs` | 新規。Q00-Q03X fixture定義(下記参照)。既存`Spike526FixtureFactory`のinternal helperのみ再利用。 |
| `tests/Amane.Mailer.Spike526.Probe/Program.cs` | 最小拡張。`Q`prefix fixtureのみ、(a) per-file/total capを2 MiB/5 MiBに切替、(b) 既存qualified estimatorによるACS envelope 8 MiB pre-invocation gateを追加。F00-F08/G01-G05の既存挙動は無変更(既存Spike526テスト29件が回帰なく成功することで確認)。 |
| `scripts/spike-532-container-entrypoint.sh` | 新規。container内で実行し、cgroup memory.max/current/peak/events、probeのJSON結果、temp残存件数を出力する。 |
| `scripts/spike-532-docker-qualify.ps1` / `.sh` | 新規。probeをlinux-x64向けに(`PublishAot=false`で)publishし、`docker create`/`docker start`/`docker inspect`でcontainer memory limitを適用・検証しつつ実行し、value-free JSON Lines Evidenceを生成する。 |

production runtime(`src/Amane.Mailer/**`)、Contracts、OpenAPI、migrationは変更していない。

### Native AOT publishについて

qualification probeはlinux-x64向けに`-p:PublishAot=false`で(framework-dependentとして)publishした。Native AOT全RID qualificationはIssue #532の非目標であり、本qualificationはmanaged runtime上のCandidate A(token-buffer)処理方式の実container total-memory挙動のみを対象とする。

## Fixture定義(Q00-Q03X)

全fixtureは決定的生成(実データ・実PII・実credentialなし)。

| Fixture | 添付数 | decoded合計 | body | 用途 |
|---|---|---|---|---|
| Q00 | 0 | 0 | 短文 | 添付なしのベースライン(受理候補) |
| Q01 | 1 | 2 MiB exact | 短文 | per-file上限ちょうど(受理候補) |
| Q01X | 1 | 2 MiB + 1 byte | 短文 | per-file上限超過(reject候補) |
| Q02 | 5 | 5 MiB exact | 短文 | total上限ちょうど(受理候補) |
| Q02X | 5 | 5 MiB + 1 byte | 短文 | total上限超過(reject候補) |
| Q03 | 5 | 5 MiB exact | 600,000 UTF-8 bytes | ACS provider envelopeが8 MiB近傍未満(受理候補、最大条件) |
| Q03X | 5 | 5 MiB exact | 750,000 UTF-8 bytes | ACS provider envelopeが8 MiB超(reject候補、provider invocation前) |

Q03/Q03Xのbody(UTF-8 ASCII繰り返し)サイズは、実測(`docker`外でのローカルdry run)によりQ03のACS envelopeが8 MiB未満、Q03Xが8 MiB超となるよう校正した(下記「Boundary matrix」参照)。

## Boundary matrix

| 境界 | 受理候補(exact) | reject候補 | 検証経路 |
|---|---|---|---|
| Per-file decoded binary | 2 MiB(Q01) | 2 MiB + 1 byte(Q01X) | token-buffer decode中、bounded chunk write時点で拒否(過大allocation前) |
| Total decoded binary | 5 MiB(Q02) | 5 MiB + 1 byte(Q02X) | token-buffer decode中、running totalで拒否(過大allocation前) |
| ACS provider envelope | 実測8,191,324 bytes(Q03、8 MiB=8,388,608 bytes未満) | 実測estimate 8 MiB超(Q03X) | 既存qualified estimator(`EstimateUpperBound`)による**provider invocation前**の拒否。offline fake transportは一切呼ばれない。 |

Consumer HTTP envelopeとACS provider envelopeは別々に測定した(Q03: Consumer envelope 8,679,497 bytes、ACS envelope 8,191,324 bytes)。

## Execution matrix

| 区分 | fixture | concurrency | repeat |
|---|---|---|---|
| 受理候補 | Q00, Q01, Q02 | 1, 2 | 1 |
| 受理候補・最大条件 | Q03 | 1 | 1 |
| 受理候補・最大条件 | Q03 | 2 | **3**(安定性確認) |
| reject候補 | Q01X, Q02X, Q03X | 1, 2 | 1 |

1メモリ条件あたり16 container実行、256 MiB / 512 MiBの2条件で計32 container実行。

## 256 MiB結果

| fixture | concurrency | repeat | result | 備考 |
|---|---|---|---|---|
| Q00 | 1, 2 | 1 | PASS | |
| Q01 | 1, 2 | 1 | PASS | |
| Q02 | 1, 2 | 1 | PASS | |
| Q03 | 1 | 1 | **REJECTED(`OUT_OF_MEMORY`)** | cgroup OOM-killではなく.NET managed `OutOfMemoryException`(`oom_killed=false`、cgroup `oom_kill`イベント0件) |
| Q03 | 2 | 3/3 | **REJECTED(`OUT_OF_MEMORY`)**(3回とも) | 再現性あり(偶然の失敗ではない) |
| Q01X | 1, 2 | 1 | REJECTED(`IO_ERROR`、想定通り) | provider invoked: false |
| Q02X | 1, 2 | 1 | REJECTED(`IO_ERROR`、想定通り) | provider invoked: false |
| Q03X | 1, 2 | 1 | REJECTED(`IO_ERROR`、想定通り) | ACS envelope estimate gateにより provider invocation前に拒否。provider invoked: false |

**256 MiBでは、最大条件(Q03、5 MiB添付+8 MiB近傍envelope)がconcurrency 1でも2でも一貫してmanaged OutOfMemoryExceptionで失敗した。** Q00-Q02(典型的なサイズ)は安定してPASS。

推定原因: `Spike526AcsEnvelopeCapture.CaptureAsync`はACS SDK側で添付を独立に再decodeしMIME/JSON messageを再構築するため、token-bufferの bounded decode(一時ファイル書き出し)に加えてSDK側のin-memory decode/serializeが重畳し、8 MiB近傍envelopeでは256 MiBでは不足する。

生Evidence: [issue-532-docker-256mib-results.jsonl](issue-532-docker-256mib-results.jsonl)

## 512 MiB結果

| fixture | concurrency | repeat | result |
|---|---|---|---|
| Q00 | 1, 2 | 1 | PASS |
| Q01 | 1, 2 | 1 | PASS |
| Q02 | 1, 2 | 1 | PASS |
| Q03 | 1 | 1 | PASS |
| Q03 | 2 | 3/3 | **PASS(3回とも安定)** |
| Q01X | 1, 2 | 1 | REJECTED(`IO_ERROR`、想定通り) |
| Q02X | 1, 2 | 1 | REJECTED(`IO_ERROR`、想定通り) |
| Q03X | 1, 2 | 1 | REJECTED(`IO_ERROR`、想定通り) |

**512 MiBでは、最大条件(Q03、concurrency 2)を含む全ての受理候補が3回とも安定して完了した。** OOM/container killは0件。

生Evidence: [issue-532-docker-512mib-results.jsonl](issue-532-docker-512mib-results.jsonl)

## 主要metric(value-free、512 MiB・Q03・concurrency 2・repeat 1の例)

| metric | 値 |
|---|---|
| consumer envelope bytes | 8,679,497 |
| ACS provider envelope bytes(exact capture) | 8,191,324 |
| decoded binary bytes | 5,242,880 |
| peak working set bytes | 325,488,640 |
| managed GC heap before/peak/after bytes | 91,420,048 / 320,062,712 / 320,062,712 |
| cgroup memory.peak bytes | 285,020,160(512 MiB = 536,870,912の約53%) |
| cgroup oom / oom_kill events | 0 / 0 |
| provider invoked | false |
| cleanup complete / temp residue count | true / 0 |

全32 container実行を通じて`temp_residue_count`は例外なく0だった。256 MiBのOOM実行を含め、cleanupは常に成功した(managed `OutOfMemoryException`は通常の.NET例外であり、cgroupによるSIGKILLとは異なりprobeの`finally`ブロックが実行されるため)。cgroup OOM-kill(`oom_killed=true`または`cgroup_oom_kill_events>0`)は256 MiB/512 MiBいずれの32実行でも一度も発生しなかった。

## Cleanup確認

- 正常完了(PASS)16ケース中16ケースでtemp残存0件
- validation reject(Q01X/Q02X)全8ケースでtemp残存0件、provider invoked: false
- provider envelope oversize reject(Q03X)全4ケースでprovider invocation前に拒否、temp残存0件、provider invoked: false
- managed `OutOfMemoryException`(256 MiB Q03、4ケース)でもtemp残存0件
- 全32 container実行の内訳: PASS 16件 / 期待されたboundary reject(Q01X/Q02X/Q03X)12件 / managed `OutOfMemoryException` 4件
- 実cgroup SIGKILL(OOM-kill)は本qualificationでは発生しなかったため、その経路のcleanup挙動は本Evidenceの対象外。PR #531で確認済みのcrash/restart orphan cleanup(`orphan-create`/`cleanup` CLIコマンド、`Restart_cleanup_removes_only_spike_owned_orphans`テスト)は、SIGKILLによるprocess強制終了を模擬しており、cgroup OOM-killも同じくSIGKILLでprocessを終了させるため、その既存Evidenceが適用されると評価する。

## Privacy確認

生成されたJSON Lines Evidence(`issue-532-docker-256mib-results.jsonl` / `issue-532-docker-512mib-results.jsonl`)およびprobeの標準出力・標準エラーに対し、以下を確認した(結果: 該当なし)。

- 絶対path(`C:\`、`/c/`、`/home/`等)
- 40文字以上のBase64的文字列
- メールアドレス(`@`を含む文字列)
- 64桁16進digest文字列

fixture識別子は`Q00`-`Q03X`の固定文字列のみを使用し、実データ・実PII・実credentialは一切使用していない。

## Validation結果

```
dotnet restore Amane.Mailer.slnx --locked-mode                                    -> 成功
dotnet format whitespace Amane.Mailer.slnx --verify-no-changes                     -> 変更なし(成功)
dotnet build Amane.Mailer.slnx -c Release --no-restore                            -> 成功(0エラー、既存warningのみ)
dotnet test tests/Amane.Mailer.Tests/... --filter "FullyQualifiedName~Spike526"    -> 29/29成功(F00-F08/G01-G05既存挙動に回帰なし)
dotnet test Amane.Mailer.slnx -c Release --no-build --verbosity normal            -> 1901成功 / 21スキップ / 0失敗
scripts/spike-532-docker-qualify.ps1 -MemoryMiB 256                               -> 実行完了(結果は上記表の通り)
scripts/spike-532-docker-qualify.ps1 -MemoryMiB 512                               -> 実行完了(結果は上記表の通り)
```

`packages.lock.json`は最終的に無変更(publish手順を`dotnet restore --locked-mode`→`dotnet publish --no-restore -p:PublishAot=false`の順に分離し、AOT無効publishがlock fileを書き換えないようにした)。

## 制約・残存リスク

- 本qualificationはWindows host上のDocker Desktop(Linuxコンテナバックエンド、WSL2)で実施した。Linuxホスト上のDocker Engineでの再現は未実施。
- cgroup OOM-kill(kernelによるSIGKILL)経路は今回発生しなかったため、その経路のcleanup挙動は実証していない(PR #531の既存crash/restart cleanup Evidenceを類推適用)。
- Q03/Q03Xのbody sizeはローカル実測により校正した固定値であり、ACS SDKのバージョンや将来の実装変更によりACS envelope実測値が変化した場合は再校正が必要。
- 256 MiBでのQ03失敗の原因(ACS SDK側の独立decode/serializeによる二重メモリ使用)は測定結果からの推定であり、ACS SDK内部の詳細プロファイリングは行っていない。
- concurrency 3以上、あるいはQ03以外のfixtureでのconcurrency 2 repeatは対象外(Issue #532のscopeに合わせ、最大条件のみrepeatした)。

## 最終判定

**PASS**

現在確定しているMVP添付上限(1ファイル2 MiB、合計5 MiB、5件、ACS provider envelope 8 MiB)と、添付本体をメモリ上でのみ処理する方式(Candidate A token-buffer)は、**production runtime container memoryを512 MiB以上に設定すること**を条件に、実Docker/cgroup total-memory制限下で安定して成立することを確認した。

- 512 MiB: 最大条件(Q03、concurrency 2)を含む全受理候補が3回repeatとも安定PASS。全reject候補がprovider invocation前に正しく拒否。
- 256 MiBは、典型的なサイズ(Q00-Q02)では安定してPASSするが、8 MiB近傍のACS provider envelopeを伴う最大条件(Q03)ではmanaged OutOfMemoryExceptionで一貫して失敗した(3回repeatとも再現)。256 MiBはproduction minimumとして推奨しない。

## Production minimum memory recommendation

**512 MiB以上**

## 明記事項

- production runtime(`src/Amane.Mailer/**`)は変更していない。
- live ACSは実行していない(offline fake transportのみ、全32実行で`provider_invoked: false`)。
- production credentialは使用していない。
- Contracts / OpenAPI / migrationは変更していない。
- ADR(`docs/adr/`)のstatus変更は行っていない。
