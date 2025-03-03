using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace BackgroundTasksSample.Services;

public interface IBackgroundTaskQueue
{
    ValueTask QueueBackgroundWorkItemAsync(Func<CancellationToken, ValueTask> workItem);

    ValueTask<Func<CancellationToken, ValueTask>> DequeueAsync(
        CancellationToken cancellationToken);
}

public class BackgroundTaskQueue : IBackgroundTaskQueue
{
    private readonly Channel<Func<CancellationToken, ValueTask>> _queue;

    public BackgroundTaskQueue(int capacity)
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

public class BackgroundTaskQueueSimple : IBackgroundTaskQueue, IDisposable
{
    private readonly ConcurrentQueue<Func<CancellationToken, ValueTask>> _queue;
    private readonly SemaphoreSlim _signal;

    public BackgroundTaskQueueSimple(int capacity)
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

public class BackgroundTaskQueueSimple2 : IBackgroundTaskQueue
{
    private readonly BlockingCollection<Func<CancellationToken, ValueTask>> _queue;

    public BackgroundTaskQueueSimple2(int capacity)
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
            return new ValueTask<Func<CancellationToken, ValueTask>>();
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding(); // キューの追加を終了
        _queue.Dispose();
    }
}
