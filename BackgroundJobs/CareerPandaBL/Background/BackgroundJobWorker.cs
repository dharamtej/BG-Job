using CareerPanda.Framework.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CareerPanda.BL.Background;

public class BackgroundJobWorker : BackgroundService
{
    private readonly IBackgroundJobQueue _queue;
    private readonly JobExecutionService _executionService;
    private readonly ILogger<BackgroundJobWorker> _logger;
    private readonly int _workerCount;

    public BackgroundJobWorker(
        IBackgroundJobQueue queue,
        JobExecutionService executionService,
        Config config,
        ILogger<BackgroundJobWorker> logger)
    {
        _queue = queue;
        _executionService = executionService;
        _logger = logger;
        _workerCount = Math.Max(1, config.BackgroundJobsConfig.WorkerCount);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CareerPanda background job worker starting with {Count} workers", _workerCount);

        var workers = Enumerable.Range(0, _workerCount)
            .Select(_ => Task.Run(() => WorkerLoopAsync(stoppingToken), stoppingToken))
            .ToArray();

        return Task.WhenAll(workers);
    }

    private async Task WorkerLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var workItem = await _queue.DequeueAsync(stoppingToken);
                await workItem(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background job worker error");
            }
        }
    }
}
