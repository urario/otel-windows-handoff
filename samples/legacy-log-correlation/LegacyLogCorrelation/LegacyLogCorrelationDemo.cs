using System.Diagnostics;
using System.Globalization;
using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Layout;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace LegacyLogCorrelation;

/// <summary>ログ出力と、その行へ設定した trace_id をまとめます。</summary>
/// <param name="TraceId">開始した Activity の trace_id。</param>
/// <param name="Output">ロガーが Layout を通して生成した出力。</param>
public sealed record CorrelationResult(string TraceId, string Output);

/// <summary>既存ログライブラリのコンテキストへ Activity の trace_id を供給する最小例です。</summary>
public static class LegacyLogCorrelationDemo
{
    private const string Log4NetMessage = "log4net async correlation";
    private const string NLogMessage = "NLog async correlation";

    /// <summary>log4net の LogicalThreadContext と PatternLayout を使って trace_id を出力します。</summary>
    /// <returns>非同期境界後に生成されたログ行と trace_id。</returns>
    public static async Task<CorrelationResult> WriteLog4NetAsync()
    {
        using Activity activity = StartActivity("legacy-log-correlation.log4net");
        string traceId = activity.TraceId.ToString();
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        string repositoryName = $"legacy-log-correlation-{Guid.NewGuid():N}";
        log4net.Repository.ILoggerRepository repository = log4net.LogManager.CreateRepository(repositoryName);
        var layout = new PatternLayout("trace_id=%property{trace_id} message=%message%newline");
        layout.ActivateOptions();
        var appender = new TextWriterAppender
        {
            ImmediateFlush = true,
            Layout = layout,
            Writer = writer,
        };
        appender.ActivateOptions();
        BasicConfigurator.Configure(repository, appender);

        try
        {
            ILog logger = log4net.LogManager.GetLogger(repositoryName, typeof(LegacyLogCorrelationDemo));
            LogicalThreadContext.Properties["trace_id"] = traceId;
            try
            {
                await WriteLog4NetAfterAsyncBoundary(logger);
            }
            finally
            {
                LogicalThreadContext.Properties.Remove("trace_id");
            }

            return new CorrelationResult(traceId, writer.ToString());
        }
        finally
        {
            appender.Close();
            log4net.LogManager.ShutdownRepository(repositoryName);
        }
    }

    /// <summary>NLog の ScopeContext と scopeproperty Layout Renderer を使って trace_id を出力します。</summary>
    /// <returns>非同期境界後に生成されたログ行と trace_id。</returns>
    public static async Task<CorrelationResult> WriteNLogAsync()
    {
        using Activity activity = StartActivity("legacy-log-correlation.nlog");
        string traceId = activity.TraceId.ToString();
        var target = new MemoryTarget("memory")
        {
            Layout = "trace_id=${scopeproperty:trace_id} message=${message}",
        };
        using var factory = new LogFactory();
        var configuration = new LoggingConfiguration(factory);
        configuration.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, target);
        factory.Configuration = configuration;
        Logger logger = factory.GetLogger("LegacyLogCorrelation");

        using (ScopeContext.PushProperty("trace_id", traceId))
        {
            await WriteNLogAfterAsyncBoundary(logger);
        }

        factory.Flush();
        string output = string.Join(Environment.NewLine, target.Logs) + Environment.NewLine;
        return new CorrelationResult(traceId, output);
    }

    private static Activity StartActivity(string operationName)
    {
        return new Activity(operationName)
            .SetIdFormat(ActivityIdFormat.W3C)
            .Start();
    }

    private static async Task WriteLog4NetAfterAsyncBoundary(ILog logger)
    {
        await Task.Yield();
        logger.Info(Log4NetMessage);
    }

    private static async Task WriteNLogAfterAsyncBoundary(Logger logger)
    {
        await Task.Yield();
        logger.Info(NLogMessage);
    }
}
