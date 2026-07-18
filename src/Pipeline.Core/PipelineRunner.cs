using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace OtelWindowsHandoff.Pipeline;

/// <summary>
/// 入力ファイルを load、transform、save の順に処理するジョブパイプラインです。
/// UI から分離しているため、同じ障害注入と計装を Console、WinUI、Linux テストで共有できます。
/// </summary>
public sealed class PipelineRunner
{
    private readonly ILogger<PipelineRunner> logger;

    /// <summary>パイプラインを作成します。</summary>
    /// <param name="logger">フェーズ境界、再試行、失敗を記録する Logger。</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> が <see langword="null"/> です。</exception>
    public PipelineRunner(ILogger<PipelineRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        this.logger = logger;
    }

    /// <summary>入力フォルダー直下のファイルを名前順に採番し、限定並列で処理します。</summary>
    /// <param name="options">フォルダー、並列度、障害注入などの実行設定。</param>
    /// <param name="progress">UI やコンソールへジョブ／フェーズ単位の途中経過を返す通知先。</param>
    /// <param name="cancellationToken">新規処理と待機を中止するためのトークン。</param>
    /// <returns>ジョブ ID 順に並べた全ジョブの結果。</returns>
    /// <remarks>
    /// 障害対象は実行開始順ではなく、名前順で決めたジョブ ID から選びます。
    /// スレッドのスケジュール順を使うと実行ごとに対象が変わるためです。
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> が <see langword="null"/> です。</exception>
    /// <exception cref="ArgumentException">入力フォルダーまたは出力フォルダーの指定が空です。</exception>
    /// <exception cref="ArgumentOutOfRangeException">並列度、障害対象間隔、再試行回数、変換回数のいずれかが範囲外です。</exception>
    /// <exception cref="DirectoryNotFoundException"><see cref="PipelineOptions.InputDirectory"/> が存在しません。</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> により処理が中止されました。</exception>
    public async Task<PipelineResult> RunAsync(
        PipelineOptions options,
        IProgress<PipelineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (!Directory.Exists(options.InputDirectory))
        {
            throw new DirectoryNotFoundException($"入力フォルダーがありません: {options.InputDirectory}");
        }

        Directory.CreateDirectory(options.OutputDirectory);

        Job[] jobs = Directory.EnumerateFiles(options.InputDirectory, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .Select((path, index) => CreateJob(path, index + 1, options))
            .ToArray();
        var results = new ConcurrentBag<JobResult>();
        var counters = new RunCounters();

        // UI は Core と同じ列挙結果から待機中の全ジョブを作る。シェル側でファイル列挙を複製しない。
        foreach (Job job in jobs)
        {
            ReportProgress(
                progress,
                PipelineProgressEvent.JobQueued,
                PipelineProgressState.Waiting,
                counters,
                jobs.Length,
                job);
        }

        // EventSource の有効化通知は非同期なので、実ジョブを先頭イベントにすると ETL で欠落し得る。
        // 捨ててもよい専用イベントと短い猶予を置き、WPR 未使用時に5秒待つ方式は測定を歪めるため採らない。
        HandoffEventSource.Log.Warmup();
        if (options.EventSourceWarmupDelay > TimeSpan.Zero)
        {
            await Task.Delay(options.EventSourceWarmupDelay, cancellationToken);
        }

        await Parallel.ForEachAsync(
            jobs,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                CancellationToken = cancellationToken,
            },
            async (job, token) =>
            {
                JobResult result = await ProcessJobAsync(
                    job,
                    options,
                    progress,
                    counters,
                    jobs.Length,
                    token);
                results.Add(result);

                if (!result.Succeeded)
                {
                    Interlocked.Increment(ref counters.Failed);
                }

                Interlocked.Increment(ref counters.Processed);
                ReportProgress(
                    progress,
                    PipelineProgressEvent.JobCompleted,
                    result.Succeeded ? PipelineProgressState.Succeeded : PipelineProgressState.Failed,
                    counters,
                    jobs.Length,
                    job,
                    duration: result.Duration,
                    retryCount: result.RetryCount,
                    traceId: result.TraceId,
                    spanId: result.SpanId,
                    startedAt: result.StartedAt,
                    errorMessage: result.ErrorMessage);
            });

        return new PipelineResult(results.OrderBy(result => result.JobId).ToArray());
    }

    private async Task<JobResult> ProcessJobAsync(
        Job job,
        PipelineOptions options,
        IProgress<PipelineProgress>? progress,
        RunCounters counters,
        int total,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        var saveRetryState = new SaveRetryState();
        string? errorMessage = null;
        bool succeeded = false;
        TimeSpan loadDuration = TimeSpan.Zero;
        TimeSpan transformDuration = TimeSpan.Zero;
        TimeSpan saveDuration = TimeSpan.Zero;

        using Activity? activity = PipelineInstrumentation.ActivitySource.StartActivity("ProcessJob");
        activity?.SetTag("job.id", job.Id);
        activity?.SetTag("file.name", job.FileName);
        activity?.SetTag("file.size_bytes", job.FileSize);
        ActivityTraceId traceId = activity?.TraceId ?? ActivityTraceId.CreateRandom();
        ActivitySpanId spanId = activity?.SpanId ?? ActivitySpanId.CreateRandom();
        string traceIdText = traceId.ToString();
        string spanIdText = spanId.ToString();
        DateTimeOffset startedAt = activity is null
            ? DateTimeOffset.UtcNow
            : new DateTimeOffset(activity.StartTimeUtc, TimeSpan.Zero);

        ReportProgress(
            progress,
            PipelineProgressEvent.JobStarted,
            PipelineProgressState.Running,
            counters,
            total,
            job,
            traceId: traceIdText,
            spanId: spanIdText,
            startedAt: startedAt);

        HandoffEventSource.Log.JobStarted(traceIdText, spanIdText, job.Id);
        string handoff =
            $"handoff ts={startedAt:O} pid={Environment.ProcessId} trace_id={traceIdText} job={job.Id}";
        logger.LogInformation("{Handoff}", handoff);
        Console.WriteLine(handoff);

        try
        {
            byte[] bytes = await ExecutePhaseAsync(
                PipelinePhase.Load,
                job,
                progress,
                counters,
                total,
                traceIdText,
                spanIdText,
                startedAt,
                () => LoadAsync(job, options, cancellationToken),
                duration => loadDuration = duration,
                () => saveRetryState.Count);

            await ExecutePhaseAsync(
                PipelinePhase.Transform,
                job,
                progress,
                counters,
                total,
                traceIdText,
                spanIdText,
                startedAt,
                () =>
                {
                    Transform(job, bytes, options);
                    return Task.FromResult(true);
                },
                duration => transformDuration = duration,
                () => saveRetryState.Count);

            await ExecutePhaseAsync(
                PipelinePhase.Save,
                job,
                progress,
                counters,
                total,
                traceIdText,
                spanIdText,
                startedAt,
                () => SaveAsync(
                    job,
                    bytes,
                    options,
                    activity,
                    saveRetryState,
                    (retryCount, delay, exception) => ReportProgress(
                        progress,
                        PipelineProgressEvent.RetryScheduled,
                        PipelineProgressState.Running,
                        counters,
                        total,
                        job,
                        PipelinePhase.Save,
                        delay,
                        retryCount,
                        traceIdText,
                        spanIdText,
                        startedAt,
                        exception.Message),
                    cancellationToken),
                duration => saveDuration = duration,
                () => saveRetryState.Count);

            succeeded = true;
            PipelineInstrumentation.JobsCompleted.Add(1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
            throw;
        }
        catch (Exception exception)
        {
            errorMessage = exception.Message;
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity?.AddException(exception);
            PipelineInstrumentation.JobsFailed.Add(1);
            logger.LogError(exception, "ジョブ失敗 job.id={JobId} file.name={FileName}", job.Id, job.FileName);
        }
        finally
        {
            stopwatch.Stop();
            activity?.SetTag("retry.count", saveRetryState.Count);
            PipelineInstrumentation.JobDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
            HandoffEventSource.Log.JobCompleted(traceIdText, job.Id);
        }

        return new JobResult(
            job.Id,
            job.FileName,
            job.FileSize,
            succeeded,
            saveRetryState.Count,
            stopwatch.Elapsed,
            loadDuration,
            transformDuration,
            saveDuration,
            traceIdText,
            spanIdText,
            startedAt,
            job.InjectedFault,
            errorMessage);
    }

    private async Task<T> ExecutePhaseAsync<T>(
        PipelinePhase phase,
        Job job,
        IProgress<PipelineProgress>? progress,
        RunCounters counters,
        int total,
        string traceId,
        string spanId,
        DateTimeOffset startedAt,
        Func<Task<T>> action,
        Action<TimeSpan> setDuration,
        Func<int> retryCount)
    {
        ReportProgress(
            progress,
            PipelineProgressEvent.PhaseStarted,
            PipelineProgressState.Running,
            counters,
            total,
            job,
            phase,
            retryCount: retryCount(),
            traceId: traceId,
            spanId: spanId,
            startedAt: startedAt);

        Stopwatch stopwatch = Stopwatch.StartNew();
        Exception? error = null;
        try
        {
            return await action();
        }
        catch (Exception exception)
        {
            error = exception;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            setDuration(stopwatch.Elapsed);
            ReportProgress(
                progress,
                PipelineProgressEvent.PhaseCompleted,
                error is null ? PipelineProgressState.Succeeded : PipelineProgressState.Failed,
                counters,
                total,
                job,
                phase,
                stopwatch.Elapsed,
                retryCount(),
                traceId,
                spanId,
                startedAt,
                error?.Message);
        }
    }

    private async Task<byte[]> LoadAsync(Job job, PipelineOptions options, CancellationToken cancellationToken)
    {
        using Activity? activity = PipelineInstrumentation.ActivitySource.StartActivity("load");
        logger.LogInformation("フェーズ開始 phase=load job.id={JobId} file.name={FileName}", job.Id, job.FileName);

        if (options.FaultMode.HasFlag(FaultMode.SlowRead) && IsFaultTarget(job.Id, options))
        {
            await Task.Delay(options.SlowReadDelay, cancellationToken);
        }

        byte[] bytes = await File.ReadAllBytesAsync(job.Path, cancellationToken);
        logger.LogInformation("フェーズ終了 phase=load job.id={JobId} file.name={FileName}", job.Id, job.FileName);
        return bytes;
    }

    private void Transform(Job job, byte[] bytes, PipelineOptions options)
    {
        using Activity? activity = PipelineInstrumentation.ActivitySource.StartActivity("transform");
        logger.LogInformation("フェーズ開始 phase=transform job.id={JobId} file.name={FileName}", job.Id, job.FileName);

        // 固定待機では CPU 障害解析の材料にならないため、1 MiB x 4 pass で数十 ms 程度の実負荷を作る。
        // 画像ライブラリは計装の読みやすさを損なうため、依存のないバイト演算に限定する。
        for (int pass = 0; pass < options.TransformPasses; pass++)
        {
            for (int index = 0; index < bytes.Length; index++)
            {
                bytes[index] = (byte)(((bytes[index] << 1) | (bytes[index] >> 7)) ^ (pass + index));
            }
        }

        logger.LogInformation("フェーズ終了 phase=transform job.id={JobId} file.name={FileName}", job.Id, job.FileName);
    }

    private async Task<bool> SaveAsync(
        Job job,
        byte[] bytes,
        PipelineOptions options,
        Activity? jobActivity,
        SaveRetryState retryState,
        Action<int, TimeSpan, UnauthorizedAccessException> reportRetry,
        CancellationToken cancellationToken)
    {
        using Activity? activity = PipelineInstrumentation.ActivitySource.StartActivity("save");
        logger.LogInformation("フェーズ開始 phase=save job.id={JobId} file.name={FileName}", job.Id, job.FileName);

        while (true)
        {
            try
            {
                if (options.FaultMode.HasFlag(FaultMode.AccessDenied) && IsFaultTarget(job.Id, options))
                {
                    throw new UnauthorizedAccessException("決定的な access-denied 障害を注入しました。");
                }

                string outputPath = Path.Combine(options.OutputDirectory, job.FileName);
                await File.WriteAllBytesAsync(outputPath, bytes, cancellationToken);
                activity?.SetTag("retry.count", retryState.Count);
                jobActivity?.SetTag("retry.count", retryState.Count);
                logger.LogInformation(
                    "フェーズ終了 phase=save job.id={JobId} file.name={FileName} retry.count={RetryCount}",
                    job.Id,
                    job.FileName,
                    retryState.Count);
                return true;
            }
            catch (UnauthorizedAccessException exception) when (retryState.Count < options.MaxSaveRetries)
            {
                activity?.AddException(exception);
                retryState.Count++;
                PipelineInstrumentation.RetriesTotal.Add(1);
                TimeSpan delay = TimeSpan.FromMilliseconds(
                    options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, retryState.Count - 1));
                reportRetry(retryState.Count, delay, exception);
                logger.LogWarning(
                    exception,
                    "save を再試行 job.id={JobId} file.name={FileName} retry.count={RetryCount} delay_ms={DelayMilliseconds}",
                    job.Id,
                    job.FileName,
                    retryState.Count,
                    delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception exception)
            {
                activity?.SetTag("retry.count", retryState.Count);
                activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
                activity?.AddException(exception);
                jobActivity?.SetTag("retry.count", retryState.Count);
                throw;
            }
        }
    }

    private static Job CreateJob(string path, int jobId, PipelineOptions options)
    {
        FaultMode injectedFault = IsFaultTarget(jobId, options) ? options.FaultMode : FaultMode.None;
        return new Job(jobId, path, Path.GetFileName(path), new FileInfo(path).Length, injectedFault);
    }

    private static void ReportProgress(
        IProgress<PipelineProgress>? progress,
        PipelineProgressEvent progressEvent,
        PipelineProgressState state,
        RunCounters counters,
        int total,
        Job job,
        PipelinePhase phase = PipelinePhase.None,
        TimeSpan duration = default,
        int retryCount = 0,
        string? traceId = null,
        string? spanId = null,
        DateTimeOffset? startedAt = null,
        string? errorMessage = null)
    {
        progress?.Report(new PipelineProgress(
            progressEvent,
            state,
            Volatile.Read(ref counters.Processed),
            Volatile.Read(ref counters.Failed),
            total,
            job.Id,
            job.FileName,
            job.FileSize,
            phase,
            duration,
            retryCount,
            traceId,
            spanId,
            startedAt,
            job.InjectedFault,
            errorMessage));
    }

    private static bool IsFaultTarget(int jobId, PipelineOptions options)
    {
        return jobId % options.FaultTargetEvery == 0;
    }

    private sealed record Job(int Id, string Path, string FileName, long FileSize, FaultMode InjectedFault);

    private sealed class RunCounters
    {
        public int Processed;
        public int Failed;
    }

    private sealed class SaveRetryState
    {
        /// <summary>保存処理が実際に行った再試行回数を取得または設定します。</summary>
        public int Count { get; set; }
    }
}
