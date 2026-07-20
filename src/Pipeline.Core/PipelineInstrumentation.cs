using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace OtelWindowsHandoff.Pipeline;

/// <summary>
/// パイプラインが公開する ActivitySource と Meter の契約を定義します。
/// 名前を一か所に集約し、Provider 側と計装側の文字列ずれを防ぎます。
/// </summary>
public static partial class PipelineInstrumentation
{
    /// <summary>ActivitySource と Meter に共通で使う名前です。</summary>
    public const string Name = "OtelWindowsHandoff.Pipeline";

    internal static readonly ActivitySource ActivitySource = new(Name);
    internal static readonly Meter Meter = new(Name);
    internal static readonly Counter<long> JobsCompleted = Meter.CreateCounter<long>("jobs.completed");
    internal static readonly Counter<long> JobsFailed = Meter.CreateCounter<long>("jobs.failed");
    internal static readonly Counter<long> RetriesTotal = Meter.CreateCounter<long>("retries.total");
    internal static readonly Histogram<double> JobDuration =
        Meter.CreateHistogram<double>("job.duration", unit: "ms");

    /// <summary>UI フリーズ操作を表す Span 名です。</summary>
    public const string UIFreezeActivityName = "UIFreezeRequested";

    /// <summary>UI フリーズ操作をログと EventSource で共有する名前です。</summary>
    public const string UIFreezeOperationName = "ui-freeze";

    /// <summary>
    /// UI スレッドをブロックする直前の相関情報を Span、EventSource、Dump マーカー、ログへ記録します。
    /// </summary>
    /// <param name="logger">handoff 行を記録する Logger。</param>
    /// <param name="uiThreadId">ブロック対象になる UI スレッドの managed thread ID。</param>
    /// <returns>全シグナルへ書き込んだ同一の相関情報。</returns>
    /// <remarks>
    /// 戻った時点で Span は完了しています。呼び出し側はこのメソッドの後に UI スレッドをブロックします。
    /// </remarks>
    public static UIFreezeHandoff RecordUIFreezeRequested(ILogger logger, int uiThreadId)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (uiThreadId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(uiThreadId));
        }

        int processId = Environment.ProcessId;
        Activity? activity = ActivitySource.StartActivity(UIFreezeActivityName, ActivityKind.Internal);
        activity?.SetTag("operation.name", UIFreezeOperationName);
        activity?.SetTag("ui.thread.id", uiThreadId);
        activity?.SetTag("process.id", processId);

        ActivityTraceId traceId = activity?.TraceId ?? ActivityTraceId.CreateRandom();
        ActivitySpanId spanId = activity?.SpanId ?? ActivitySpanId.CreateRandom();
        DateTimeOffset timestamp = activity is null
            ? DateTimeOffset.UtcNow
            : new DateTimeOffset(activity.StartTimeUtc, TimeSpan.Zero);
        activity?.Dispose();

        string traceIdText = traceId.ToString();
        string spanIdText = spanId.ToString();
        HandoffEventSource.Log.UIFreezeRequested(
            traceIdText,
            spanIdText,
            processId,
            uiThreadId,
            UIFreezeOperationName);
        HandoffDumpMarker.UpdateFreeze(
            traceIdText,
            spanIdText,
            processId,
            uiThreadId,
            timestamp);

        string handoffLine =
            $"handoff ts={timestamp:O} pid={processId} trace_id={traceIdText} span_id={spanIdText} ui_thread={uiThreadId} operation={UIFreezeOperationName}";
        LogHandoff(logger, handoffLine);
        Console.WriteLine(handoffLine);

        return new UIFreezeHandoff(
            traceIdText,
            spanIdText,
            processId,
            uiThreadId,
            UIFreezeOperationName,
            timestamp,
            handoffLine);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Handoff}")]
    private static partial void LogHandoff(ILogger logger, string handoff);
}
