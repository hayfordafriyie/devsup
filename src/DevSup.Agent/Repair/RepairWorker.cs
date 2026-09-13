namespace DevSup.Agent.Repair;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Periodic sweep that runs the repair loop over pending tickets. Runs on an
/// interval so ingest never blocks on cloning or pushing; each ticket is claimed
/// exactly once because the processor only adopts tickets still <c>New</c>.
/// </summary>
public sealed class RepairWorker(
    IServiceScopeFactory scopeFactory,
    RepairWorkerOptions options,
    ILogger<RepairWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Repair worker started (interval {Interval}s, batch {Batch})",
            options.IntervalSeconds, options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<RepairProcessor>();
                var repaired = await processor.ProcessPendingAsync(options.BatchSize, stoppingToken);
                if (repaired > 0)
                {
                    logger.LogInformation("Repair sweep handled {Count} ticket(s)", repaired);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Repair sweep failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}