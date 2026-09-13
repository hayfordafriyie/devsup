using Microsoft.EntityFrameworkCore;
using DevSup.Core;
using DevSup.Core.Models;

namespace DevSup.Infrastructure.Persistence;

public sealed class DevSupDbContext(DbContextOptions<DevSupDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<ConnectedRepository> ConnectedRepositories => Set<ConnectedRepository>();
    public DbSet<FailureEvent> FailureEvents => Set<FailureEvent>();
    public DbSet<RepairTicket> RepairTickets => Set<RepairTicket>();
    public DbSet<AiModelKeyBinding> AiModelKeyBindings => Set<AiModelKeyBinding>();
    public DbSet<EmailMessage> EmailMessages => Set<EmailMessage>();
    public DbSet<OAuthToken> OAuthTokens => Set<OAuthToken>();
    public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();

    public DbSet<RepositoryMember> RepositoryMembers => Set<RepositoryMember>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Email).HasMaxLength(320).IsRequired();
            entity.HasIndex(u => u.Email).IsUnique();
            entity.Property(u => u.DisplayName).HasMaxLength(100).IsRequired();
            entity.Property(u => u.PasswordHash).HasMaxLength(512).IsRequired();
            entity.Property(u => u.IsAdmin).HasDefaultValue(false);
            entity.Property(u => u.Active).HasDefaultValue(true);
            entity.Property(u => u.DigestFrequency).HasConversion<int>();
        });

        modelBuilder.Entity<ConnectedRepository>(entity =>
        {
            entity.ToTable("connected_repositories");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.CloneUrl).HasMaxLength(2048).IsRequired();
            entity.Property(r => r.DefaultBranch).HasMaxLength(255).IsRequired();
            entity.Property(r => r.AppUrl).HasMaxLength(2048);
            entity.Property(r => r.AppHealthLastError).HasMaxLength(2048);
            entity.Property(r => r.Provider).HasConversion<int>();
            entity.Property(r => r.RepairMode).HasConversion<int>();
            entity.Property(r => r.Archived).HasDefaultValue(false);
            entity.HasIndex(r => new { r.OwnerUserId, r.CloneUrl }).IsUnique();        });

        modelBuilder.Entity<FailureEvent>(entity =>
        {
            entity.ToTable("failure_events");
            entity.HasKey(f => f.Id);
            entity.Property(f => f.Method).HasMaxLength(16).IsRequired();
            entity.Property(f => f.Path).HasMaxLength(1024).IsRequired();
            entity.Property(f => f.RequestPayload).HasMaxLength(8192);
            entity.Property(f => f.ResponsePayload).HasMaxLength(8192);
            entity.Property(f => f.ExceptionMessage).HasMaxLength(4096);
            entity.Property(f => f.StackTrace).HasMaxLength(16_384);
            entity.HasIndex(f => new { f.RepositoryId, f.OccurredAt });
        });

        modelBuilder.Entity<RepairTicket>(entity =>
        {
            entity.ToTable("repair_tickets");
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Category).HasConversion<int>();
            entity.Property(t => t.Kind).HasConversion<int>();
            entity.Property(t => t.Status).HasConversion<int>();
            entity.Property(t => t.Analysis).HasMaxLength(16_384);
            entity.Property(t => t.PatchSummary).HasMaxLength(8192);
            entity.Property(t => t.CommitSha).HasMaxLength(64);
            entity.Property(t => t.PullRequestUrl).HasMaxLength(2048);
            entity.Property(t => t.LastError).HasMaxLength(2048);
            entity.HasIndex(t => new { t.RepositoryId, t.Status });
        });

        modelBuilder.Entity<AiModelKeyBinding>(entity =>
        {
            entity.ToTable("ai_model_key_bindings");
            entity.HasKey(k => k.Id);
            entity.Property(k => k.Provider).HasConversion<int>();
            entity.Property(k => k.Model).HasMaxLength(100).IsRequired();
            entity.Property(k => k.EncryptedApiKey).HasMaxLength(2048).IsRequired();
            entity.Property(k => k.KeyMask).HasMaxLength(16);
            entity.HasIndex(k => new { k.UserId, k.Provider, k.Model }).IsUnique();
        });

        modelBuilder.Entity<EmailMessage>(entity =>
        {
            entity.ToTable("email_messages");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.To).HasMaxLength(320).IsRequired();
            entity.Property(m => m.Subject).HasMaxLength(512).IsRequired();
            entity.Property(m => m.HtmlBody).HasMaxLength(16_384).IsRequired();
            entity.Property(m => m.LastError).HasMaxLength(2048);
            entity.HasIndex(m => new { m.Sent, m.CreatedAt });
        });

        modelBuilder.Entity<OAuthToken>(entity =>
        {
            entity.ToTable("oauth_tokens");
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Provider).HasConversion<int>();
            entity.Property(t => t.EncryptedAccessToken).HasMaxLength(4096).IsRequired();
            entity.Property(t => t.Scope).HasMaxLength(512);
            entity.HasIndex(t => new { t.UserId, t.Provider }).IsUnique();
        });

        modelBuilder.Entity<WebhookEndpoint>(entity =>
        {
            entity.ToTable("webhook_endpoints");
            entity.HasKey(w => w.Id);
            entity.Property(w => w.Url).HasMaxLength(2048).IsRequired();
            entity.Property(w => w.Name).HasMaxLength(128);
            entity.Property(w => w.Channel).HasConversion<int>();
            entity.Property(w => w.EncryptedSecret).HasMaxLength(1024).IsRequired();
            entity.HasIndex(w => new { w.UserId, w.Url }).IsUnique();
        });

        modelBuilder.Entity<WebhookDelivery>(entity =>
        {
            entity.ToTable("webhook_deliveries");
            entity.HasKey(d => d.Id);
            entity.Property(d => d.Event).HasConversion<int>();
            entity.Property(d => d.Payload).HasMaxLength(16_384).IsRequired();
            entity.Property(d => d.LastError).HasMaxLength(2048);
            entity.HasIndex(d => new { d.Sent, d.CreatedAt });
        });

        modelBuilder.Entity<AuditEntry>(entity =>
        {
            entity.ToTable("audit_entries");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.ActorEmail).HasMaxLength(256).IsRequired();
            entity.Property(a => a.Action).HasMaxLength(64).IsRequired();
            entity.Property(a => a.EntityType).HasMaxLength(64).IsRequired();
            entity.Property(a => a.EntityId).HasMaxLength(64);
            entity.Property(a => a.Before).HasMaxLength(4096);
            entity.Property(a => a.After).HasMaxLength(4096);
            entity.Property(a => a.IpAddress).HasMaxLength(64);
            entity.HasIndex(a => new { a.Timestamp });
            entity.HasIndex(a => new { a.ActorUserId, a.Timestamp });
        });

        modelBuilder.Entity<NotificationPreference>(entity =>
        {
            entity.ToTable("notification_preferences");
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => new { p.UserId, p.RepositoryId }).IsUnique();
            entity.Property(p => p.EmailEnabled).HasDefaultValue(true);
        });

        modelBuilder.Entity<RepositoryMember>(entity =>
        {
            entity.ToTable("repository_members");
            entity.HasKey(m => new { m.RepositoryId, m.UserId });
            entity.Property(m => m.Role).HasConversion<int>();
            entity.HasIndex(m => new { m.RepositoryId, m.UserId }).IsUnique();
        });

        base.OnModelCreating(modelBuilder);
    }
}