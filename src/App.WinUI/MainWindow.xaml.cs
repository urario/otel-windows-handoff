using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OtelWindowsHandoff.Pipeline;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace OtelWindowsHandoff.WinUI;

/// <summary>
/// Core の設定入力、実行制御、進捗表示だけを行う薄い WinUI シェルです。
/// 障害注入や再試行を UI に複製しないため、Console と同じ再現条件を保てます。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly WindowArguments arguments;
    private readonly Dictionary<int, JobRowViewModel> jobsById = [];
    private readonly Stopwatch runStopwatch = new();
    private readonly DispatcherQueueTimer summaryTimer;
    private CancellationTokenSource? cancellation;
    private bool running;
    private int processed;
    private int failed;
    private int total;

    /// <summary>コマンドライン引数を解釈し、画面の初期値へ反映します。</summary>
    /// <param name="commandLineArguments">実行ファイル名を除いた起動引数。</param>
    public MainWindow(string[] commandLineArguments)
    {
        InitializeComponent();
        RootGrid.DataContext = this;
        arguments = WindowArguments.Parse(commandLineArguments);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(1180, 760));

        ProcessIdTextBlock.Text = $"PID {Environment.ProcessId}";
        summaryTimer = DispatcherQueue.CreateTimer();
        summaryTimer.Interval = TimeSpan.FromMilliseconds(200);
        summaryTimer.Tick += (_, _) => UpdateSummary();

        ApplyArguments();
        RootGrid.Loaded += RootGrid_Loaded;
    }

    /// <summary>仮想化されたジョブ一覧の表示モデルを取得します。</summary>
    public ObservableCollection<JobRowViewModel> JobRows { get; } = [];

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

    private void JobsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        JobRowViewModel? selected = JobsListView.SelectedItem as JobRowViewModel;
        DetailPanel.DataContext = selected;
        CopyTraceButton.IsEnabled = selected?.CanCopyTraceId == true;
    }

    private void CopyTraceButton_Click(object sender, RoutedEventArgs e)
    {
        if (JobsListView.SelectedItem is not JobRowViewModel selected || !selected.CanCopyTraceId)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(selected.TraceId);
        Clipboard.SetContent(package);
        SetStatus("コピー完了", "選択したジョブの trace_id をクリップボードへコピーしました。", InfoBarSeverity.Success);
    }

    private async Task RunPipelineAsync()
    {
        if (running)
        {
            return;
        }

        running = true;
        cancellation = new CancellationTokenSource();
        runStopwatch.Reset();
        ResetRunView();
        SetConfigurationEnabled(false);
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        SetStatus("実行中", "ジョブを load → transform → save の順に処理しています。", InfoBarSeverity.Informational);

        var progress = new ProgressBuffer<PipelineProgress>(DispatcherQueue, ApplyProgressBatch);
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
            runStopwatch.Restart();
            summaryTimer.Start();
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
            runStopwatch.Stop();
            summaryTimer.Stop();
            progress.Flush();
            UpdateSummary();

            InfoBarSeverity severity = result.Failed == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            SetStatus(
                "処理完了",
                $"成功 {result.Completed} 件 / 失敗 {result.Failed} 件 / リトライ {result.TotalRetries} 回",
                severity);
        }
        catch (OperationCanceledException)
        {
            progress.Flush();
            SetStatus("停止", "処理を停止しました。", InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            progress.Flush();
            SetStatus("エラー", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            runStopwatch.Stop();
            summaryTimer.Stop();
            UpdateSummary();
            cancellation.Dispose();
            cancellation = null;
            running = false;
            SetConfigurationEnabled(true);
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;

            if (arguments.ExitAfter)
            {
                Close();
            }
        }
    }

    private void ApplyProgressBatch(IReadOnlyList<PipelineProgress> updates)
    {
        foreach (PipelineProgress progress in updates)
        {
            processed = Math.Max(processed, progress.Processed);
            failed = Math.Max(failed, progress.Failed);
            total = Math.Max(total, progress.Total);

            if (!jobsById.TryGetValue(progress.JobId, out JobRowViewModel? job))
            {
                job = new JobRowViewModel(progress);
                jobsById.Add(progress.JobId, job);
                JobRows.Add(job);
            }

            if (progress.Event != PipelineProgressEvent.JobQueued)
            {
                job.Apply(progress);
            }
        }

        if (JobsListView.SelectedItem is null && JobRows.Count > 0)
        {
            JobsListView.SelectedIndex = 0;
        }

        if (JobsListView.SelectedItem is JobRowViewModel selected)
        {
            CopyTraceButton.IsEnabled = selected.CanCopyTraceId;
        }

        PipelineProgressBar.Maximum = Math.Max(1, total);
        PipelineProgressBar.Value = processed;
        UpdateSummary();
    }

    private void ResetRunView()
    {
        processed = 0;
        failed = 0;
        total = 0;
        jobsById.Clear();
        JobRows.Clear();
        DetailPanel.DataContext = null;
        CopyTraceButton.IsEnabled = false;
        PipelineProgressBar.Maximum = 1;
        PipelineProgressBar.Value = 0;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        double elapsedSeconds = runStopwatch.Elapsed.TotalSeconds;
        int retries = JobRows.Sum(job => job.RetryCount);
        double throughput = elapsedSeconds > 0 ? processed / elapsedSeconds : 0;

        ProcessedSummaryTextBlock.Text = $"{processed} / {total}";
        FailedSummaryTextBlock.Text = failed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RetrySummaryTextBlock.Text = retries.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ElapsedSummaryTextBlock.Text = $"{elapsedSeconds:0.0} s";
        ThroughputSummaryTextBlock.Text = $"{throughput:0.0} 件/秒";
    }

    private void SetStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.IsOpen = true;
    }

    private void SetConfigurationEnabled(bool enabled)
    {
        InputDirectoryTextBox.IsEnabled = enabled;
        OutputDirectoryTextBox.IsEnabled = enabled;
        OtelModeComboBox.IsEnabled = enabled;
        ParallelismNumberBox.IsEnabled = enabled;
        SlowReadCheckBox.IsEnabled = enabled;
        AccessDeniedCheckBox.IsEnabled = enabled;
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
        /// <summary>WinUI の自動実行と画面初期値に使う起動引数を解析します。</summary>
        /// <param name="args">実行ファイル名を除いたコマンドライン引数。</param>
        /// <returns>既定値を補完した起動設定。</returns>
        /// <exception cref="ArgumentException">不明な引数、値の欠落、範囲外の数値を検出しました。</exception>
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
