using System.Globalization;

namespace OtelWindowsHandoff.Pipeline;

/// <summary>
/// フル Dump から UI フリーズ操作の trace ID を読み出すため、静的ルートから文字列を保持します。
/// </summary>
/// <remarks>
/// <see cref="Current"/> がインスタンスを静的に保持するため、GC 後も WinDbg の <c>!dumpheap</c> から探索できます。
/// </remarks>
public sealed class HandoffDumpMarker
{
    /// <summary>Dump の UTF-16LE 検索に使う固定キーです。</summary>
    public const string SearchKey = "OTEL-HANDOFF";

    // currentValue が GC ルート、value は SOS の !dumpheap → !do で同じ文字列を辿るための参照です。
    private static string currentValue = SearchKey;
    private string value = currentValue;

    private HandoffDumpMarker()
    {
    }

    /// <summary>プロセス寿命で保持するマーカーインスタンスです。</summary>
    public static HandoffDumpMarker Current { get; } = new();

    /// <summary>最後に記録したマーカー文字列です。</summary>
    public string Value => Volatile.Read(ref value);

    /// <summary>UI フリーズ操作の相関情報を単一の文字列へ置き換えます。</summary>
    /// <param name="traceId">32桁の16進 trace ID。</param>
    /// <param name="spanId">16桁の16進 span ID。</param>
    /// <param name="processId">対象プロセスの ID。</param>
    /// <param name="uiThreadId">ブロック対象の managed thread ID。</param>
    /// <param name="timestamp">操作を記録した時刻。</param>
    public static void UpdateFreeze(
        string traceId,
        string spanId,
        int processId,
        int uiThreadId,
        DateTimeOffset timestamp)
    {
        string marker = string.Create(
            CultureInfo.InvariantCulture,
            $"{SearchKey} freeze trace_id={traceId} span_id={spanId} pid={processId} ui_thread={uiThreadId} ts={timestamp:O}");
        Volatile.Write(ref currentValue, marker);
        Volatile.Write(ref Current.value, Volatile.Read(ref currentValue));
    }
}
