using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevSup.Infrastructure.Email;

/// <summary>
/// Periodic sweep that drains the email outbox. Runs on an interval so the ingest
/// path never blocks on SMTP; the processor handles backoff via Attempts.
/// </summary>
public sealed class EmailOutboxWorker(
    IServiceScopeFactory scopeFactory,
    EmailWorkerOptions options,
    ILogger<EmailOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Email outbox worker started (interval {Interval}s, batch {Batch}, max attempts {MaxAttempts})",
            options.IntervalSeconds, options.BatchSize, options.MaxAttempts);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<EmailOutboxProcessor>();
                var delivered = await processor.ProcessPendingAsync(options.BatchSize, stoppingToken);
                if (delivered > 0)
                {
                    logger.LogInformation("Email outbox sweep delivered {Count} message(s)", delivered);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Email outbox sweep failed");
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