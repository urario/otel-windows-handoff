namespace OtelWindowsHandoff.Pipeline;

/// <summary>
/// OpenTelemetry SDK と Exporter の構築方法を表します。
/// 計測コストを三条件で比較できるよう、単なる送信有無ではなく SDK 構築有無も分けています。
/// </summary>
public enum OtelMode
{
    /// <summary>OpenTelemetry の Provider を一切構築しません。</summary>
    Off,

    /// <summary>Provider を構築しますが Exporter は登録しません。</summary>
    Sdk,

    /// <summary>Provider と OTLP gRPC Exporter を構築します。</summary>
    Otlp,
}
