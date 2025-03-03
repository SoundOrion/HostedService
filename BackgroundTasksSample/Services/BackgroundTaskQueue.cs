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
            FullMode = BoundedChannelFullMode.Wait // �L���[�����t�Ȃ�ҋ@
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

public class BackgroundTaskQueueSimple : IBackgroundTaskQueue
{
    private readonly BlockingCollection<Func<CancellationToken, ValueTask>> _queue;

    public BackgroundTaskQueueSimple(int capacity)
    {
        // �L���[�̍ő�e�ʂ�ݒ�
        _queue = new BlockingCollection<Func<CancellationToken, ValueTask>>(
            new ConcurrentQueue<Func<CancellationToken, ValueTask>>(), capacity);
    }

    public ValueTask QueueBackgroundWorkItemAsync(
        Func<CancellationToken, ValueTask> workItem)
    {
        if (workItem == null) throw new ArgumentNullException(nameof(workItem));

        try
        {
            _queue.Add(workItem); // �L���[�Ƀ^�X�N��ǉ�
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
            var workItem = _queue.Take(cancellationToken); // �L���[����^�X�N���擾�i��Ȃ�ҋ@�j
            return new ValueTask<Func<CancellationToken, ValueTask>>(workItem);
        }
        catch (OperationCanceledException)
        {
            //return new ValueTask<Func<CancellationToken, ValueTask>>(() => new ValueTask());
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding(); // �L���[�̒ǉ����I��
        _queue.Dispose();
    }
}
