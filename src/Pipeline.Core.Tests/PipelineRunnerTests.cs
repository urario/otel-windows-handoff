using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using Microsoft.Extensions.Logging.Abstractions;
using OtelWindowsHandoff.Pipeline;
using Xunit;

namespace Pipeline.Core.Tests;

/// <summary>
/// パイプラインの完了数、決定的障害、再試行、Activity 契約を検証します。
/// 実 ACL や Windows API を使わず、Linux CI でも同じ条件を再現します。
/// </summary>
public sealed class PipelineRunnerTests
{
    /// <summary>正常系で列挙した全ジョブが完了し、出力数も一致することを検証します。</summary>
    /// <returns>非同期テストの完了を表すタスク。</returns>
    [Fact]
    public async Task NormalRunCompletesEveryJob()
    {
        using var workspace = new TestWorkspace();
        await workspace.WriteInputsAsync(5);
        var runner = new PipelineRunner(NullLogger<PipelineRunner>.Instance);

        PipelineResult result = await runner.RunAsync(workspace.CreateOptions());

        Assert.Equal(5, result.Completed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(5, Directory.GetFiles(workspace.OutputDirectory).Length);
    }

    /// <summary>slow-read が名前順で決めた対象ジョブだけを遅延させることを検証します。</summary>
    /// <returns>非同期テストの完了を表すタスク。</returns>
    [Fact]
    public async Task SlowReadOnlyDelaysDeterministicTargets()
    {
        using var workspace = new TestWorkspace();
        await workspace.WriteInputsAsync(3);
        var runner = new PipelineRunner(NullLogger<PipelineRunner>.Instance);
        PipelineOptions options = workspace.CreateOptions() with
        {
            MaxDegreeOfParallelism = 1,
            FaultMode = FaultMode.SlowRead,
            FaultTargetEvery = 2,
            SlowReadDelay = TimeSpan.FromMilliseconds(150),
        };

        PipelineResult result = await runner.RunAsync(options);

        JobResult delayed = Assert.Single(result.Jobs, job => job.JobId == 2);
        Assert.True(delayed.Duration >= TimeSpan.FromMilliseconds(125), $"実測: {delayed.Duration}");
        Assert.All(
            result.Jobs.Where(job => job.JobId != 2),
            job => Assert.True(job.Duration < delayed.Duration, $"job {job.JobId}: {job.Duration}"));
    }

    /// <summary>access-denied が規定回数再試行し、対象ジョブだけを失敗にすることを検証します。</summary>
    /// <returns>非同期テストの完了を表すタスク。</returns>
    [Fact]
    public async Task AccessDeniedRetriesAndFailsOnlyDeterministicTargets()
    {
        using var workspace = new TestWorkspace();
        await workspace.WriteInputsAsync(2);
        var runner = new PipelineRunner(NullLogger<PipelineRunner>.Instance);
        PipelineOptions options = workspace.CreateOptions() with
        {
            FaultMode = FaultMode.AccessDenied,
            FaultTargetEvery = 2,
            MaxSaveRetries = 3,
            InitialRetryDelay = TimeSpan.FromMilliseconds(1),
        };

        PipelineResult result = await runner.RunAsync(options);

        Assert.Equal(1, result.Completed);
        Assert.Equal(1, result.Failed);
        JobResult failed = Assert.Single(result.Jobs, job => !job.Succeeded);
        Assert.Equal(2, failed.JobId);
        Assert.Equal(3, failed.RetryCount);
        Assert.Contains("access-denied", failed.ErrorMessage, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(workspace.OutputDirectory, "input-001.bin")));
        Assert.False(File.Exists(Path.Combine(workspace.OutputDirectory, "input-002.bin")));
    }

    /// <summary>障害注入以外の保存時アクセス拒否でも、実際の再試行回数を結果と Span に残すことを検証します。</summary>
    /// <returns>非同期テストの完了を表すタスク。</returns>
    [Fact]
    public async Task RealSaveAccessDeniedReportsActualRetryCount()
    {
        using var workspace = new TestWorkspace();
        await workspace.WriteInputsAsync(1);

        // ACL は OS ごとに挙動が異なるため、出力ファイルと同名のディレクトリで移植可能なアクセス拒否を起こします。
        Directory.CreateDirectory(Path.Combine(workspace.OutputDirectory, "input-001.bin"));
        var stopped = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PipelineInstrumentation.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);
        var runner = new PipelineRunner(NullLogger<PipelineRunner>.Instance);
        PipelineOptions options = workspace.CreateOptions() with
        {
            FaultMode = FaultMode.None,
            MaxSaveRetries = 2,
            InitialRetryDelay = TimeSpan.FromMilliseconds(1),
        };

        PipelineResult result = await runner.RunAsync(options);

        JobResult failed = Assert.Single(result.Jobs);
        Assert.False(failed.Succeeded);
        Assert.Equal(2, failed.RetryCount);
        Activity root = Assert.Single(stopped, activity => activity.DisplayName == "ProcessJob");
        Assert.Equal(2, root.GetTagItem("retry.count"));
    }

    /// <summary>ジョブ Span と三つのフェーズ Span の親子関係、安全なタグ値を検証します。</summary>
    /// <returns>非同期テストの完了を表すタスク。</returns>
    [Fact]
    public async Task ActivitiesHaveExpectedHierarchyAndSafeTags()
    {
        using var workspace = new TestWorkspace();
        await workspace.WriteInputsAsync(1);
        var stopped = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PipelineInstrumentation.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);
        var runner = new PipelineRunner(NullLogger<PipelineRunner>.Instance);

        PipelineResult result = await runner.RunAsync(workspace.CreateOptions());

        Assert.Equal(1, result.Completed);
        Activity root = Assert.Single(stopped, activity => activity.DisplayName == "ProcessJob");
        Assert.Equal(default, root.ParentSpanId);
        Assert.Equal(1, root.GetTagItem("job.id"));
        Assert.Equal("input-001.bin", root.GetTagItem("file.name"));
        Assert.DoesNotContain(workspace.Root, root.GetTagItem("file.name")?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(16 * 1024L, root.GetTagItem("file.size_bytes"));
        Assert.Equal(0, root.GetTagItem("retry.count"));

        Activity[] phases = stopped.Where(activity => activity.DisplayName is "load" or "transform" or "save").ToArray();
        Assert.Equal(3, phases.Length);
        Assert.All(phases, phase =>
        {
            Assert.Equal(root.TraceId, phase.TraceId);
            Assert.Equal(root.SpanId, phase.ParentSpanId);
        });
    }

    /// <summary>UI フリーズの Span、ETW、Dump、handoff 行が同じ相関情報を共有することを検証します。</summary>
    [Fact]
    public void UIFreezeHandoffCompletesSpanBeforeReturningAndSharesIdentifiers()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PipelineInstrumentation.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(activityListener);
        using var eventListener = new HandoffRecordingEventListener();
        int uiThreadId = Environment.CurrentManagedThreadId;

        UIFreezeHandoff handoff = PipelineInstrumentation.RecordUIFreezeRequested(
            NullLogger.Instance,
            uiThreadId);

        Activity activity = Assert.Single(stopped);
        Assert.Equal(PipelineInstrumentation.UIFreezeActivityName, activity.DisplayName);
        Assert.Equal(handoff.TraceId, activity.TraceId.ToString());
        Assert.Equal(handoff.SpanId, activity.SpanId.ToString());
        Assert.Equal(PipelineInstrumentation.UIFreezeOperationName, activity.GetTagItem("operation.name"));
        Assert.Equal(uiThreadId, activity.GetTagItem("ui.thread.id"));
        Assert.Equal(Environment.ProcessId, activity.GetTagItem("process.id"));

        object?[] payload = Assert.Single(eventListener.Events);
        Assert.Equal(handoff.TraceId, payload[0]);
        Assert.Equal(handoff.SpanId, payload[1]);
        Assert.Equal(Environment.ProcessId, payload[2]);
        Assert.Equal(uiThreadId, payload[3]);
        Assert.Equal(PipelineInstrumentation.UIFreezeOperationName, payload[4]);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.Equal(
            $"{HandoffDumpMarker.SearchKey} freeze trace_id={handoff.TraceId} span_id={handoff.SpanId} " +
            $"pid={handoff.ProcessId} ui_thread={handoff.UIThreadId} ts={handoff.Timestamp:O}",
            HandoffDumpMarker.Current.Value);
        Assert.Equal(
            $"handoff ts={handoff.Timestamp:O} pid={handoff.ProcessId} trace_id={handoff.TraceId} " +
            $"span_id={handoff.SpanId} ui_thread={handoff.UIThreadId} operation={handoff.OperationName}",
            handoff.HandoffLine);
    }

    /// <summary>進捗通知が待機から失敗までの全状態と、実際の ProcessJob Span の ID を共有することを検証します。</summary>
    /// <returns>非同期テストの完了を表すタスク。</returns>
    [Fact]
    public async Task ProgressReportsJobAndPhaseLifecycleWithTelemetryIdentifiers()
    {
        using var workspace = new TestWorkspace();
        await workspace.WriteInputsAsync(1);
        var stopped = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PipelineInstrumentation.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);
        var reports = new ConcurrentQueue<PipelineProgress>();
        var runner = new PipelineRunner(NullLogger<PipelineRunner>.Instance);
        PipelineOptions options = workspace.CreateOptions() with
        {
            FaultMode = FaultMode.AccessDenied,
            FaultTargetEvery = 1,
            MaxSaveRetries = 2,
            InitialRetryDelay = TimeSpan.FromMilliseconds(1),
        };

        PipelineResult result = await runner.RunAsync(options, new RecordingProgress<PipelineProgress>(reports.Enqueue));

        PipelineProgress[] events = reports.ToArray();
        PipelineProgress queued = Assert.Single(events, value => value.Event == PipelineProgressEvent.JobQueued);
        Assert.Equal(PipelineProgressState.Waiting, queued.State);
        Assert.Equal("input-001.bin", queued.FileName);
        Assert.Equal(16 * 1024L, queued.FileSize);
        Assert.Equal(FaultMode.AccessDenied, queued.InjectedFault);
        Assert.Null(queued.TraceId);

        PipelineProgress started = Assert.Single(events, value => value.Event == PipelineProgressEvent.JobStarted);
        Activity root = Assert.Single(stopped, activity => activity.DisplayName == "ProcessJob");
        Assert.Equal(root.TraceId.ToString(), started.TraceId);
        Assert.Equal(root.SpanId.ToString(), started.SpanId);
        Assert.NotNull(started.StartedAt);

        AssertPhaseSucceeded(events, PipelinePhase.Load);
        AssertPhaseSucceeded(events, PipelinePhase.Transform);
        PipelineProgress save = Assert.Single(
            events,
            value => value.Event == PipelineProgressEvent.PhaseCompleted && value.Phase == PipelinePhase.Save);
        Assert.Equal(PipelineProgressState.Failed, save.State);
        Assert.Equal(2, save.RetryCount);
        Assert.Contains("access-denied", save.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(2, events.Count(value => value.Event == PipelineProgressEvent.RetryScheduled));

        PipelineProgress completed = Assert.Single(events, value => value.Event == PipelineProgressEvent.JobCompleted);
        Assert.Equal(PipelineProgressState.Failed, completed.State);
        Assert.Equal(1, completed.Processed);
        Assert.Equal(1, completed.Failed);
        Assert.Equal(started.TraceId, completed.TraceId);
        Assert.Equal(started.SpanId, completed.SpanId);
        Assert.Equal(started.StartedAt, completed.StartedAt);
        Assert.Equal(result.Jobs[0].TraceId, completed.TraceId);
    }

    private static void AssertPhaseSucceeded(IEnumerable<PipelineProgress> events, PipelinePhase phase)
    {
        PipelineProgress started = Assert.Single(
            events,
            value => value.Event == PipelineProgressEvent.PhaseStarted && value.Phase == phase);
        PipelineProgress completed = Assert.Single(
            events,
            value => value.Event == PipelineProgressEvent.PhaseCompleted && value.Phase == phase);
        Assert.Equal(PipelineProgressState.Running, started.State);
        Assert.Equal(PipelineProgressState.Succeeded, completed.State);
        Assert.True(completed.Duration >= TimeSpan.Zero);
        Assert.Equal(started.TraceId, completed.TraceId);
    }

    private sealed class TestWorkspace : IDisposable
    {
        /// <summary>テストごとに独立した一時入力フォルダーを作成します。</summary>
        public TestWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), $"otel-pipeline-tests-{Guid.NewGuid():N}");
            InputDirectory = Path.Combine(Root, "in");
            OutputDirectory = Path.Combine(Root, "out");
            Directory.CreateDirectory(InputDirectory);
        }

        /// <summary>このテストだけが使用する一時フォルダーを取得します。</summary>
        public string Root { get; }

        /// <summary>テスト入力ファイルを配置するフォルダーを取得します。</summary>
        public string InputDirectory { get; }

        /// <summary>パイプラインの出力先フォルダーを取得します。</summary>
        public string OutputDirectory { get; }

        /// <summary>ファイル名順を検証できる小さな入力ファイルを生成します。</summary>
        /// <param name="count">生成するファイル数。</param>
        /// <returns>全入力ファイルの書き込み完了を表すタスク。</returns>
        public async Task WriteInputsAsync(int count)
        {
            byte[] data = Enumerable.Range(0, 16 * 1024).Select(index => (byte)index).ToArray();
            for (int index = 1; index <= count; index++)
            {
                await File.WriteAllBytesAsync(Path.Combine(InputDirectory, $"input-{index:D3}.bin"), data);
            }
        }

        /// <summary>長い待機と不要な CPU 負荷を除いたテスト用設定を作成します。</summary>
        /// <returns>このワークスペースを入出力先にした設定。</returns>
        public PipelineOptions CreateOptions()
        {
            return new PipelineOptions
            {
                InputDirectory = InputDirectory,
                OutputDirectory = OutputDirectory,
                TransformPasses = 1,
                EventSourceWarmupDelay = TimeSpan.Zero,
            };
        }

        /// <summary>テストで生成した一時ファイルを再帰的に削除します。</summary>
        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class RecordingProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            report(value);
        }
    }

    private sealed class HandoffRecordingEventListener : EventListener
    {
        public ConcurrentQueue<object?[]> Events { get; } = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "OtelWindowsHandoff-Handoff")
            {
                EnableEvents(eventSource, EventLevel.Informational);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventId == 4)
            {
                Events.Enqueue(eventData.Payload?.ToArray() ?? []);
            }
        }
    }
}
