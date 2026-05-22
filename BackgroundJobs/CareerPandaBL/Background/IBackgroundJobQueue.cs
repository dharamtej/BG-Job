namespace CareerPanda.BL.Background;

/// <summary>
/// Thread-safe queue for background work items (same pattern as CourseED IBackgroundQuestionQueue).
/// </summary>
public interface IBackgroundJobQueue
{
    ValueTask QueueBackgroundWorkItemAsync(Func<CancellationToken, Task> workItem);

    ValueTask<Func<CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken);
}
