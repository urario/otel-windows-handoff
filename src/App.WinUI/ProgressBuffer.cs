using System.Collections.Concurrent;
using Microsoft.UI.Dispatching;

namespace OtelWindowsHandoff.WinUI;

/// <summary>並列処理の細かな通知を一回の UI ディスパッチへまとめます。</summary>
/// <typeparam name="T">通知値の型。</typeparam>
internal sealed class ProgressBuffer<T> : IProgress<T>
{
    private readonly ConcurrentQueue<T> queue = new();
    private readonly DispatcherQueue dispatcherQueue;
    private readonly Action<IReadOnlyList<T>> applyBatch;
    private int dispatchScheduled;

    /// <summary>UI ディスパッチャーと一括適用処理を指定して作成します。</summary>
    public ProgressBuffer(DispatcherQueue dispatcherQueue, Action<IReadOnlyList<T>> applyBatch)
    {
        this.dispatcherQueue = dispatcherQueue;
        this.applyBatch = applyBatch;
    }

    /// <inheritdoc />
    public void Report(T value)
    {
        queue.Enqueue(value);
        ScheduleDrain();
    }

    /// <summary>呼び出し時点までの通知を直ちに適用します。UI スレッドから呼び出します。</summary>
    public void Flush()
    {
        Drain();
    }

    private void ScheduleDrain()
    {
        if (Interlocked.Exchange(ref dispatchScheduled, 1) != 0)
        {
            return;
        }

        if (!dispatcherQueue.TryEnqueue(Drain))
        {
            Interlocked.Exchange(ref dispatchScheduled, 0);
        }
    }

    private void Drain()
    {
        var values = new List<T>();
        while (queue.TryDequeue(out T? value))
        {
            values.Add(value);
        }

        Interlocked.Exchange(ref dispatchScheduled, 0);
        if (values.Count > 0)
        {
            applyBatch(values);
        }

        if (!queue.IsEmpty)
        {
            ScheduleDrain();
        }
    }
}
