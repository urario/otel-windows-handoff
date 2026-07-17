# E2 / E3 補助スクリプト

リポジトリのルートをカレントディレクトリにした PowerShell で実行します。Windows PowerShell 5.1 と PowerShell 7 の両方を対象にしています。著者向け実験の全体的な進め方は、実験手順書に従ってください。

## 前提ツール

- .NET 10 SDK
- E3 の OTLP 条件では OpenTelemetry Collector Contrib。`collector/otelcol-contrib.yaml` を使い、`localhost:4317` で待ち受けていること
- E2 の記録では Windows Performance Toolkit の WPR
- E2 のハング Dump では Sysinternals ProcDump
- ETL / Dump の解析では WPA / WinDbg。解析自体はこのスクリプトの対象外

WPR を使う PowerShell は管理者として起動してください。ProcDump は同じユーザーのプロセスなら通常の PowerShell でも監視できますが、アクセス拒否になる対象では管理者 PowerShell から再実行します。ツールは `PATH` から検索します。

## Measure-E3.ps1

4条件を `off` → `sdk` → `otlp_up` → `otlp_down` の順に実行します。各条件はウォームアップ1回と本測定を `-Runs` 回行います。既定ターゲットは WinUI、本測定は5回です。

既存入力を使う基本例です。

```powershell
.\scripts\Measure-E3.ps1 -Target winui -Runs 5 -InputDirectory .\data\in
```

Console を測定する例です。

```powershell
.\scripts\Measure-E3.ps1 -Target console -Runs 5 -InputDirectory .\data\in
```

入力データを明示的に生成する場合だけ `-GenerateData` を付けます。同じ `count` / `size-mb` で決定的な入力を生成します。

```powershell
.\scripts\Measure-E3.ps1 -Target console -Runs 5 -GenerateData -GenerateCount 100 -GenerateSizeMb 1
```

`otlp_up` の測定後、既定では `otelcol-contrib` というプロセスを停止して `otlp_down` を測ります。コンテナやサービスとして起動した Collector は、停止方法を `-CollectorStopAction` で渡します。停止処理が失敗した場合は `otlp_down` をスキップし、CSV の `notes` に理由を残します。

```powershell
.\scripts\Measure-E3.ps1 -Target winui -CollectorStopAction { docker stop otel-collector }
```

Collector が開始時点で `localhost:4317` にいない場合、警告を表示して `otlp_up` と `otlp_down` をスキップします。スキップ行の `run_index` は `-1`、ウォームアップ行は `0` です。

主な引数は次のとおりです。

| 引数 | 既定値 | 説明 |
|---|---:|---|
| `-Target` | `winui` | `winui` は `--auto-run --exit-after`、`console` は `run` で起動 |
| `-Runs` | `5` | 各条件の本測定回数。別にウォームアップ1回を実行 |
| `-InputDirectory` | `.\data\in` | 既存入力ディレクトリ |
| `-GenerateData` | 無効 | 指定時だけ Console の `generate-data` を実行 |
| `-GenerateCount` | `100` | 生成ファイル数 |
| `-GenerateSizeMb` | `1` | 生成する各ファイルの MiB 数 |
| `-Parallel` | `2` | アプリへ渡す並列度 |
| `-FlushTimeoutMs` | `5000` | アプリへ渡す flush タイムアウト |
| `-Configuration` | `Release` | `Debug` または `Release` |
| `-SkipBuild` | 無効 | 既存ビルドを使い、スクリプト内ビルドを省略 |
| `-ApplicationPath` | 自動検出 | 測定対象の実行ファイルを明示指定 |
| `-ResultsDirectory` | `.\results` | CSV、環境情報、生ログ、処理結果の出力先 |
| `-CollectorOutputPath` | `.\artifacts\otel\telemetry.json` | Collector file exporter の出力 |
| `-CollectorHost` / `-CollectorPort` | `localhost` / `4317` | Collector の稼働判定先 |
| `-CollectorProcessName` | `otelcol-contrib` | 既定停止方式で使うプロセス名 |
| `-CollectorStopAction` | 未指定 | Collector の停止処理を表す ScriptBlock |
| `-CollectorWaitSeconds` | `5` | file exporter の反映待ち上限 |

出力は次のとおりです。すべて `results/` 配下にあり、Git の追跡対象外です。

- `e3_<timestamp>.csv`: `condition`、`target`、`run_index`、`elapsed_ms`、`private_bytes_peak`、`flush_ms`、`sent_bytes`、`dropped_count`、`notes`
- `e3_<timestamp>_environment.txt`: CPU、RAM、OS、PowerShell、.NET SDK、アプリ、Collector のバージョン
- `e3_<timestamp>_logs/`: 各試行の標準出力と標準エラーの生ログ
- `e3_<timestamp>_work/`: 各試行のパイプライン出力

`otlp_up` の `sent_bytes` は file exporter ファイルの試行前後の増分です。`dropped_count` は入力ファイル数から増分内の `ProcessJob` Span 数を引いて求めます。正しい差分を得るため、測定中は同じ Collector へ別アプリから送信しないでください。`otlp_down` は `sent_bytes=0`、`dropped_count=入力ファイル数` として記録します。

## Invoke-E2Capture.ps1

有効化前イベントの欠落を避けるため、順序は必ず「WPR 記録開始 → アプリ起動 → ProcDump 監視開始 → UI フリーズ発生 → WPR 停止」です。`-Start` が成功する前にアプリを起動しないでください。

管理者 PowerShell で WPR を開始します。プロファイルは既定で `collector/handoff.wprp` です。

```powershell
.\scripts\Invoke-E2Capture.ps1 -Start
```

開始成功のメッセージを確認してから、別の PowerShell で WinUI を起動します。

```powershell
dotnet run --project .\src\App.WinUI --configuration Release
```

WinUI 起動後、管理者 PowerShell から ProcDump のハング監視を開始します。対象プロセスが1つである必要があります。Dump 名は `<PID>_<UTC timestamp>.dmp` です。

```powershell
.\scripts\Invoke-E2Capture.ps1 -Hang -ProcessName App.WinUI -Out .\artifacts\dumps
```

監視開始後に UI のフリーズ操作を行います。Dump 取得後、WPR を停止します。

```powershell
.\scripts\Invoke-E2Capture.ps1 -Stop .\artifacts\handoff.etl
```

`-Profile` で別の WPR プロファイルを指定できます。スクリプトは WPR / ProcDump のバージョンだけを表示し、ツールのフルパスやマシン名は表示しません。ETL と Dump は `artifacts/` 配下に置けば Git の追跡対象外です。
