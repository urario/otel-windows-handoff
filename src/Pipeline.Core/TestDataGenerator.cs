using System.Security.Cryptography;

namespace OtelWindowsHandoff.Pipeline;

/// <summary>
/// 記事の再現実験に使うダミーファイルを生成します。
/// バイナリをリポジトリへ置かず、読者の環境で同じ件数とサイズを用意するための API です。
/// </summary>
public static class TestDataGenerator
{
    /// <summary>暗号学的乱数で埋めた指定件数のファイルを生成します。</summary>
    /// <param name="directory">生成先フォルダー。</param>
    /// <param name="count">生成するファイル数。</param>
    /// <param name="sizeMegabytes">1ファイルあたりのサイズを MiB 単位で指定します。</param>
    /// <param name="cancellationToken">生成を中止するためのトークン。</param>
    /// <returns>全ファイルの書き込み完了を表すタスク。</returns>
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
