namespace CareerPanda.BL.Background;

/// <summary>
/// Implement per job type; register in DI as IEnumerable&lt;IJobHandler&gt;.
/// </summary>
public interface IJobHandler
{
    string JobType { get; }

    Task ExecuteAsync(JobWorkRequest request, IJobProgressReporter progress, CancellationToken cancellationToken);
}

public interface IJobProgressReporter
{
    Task ReportProgressAsync(int percent, string? message = null);
}
