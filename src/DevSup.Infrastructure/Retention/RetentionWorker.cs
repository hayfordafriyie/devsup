using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevSup.Infrastructure.Retention;

/// <summary>
/// Periodic sweep that enforces the retention window. Cheap no-op runs when there is
/// nothing to purge, so a frequent tick keeps lag bounded without much load.
/// </summary>
public sealed class RetentionWorker(
    IServiceScopeFactory scopeFactory,
    RetentionOptions options,
    ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Retention worker started (window {WindowDays} days, every {IntervalHours}h)",
            options.WindowDays, options.IntervalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var cleaner = scope.ServiceProvider.GetRequiredService<RetentionCleaner>();
                await cleaner.PurgeAsync(options.WindowDays, options.BatchSize, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Retention sweep failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(options.IntervalHours), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}