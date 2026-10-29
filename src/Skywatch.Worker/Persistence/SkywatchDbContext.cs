using Microsoft.EntityFrameworkCore;
using Skywatch.Worker.Persistence.Entities;
using Skywatch.Worker.Telegram;

namespace Skywatch.Worker.Persistence;

/// <summary>Spec §7 schema. Create one context per unit of work through <see cref="IDbContextFactory{TContext}"/>.</summary>
internal sealed class SkywatchDbContext(DbContextOptions<SkywatchDbContext> options) : DbContext(options)
{
    public DbSet<Source> Sources => Set<Source>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<ServiceEvent> ServiceEvents => Set<ServiceEvent>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();

        // Enums by name, so the experiment data reads well in plain SQL.
        configurationBuilder.Properties<SourceType>().HaveConversion<string>();
        configurationBuilder.Properties<DuplicateReason>().HaveConversion<string>();
        configurationBuilder.Properties<NotificationKind>().HaveConversion<string>();
        configurationBuilder.Properties<NotificationStatus>().HaveConversion<string>();
        configurationBuilder.Properties<NotificationLabel>().HaveConversion<string>();
        configurationBuilder.Properties<ServiceEventKind>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Source>().HasIndex(s => s.TelegramChatId).IsUnique();

        modelBuilder.Entity<Message>(message =>
        {
            message.HasIndex(m => new { m.SourceId, m.TelegramMessageId }).IsUnique();
            message.HasIndex(m => new { m.ContentHash, m.ReceivedAtUtc });
            message.HasIndex(m => new { m.OriginChatId, m.OriginMessageId, m.ReceivedAtUtc });
            message.HasIndex(m => m.ReceivedAtUtc);

            // Database-level actions, because retention deletes with ExecuteDelete, which bypasses the change tracker.
            message.HasOne(m => m.DuplicateOf).WithMany().HasForeignKey(m => m.DuplicateOfMessageId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Notification>()
            .HasOne(n => n.Message).WithMany().HasForeignKey(n => n.MessageId).OnDelete(DeleteBehavior.Cascade);
    }
}
