namespace OtelWindowsHandoff.Pipeline;

/// <summary>
/// ジョブへ決定的に注入する障害の種類を表します。
/// フラグ列挙型にすることで、WinUI では二つの障害を同時に選べます。
/// </summary>
[Flags]
public enum FaultMode
{
    /// <summary>障害を注入しません。</summary>
    None = 0,

    /// <summary>対象ジョブの読み込みを固定時間だけ遅延させます。</summary>
    SlowRead = 1,

    /// <summary>対象ジョブの保存時にアクセス拒否例外を発生させます。</summary>
    AccessDenied = 2,
}
