using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace OtelWindowsHandoff.Pipeline;

/// <summary>
/// OpenTelemetry Provider と <see cref="ILoggerFactory"/> のライフサイクルをまとめて管理します。
/// アプリ側へ Provider の終了順序を分散させないことで、終了直前のテレメトリ欠落を避けます。
/// </summary>
public sealed class TelemetrySession : IDisposable
{
    /// <summary>全シグナルで共通に設定するサービス名です。</summary>
    public const string ServiceName = "otel-windows-handoff";

    private readonly OtelMode mode;
    private readonly TimeSpan shutdownTimeout;
    private readonly ServiceProvider serviceProvider;
    private readonly TracerProvider? tracerProvider;
    private readonly MeterProvider? meterProvider;
    private readonly LoggerProvider? loggerProvider;
    private bool disposed;

    private TelemetrySession(
        OtelMode mode,
        TimeSpan shutdownTimeout,
        ServiceProvider serviceProvider,
        ILoggerFactory loggerFactory,
        TracerProvider? tracerProvider,
        MeterProvider? meterProvider,
        LoggerProvider? loggerProvider)
    {
        this.mode = mode;
        this.shutdownTimeout = shutdownTimeout;
        this.serviceProvider = serviceProvider;
        LoggerFactory = loggerFactory;
        this.tracerProvider = tracerProvider;
        this.meterProvider = meterProvider;
        this.loggerProvider = loggerProvider;
    }

    /// <summary>パイプラインが利用する Logger を作成するファクトリを取得します。</summary>
    public ILoggerFactory LoggerFactory { get; }

    /// <summary>指定された動作モードのテレメトリセッションを作成します。</summary>
    /// <param name="mode">Provider と Exporter の構築方法。</param>
    /// <param name="shutdownTimeout">終了時の ForceFlush と Shutdown に使える合計時間。省略時は5秒です。</param>
    /// <param name="otlpEndpoint">OTLP gRPC の送信先。省略時は <c>http://localhost:4317</c> です。</param>
    /// <returns>LoggerFactory と Provider の所有権を持つセッション。</returns>
    /// <remarks>
    /// <see cref="OtelMode.Off"/> では通常のコンソール Logger だけを作り、OpenTelemetry Provider は登録しません。
    /// これにより SDK 初期化コストを含まない測定条件を保ちます。
    /// </remarks>
    public static TelemetrySession Create(
        OtelMode mode,
        TimeSpan? shutdownTimeout = null,
        Uri? otlpEndpoint = null)
    {
        TimeSpan timeout = shutdownTimeout ?? TimeSpan.FromSeconds(5);
        Uri endpoint = otlpEndpoint ?? new Uri("http://localhost:4317");

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
                options.UseUtcTimestamp = true;
            });
        });

        if (mode == OtelMode.Off)
        {
            ServiceProvider offServiceProvider = services.BuildServiceProvider();
            return new TelemetrySession(
                mode,
                timeout,
                offServiceProvider,
                offServiceProvider.GetRequiredService<ILoggerFactory>(),
                null,
                null,
                null);
        }

        services.AddOpenTelemetry()
            .ConfigureResource(builder => ConfigureResource(builder))
            .WithTracing(builder =>
            {
                builder.AddSource(PipelineInstrumentation.Name);
                if (mode == OtelMode.Otlp)
                {
                    builder.AddOtlpExporter(options => ConfigureExporter(options, endpoint));
                }
            })
            .WithMetrics(builder =>
            {
                builder.AddMeter(PipelineInstrumentation.Name);
                if (mode == OtelMode.Otlp)
                {
                    builder.AddOtlpExporter(options => ConfigureExporter(options, endpoint));
                }
            })
            .WithLogging(
                builder =>
                {
                    if (mode == OtelMode.Otlp)
                    {
                        builder.AddOtlpExporter(options => ConfigureExporter(options, endpoint));
                    }
                },
                options => options.IncludeFormattedMessage = true);

        ServiceProvider serviceProvider = services.BuildServiceProvider();
        TracerProvider tracerProvider = serviceProvider.GetRequiredService<TracerProvider>();
        MeterProvider meterProvider = serviceProvider.GetRequiredService<MeterProvider>();
        LoggerProvider loggerProvider = serviceProvider.GetRequiredService<LoggerProvider>();
        ILoggerFactory loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();

        return new TelemetrySession(
            mode,
            timeout,
            serviceProvider,
            loggerFactory,
            tracerProvider,
            meterProvider,
            loggerProvider);
    }

    /// <summary>
    /// Provider を ForceFlush、Shutdown の順に停止し、所要時間を標準出力へ記録します。
    /// Dispose だけに任せると各 Provider の終了結果を測れないため、明示的に二段階で停止します。
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Stopwatch stopwatch = Stopwatch.StartNew();
        int timeoutMilliseconds = checked((int)Math.Clamp(shutdownTimeout.TotalMilliseconds, 0, int.MaxValue));
        bool succeeded = true;

        if (tracerProvider is not null && meterProvider is not null && loggerProvider is not null)
        {
            succeeded &= tracerProvider.ForceFlush(RemainingMilliseconds(stopwatch, timeoutMilliseconds));
            succeeded &= meterProvider.ForceFlush(RemainingMilliseconds(stopwatch, timeoutMilliseconds));
            succeeded &= loggerProvider.ForceFlush(RemainingMilliseconds(stopwatch, timeoutMilliseconds));
            succeeded &= tracerProvider.Shutdown(RemainingMilliseconds(stopwatch, timeoutMilliseconds));
            succeeded &= meterProvider.Shutdown(RemainingMilliseconds(stopwatch, timeoutMilliseconds));
            succeeded &= loggerProvider.Shutdown(RemainingMilliseconds(stopwatch, timeoutMilliseconds));
        }

        serviceProvider.Dispose();
        stopwatch.Stop();
        Console.WriteLine(
            $"otel flush mode={mode.ToString().ToLowerInvariant()} elapsed_ms={stopwatch.ElapsedMilliseconds} success={succeeded.ToString().ToLowerInvariant()}");
    }

    private static void ConfigureResource(ResourceBuilder builder)
    {
        string version = typeof(TelemetrySession).Assembly.GetName().Version?.ToString()
            ?? "unknown";

        builder.AddService(ServiceName, serviceVersion: version)
            .AddAttributes(new[]
            {
                new KeyValuePair<string, object>("host.name", Environment.MachineName),
            });
    }

    private static void ConfigureExporter(OtlpExporterOptions options, Uri endpoint)
    {
        options.Endpoint = endpoint;
        options.Protocol = OtlpExportProtocol.Grpc;
    }

    private static int RemainingMilliseconds(Stopwatch stopwatch, int timeoutMilliseconds)
    {
        return Math.Max(0, timeoutMilliseconds - checked((int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue)));
    }
}
