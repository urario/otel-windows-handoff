using System.Collections.Concurrent;
using System.Diagnostics;
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

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), $"otel-pipeline-tests-{Guid.NewGuid():N}");
            InputDirectory = Path.Combine(Root, "in");
            OutputDirectory = Path.Combine(Root, "out");
            Directory.CreateDirectory(InputDirectory);
        }

        public string Root { get; }

        public string InputDirectory { get; }

        public string OutputDirectory { get; }

        public async Task WriteInputsAsync(int count)
        {
            byte[] data = Enumerable.Range(0, 16 * 1024).Select(index => (byte)index).ToArray();
            for (int index = 1; index <= count; index++)
            {
                await File.WriteAllBytesAsync(Path.Combine(InputDirectory, $"input-{index:D3}.bin"), data);
            }
        }

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

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
