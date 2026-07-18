namespace OtelWindowsHandoff.Pipeline;

/// <summary>パイプラインから通知されるイベントの種類を表します。</summary>
public enum PipelineProgressEvent
{
    /// <summary>入力ファイルがジョブとして列挙されました。</summary>
    JobQueued,

    /// <summary>ジョブの処理を開始しました。</summary>
    JobStarted,

    /// <summary>フェーズの処理を開始しました。</summary>
    PhaseStarted,

    /// <summary>フェーズの処理を完了または失敗しました。</summary>
    PhaseCompleted,

    /// <summary>save フェーズの再試行を予定しました。</summary>
    RetryScheduled,

    /// <summary>ジョブの処理を完了または失敗しました。</summary>
    JobCompleted,
}

/// <summary>ジョブパイプラインのフェーズを表します。</summary>
public enum PipelinePhase
{
    /// <summary>ジョブ全体に対する通知です。</summary>
    None,

    /// <summary>入力ファイルを読み込むフェーズです。</summary>
    Load,

    /// <summary>読み込んだデータを変換するフェーズです。</summary>
    Transform,

    /// <summary>変換結果を保存するフェーズです。</summary>
    Save,
}

/// <summary>通知時点のジョブまたはフェーズ状態を表します。</summary>
public enum PipelineProgressState
{
    /// <summary>実行を待っています。</summary>
    Waiting,

    /// <summary>実行中です。</summary>
    Running,

    /// <summary>正常に完了しました。</summary>
    Succeeded,

    /// <summary>失敗しました。</summary>
    Failed,
}

/// <summary>UI とコンソールへ通知するジョブ／フェーズ単位の進捗を表します。</summary>
/// <param name="Event">発生したイベント。</param>
/// <param name="State">イベント後の状態。</param>
/// <param name="Processed">処理を終了したジョブ数。</param>
/// <param name="Failed">失敗したジョブ数。</param>
/// <param name="Total">列挙済みの全ジョブ数。</param>
/// <param name="JobId">ファイル名順で採番した1始まりのジョブ ID。</param>
/// <param name="FileName">パスを含まないファイル名。</param>
/// <param name="FileSize">入力ファイルのバイト数。</param>
/// <param name="Phase">対象フェーズ。ジョブ全体の通知では <see cref="PipelinePhase.None"/>。</param>
/// <param name="Duration">完了したフェーズまたはジョブの実測所要時間。</param>
/// <param name="RetryCount">通知時点の save 再試行回数。</param>
/// <param name="TraceId">ProcessJob Span と handoff 行で共有する trace_id。開始前は <see langword="null"/>。</param>
/// <param name="SpanId">ProcessJob Span の span_id。開始前は <see langword="null"/>。</param>
/// <param name="StartedAt">ジョブ開始時刻。開始前は <see langword="null"/>。</param>
/// <param name="InjectedFault">このジョブへ適用した決定的な障害。対象外では <see cref="FaultMode.None"/>。</param>
/// <param name="ErrorMessage">失敗理由。正常時は <see langword="null"/>。</param>
public sealed record PipelineProgress(
    PipelineProgressEvent Event,
    PipelineProgressState State,
    int Processed,
    int Failed,
    int Total,
    int JobId,
    string FileName,
    long FileSize,
    PipelinePhase Phase,
    TimeSpan Duration,
    int RetryCount,
    string? TraceId,
    string? SpanId,
    DateTimeOffset? StartedAt,
    FaultMode InjectedFault,
    string? ErrorMessage)
{
    /// <summary>従来の集計表示との互換用に、通知元ジョブのファイル名を取得します。</summary>
    public string CurrentJob => FileName;
}

/// <summary>一つのジョブの最終結果を表します。</summary>
/// <param name="JobId">ファイル名順で採番した1始まりの識別子。</param>
/// <param name="FileName">パスを含まないファイル名。</param>
/// <param name="FileSize">入力ファイルのバイト数。</param>
/// <param name="Succeeded">全フェーズが完了した場合は <see langword="true"/>。</param>
/// <param name="RetryCount">保存処理を再試行した回数。</param>
/// <param name="Duration">ジョブ全体の所要時間。</param>
/// <param name="LoadDuration">load フェーズの所要時間。</param>
/// <param name="TransformDuration">transform フェーズの所要時間。</param>
/// <param name="SaveDuration">save フェーズの所要時間。</param>
/// <param name="TraceId">ProcessJob Span と handoff 行で共有する trace_id。</param>
/// <param name="SpanId">ProcessJob Span の span_id。</param>
/// <param name="StartedAt">ジョブの開始時刻。</param>
/// <param name="InjectedFault">このジョブへ適用した決定的な障害。</param>
/// <param name="ErrorMessage">失敗理由。成功時は <see langword="null"/>。</param>
public sealed record JobResult(
    int JobId,
    string FileName,
    long FileSize,
    bool Succeeded,
    int RetryCount,
    TimeSpan Duration,
    TimeSpan LoadDuration,
    TimeSpan TransformDuration,
    TimeSpan SaveDuration,
    string TraceId,
    string SpanId,
    DateTimeOffset StartedAt,
    FaultMode InjectedFault,
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
