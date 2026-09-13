using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevSup.Infrastructure.HealthChecks;

/// <summary>
/// Periodic sweep that probes configured app URLs and records failures. Runs on its
/// own interval so probing never blocks the API; the checker deduplicates consecutive
/// failures so a single outage produces one ticket.
/// </summary>
public sealed class AppHealthCheckWorker(
    IServiceScopeFactory scopeFactory,
    HealthCheckOptions options,
    ILogger<AppHealthCheckWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("App health check worker started (interval {Interval}s, {Enabled})",
            options.IntervalSeconds, options.Enabled ? "enabled" : "disabled");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (options.Enabled)
                {
                    using var scope = scopeFactory.CreateScope();
                    var checker = scope.ServiceProvider.GetRequiredService<AppHealthChecker>();
                    var failed = await checker.ProbeDueAsync(options.BatchSize, TimeSpan.FromSeconds(options.TimeoutSeconds), stoppingToken);
                    if (failed > 0)
                    {
                        logger.LogInformation("App health sweep recorded {Count} new failure(s)", failed);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "App health sweep failed");
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