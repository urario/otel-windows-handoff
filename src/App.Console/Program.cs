using Microsoft.Extensions.Logging;
using OtelWindowsHandoff.Pipeline;

namespace OtelWindowsHandoff.ConsoleApp;

internal static class Program
{
    /// <summary>コマンドラインを解析し、パイプライン実行またはテストデータ生成を開始します。</summary>
    /// <param name="args">実行ファイル名を除いたコマンドライン引数。</param>
    /// <returns>処理結果を表す終了コード。</returns>
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            return args[0] switch
            {
                "run" => await RunPipelineAsync(args[1..]),
                "generate-data" => await GenerateDataAsync(args[1..]),
                _ => throw new ArgumentException($"不明なコマンドです: {args[0]}"),
            };
        }
        catch (OperationCanceledException)
        {
            System.Console.Error.WriteLine("処理をキャンセルしました。");
            return 130;
        }
        catch (Exception exception)
        {
            System.Console.Error.WriteLine($"エラー: {exception.Message}");
            PrintUsage();
            return 2;
        }
    }

    private static async Task<int> RunPipelineAsync(string[] args)
    {
        CliArguments values = CliArguments.Parse(args);
        string input = values.Required("--input");
        string output = values.Required("--output");
        int parallel = values.Integer("--parallel", 2, minimum: 1);
        int flushTimeoutMilliseconds = values.Integer("--flush-timeout-ms", 5000, minimum: 0);
        OtelMode otelMode = ParseOtelMode(values.Value("--otel") ?? "otlp");
        FaultMode faultMode = ParseFaultMode(values.Value("--fault"));

        using var cancellation = new CancellationTokenSource();
        System.Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        using TelemetrySession telemetry = TelemetrySession.Create(
            otelMode,
            TimeSpan.FromMilliseconds(flushTimeoutMilliseconds));
        ILogger<PipelineRunner> logger = telemetry.LoggerFactory.CreateLogger<PipelineRunner>();
        var runner = new PipelineRunner(logger);
        var progress = new Progress<PipelineProgress>(value =>
        {
            // 詳細通知は WinUI の可視化用。Console の出力量は従来同等の開始／完了2行に保つ。
            if (value.Event is not PipelineProgressEvent.JobStarted and not PipelineProgressEvent.JobCompleted)
            {
                return;
            }

            System.Console.WriteLine(
                $"progress processed={value.Processed}/{value.Total} failed={value.Failed} current={value.CurrentJob}");
        });

        PipelineResult result = await runner.RunAsync(
            new PipelineOptions
            {
                InputDirectory = input,
                OutputDirectory = output,
                MaxDegreeOfParallelism = parallel,
                FaultMode = faultMode,
            },
            progress,
            cancellation.Token);

        System.Console.WriteLine(
            $"summary completed={result.Completed} failed={result.Failed} retries={result.TotalRetries}");
        return result.Failed == 0 ? 0 : 1;
    }

    private static async Task<int> GenerateDataAsync(string[] args)
    {
        CliArguments values = CliArguments.Parse(args);
        string directory = values.Required("--dir");
        int count = values.Integer("--count", 100, minimum: 1);
        int sizeMegabytes = values.Integer("--size-mb", 1, minimum: 1);

        await TestDataGenerator.GenerateAsync(directory, count, sizeMegabytes);
        System.Console.WriteLine($"generated count={count} size_mb={sizeMegabytes} dir={directory}");
        return 0;
    }

    internal static OtelMode ParseOtelMode(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "off" => OtelMode.Off,
            "sdk" => OtelMode.Sdk,
            "otlp" => OtelMode.Otlp,
            _ => throw new ArgumentException("--otel は off、sdk、otlp のいずれかです。"),
        };
    }

    internal static FaultMode ParseFaultMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return FaultMode.None;
        }

        return value.ToLowerInvariant() switch
        {
            "slow-read" => FaultMode.SlowRead,
            "access-denied" => FaultMode.AccessDenied,
            _ => throw new ArgumentException("--fault は slow-read または access-denied です。"),
        };
    }

    private static void PrintUsage()
    {
        System.Console.WriteLine(
            """
            使用方法:
              dotnet run --project src/App.Console -- run --input <dir> --output <dir> [--fault slow-read|access-denied] [--otel off|sdk|otlp] [--parallel N] [--flush-timeout-ms N]
              dotnet run --project src/App.Console -- generate-data --dir <dir> [--count N] [--size-mb M]

            run の終了コード: 全ジョブ成功=0、一部失敗=1、引数エラー/キャンセル=2/130
            """);
    }
}

internal sealed class CliArguments
{
    private readonly Dictionary<string, string> values;

    private CliArguments(Dictionary<string, string> values)
    {
        this.values = values;
    }

    /// <summary>オプション名と値が交互に並ぶ引数を解析します。</summary>
    /// <param name="args">コマンド名を除いた引数。</param>
    /// <returns>名前から値を参照できる解析結果。</returns>
    /// <exception cref="ArgumentException">不明な形式、値の欠落、同じオプションの重複を検出しました。</exception>
    public static CliArguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"不明な引数です: {args[index]}");
            }

            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"値がありません: {args[index]}");
            }

            if (!values.TryAdd(args[index], args[index + 1]))
            {
                throw new ArgumentException($"引数が重複しています: {args[index]}");
            }
        }

        return new CliArguments(values);
    }

    /// <summary>必須オプションの値を取得します。</summary>
    /// <param name="name">先頭の <c>--</c> を含むオプション名。</param>
    /// <returns>指定されたオプションの値。</returns>
    /// <exception cref="ArgumentException">指定されたオプションがありません。</exception>
    public string Required(string name)
    {
        return Value(name) ?? throw new ArgumentException($"必須引数がありません: {name}");
    }

    /// <summary>任意オプションの値を取得します。</summary>
    /// <param name="name">先頭の <c>--</c> を含むオプション名。</param>
    /// <returns>指定された値。オプションがない場合は <see langword="null"/>。</returns>
    public string? Value(string name)
    {
        return values.GetValueOrDefault(name);
    }

    /// <summary>整数オプションを範囲検証して取得します。</summary>
    /// <param name="name">先頭の <c>--</c> を含むオプション名。</param>
    /// <param name="defaultValue">オプションがない場合に返す値。</param>
    /// <param name="minimum">許容する最小値。</param>
    /// <returns>解析済みの整数、または <paramref name="defaultValue"/>。</returns>
    /// <exception cref="ArgumentException">値が整数ではないか、<paramref name="minimum"/> 未満です。</exception>
    public int Integer(string name, int defaultValue, int minimum)
    {
        string? value = Value(name);
        if (value is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(value, out int parsed) || parsed < minimum)
        {
            throw new ArgumentException($"{name} は {minimum} 以上の整数で指定してください。");
        }

        return parsed;
    }
}
