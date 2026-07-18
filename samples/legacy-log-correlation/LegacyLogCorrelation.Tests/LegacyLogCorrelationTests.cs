using Xunit;

namespace LegacyLogCorrelation.Tests;

/// <summary>既存ログライブラリの出力行へ trace_id が実際に入ることを検証します。</summary>
public sealed class LegacyLogCorrelationTests
{
    /// <summary>log4net の LogicalThreadContext が非同期境界を越えて PatternLayout へ渡ることを検証します。</summary>
    /// <returns>非同期テストの完了を表すタスク。</returns>
    [Fact]
    public async Task Log4NetIncludesActivityTraceIdAfterAsyncBoundary()
    {
        CorrelationResult result = await LegacyLogCorrelationDemo.WriteLog4NetAsync();

        AssertCorrelation(result, "log4net async correlation");
    }

    /// <summary>NLog の ScopeContext が非同期境界を越えて scopeproperty Renderer へ渡ることを検証します。</summary>
    /// <returns>非同期テストの完了を表すタスク。</returns>
    [Fact]
    public async Task NLogIncludesActivityTraceIdAfterAsyncBoundary()
    {
        CorrelationResult result = await LegacyLogCorrelationDemo.WriteNLogAsync();

        AssertCorrelation(result, "NLog async correlation");
    }

    private static void AssertCorrelation(CorrelationResult result, string message)
    {
        Assert.Matches("^[0-9a-f]{32}$", result.TraceId);
        Assert.Contains($"trace_id={result.TraceId}", result.Output, StringComparison.Ordinal);
        Assert.Contains($"message={message}", result.Output, StringComparison.Ordinal);
    }
}
