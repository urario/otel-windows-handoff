namespace OtelWindowsHandoff.Pipeline;

/// <summary>UI とコンソールへ通知する処理途中の集計値を表します。</summary>
/// <param name="Processed">処理を終了したジョブ数。</param>
/// <param name="Failed">失敗したジョブ数。</param>
/// <param name="Total">列挙済みの全ジョブ数。</param>
/// <param name="CurrentJob">通知を発生させたジョブのファイル名。</param>
public sealed record PipelineProgress(
    int Processed,
    int Failed,
    int Total,
    string CurrentJob);

/// <summary>一つのジョブの最終結果を表します。</summary>
/// <param name="JobId">ファイル名順で採番した1始まりの識別子。</param>
/// <param name="FileName">パスを含まないファイル名。</param>
/// <param name="Succeeded">全フェーズが完了した場合は <see langword="true"/>。</param>
/// <param name="RetryCount">保存処理を再試行した回数。</param>
/// <param name="Duration">ジョブ全体の所要時間。</param>
/// <param name="ErrorMessage">失敗理由。成功時は <see langword="null"/>。</param>
public sealed record JobResult(
    int JobId,
    string FileName,
    bool Succeeded,
    int RetryCount,
    TimeSpan Duration,
    string? ErrorMessage);

/// <summary>
/// パイプライン全体の結果を表します。
/// 元のジョブ結果を残すことで、集計値だけでは分からない決定的な障害対象を検証できます。
/// </summary>
/// <param name="Jobs">ジョブ ID 順の結果一覧。</param>
public sealed record PipelineResult(IReadOnlyList<JobResult> Jobs)
{
    /// <summary>成功したジョブ数を取得します。</summary>
    public int Completed => Jobs.Count(job => job.Succeeded);

    /// <summary>失敗したジョブ数を取得します。</summary>
    public int Failed => Jobs.Count - Completed;

    /// <summary>全ジョブで行った保存再試行の合計を取得します。</summary>
    public int TotalRetries => Jobs.Sum(job => job.RetryCount);
}
