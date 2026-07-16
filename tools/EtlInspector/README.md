# ETL Inspector

WPRで取得したETLを直接読み取り、`OtelEtwSpike-Handoff` プロバイダーの
`JobStarted` と `JobCompleted` を一覧表示する確認用CLIです。
WPAのフィルターや表示状態に依存せず、ETL内部のイベントとペイロードを確認できます。

## 前提

| 項目 | バージョン |
|---|---:|
| .NET SDK | 10.0.100以上の10.0 feature band |
| Microsoft.Diagnostics.Tracing.TraceEvent | 3.1.8 |

## 実行

リポジトリのルートで次を実行します。

```powershell
dotnet run --project .\tools\EtlInspector -- .\artifacts\issue-36-manual\spike.etl
```

## 出力

イベントごとに名前とペイロードを表示し、最後に件数を集計します。

```text
JobStarted    traceId=<32桁の値>;spanId=<16桁の値>;jobId=1
JobCompleted  traceId=<32桁の値>;jobId=1
...

JobStarted=5
JobCompleted=5
Total=10
```

`JobStarted` は `traceId`、`spanId`、`jobId`、`JobCompleted` は
`traceId`、`jobId` を出力します。
