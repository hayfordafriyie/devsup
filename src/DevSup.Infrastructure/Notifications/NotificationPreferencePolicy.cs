using DevSup.Core;
using DevSup.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DevSup.Infrastructure.Notifications;

/// <summary>
/// Decides whether an email should be sent for a given domain event on a repository,
/// honouring the owner's per-repository notification preferences. A missing preference
/// row means "email everything" (safe default).
/// </summary>
public static class NotificationPreferencePolicy
{
    public static bool ShouldSendEmail(DevSupDbContext db, Guid userId, Guid repositoryId, WebhookEvent e)
    {
        var preference = db.NotificationPreferences.AsNoTracking()
            .FirstOrDefault(p => p.UserId == userId && p.RepositoryId == repositoryId);
        if (preference is null)
        {
            return true;
        }
        if (!preference.EmailEnabled)
        {
            return false;
        }

        return (preference.MutedEmailEvents & (1 << (int)e)) == 0;
    }

    /// <summary>
    /// Returns the UTC instant until which email should be held for this repository's
    /// quiet hours, or null when no quiet window applies. A window may wrap midnight
    /// (e.g. 22 → 7). Hours are UTC.
    /// </summary>
    public static DateTimeOffset? QuietHoursEndUtc(DevSupDbContext db, Guid userId, Guid repositoryId, DateTimeOffset now)
    {
        var preference = db.NotificationPreferences.AsNoTracking()
            .FirstOrDefault(p => p.UserId == userId && p.RepositoryId == repositoryId);
        if (preference?.QuietHoursStart is not int start || preference.QuietHoursEnd is not int end)
        {
            return null;
        }
        if (start == end)
        {
            return null;
        }

        var hour = now.UtcDateTime.Hour;
        var inWindow = start < end
            ? hour >= start && hour < end
            : hour >= start || hour < end;
        if (!inWindow)
        {
            return null;
        }

        var endToday = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddHours(end);
        return start > end && hour >= start ? endToday.AddDays(1) : endToday;
    }
}