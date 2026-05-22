namespace CareerPanda.BL.Background;

/// <summary>
/// Placeholder handler — replace with real domain work per JobType.
/// </summary>
public class DefaultJobHandler : IJobHandler
{
    public string JobType => "Default";

    public async Task ExecuteAsync(JobWorkRequest request, IJobProgressReporter progress, CancellationToken cancellationToken)
    {
        await progress.ReportProgressAsync(10, "Starting job");
        await Task.Delay(500, cancellationToken);

        await progress.ReportProgressAsync(50, "Processing");
        await Task.Delay(500, cancellationToken);

        await progress.ReportProgressAsync(100, "Completed");
    }
}
