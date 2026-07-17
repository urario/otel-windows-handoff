namespace OtelWindowsHandoff.Pipeline;

/// <summary>
/// パイプライン実行時の設定を保持します。
/// ファイルシステムや待機時間を外から渡せるため、実 ACL 操作や長い待機を使わずにテストできます。
/// </summary>
public sealed record PipelineOptions
{
    /// <summary>処理対象ファイルを直下から列挙する入力フォルダーを取得します。</summary>
    public required string InputDirectory { get; init; }

    /// <summary>処理後のファイルを書き込む出力フォルダーを取得します。</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>同時に処理できるジョブ数を取得します。</summary>
    public int MaxDegreeOfParallelism { get; init; } = 2;

    /// <summary>注入する障害の組み合わせを取得します。</summary>
    public FaultMode FaultMode { get; init; } = FaultMode.None;

    /// <summary>障害対象にするジョブの間隔を取得します。既定値10では10、20、30番目が対象です。</summary>
    public int FaultTargetEvery { get; init; } = 10;

    /// <summary>slow-read 対象ジョブへ加える固定遅延を取得します。</summary>
    public TimeSpan SlowReadDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>最初の保存試行が失敗した後に行う最大再試行回数を取得します。</summary>
    public int MaxSaveRetries { get; init; } = 3;

    /// <summary>指数バックオフの初回待機時間を取得します。</summary>
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>バイト列へ擬似フィルターを適用する回数を取得します。</summary>
    public int TransformPasses { get; init; } = 4;

    /// <summary>実ジョブ開始前に EventSource の有効化通知を待つウォームアップ時間を取得します。</summary>
    public TimeSpan EventSourceWarmupDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(InputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(OutputDirectory);

        if (MaxDegreeOfParallelism < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDegreeOfParallelism));
        }

        if (FaultTargetEvery < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(FaultTargetEvery));
        }

        if (MaxSaveRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSaveRetries));
        }

        if (TransformPasses < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(TransformPasses));
        }
    }
}
