namespace CareerPanda.Framework.Configuration;

public class BackgroundJobsConfig
{
    public int QueueCapacity { get; set; } = 500;

    public int WorkerCount { get; set; } = 3;
}
