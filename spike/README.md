# OpenTelemetry trace_id と ETW の引き継ぎ PoC

この PoC は、OpenTelemetry の `trace_id` をカスタム EventSource の ETW イベントへ書き込み、同じ値を Windows Performance Analyzer (WPA) で確認できることを検証する。外部サービスや Collector は使わない。

以下のコマンドは、リポジトリのルートをカレントディレクトリにした PowerShell で実行する。

## バージョン固定

| 項目 | 固定値 | 固定箇所 |
|---|---:|---|
| .NET SDK | 10.0.100 以上の 10.0 feature band | `global.json` の `10.0.100` と `rollForward: latestFeature` |
| Target Framework | `net10.0` | `OtelEtwSpike.csproj` |
| OpenTelemetry | `1.16.0` | `OtelEtwSpike.csproj` |
| OpenTelemetry.Exporter.Console | `1.16.0` | `OtelEtwSpike.csproj` |

`global.json` は .NET 10 SDK のメジャーを固定し、インストール済みの最新 10.0 feature band を選ぶ。プレビュー SDK は選ばない。

## ETW イベント契約

WPA で探す名前とペイロードは次のとおり。

| プロバイダー名 | イベント名 | Event ID | Level | ペイロード名と型 |
|---|---|---:|---|---|
| `OtelEtwSpike-Handoff` | `JobStarted` | 1 | Informational | `traceId: string`, `spanId: string`, `jobId: int` |
| `OtelEtwSpike-Handoff` | `JobCompleted` | 2 | Informational | `traceId: string`, `jobId: int` |

EventSource 名から導出されるプロバイダー GUID は `313ca412-ded3-5397-cdf4-cbd5525e98cd`。次の PowerShell で同じ値を確認できる。

```powershell
$source = [System.Diagnostics.Tracing.EventSource]::new('OtelEtwSpike-Handoff')
$source.Guid
$source.Dispose()
```

`poc.wprp` は、名前から GUID を導出する TraceLogging/EventSource 用の `*OtelEtwSpike-Handoff` 指定を使う。CPU サンプリングなど、PoC に不要なプロバイダーは収集しない。

## 1. 前提を確認する

- Windows 10 または Windows 11
- .NET 10 SDK
- Windows Performance Toolkit の WPR と WPA
- WPR の開始と停止では管理者 PowerShell が必要

通常の PowerShell で .NET SDK を確認する。

```powershell
dotnet --version
dotnet --list-sdks
```

`dotnet --version` が `10.0.x` にならない場合は、.NET 10 SDK をインストールしてから再実行する。

Windows Performance Toolkit は Windows ADK のインストーラーで「Windows Performance Toolkit」を選択して導入する。導入後、次のコマンドがどちらもパスを返すことを確認する。

```powershell
(Get-Command wpr.exe -ErrorAction Stop).Source
(Get-Command wpa.exe -ErrorAction Stop).Source
```

WPA がインストール済みでも `PATH` にない場合は、Windows Performance Toolkit のインストール先を現在の PowerShell の `PATH` に追加してから同じ確認を行う。

## 2. ビルドする

通常の PowerShell で復元とビルドを行う。

```powershell
dotnet build .\spike\OtelEtwSpike\OtelEtwSpike.csproj
```

出力が `Build succeeded.`、`0 Warning(s)`、`0 Error(s)` で終わることを確認する。

## 3. WPR 記録を開始する

リポジトリのルートで管理者 PowerShell を開き、次を実行する。

最初に WPR がプロファイルを読み取れることを確認する。このコマンドは、プロファイル名 `OtelEtwSpike` と説明を表示する。

```powershell
wpr -profiles .\spike\poc.wprp
```

続けて記録を開始する。

```powershell
wpr -start .\spike\poc.wprp -filemode
```

`The command completed successfully.` と表示されてから次へ進む。別の WPR セッションが実行中というエラーになった場合は、その記録の所有者に確認して停止または保存してから再実行する。

## 4. アプリを実行する

WPR の記録中に、リポジトリのルートで次を実行する。管理者 PowerShell のまま実行しても、別の通常 PowerShell で実行してもよい。

```powershell
dotnet run --project .\spike\OtelEtwSpike --no-build
```

ConsoleExporter の出力に、表示名が `job` の Span が5件あることを確認する。各 Span には `Activity.TraceId`、`Activity.SpanId`、`job.id` が表示される。また、次の形式の `handoff` 行がジョブごとに1行、合計5行表示される。

```text
handoff ts=<UTCのISO 8601時刻> pid=<プロセスID> trace_id=<32桁のtrace_id> job=<1から5>
```

## 5. WPR 記録を停止する

手順3で使った管理者 PowerShell で、ETL をリポジトリのルートへ保存する。

```powershell
wpr -stop .\spike.etl
```

`The command completed successfully.` と `spike.etl` の作成を確認する。

手順4より前にアプリが失敗したなど、ETL を保存せず記録を破棄する場合だけ、管理者 PowerShell で次を実行する。

```powershell
wpr -cancel
```

## 6. WPA で ETW イベントを確認する

1. WPA を起動し、`File` > `Open` からリポジトリ直下の `spike.etl` を開く。
2. `Graph Explorer` の `System Activity` を展開する。`Generic Events` をダブルクリックするか、`Analysis` ペインへドラッグする。
3. `Generic Events` の表で `Provider Name` 列の `OtelEtwSpike-Handoff` を探す。該当行を右クリックして `Filter To Selection` を選ぶ。
4. プロバイダー行を展開し、`Event Name` が `JobStarted` と `JobCompleted` であることを確認する。`JobStarted` と `JobCompleted` は各5件、合計10件になる。
5. 表の列見出しを右クリックして列選択を開く。ペイロード列の `traceId`、`spanId`、`jobId` を有効にする。WPA の版によって列選択が見当たらない場合は、表の右上にある歯車アイコンの `View Editor` を開き、同じ3列を `Visible Columns` へ追加する。
6. `JobStarted` 行には `traceId`、`spanId`、`jobId`、`JobCompleted` 行には `traceId` と `jobId` が表示されることを確認する。`JobCompleted` の `spanId` が空欄なのはイベント契約どおりである。

`Generic Events` が `Graph Explorer` に出ない場合は、`Window` > `Select Tables` で `System Activity` の `Generic Events` を有効にして ETL を開き直す。プロバイダー行がない場合は、WPR 開始成功後にアプリを実行したことと、手順3で `poc.wprp` を指定したことを確認して記録をやり直す。

## 7. trace_id の一致を判定する

1. コンソール出力で `job.id: 1` の Span を探し、その `Activity.TraceId` をコピーする。
2. WPA の `JobStarted` 行を `jobId = 1` で特定し、`traceId` ペイロードをコピーする。
3. 2つの値が、大文字小文字を無視した32桁の16進文字列として完全一致することを確認する。
4. `jobId` 2〜5でも同じ比較を行う。5件すべてが一致すれば、`trace_id` を引き継ぎキーとして OpenTelemetry と ETW を突合できると判定する。

EventSource と WPA の経路で確認できない場合は、コンソールの `handoff` 行にある UTC 時刻、PID、`trace_id`、ジョブ ID を使い、ETL の同時刻・同一プロセスと手動で突合する。この行形式の確認自体は WPR/WPA に依存しない。
