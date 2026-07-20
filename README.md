# otel-windows-handoff

[![CI](https://github.com/urario/otel-windows-handoff/actions/workflows/ci.yml/badge.svg)](https://github.com/urario/otel-windows-handoff/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Windows デスクトップアプリの障害解析で、OpenTelemetry の Trace / Log / Metrics と ETW をどこまで接続できるか再現するサンプルです。画像処理を模したジョブは `load` → `transform` → `save` の3フェーズで動き、10件ごとの同じジョブへ遅延または保存失敗を注入できます。

> `AGENTS.md` / `CLAUDE.md` / `.claude/` は開発体制用です。実験の再現にはこの README だけを使います。

## 構成

| パス | 役割 |
|---|---|
| `src/Pipeline.Core` | プラットフォーム中立なパイプライン、障害注入、手動 OTel 計装、EventSource |
| `src/Pipeline.Core.Tests` | Linux でも実行できる xUnit テスト |
| `src/App.Console` | E3 自動測定と WinUI 後退時に使う CLI |
| `src/App.WinUI` | Core の設定と進捗だけを扱う unpackaged WinUI 3 シェル |
| `collector` | otelcol-contrib 設定と WPR プロファイル |
| `scripts` | E2 の WPR / Dump 取得と E3 の4条件測定を補助する PowerShell スクリプト（[使い方](scripts/README.md)） |
| `tools/EtlInspector` | WPA の表示状態に依存せず ETL ペイロードを読む補助 CLI |

`OtelWindowsHandoff.sln` は Windows で WinUI を含む全体をビルドします。Linux CI は `OtelWindowsHandoff.Linux.slnf` を使い、Core / Console / Tests だけをビルドします。

## 前提

- Windows 10 1809 以降または Windows 11
- .NET 10 SDK。`global.json` は 10.0 feature band の安定版だけを選びます
- Windows App SDK 2.2 Runtime x64
- Docker Desktop（Aspire Dashboard を使う場合だけ）
- OpenTelemetry Collector Contrib 0.153.0
- ETW を記録する場合は Windows ADK の Windows Performance Toolkit（WPR / WPA）

まず全体をビルドしてテストします。

```powershell
dotnet build .\OtelWindowsHandoff.sln --configuration Release
dotnet test .\OtelWindowsHandoff.sln --configuration Release --no-build
```

## 最短の再現手順

以下はすべてリポジトリのルートをカレントディレクトリにした PowerShell で実行します。

### 1. テストデータを生成する

既定値は100ファイル、各1 MiBです。生成物は `artifacts/` と同様にコミットしません。

```powershell
dotnet run --project .\src\App.Console --configuration Release -- generate-data --dir .\data\in
```

### 2. Collector を起動する

固定した Windows x64 バイナリを `artifacts/` へ取得します。

```powershell
$CollectorVersion = "0.153.0"
New-Item -ItemType Directory -Force .\artifacts\otel | Out-Null
Invoke-WebRequest "https://github.com/open-telemetry/opentelemetry-collector-releases/releases/download/v$CollectorVersion/otelcol-contrib_${CollectorVersion}_windows_amd64.tar.gz" -OutFile .\artifacts\otelcol-contrib.tar.gz
tar -xzf .\artifacts\otelcol-contrib.tar.gz -C .\artifacts
.\artifacts\otelcol-contrib.exe --config .\collector\otelcol-contrib.yaml
```

Collector は `localhost:4317` でアプリから受信し、`localhost:4318` のローカル UI と `artifacts/otel/telemetry.json` の両方へ送ります。次の UI をまだ起動していない間は再送ログが出ますが、起動後に回復します。

### 3. Aspire Dashboard を起動する

別の PowerShell で、固定タグのスタンドアロン Dashboard を1行で起動します。課金アカウントは不要です。

```powershell
docker run --rm --name aspire-dashboard -d -p 18888:18888 -p 4318:18889 -e DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true mcr.microsoft.com/dotnet/aspire-dashboard:13.1.0
```

ブラウザーで `http://localhost:18888` を開きます。終了時は `docker stop aspire-dashboard` を実行します。

### 4. Console アプリで100ジョブを実行する

```powershell
dotnet run --project .\src\App.Console --configuration Release -- run --input .\data\in --output .\data\out --otel otlp --parallel 2
```

終了コード0と `summary completed=100 failed=0` を確認します。Dashboard では次を確認できます。

- Traces: `ProcessJob` の下に `load`、`transform`、`save` がある
- Span attributes: `job.id`、パスを含まない `file.name`、`file.size_bytes`、`retry.count`
- Logs: フェーズ開始/終了ログから同じ trace ID の Trace へ移動できる
- Metrics: `jobs.completed` と `job.duration` が記録される

ログと Trace の相関は `ILogger` 呼び出し時の `Activity.Current` を SDK が取り込むことで成立します。アプリ独自の trace ID コピー処理は、取り違えを起こしやすいため使っていません。

## 障害注入

ファイル名を序数で並べて1始まりの `job.id` を付けます。並列実行の開始順ではなく `job.id % 10 == 0` で対象を決めるため、同じ入力なら常に10、20、30番目が対象です。

### slow-read

対象ジョブの `load` に3秒の固定遅延を加えます。

```powershell
dotnet run --project .\src\App.Console --configuration Release -- run --input .\data\in --output .\data\out-slow --fault slow-read --otel otlp --parallel 2
```

Dashboard では対象の `load` Span と `ProcessJob` Span だけが約3秒長くなります。他ジョブの transform を遅らせないため、単なるアプリ全体の待機は使っていません。

### access-denied

対象ジョブの `save` で `UnauthorizedAccessException` を擬似送出します。実 ACL は環境差が大きく Linux テストにも持ち込めないため変更しません。初回失敗後に最大3回、100 ms、200 ms、400 ms の指数バックオフで再試行し、対象ジョブだけが最終失敗します。

```powershell
dotnet run --project .\src\App.Console --configuration Release -- run --input .\data\in --output .\data\out-denied --fault access-denied --otel otlp --parallel 2
if ($LASTEXITCODE -ne 1) { throw "一部失敗の終了コード1ではありません" }
```

Dashboard では対象 `ProcessJob` / `save` が Error、`retry.count=3` になります。ログには3回の再試行と最終例外、Metrics には `retries.total` と `jobs.failed` が記録されます。

## OTel 3モード（E3）

| 引数 | 構築内容 | 測定条件 |
|---|---|---|
| `--otel off` | TracerProvider / MeterProvider / OTel LoggerProvider を構築しない | OTel SDK off。API 呼び出し、EventSource、通常ログは残り、Activity はリスナー不在で no-op |
| `--otel sdk` | 3 Provider を構築するが Exporter を登録しない | Span 等は生成後に破棄 |
| `--otel otlp` | 3 Provider と OTLP gRPC Exporter を構築 | Collector の有無を含む測定。既定値 |

各条件は次のように実行します。

```powershell
dotnet run --project .\src\App.Console --configuration Release -- run --input .\data\in --output .\data\e3-off --otel off --parallel 2
dotnet run --project .\src\App.Console --configuration Release -- run --input .\data\in --output .\data\e3-sdk --otel sdk --parallel 2
dotnet run --project .\src\App.Console --configuration Release -- run --input .\data\in --output .\data\e3-otlp --otel otlp --parallel 2
```

アプリは `PipelineRunner.RunAsync` の完了時にパイプライン部分の所要時間を出力します。

```text
pipeline elapsed_ms=<実測値>
```

終了時は全 Provider を ForceFlush、Shutdown の順に停止し、既定5秒の合計タイムアウト内で処理します。次の行をアプリ終了時間の切り分けに使えます。

```text
otel flush mode=otlp elapsed_ms=<実測値> success=true
```

タイムアウトは `--flush-timeout-ms 5000` で変更できます。

E3 の比較測定では Performance Counter の混入を避けるため、`collector/otelcol-e3.yaml` と `scripts/Measure-E3.ps1` を使います。条件のインターリーブ順、各列が表す範囲、中央値と最小/最大の集計方針は [E2 / E3 補助手順](scripts/README.md#measure-e3ps1)に記載しています。

## WinUI 3 シェル

通常起動は次のコマンドです。入力/出力、二つの障害トグル、並列度、OTel モードは上部の「実行設定」にまとめています。

```powershell
dotnet run --project .\src\App.WinUI --configuration Release
```

主画面には全ジョブをファイル名順で表示し、load → transform → save の状態と実測時間、再試行、結果を追跡します。10件ごとの `slow-read` は警告色、`access-denied` はエラー色で識別できます。ジョブを選択すると、Trace UI・ETL・handoff 行と同じ `trace_id` / `span_id`、開始時刻、フェーズ別所要時間を確認し、`trace_id` をコピーできます。タイトルバーの PID は ProcDump の指定に使います。

画面は OS のライト/ダークテーマに追従し、Windows 11 では Mica、非対応環境では標準背景へフォールバックします。完了とエラーの要約は画面下部の通知領域に表示します。

`TelemetrySession` はウィンドウの寿命で保持します。OTel モードを変更すると既存セッションを Dispose してから選択モードで再作成し、ウィンドウを閉じると最後のセッションを Dispose します。そのためパイプライン実行外の UI 操作も計装でき、`--exit-after` でもプロセス終了前に flush 行が出ます。

「UIを30秒フリーズ」は UI スレッドを意図的にブロックします。ブロック前に短い完了済み `UIFreezeRequested` Span、同名の EventSource イベント、Dump マーカー、handoff 行を同じ `trace_id` / `span_id` で記録してから `Thread.Sleep` を呼びます。Freeze ボタンはパイプライン実行中も有効なため、複数ジョブの trace ID が並ぶ状況でもフリーズ操作そのものを識別できます。

E3 の無操作実行では `--auto-run --exit-after` を併用します。完了後、テレメトリの終了処理を済ませてウィンドウを閉じます。

```powershell
dotnet run --project .\src\App.WinUI --configuration Release -- --auto-run --exit-after --input .\data\in --output .\data\winui-out --fault slow-read --otel otlp --parallel 2
```

`--fault` は複数回指定できるため、WinUI 自動実行では二障害の同時注入もできます。

## 既存ログライブラリとの相関

`samples/legacy-log-correlation/` は、既存アプリへ1行ずつ相関情報を足す場合の独立サンプルです。log4net の `LogicalThreadContext` と NLog の `ScopeContext` へ `Activity` の trace_id を設定し、非同期メソッドをまたいだ後も実際の出力行へ残ることを xUnit で検証します。

```powershell
dotnet run --project .\samples\legacy-log-correlation\LegacyLogCorrelation --configuration Release
dotnet test .\samples\legacy-log-correlation\LegacyLogCorrelation.Tests --configuration Release
```

log4net / NLog の参照はこのサンプル内だけです。メインアプリと `Pipeline.Core` の依存関係は変更しません。

## OpenTelemetry と ETW / Dump の引き継ぎ

Core の EventSource 名は `OtelWindowsHandoff-Handoff` です。各ジョブで次のイベントを発行します。

| Event ID | 名前 | ペイロード |
|---:|---|---|
| 1 | `JobStarted` | `traceId`, `spanId`, `jobId` |
| 2 | `JobCompleted` | `traceId`, `jobId` |
| 3 | `Warmup` | なし |
| 4 | `UIFreezeRequested` | `traceId`, `spanId`, `processId`, `uiThreadId`, `operationName` |

EventSource はジョブ列挙後ではなく、最初のジョブより前に初期化します。有効化通知は非同期なので、捨ててもよい `Warmup` と250 msの猶予を置きます。WPR を使わない通常実行で最大5秒を常に待つ方式は E3 の測定値を支配するため採用していません。WPR は必ずアプリより先に開始してください。

管理者 PowerShell で記録を開始し、別の PowerShell でアプリを実行した後に停止します。`handoff.wprp` はカスタム EventSource と Wait 解析用の `ProcessThread` / `CSwitch` / `ReadyThread` / `SampledProfile` を同時に収録します。カスタム EventSource だけの ETL では WPA の CPU Usage (Precise) / Wait 解析はできません。カーネルイベントを含むため ETL は大きくなります。

```powershell
wpr -profiles .\collector\handoff.wprp
wpr -start .\collector\handoff.wprp -filemode
```

```powershell
dotnet run --project .\src\App.Console --configuration Release -- run --input .\data\in --output .\data\etw-out --otel otlp
```

```powershell
New-Item -ItemType Directory -Force .\artifacts | Out-Null
wpr -stop .\artifacts\handoff.etl
```

ジョブ開始時は EventSource に加えて ILogger と標準出力へ次の1行を出します。

```text
handoff ts=<UTC ISO 8601> pid=<PID> trace_id=<32桁> job=<job.id>
```

UI フリーズ操作では、Span と ETW に渡した追加情報も同じ行へ残します。

```text
handoff ts=<UTC ISO 8601> pid=<PID> trace_id=<32桁> span_id=<16桁> ui_thread=<thread ID> operation=ui-freeze
```

ProcDump 等の Dump 名は `1234_20260717T123456Z.dmp` のように `{PID}_{timestamp}` を含めると、handoff 行と時刻・プロセスで突合しやすくなります。

UI フリーズ時は、フル Dump 内にも UTF-16LE で次のマーカーが残ります。

```text
OTEL-HANDOFF freeze trace_id=<32hex> span_id=<16hex> pid=<n> ui_thread=<n> ts=<ISO8601>
```

WinDbg + SOS の `!dumpheap -type OtelWindowsHandoff.Pipeline.HandoffDumpMarker` → `!do`、または `s -su` による固定キー検索で読み出せます。具体的な取得順序と解析コマンドは [E2 / E3 補助手順](scripts/README.md#dump-から-trace_id-を読み出す)にまとめています。

WPA の Generic Events はイベント間で Field 列を共有します。そのため `JobStarted` の Field 2 は job ID ではなく16進の span ID として見える場合があります。列の表示状態で判断せず、必要なら ETL 内部を直接確認します。

```powershell
dotnet run --project .\tools\EtlInspector -- .\artifacts\handoff.etl
```

## Collector の Windows メトリクス

`windowsperfcountersreceiver` は10秒ごとに次の最小セットを収集します。

- `Memory\Available MBytes`
- `LogicalDisk(_Total)\Avg. Disk sec/Read`

設定名は英語 OS のカウンター名です。日本語 OS ではカウンター名のローカライズやカウンター未登録により取得できない場合があります。Collector の起動ログを確認し、その OS が公開する名前へ調整してください。記事の比較条件を変えないため、調整した名前は検証環境欄へ記録します。

`windowseventlogreceiver` は Alpha のため `collector/otelcol-contrib.yaml` でコメントアウトしています。試す場合だけ `windowseventlog` receiver を有効にし、logs pipeline の receivers に追加してください。既定手順へ含めると Collector 更新による破壊的変更が再現手順を不安定にするため、有効化しません。

## Docker を使わない代替手順

Jaeger は Trace だけを表示します。Log / Metrics と送信生データは Collector の `artifacts/otel/telemetry.json` で確認します。課金アカウントは不要です。

Jaeger 2.19.0 の Windows amd64 アーカイブを公式 Releases から取得して展開し、Collector と衝突しない gRPC 4318 / HTTP 4319 で起動します。

```powershell
$JaegerVersion = "2.19.0"
New-Item -ItemType Directory -Force .\artifacts\jaeger | Out-Null
Invoke-WebRequest "https://github.com/jaegertracing/jaeger/releases/download/v$JaegerVersion/jaeger-${JaegerVersion}-windows-amd64.tar.gz" -OutFile .\artifacts\jaeger.tar.gz
tar -xzf .\artifacts\jaeger.tar.gz -C .\artifacts\jaeger --strip-components 1
.\artifacts\jaeger\jaeger.exe --set=receivers.otlp.protocols.grpc.endpoint=localhost:4318 --set=receivers.otlp.protocols.http.endpoint=localhost:4319
```

`http://localhost:16686` で Trace を確認します。Collector 設定の転送先は Aspire と Jaeger のどちらも同じ `localhost:4318` なので変更不要です。

## Resource と情報管理

全シグナルに `service.name=otel-windows-handoff`、Core アセンブリの `service.version`、`host.name` を設定します。端末単位の絞り込みが記事の目的なので `host.name` は既定で含めますが、実環境へ展開する場合は送信先の情報管理・匿名化ポリシーに従ってください。

Span の `file.name` は `Path.GetFileName` の結果だけを設定し、フルパスを送信しません。出力ログにもサンプル固有の秘密情報、API キー、個人名を埋め込まないでください。

## CI

PR ごとに二つの job を実行します。

- `linux`: `OtelWindowsHandoff.Linux.slnf` の Core / Console / Tests を Release ビルドし、全テストを実行
- `windows`: `OtelWindowsHandoff.sln` を Release ビルドし、WinUI のコンパイルを保証

Linux job へ WinUI を復元させないため solution filter を使います。Core 側へ条件コンパイルを増やす方式は Windows API の混入を見逃しやすいため使っていません。

## バージョン固定表

| 項目 | 固定値 | 固定箇所 |
|---|---:|---|
| .NET SDK | 10.0.100、`latestFeature`、preview不可 | `global.json` |
| Target Framework | `net10.0` / `net10.0-windows10.0.19041.0` | 各 csproj |
| Windows SDK target | 10.0.19041.0 | `App.WinUI.csproj` |
| Microsoft.WindowsAppSDK | 2.2.0 | `Directory.Packages.props` |
| log4net（相関サンプル） | 3.3.2 | `Directory.Packages.props` |
| NLog（相関サンプル） | 6.1.4 | `Directory.Packages.props` |
| OpenTelemetry | 1.17.0 | `Directory.Packages.props` |
| OpenTelemetry.Exporter.OpenTelemetryProtocol | 1.17.0 | `Directory.Packages.props` |
| OpenTelemetry.Extensions.Hosting | 1.17.0 | `Directory.Packages.props` |
| OpenTelemetry.Exporter.Console（PoC） | 1.17.0 | `Directory.Packages.props` |
| Microsoft.Extensions.* | 10.0.0 | `Directory.Packages.props` |
| Microsoft.NET.Test.Sdk | 18.0.1 | `Directory.Packages.props` |
| xunit | 2.9.3 | `Directory.Packages.props` |
| xunit.runner.visualstudio | 3.1.5 | `Directory.Packages.props` |
| Microsoft.Diagnostics.Tracing.TraceEvent | 3.1.8 | `Directory.Packages.props` |
| otelcol-contrib | 0.153.0 | 本 README の取得コマンド |
| Aspire Dashboard | 13.1.0 | 本 README の Docker コマンド |
| Jaeger | 2.19.0 | 本 README の代替手順 |

NuGet のバージョンは Central Package Management で一か所に固定し、プロジェクトごとのずれを防いでいます。

## 検証環境の記録欄

記事の実測時に次を記入してください。

| 項目 | 記録値 |
|---|---|
| CPU | |
| RAM | |
| OS エディション / バージョン / ビルド | |
| .NET SDK (`dotnet --version`) | |
| ストレージ種別 | |
| Perf Counter 名の調整有無 | |
