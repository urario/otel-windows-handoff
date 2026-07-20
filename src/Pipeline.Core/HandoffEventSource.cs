using System.Diagnostics.Tracing;

namespace OtelWindowsHandoff.Pipeline;

/// <summary>
/// OpenTelemetry の識別子を ETW へ引き継ぐイベントを発行します。
/// OS 固有 API を直接呼ばず <see cref="EventSource"/> を使うため、Core は Linux でもビルドできます。
/// </summary>
[EventSource(Name = "OtelWindowsHandoff-Handoff")]
public sealed class HandoffEventSource : EventSource
{
    /// <summary>プロセス内で共有する EventSource インスタンスです。</summary>
    public static readonly HandoffEventSource Log = new();

    private HandoffEventSource()
    {
    }

    /// <summary>ジョブ開始時の trace ID、span ID、ジョブ ID を発行します。</summary>
    /// <param name="traceId">32桁の16進 trace ID。</param>
    /// <param name="spanId">16桁の16進 span ID。</param>
    /// <param name="jobId">ファイル名順で採番したジョブ ID。</param>
    [Event(1, Level = EventLevel.Informational)]
    public void JobStarted(string traceId, string spanId, int jobId)
    {
        WriteEvent(1, traceId, spanId, jobId);
    }

    /// <summary>ジョブ終了時の trace ID とジョブ ID を発行します。</summary>
    /// <param name="traceId">開始イベントと同じ trace ID。</param>
    /// <param name="jobId">開始イベントと同じジョブ ID。</param>
    [Event(2, Level = EventLevel.Informational)]
    public void JobCompleted(string traceId, int jobId)
    {
        WriteEvent(2, traceId, jobId);
    }

    /// <summary>
    /// 実ジョブより前に破棄可能なイベントを発行し、ETW セッションの有効化通知を促します。
    /// 先頭の JobStarted を試験用イベントにすると欠落時の対応付けが壊れるため、専用イベントを使います。
    /// </summary>
    [Event(3, Level = EventLevel.Informational)]
    public void Warmup()
    {
        WriteEvent(3);
    }

    /// <summary>UI フリーズ操作の相関情報を、UI スレッドをブロックする前に発行します。</summary>
    /// <param name="traceId">32桁の16進 trace ID。</param>
    /// <param name="spanId">16桁の16進 span ID。</param>
    /// <param name="processId">対象プロセスの ID。</param>
    /// <param name="uiThreadId">ブロック対象の managed thread ID。</param>
    /// <param name="operationName">ログと共有する操作名。</param>
    [Event(4, Level = EventLevel.Informational)]
    public void UIFreezeRequested(
        string traceId,
        string spanId,
        int processId,
        int uiThreadId,
        string operationName)
    {
        WriteEvent(4, traceId, spanId, processId, uiThreadId, operationName);
    }
}
