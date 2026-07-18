using System.ComponentModel;
using System.Runtime.CompilerServices;
using OtelWindowsHandoff.Pipeline;

namespace OtelWindowsHandoff.WinUI;

/// <summary>Core の進捗イベントを一行分の表示状態へ写像します。</summary>
public sealed class JobRowViewModel : INotifyPropertyChanged
{
    private PipelineProgressState jobState = PipelineProgressState.Waiting;
    private PipelineProgressState loadState = PipelineProgressState.Waiting;
    private PipelineProgressState transformState = PipelineProgressState.Waiting;
    private PipelineProgressState saveState = PipelineProgressState.Waiting;
    private TimeSpan duration;
    private TimeSpan loadDuration;
    private TimeSpan transformDuration;
    private TimeSpan saveDuration;
    private int retryCount;
    private string? traceId;
    private string? spanId;
    private DateTimeOffset? startedAt;
    private string? errorMessage;

    /// <summary>待機ジョブの通知から表示行を作成します。</summary>
    /// <param name="progress">Core が通知した待機ジョブ。</param>
    public JobRowViewModel(PipelineProgress progress)
    {
        JobId = progress.JobId;
        FileName = progress.FileName;
        FileSize = progress.FileSize;
        InjectedFault = progress.InjectedFault;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>ジョブ ID を取得します。</summary>
    public int JobId { get; }

    /// <summary>ファイル名を取得します。</summary>
    public string FileName { get; }

    /// <summary>入力ファイルのバイト数を取得します。</summary>
    public long FileSize { get; }

    /// <summary>Core がこのジョブへ適用した決定的な障害を取得します。</summary>
    public FaultMode InjectedFault { get; }

    /// <summary>ジョブ全体の状態を取得します。</summary>
    public PipelineProgressState JobState => jobState;

    /// <summary>load フェーズの状態を取得します。</summary>
    public PipelineProgressState LoadState => loadState;

    /// <summary>transform フェーズの状態を取得します。</summary>
    public PipelineProgressState TransformState => transformState;

    /// <summary>save フェーズの状態を取得します。</summary>
    public PipelineProgressState SaveState => saveState;

    /// <summary>ファイルサイズの表示文字列を取得します。</summary>
    public string FileSizeText => FormatBytes(FileSize);

    /// <summary>ジョブ所要時間の表示文字列を取得します。</summary>
    public string DurationText => FormatDuration(duration);

    /// <summary>load フェーズ状態の表示文字列を取得します。</summary>
    public string LoadText => FormatPhase("load", loadState, loadDuration);

    /// <summary>transform フェーズ状態の表示文字列を取得します。</summary>
    public string TransformText => FormatPhase("transform", transformState, transformDuration);

    /// <summary>save フェーズ状態の表示文字列を取得します。</summary>
    public string SaveText => FormatPhase("save", saveState, saveDuration);

    /// <summary>再試行回数の表示文字列を取得します。</summary>
    public string RetryText => retryCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>結果と障害種別の表示文字列を取得します。</summary>
    public string ResultText
    {
        get
        {
            string result = jobState switch
            {
                PipelineProgressState.Running => "実行中",
                PipelineProgressState.Succeeded => "成功",
                PipelineProgressState.Failed => "失敗",
                _ => "待機",
            };
            string fault = FormatFault(InjectedFault);
            return string.IsNullOrEmpty(fault) ? result : $"{result}\n{fault}";
        }
    }

    /// <summary>ProcessJob Span の trace_id を取得します。</summary>
    public string TraceId => traceId ?? "-";

    /// <summary>ProcessJob Span の span_id を取得します。</summary>
    public string SpanId => spanId ?? "-";

    /// <summary>ジョブ開始時刻の表示文字列を取得します。</summary>
    public string StartedAtText => startedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz") ?? "-";

    /// <summary>load フェーズ所要時間の表示文字列を取得します。</summary>
    public string LoadDurationText => FormatDuration(loadDuration);

    /// <summary>transform フェーズ所要時間の表示文字列を取得します。</summary>
    public string TransformDurationText => FormatDuration(transformDuration);

    /// <summary>save フェーズ所要時間の表示文字列を取得します。</summary>
    public string SaveDurationText => FormatDuration(saveDuration);

    /// <summary>エラーメッセージを取得します。</summary>
    public string ErrorMessage => errorMessage ?? "-";

    /// <summary>実際の trace_id が通知済みかどうかを取得します。</summary>
    public bool CanCopyTraceId => !string.IsNullOrEmpty(traceId);

    /// <summary>通知済みの save 再試行回数を取得します。</summary>
    public int RetryCount => retryCount;

    /// <summary>Core の進捗イベントを現在の表示状態へ適用します。</summary>
    /// <param name="progress">同じジョブ ID の進捗イベント。</param>
    public void Apply(PipelineProgress progress)
    {
        traceId = progress.TraceId ?? traceId;
        spanId = progress.SpanId ?? spanId;
        startedAt = progress.StartedAt ?? startedAt;
        retryCount = Math.Max(retryCount, progress.RetryCount);
        errorMessage = progress.ErrorMessage ?? errorMessage;

        switch (progress.Event)
        {
            case PipelineProgressEvent.JobStarted:
                jobState = PipelineProgressState.Running;
                break;
            case PipelineProgressEvent.PhaseStarted:
            case PipelineProgressEvent.PhaseCompleted:
                SetPhase(progress.Phase, progress.State, progress.Duration);
                break;
            case PipelineProgressEvent.RetryScheduled:
                saveState = PipelineProgressState.Running;
                break;
            case PipelineProgressEvent.JobCompleted:
                jobState = progress.State;
                duration = progress.Duration;
                break;
        }

        NotifyAll();
    }

    private static string FormatBytes(long bytes)
    {
        const double kibibyte = 1024;
        const double mebibyte = 1024 * 1024;
        return bytes >= mebibyte
            ? $"{bytes / mebibyte:0.0} MiB"
            : bytes >= kibibyte
                ? $"{bytes / kibibyte:0.0} KiB"
                : $"{bytes} B";
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
        {
            return "-";
        }

        return value >= TimeSpan.FromSeconds(1)
            ? $"{value.TotalSeconds:0.00} s"
            : $"{value.TotalMilliseconds:0} ms";
    }

    private static string FormatPhase(string name, PipelineProgressState state, TimeSpan duration)
    {
        return state switch
        {
            PipelineProgressState.Running => $"{name}  実行中",
            PipelineProgressState.Succeeded => $"{name}  {FormatDuration(duration)}",
            PipelineProgressState.Failed => $"{name}  失敗",
            _ => $"{name}  待機",
        };
    }

    private static string FormatFault(FaultMode fault)
    {
        if (fault == FaultMode.None)
        {
            return string.Empty;
        }

        var names = new List<string>(2);
        if (fault.HasFlag(FaultMode.SlowRead))
        {
            names.Add("slow-read");
        }

        if (fault.HasFlag(FaultMode.AccessDenied))
        {
            names.Add("access-denied");
        }

        return string.Join(" + ", names);
    }

    private void SetPhase(PipelinePhase phase, PipelineProgressState state, TimeSpan phaseDuration)
    {
        switch (phase)
        {
            case PipelinePhase.Load:
                loadState = state;
                loadDuration = phaseDuration;
                break;
            case PipelinePhase.Transform:
                transformState = state;
                transformDuration = phaseDuration;
                break;
            case PipelinePhase.Save:
                saveState = state;
                saveDuration = phaseDuration;
                break;
        }
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(JobState));
        OnPropertyChanged(nameof(LoadState));
        OnPropertyChanged(nameof(TransformState));
        OnPropertyChanged(nameof(SaveState));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(LoadText));
        OnPropertyChanged(nameof(TransformText));
        OnPropertyChanged(nameof(SaveText));
        OnPropertyChanged(nameof(RetryText));
        OnPropertyChanged(nameof(ResultText));
        OnPropertyChanged(nameof(TraceId));
        OnPropertyChanged(nameof(SpanId));
        OnPropertyChanged(nameof(StartedAtText));
        OnPropertyChanged(nameof(LoadDurationText));
        OnPropertyChanged(nameof(TransformDurationText));
        OnPropertyChanged(nameof(SaveDurationText));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(CanCopyTraceId));
        OnPropertyChanged(nameof(RetryCount));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
