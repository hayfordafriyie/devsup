using DevSup.Infrastructure.Persistence;
using DevSup.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DevSup.Infrastructure.Webhooks;

/// <summary>
/// Drains the webhook outbox: picks unsent rows within the attempt budget, decrypts
/// the endpoint secret, attempts delivery, and records success or a retryable error.
/// Deliveries whose endpoint was deleted are dropped instead of retried forever.
/// </summary>
public sealed class WebhookOutboxProcessor(
    DevSupDbContext db,
    IWebhookDeliverer deliverer,
    IKeyProtector protector,
    ILogger<WebhookOutboxProcessor> logger,
    int maxAttempts)
{
    public async Task<int> ProcessPendingAsync(int batchSize, CancellationToken ct)
    {
        var pending = await db.WebhookDeliveries
            .Where(d => !d.Sent && d.Attempts < maxAttempts)
            .OrderBy(d => d.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);

        var delivered = 0;

        foreach (var delivery in pending)
        {
            var endpoint = await db.WebhookEndpoints.AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == delivery.WebhookId, ct);

            var entry = db.Entry(delivery);
            if (endpoint is null || !endpoint.Active)
            {
                entry.Property(d => d.Sent).CurrentValue = true;
                entry.Property(d => d.SentAt).CurrentValue = DateTimeOffset.UtcNow;
                entry.Property(d => d.Attempts).CurrentValue = delivery.Attempts + 1;
                entry.Property(d => d.LastError).CurrentValue = "webhook endpoint removed";
                continue;
            }

            try
            {
                var secret = protector.Unprotect(endpoint.EncryptedSecret);
                await deliverer.DeliverAsync(endpoint.Url, secret, delivery.Payload, delivery.Event, endpoint.Channel, ct);
                entry.Property(d => d.Sent).CurrentValue = true;
                entry.Property(d => d.SentAt).CurrentValue = DateTimeOffset.UtcNow;
                entry.Property(d => d.Attempts).CurrentValue = delivery.Attempts + 1;
                entry.Property(d => d.LastError).CurrentValue = null;
                delivered++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Delivery of webhook outbox row {DeliveryId} failed (attempt {Attempt})",
                    delivery.Id, delivery.Attempts + 1);
                entry.Property(d => d.Attempts).CurrentValue = delivery.Attempts + 1;
                entry.Property(d => d.LastError).CurrentValue = ex.Message;
            }
        }

        if (pending.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return delivered;
    }
}

public sealed class WebhookWorkerOptions
{
    public int IntervalSeconds { get; init; } = 15;
    public int BatchSize { get; init; } = 50;
    public int MaxAttempts { get; init; } = 8;
}