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
}