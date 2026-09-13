using DevSup.Core.Models;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DevSup.Api.Audit;

/// <summary>Persists a write-audit trail for sensitive user and admin actions.</summary>
public sealed class AuditRecorder
{
    private readonly DevSupDbContext _db;

    public AuditRecorder(DevSupDbContext db)
    {
        _db = db;
    }

    public Task RecordAsync(
        Guid actorUserId,
        string actorEmail,
        string action,
        string entityType,
        string? entityId = null,
        string? before = null,
        string? after = null,
        string? ipAddress = null,
        CancellationToken ct = default)
    {
        _db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            ActorUserId = actorUserId,
            ActorEmail = actorEmail,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Before = before,
            After = after,
            IpAddress = ipAddress,
            Timestamp = DateTimeOffset.UtcNow
        });
        return _db.SaveChangesAsync(ct);
    }
}