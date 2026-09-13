using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevSup.Infrastructure.Digest;

/// <summary>
/// Periodically runs the digest sweep: one summary email per active user on the
/// configured interval. Disabled until the first interval elapses so a fresh install
/// doesn't flood mailboxes on boot.
/// </summary>
public sealed class DigestWorker(
    IServiceScopeFactory scopeFactory,
    DigestOptions options,
    ILogger<DigestWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Email digests are disabled (Digests:Enabled=false).");
            return;
        }

        var period = TimeSpan.FromHours(options.IntervalHours);
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(period, stoppingToken);

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<DigestProcessor>();
                var generated = await processor.RunAsync(stoppingToken);
                logger.LogInformation("Digest sweep generated {Count} summary email(s).", generated);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Digest sweep failed.");
            }
        }
    }
}