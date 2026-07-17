using System.Security.Cryptography;

namespace OtelWindowsHandoff.Pipeline;

/// <summary>
/// 記事の再現実験に使うダミーファイルを生成します。
/// バイナリをリポジトリへ置かず、読者の環境で同じ件数とサイズを用意するための API です。
/// </summary>
public static class TestDataGenerator
{
    /// <summary>暗号学的乱数で埋めたダミーファイルを指定件数生成します。</summary>
    /// <param name="directory">
    /// 生成先フォルダー。存在しない場合は作成します。
    /// </param>
    /// <param name="count">生成するファイル数。1以上を指定します。</param>
    /// <param name="sizeMegabytes">1ファイルあたりのサイズ。MiB 単位で1以上を指定します。</param>
    /// <param name="cancellationToken">ファイル生成を中止するためのトークン。</param>
    /// <returns>すべてのファイルの書き込み完了を表すタスク。</returns>
    /// <remarks>
    /// ファイル名は <c>input-0001.bin</c> からの連番です。同名ファイルは上書きします。
    /// リポジトリに固定バイナリを置かず、指定した件数とサイズを各環境で再現するためのメソッドです。
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="directory"/> が空文字列または空白だけです。</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="count"/> または <paramref name="sizeMegabytes"/> が1未満です。
    /// </exception>
    /// <exception cref="OverflowException">
    /// <paramref name="sizeMegabytes"/> をバイト数へ変換した値が <see cref="int"/> の範囲を超えます。
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> により処理が中止されました。</exception>
    public static async Task GenerateAsync(
        string directory,
        int count = 100,
        int sizeMegabytes = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeMegabytes);

        Directory.CreateDirectory(directory);
        byte[] buffer = new byte[checked(sizeMegabytes * 1024 * 1024)];

        for (int index = 1; index <= count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RandomNumberGenerator.Fill(buffer);
            string path = Path.Combine(directory, $"input-{index:D4}.bin");
            await File.WriteAllBytesAsync(path, buffer, cancellationToken);
        }
    }
}
