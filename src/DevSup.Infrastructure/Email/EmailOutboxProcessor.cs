using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DevSup.Infrastructure.Email;

/// <summary>
/// Drains the email outbox: picks unsent rows within the attempt budget, attempts
/// delivery via <see cref="IEmailSender"/>, and records success or a retryable error.
/// Messages are marked <c>Sent</c> only after delivery succeeds — a failed send is
/// never silently dropped.
/// </summary>
public sealed class EmailOutboxProcessor(
    DevSupDbContext db,
    IEmailSender sender,
    ILogger<EmailOutboxProcessor> logger,
    int maxAttempts)
{
    public async Task<int> ProcessPendingAsync(int batchSize, CancellationToken ct)
    {
        var pending = await db.EmailMessages
            .Where(m => !m.Sent && m.Attempts < maxAttempts)
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);

        var delivered = 0;

        foreach (var message in pending)
        {
            try
            {
                await sender.SendAsync(message, ct);
                var entry = db.Entry(message);
                entry.Property(m => m.Sent).CurrentValue = true;
                entry.Property(m => m.SentAt).CurrentValue = DateTimeOffset.UtcNow;
                entry.Property(m => m.Attempts).CurrentValue = message.Attempts + 1;
                entry.Property(m => m.LastError).CurrentValue = null;
                delivered++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Delivery of outbox message {MessageId} failed (attempt {Attempt})",
                    message.Id, message.Attempts + 1);
                var entry = db.Entry(message);
                entry.Property(m => m.Attempts).CurrentValue = message.Attempts + 1;
                entry.Property(m => m.LastError).CurrentValue = ex.Message;
            }
        }

        if (pending.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return delivered;
    }
}