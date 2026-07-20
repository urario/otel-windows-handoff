# E2 / E3 補助スクリプト

リポジトリのルートをカレントディレクトリにした PowerShell で実行します。Windows PowerShell 5.1 と PowerShell 7 の両方を対象にしています。著者向け実験の全体的な進め方は、実験手順書に従ってください。

## 前提ツール

- .NET 10 SDK
- E3 の OTLP 条件では OpenTelemetry Collector Contrib。測定専用の `collector/otelcol-e3.yaml` を使い、OTLP の `localhost:4317` と構成確認用 health check の `localhost:13134` で待ち受けていること
- E2 の記録では Windows Performance Toolkit の WPR
- E2 のハング Dump では Sysinternals ProcDump
- ETL / Dump の解析では WPA / WinDbg。解析自体はこのスクリプトの対象外

WPR を使う PowerShell は管理者として起動してください。ProcDump は同じユーザーのプロセスなら通常の PowerShell でも監視できますが、アクセス拒否になる対象では管理者 PowerShell から再実行します。ツールは `PATH` から検索します。

## Measure-E3.ps1

4条件を測定します。`off` / `sdk` / `otlp_up` は決定的な全順列で実行し、各条件の位置だけでなく直前条件の偏りも減らします。3条件では6 run、Collector 不在時の2条件では2 run で1周期です。完全に均衡させるには `-Runs` を周期の倍数にしてください。`otlp_down` だけは Collector の停止を伴うため最後にまとめて実行し、CSV の `notes` に `order_fixed:last_after_collector_stop;late_run_drift_not_balanced` を残します。この条件は時間ドリフトと分離できないため、他条件との小さな差を因果効果と解釈しません。各条件はウォームアップ1回と本測定を `-Runs` 回行います。既定ターゲットは WinUI、本測定は均衡周期に合わせた6回です。

`off` は「完全な未計装ビルド」ではなく「OTel SDK off」です。ActivitySource / Meter の API 呼び出し、EventSource、通常のコンソールログは残り、Provider だけを構築しません。比較結果はこの範囲の差として解釈します。

E3 では Performance Counter の定期出力をアプリ1実行分へ混入させないため、通常用の `otelcol-contrib.yaml` ではなく `windowsperfcounters` receiver を持たない専用設定で Collector を起動します。file exporter の出力先は通常用と同じです。専用設定だけが `localhost:13134` の health check を公開し、測定スクリプトは OTLP ポートとこのポートの両方を確認して設定取り違えを検出します。

```powershell
.\artifacts\otelcol-contrib.exe --config .\collector\otelcol-e3.yaml
```

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

Collector が開始時点で `localhost:4317` にいない場合、または E3 専用 health check の `localhost:13134` を確認できない場合、警告を表示して `otlp_up` と `otlp_down` をスキップします。前者は `collector_not_running`、後者は `collector_e3_config_not_verified` を CSV の `notes` に残します。スキップ行の `run_index` は `-1`、ウォームアップ行は `0` です。

主な引数は次のとおりです。

| 引数 | 既定値 | 説明 |
|---|---:|---|
| `-Target` | `winui` | `winui` は `--auto-run --exit-after`、`console` は `run` で起動 |
| `-Runs` | `6` | 各条件の本測定回数。別にウォームアップ1回を実行。3条件の順序均衡は6 runで1周期 |
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
| `-CollectorHealthPort` | `13134` | E3 専用設定の health check 判定先 |
| `-CollectorProcessName` | `otelcol-contrib` | 既定停止方式で使うプロセス名 |
| `-CollectorStopAction` | 未指定 | Collector の停止処理を表す ScriptBlock |
| `-CollectorWaitSeconds` | `5` | file exporter の反映待ち上限 |

出力は次のとおりです。すべて `results/` 配下にあり、Git の追跡対象外です。

- `e3_<timestamp>.csv`: `condition`、`target`、`run_index`、`process_elapsed_ms`、`pipeline_elapsed_ms`、`private_bytes_peak`、`flush_elapsed_ms`、`exporter_file_delta_bytes`、`processjob_rootspan_missing`、`notes`
- `e3_<timestamp>_environment.txt`: CPU、RAM、OS、PowerShell、.NET SDK、アプリ、Collector のバージョンと、OS ファイルキャッシュ・ウイルススキャンを制御していない旨
- `e3_<timestamp>_logs/`: 各試行の標準出力と標準エラーの生ログ
- `e3_<timestamp>_work/`: 各試行のパイプライン出力

各列の測定対象は次のとおりです。

| 列 | 測定対象 |
|---|---|
| `process_elapsed_ms` | プロセスの起動から終了まで。SDK 初期化、パイプライン、flush を含む |
| `pipeline_elapsed_ms` | アプリの `pipeline elapsed_ms=<n>` 行から取得した `PipelineRunner.RunAsync` の呼び出し時間 |
| `flush_elapsed_ms` | アプリの `otel flush ... elapsed_ms=<n>` 行から取得した終了処理時間 |
| `exporter_file_delta_bytes` | Collector の file exporter ファイルの試行前後の増分。ネットワーク上の wire bytes ではない |
| `processjob_rootspan_missing` | 入力ファイル数から増分内の `ProcessJob` ルート Span 数を引いた観測欠落数。子 Span、ログ、メトリクスの欠落は対象外 |

正しい file exporter 差分を得るため、測定中は同じ Collector へ別アプリから送信しないでください。`otlp_down` は `exporter_file_delta_bytes=0`、`processjob_rootspan_missing=入力ファイル数` として記録します。ウォームアップ行を除外したうえで、各条件は中央値と最小/最大（外れ値の分布を見る場合は IQR）を併記して比較します。

## Invoke-E2Capture.ps1

有効化前イベントの欠落を避けるため、順序は必ず「WPR 記録開始 → アプリ起動 → ProcDump 監視開始 → UI フリーズ発生 → WPR 停止」です。`-Start` が成功する前にアプリを起動しないでください。

管理者 PowerShell で WPR を開始します。既定の `collector/handoff.wprp` はカスタム EventSource に加え、`ProcessThread`、`CSwitch`、`ReadyThread`、`SampledProfile` のカーネルイベントを収録します。これにより handoff イベントは WPA の Generic Events、UI スレッドの待機は CPU Usage (Precise) / Wait 系グラフで確認できます。カスタム EventSource だけを収録した ETL では Wait 解析はできません。カーネル収録を含むため ETL サイズは EventSource だけの場合より大きくなります。

`-Start` は開始前に `wpr -status` を表示します。別のカーネル記録や残留セッションと競合して開始できない場合は、まず `wpr -status` で所有者を確認します。他ツールの記録ならそのツール側で停止し、この実験で残ったセッションだと確認できた場合だけ `wpr -cancel` を実行してください。スクリプトは他の記録を破壊しないよう、自動 cancel は行いません。

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

### Dump から trace_id を読み出す

フリーズボタンはブロック前に、静的ルートから到達可能な `HandoffDumpMarker` へ次の単一文字列を書き込みます。

```text
OTEL-HANDOFF freeze trace_id=<32hex> span_id=<16hex> pid=<n> ui_thread=<n> ts=<ISO8601>
```

WinDbg でフル Dump を開き、SOS を読み込んでマーカー型を探します。`!dumpheap` が返した `HandoffDumpMarker` のアドレスを `!do` へ渡し、表示された `value` フィールドの文字列アドレスをもう一度 `!do` で開きます。

```text
.loadby sos coreclr
!dumpheap -type OtelWindowsHandoff.Pipeline.HandoffDumpMarker
!do <HandoffDumpMarker のアドレス>
!do <value が参照する System.String のアドレス>
```

SOS を利用できない場合は、WinDbg の Unicode 文字列検索で固定キーを直接探します。ProcDump の `-ma` で取得したフル Dump には .NET 文字列の UTF-16LE バイト列として残ります。

```text
s -su 0 L?7fffffffffffffff "OTEL-HANDOFF"
```

取得した文字列の `trace_id` を Trace UI の `UIFreezeRequested` Span および ETL の同名イベントと照合します。PID と時刻だけに依存せず、フリーズ操作そのものを Trace → ETW → Dump と同じキーで追えることが E2 の確認点です。
