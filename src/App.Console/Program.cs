using Microsoft.Extensions.Logging;
using OtelWindowsHandoff.Pipeline;

namespace OtelWindowsHandoff.ConsoleApp;

internal static class Program
{
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

    public string Required(string name)
    {
        return Value(name) ?? throw new ArgumentException($"必須引数がありません: {name}");
    }

    public string? Value(string name)
    {
        return values.GetValueOrDefault(name);
    }

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
