using System.Threading.Channels;

namespace CareerPanda.BL.Background;

public class BackgroundJobQueue : IBackgroundJobQueue
{
    private readonly Channel<Func<CancellationToken, Task>> _queue;

    public BackgroundJobQueue(int capacity)
    {
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        };
        _queue = Channel.CreateBounded<Func<CancellationToken, Task>>(options);
    }

    public async ValueTask QueueBackgroundWorkItemAsync(Func<CancellationToken, Task> workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        await _queue.Writer.WriteAsync(workItem);
    }

    public async ValueTask<Func<CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken)
    {
        return await _queue.Reader.ReadAsync(cancellationToken);
    }
}
