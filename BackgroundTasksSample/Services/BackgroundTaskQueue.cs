using Disruptor.Dsl;
using Disruptor;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace BackgroundTasksSample.Services;

/// <summary>
/// バックグラウンドタスクキューのインターフェース
/// </summary>
public interface IBackgroundTaskQueue
{
    ValueTask QueueBackgroundWorkItemAsync(Func<CancellationToken, ValueTask> workItem);

    ValueTask<Func<CancellationToken, ValueTask>> DequeueAsync(
        CancellationToken cancellationToken);
}

/// <summary>
/// **🚀 推奨！** `Channel<T>` を使用したバックグラウンドタスクキュー
/// - `BoundedChannel` によりバックプレッシャー制御が可能
/// - `UnboundedChannel` にすれば無制限キューも OK
/// - `async/await` に完全対応
/// </summary>
public class ChannelBasedTaskQueue : IBackgroundTaskQueue
{
    private readonly Channel<Func<CancellationToken, ValueTask>> _queue;

    public ChannelBasedTaskQueue(int capacity)
    {
        // Capacity should be set based on the expected application load and
        // number of concurrent threads accessing the queue.
        // BoundedChannelFullMode.Wait will cause calls to WriteAsync() to return a task,
        // which completes only when space became available. This leads to backpressure,
        // in case too many publishers/calls start accumulating.
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait // キューが満杯なら待機
        };
        _queue = Channel.CreateBounded<Func<CancellationToken, ValueTask>>(options);

        //// 無制限のキューを作成
        //_queue = Channel.CreateUnbounded<Func<CancellationToken, ValueTask>>();
    }

    public async ValueTask QueueBackgroundWorkItemAsync(
        Func<CancellationToken, ValueTask> workItem)
    {
        if (workItem == null)
        {
            throw new ArgumentNullException(nameof(workItem));
        }

        await _queue.Writer.WriteAsync(workItem);
    }

    public async ValueTask<Func<CancellationToken, ValueTask>> DequeueAsync(
        CancellationToken cancellationToken)
    {
        var workItem = await _queue.Reader.ReadAsync(cancellationToken);

        return workItem;
    }

    public Task StopAsync()
    {
        _queue.Writer.Complete();
        return Task.CompletedTask;
    }
}

/// <summary>
/// `ConcurrentQueue<T>` + `SemaphoreSlim` を使用したバックグラウンドタスクキュー
/// - 非同期 (`async/await`) に対応
/// - **キューのサイズ制限はできない**
/// </summary>
public class ConcurrentQueueTaskQueue : IBackgroundTaskQueue, IDisposable
{
    private readonly ConcurrentQueue<Func<CancellationToken, ValueTask>> _queue;
    private readonly SemaphoreSlim _signal;

    public ConcurrentQueueTaskQueue()
    {
        _queue = new ConcurrentQueue<Func<CancellationToken, ValueTask>>();
        _signal = new SemaphoreSlim(0);
    }

    public ValueTask QueueBackgroundWorkItemAsync(Func<CancellationToken, ValueTask> workItem)
    {
        if (workItem == null) throw new ArgumentNullException(nameof(workItem));

        _queue.Enqueue(workItem);
        _signal.Release(); // 待機しているスレッドを解放
        return ValueTask.CompletedTask;
    }

    public async ValueTask<Func<CancellationToken, ValueTask>> DequeueAsync(CancellationToken cancellationToken)
    {
        await _signal.WaitAsync(cancellationToken); // キューにデータが入るまで待機

        if (_queue.TryDequeue(out var workItem))
        {
            return workItem;
        }

        throw new InvalidOperationException("Failed to dequeue an item.");
    }

    public void Dispose()
    {
        _signal.Dispose();
    }
}

/// <summary>
/// `BlockingCollection<T>` を使用したバックグラウンドタスクキュー
/// - **同期処理用**
/// - **`async/await` 非対応** (`Take()` はブロッキング)
/// - **キューのサイズ制限が可能**
/// </summary>
public class BlockingCollectionTaskQueue : IBackgroundTaskQueue
{
    private readonly BlockingCollection<Func<CancellationToken, ValueTask>> _queue;

    public BlockingCollectionTaskQueue(int capacity)
    {
        // キューの最大容量を設定
        _queue = new BlockingCollection<Func<CancellationToken, ValueTask>>(
            new ConcurrentQueue<Func<CancellationToken, ValueTask>>(), capacity);
    }

    public ValueTask QueueBackgroundWorkItemAsync(
        Func<CancellationToken, ValueTask> workItem)
    {
        if (workItem == null) throw new ArgumentNullException(nameof(workItem));

        try
        {
            _queue.Add(workItem); // キューにタスクを追加
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine("Queue is closed for adding new tasks.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<Func<CancellationToken, ValueTask>> DequeueAsync(CancellationToken cancellationToken)
    {
        try
        {
            var workItem = _queue.Take(cancellationToken); // キューからタスクを取得（空なら待機）
            return new ValueTask<Func<CancellationToken, ValueTask>>(workItem);
        }
        catch (OperationCanceledException)
        {
            return new ValueTask<Func<CancellationToken, ValueTask>>(ct => new ValueTask());
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding(); // キューの追加を終了
        _queue.Dispose();
    }
}

/// <summary>
/// **🚀 `Disruptor` を使用した超高速バックグラウンドタスクキュー**
/// </summary>
public class DisruptorTaskQueue : IBackgroundTaskQueue, IDisposable
{
    private readonly Disruptor<TaskEvent> _disruptor;
    private readonly RingBuffer<TaskEvent> _ringBuffer;

    public DisruptorTaskQueue(int bufferSize = 1024)
    {
        _disruptor = new Disruptor<TaskEvent>(() => new TaskEvent(), bufferSize, TaskScheduler.Default);

        _disruptor.HandleEventsWith(new DisruptorEventHandler());
        _ringBuffer = _disruptor.Start();
    }

    public ValueTask QueueBackgroundWorkItemAsync(Func<CancellationToken, ValueTask> workItem)
    {
        if (workItem == null) throw new ArgumentNullException(nameof(workItem));

        long sequence = _ringBuffer.Next();
        try
        {
            var eventRef = _ringBuffer[sequence];  // **読み取り専用なのでインスタンスを取得**
            eventRef.WorkItem = workItem;          // **フィールドを変更**
        }
        finally
        {
            _ringBuffer.Publish(sequence);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<Func<CancellationToken, ValueTask>> DequeueAsync(CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Disruptor processes tasks internally via event handlers.");
    }

    public void Dispose()
    {
        _disruptor.Shutdown();
    }

    private class DisruptorEventHandler : IEventHandler<TaskEvent>
    {
        public void OnEvent(TaskEvent data, long sequence, bool endOfBatch)
        {
            data.WorkItem?.Invoke(CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// **Disruptor に格納するタスクデータオブジェクト**
    /// </summary>
    public class TaskEvent
    {
        public Func<CancellationToken, ValueTask> WorkItem { get; set; } = _ => new ValueTask();
    }
}
