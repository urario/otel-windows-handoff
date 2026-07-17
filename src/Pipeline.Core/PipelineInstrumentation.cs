using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OtelWindowsHandoff.Pipeline;

/// <summary>
/// パイプラインが公開する ActivitySource と Meter の契約を定義します。
/// 名前を一か所に集約し、Provider 側と計装側の文字列ずれを防ぎます。
/// </summary>
public static class PipelineInstrumentation
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
}
