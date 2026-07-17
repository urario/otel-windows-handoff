using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OtelWindowsHandoff.Pipeline;

namespace OtelWindowsHandoff.WinUI;

/// <summary>
/// Core の設定入力、実行制御、進捗表示だけを行う薄い WinUI シェルです。
/// 障害注入や再試行を UI に複製しないため、Console と同じ再現条件を保てます。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly WindowArguments arguments;
    private CancellationTokenSource? cancellation;
    private bool running;

    /// <summary>コマンドライン引数を解釈し、画面の初期値へ反映します。</summary>
    /// <param name="commandLineArguments">実行ファイル名を除いた起動引数。</param>
    public MainWindow(string[] commandLineArguments)
    {
        InitializeComponent();
        arguments = WindowArguments.Parse(commandLineArguments);
        ApplyArguments();
        RootGrid.Loaded += RootGrid_Loaded;
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= RootGrid_Loaded;
        if (arguments.AutoRun)
        {
            await RunPipelineAsync();
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPipelineAsync();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        cancellation?.Cancel();
    }

    private void FreezeButton_Click(object sender, RoutedEventArgs e)
    {
        Thread.Sleep(TimeSpan.FromSeconds(30));
    }

    private async Task RunPipelineAsync()
    {
        if (running)
        {
            return;
        }

        running = true;
        cancellation = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StatusTextBlock.Text = "実行中";

        try
        {
            OtelMode otelMode = ParseOtelMode(SelectedOtelMode());
            FaultMode faultMode = FaultMode.None;
            if (SlowReadCheckBox.IsChecked == true)
            {
                faultMode |= FaultMode.SlowRead;
            }

            if (AccessDeniedCheckBox.IsChecked == true)
            {
                faultMode |= FaultMode.AccessDenied;
            }

            using TelemetrySession telemetry = TelemetrySession.Create(
                otelMode,
                TimeSpan.FromMilliseconds(arguments.FlushTimeoutMilliseconds));
            ILogger<PipelineRunner> logger = telemetry.LoggerFactory.CreateLogger<PipelineRunner>();
            var runner = new PipelineRunner(logger);
            var progress = new Progress<PipelineProgress>(UpdateProgress);
            PipelineResult result = await runner.RunAsync(
                new PipelineOptions
                {
                    InputDirectory = InputDirectoryTextBox.Text,
                    OutputDirectory = OutputDirectoryTextBox.Text,
                    FaultMode = faultMode,
                    MaxDegreeOfParallelism = checked((int)ParallelismNumberBox.Value),
                },
                progress,
                cancellation.Token);

            StatusTextBlock.Text = $"完了: 成功 {result.Completed} / 失敗 {result.Failed}";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "停止しました";
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = $"エラー: {exception.Message}";
        }
        finally
        {
            cancellation.Dispose();
            cancellation = null;
            running = false;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;

            if (arguments.ExitAfter)
            {
                Close();
            }
        }
    }

    private void UpdateProgress(PipelineProgress progress)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ProgressBar.Maximum = Math.Max(1, progress.Total);
            ProgressBar.Value = progress.Processed;
            ProgressTextBlock.Text = $"処理済み {progress.Processed} / 失敗 {progress.Failed}";
            CurrentJobTextBlock.Text = $"現在のジョブ: {progress.CurrentJob}";
        });
    }

    private void ApplyArguments()
    {
        InputDirectoryTextBox.Text = arguments.InputDirectory;
        OutputDirectoryTextBox.Text = arguments.OutputDirectory;
        ParallelismNumberBox.Value = arguments.Parallelism;
        SlowReadCheckBox.IsChecked = arguments.FaultMode.HasFlag(FaultMode.SlowRead);
        AccessDeniedCheckBox.IsChecked = arguments.FaultMode.HasFlag(FaultMode.AccessDenied);
        OtelModeComboBox.SelectedIndex = arguments.OtelMode switch
        {
            OtelMode.Off => 0,
            OtelMode.Sdk => 1,
            _ => 2,
        };
    }

    private string SelectedOtelMode()
    {
        return ((ComboBoxItem)OtelModeComboBox.SelectedItem).Content.ToString() ?? "otlp";
    }

    private static OtelMode ParseOtelMode(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "off" => OtelMode.Off,
            "sdk" => OtelMode.Sdk,
            "otlp" => OtelMode.Otlp,
            _ => throw new ArgumentException("--otel は off、sdk、otlp のいずれかです。"),
        };
    }

    private sealed record WindowArguments(
        bool AutoRun,
        bool ExitAfter,
        string InputDirectory,
        string OutputDirectory,
        FaultMode FaultMode,
        OtelMode OtelMode,
        int Parallelism,
        int FlushTimeoutMilliseconds)
    {
        public static WindowArguments Parse(string[] args)
        {
            bool autoRun = false;
            bool exitAfter = false;
            string input = "./data/in";
            string output = "./data/out";
            FaultMode fault = FaultMode.None;
            OtelMode otel = OtelMode.Otlp;
            int parallelism = 2;
            int flushTimeoutMilliseconds = 5000;

            for (int index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--auto-run":
                        autoRun = true;
                        break;
                    case "--exit-after":
                        exitAfter = true;
                        break;
                    case "--input":
                        input = NextValue(args, ref index, "--input");
                        break;
                    case "--output":
                        output = NextValue(args, ref index, "--output");
                        break;
                    case "--fault":
                        fault |= NextValue(args, ref index, "--fault") switch
                        {
                            "slow-read" => FaultMode.SlowRead,
                            "access-denied" => FaultMode.AccessDenied,
                            _ => throw new ArgumentException("--fault は slow-read または access-denied です。"),
                        };
                        break;
                    case "--otel":
                        otel = ParseOtelMode(NextValue(args, ref index, "--otel"));
                        break;
                    case "--parallel":
                        parallelism = PositiveInteger(NextValue(args, ref index, "--parallel"), "--parallel");
                        break;
                    case "--flush-timeout-ms":
                        flushTimeoutMilliseconds = NonNegativeInteger(
                            NextValue(args, ref index, "--flush-timeout-ms"),
                            "--flush-timeout-ms");
                        break;
                    default:
                        throw new ArgumentException($"不明な引数です: {args[index]}");
                }
            }

            return new WindowArguments(
                autoRun,
                exitAfter,
                input,
                output,
                fault,
                otel,
                parallelism,
                flushTimeoutMilliseconds);
        }

        private static string NextValue(string[] args, ref int index, string name)
        {
            if (++index >= args.Length)
            {
                throw new ArgumentException($"値がありません: {name}");
            }

            return args[index];
        }

        private static int PositiveInteger(string value, string name)
        {
            if (!int.TryParse(value, out int parsed) || parsed < 1)
            {
                throw new ArgumentException($"{name} は1以上の整数で指定してください。");
            }

            return parsed;
        }

        private static int NonNegativeInteger(string value, string name)
        {
            if (!int.TryParse(value, out int parsed) || parsed < 0)
            {
                throw new ArgumentException($"{name} は0以上の整数で指定してください。");
            }

            return parsed;
        }
    }
}
