using System.Text.Json;
using System.Text.Json.Serialization;
using DevSup.Core;
using DevSup.Core.Models;
using DevSup.Infrastructure.Persistence;

namespace DevSup.Infrastructure.Webhooks;

/// <summary>
/// Rows appended to the webhook outbox at event time. Actual HTTP delivery is the
/// worker's job, so ingestion never blocks on the target URL. Payloads are stored
/// as JSON and signed with the per-endpoint secret when delivered.
/// </summary>
public static class WebhookQueue
{
    public const string EventHeader = "X-DevSup-Event";
    public const string SignatureHeader = "X-DevSup-Signature";

    /// <summary>camelCase + string-enum serialization so the wire payload matches the API contract.</summary>
    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static void Enqueue(DevSupDbContext db, Guid userId, WebhookEvent webhookEvent, object payload, Guid? repositoryId = null)
    {
        var endpoints = db.WebhookEndpoints
            .Where(w => w.UserId == userId && w.Active)
            .ToList();

        if (endpoints.Count == 0)
        {
            return;
        }

        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        var createdAt = DateTimeOffset.UtcNow;
        var bit = 1 << (int)webhookEvent;

        foreach (var endpoint in endpoints)
        {
            if (endpoint.EventMask != 0 && (endpoint.EventMask & bit) == 0)
            {
                continue;
            }

            if (!WebhookScope.Matches(endpoint.RepositoryIds, repositoryId))
            {
                continue;
            }

            db.WebhookDeliveries.Add(new WebhookDelivery
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                WebhookId = endpoint.Id,
                Event = webhookEvent,
                Payload = json,
                CreatedAt = createdAt
            });
        }
    }
}

/// <summary>
/// Resolves whether a webhook endpoint subscribed to a repository. A null/empty scope
/// means "all repositories"; a scoped endpoint only receives events for its repos.
/// </summary>
public static class WebhookScope
{
    public static bool Matches(string? scope, Guid? repositoryId)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return true;
        }
        if (repositoryId is null)
        {
            return false;
        }

        foreach (var part in scope.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(part, out var parsed) && parsed == repositoryId.Value)
            {
                return true;
            }
        }
        return false;
    }
}