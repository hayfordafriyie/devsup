using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevSup.Infrastructure.Verification;

/// <summary>
/// Periodic sweep that verifies pushed fixes by probing the repository's app URL.
/// Waits a full interval before the first pass so freshly started instances don't
/// tamper with tests or deployments that are still settling.
/// </summary>
public sealed class VerificationWorker(
    IServiceScopeFactory scopeFactory,
    VerificationOptions options,
    ILogger<VerificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Verification worker started (interval {Interval}s, maturity {Delay}m, window {Window}m, {Enabled})",
            options.IntervalSeconds, options.ProbeDelayMinutes, options.WindowMinutes,
            options.Enabled ? "enabled" : "disabled");

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (options.Enabled)
                {
                    using var scope = scopeFactory.CreateScope();
                    var processor = scope.ServiceProvider.GetRequiredService<VerificationProcessor>();
                    var verified = await processor.RunAsync(stoppingToken);
                    if (verified > 0)
                    {
                        logger.LogInformation("Verification sweep confirmed {Count} fix(es)", verified);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Verification sweep failed");
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