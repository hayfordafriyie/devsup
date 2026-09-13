using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevSup.Infrastructure.Webhooks;

/// <summary>
/// Periodic sweep that drains the webhook outbox. Runs on its own interval so event
/// enqueueing never blocks on the target URL; the processor owns retry backoff.
/// </summary>
public sealed class WebhookOutboxWorker(
    IServiceScopeFactory scopeFactory,
    WebhookWorkerOptions options,
    ILogger<WebhookOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Webhook outbox worker started (interval {Interval}s, batch {Batch}, max attempts {MaxAttempts})",
            options.IntervalSeconds, options.BatchSize, options.MaxAttempts);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<WebhookOutboxProcessor>();
                var delivered = await processor.ProcessPendingAsync(options.BatchSize, stoppingToken);
                if (delivered > 0)
                {
                    logger.LogInformation("Webhook outbox sweep delivered {Count} delivery(ies)", delivered);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Webhook outbox sweep failed");
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