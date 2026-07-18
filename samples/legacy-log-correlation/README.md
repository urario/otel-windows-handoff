# 既存ログライブラリと trace_id の相関

既存アプリへ OpenTelemetry を段階導入するときに、開始した `Activity` の `TraceId`(実運用では `Activity.Current?.TraceId` に相当)を現在のログライブラリへ渡す最小サンプルです。メインアプリや `Pipeline.Core` は log4net / NLog を参照しません。

## 実行

```powershell
dotnet run --project .\samples\legacy-log-correlation\LegacyLogCorrelation --configuration Release
```

log4net と NLog が、それぞれ次の形式で trace_id 付きの行を出力します。

```text
trace_id=<32桁の16進数> message=log4net async correlation
trace_id=<32桁の16進数> message=NLog async correlation
```

## 相関方法

log4net は `LogicalThreadContext.Properties["trace_id"]` と PatternLayout の `%property{trace_id}` を組み合わせます。

NLog は `ScopeContext.PushProperty("trace_id", value)` と Layout Renderer の `${scopeproperty:trace_id}` を組み合わせます。

どちらもプロパティを設定した後に非同期メソッドへ移動してからログを出します。xUnit は、Layout 適用後の出力行に開始した `Activity` と同じ trace_id が含まれることを検証します。

```powershell
dotnet test .\samples\legacy-log-correlation\LegacyLogCorrelation.Tests --configuration Release
```
