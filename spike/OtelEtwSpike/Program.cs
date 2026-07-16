using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace OtelEtwSpike;

internal static class Program
{
    private const string ActivitySourceName = "OtelEtwSpike";
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public static async Task Main()
    {
        TracerProvider tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(ActivitySourceName)
            .AddConsoleExporter()
            .Build();

        try
        {
            for (int jobId = 1; jobId <= 5; jobId++)
            {
                await RunJobAsync(jobId);
            }
        }
        finally
        {
            tracerProvider.Dispose();
            ActivitySource.Dispose();
            HandoffEventSource.Log.Dispose();
        }
    }

    private static async Task RunJobAsync(int jobId)
    {
        using Activity activity = ActivitySource.StartActivity("job")
            ?? throw new InvalidOperationException("Activity の開始に失敗しました。");

        activity.SetTag("job.id", jobId);

        Activity current = Activity.Current
            ?? throw new InvalidOperationException("現在の Activity を取得できませんでした。");
        string traceId = current.TraceId.ToString();
        string spanId = current.SpanId.ToString();

        HandoffEventSource.Log.JobStarted(traceId, spanId, jobId);
        Console.WriteLine(
            $"handoff ts={DateTimeOffset.UtcNow:O} pid={Environment.ProcessId} trace_id={traceId} job={jobId}");

        await Task.Delay(TimeSpan.FromMilliseconds(100 + ((jobId - 1) * 50)));

        HandoffEventSource.Log.JobCompleted(Activity.Current.TraceId.ToString(), jobId);
    }
}
