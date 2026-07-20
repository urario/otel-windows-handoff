namespace OtelWindowsHandoff.Pipeline;

/// <summary>UI フリーズ操作を複数の診断シグナルへ引き継いだ結果です。</summary>
/// <param name="TraceId">32桁の16進 trace ID。</param>
/// <param name="SpanId">16桁の16進 span ID。</param>
/// <param name="ProcessId">対象プロセスの ID。</param>
/// <param name="UIThreadId">ブロック対象の managed thread ID。</param>
/// <param name="OperationName">ログと EventSource で共有する操作名。</param>
/// <param name="Timestamp">操作を記録した UTC 時刻。</param>
/// <param name="HandoffLine">標準出力と Logger へ書き込んだ行。</param>
public sealed record UIFreezeHandoff(
    string TraceId,
    string SpanId,
    int ProcessId,
    int UIThreadId,
    string OperationName,
    DateTimeOffset Timestamp,
    string HandoffLine);
