using System.Diagnostics.Tracing;

namespace OtelEtwSpike;

[EventSource(Name = "OtelEtwSpike-Handoff")]
internal sealed class HandoffEventSource : EventSource
{
    public static readonly HandoffEventSource Log = new();

    private HandoffEventSource()
    {
    }

    [Event(1, Level = EventLevel.Informational)]
    public void JobStarted(string traceId, string spanId, int jobId)
    {
        WriteEvent(1, traceId, spanId, jobId);
    }

    [Event(2, Level = EventLevel.Informational)]
    public void JobCompleted(string traceId, int jobId)
    {
        WriteEvent(2, traceId, jobId);
    }
}
