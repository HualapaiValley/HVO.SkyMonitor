using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Data;

internal static class CentralTransientNotificationConfiguration
{
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    public static void Configure(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ConfigureNotification(builder);
        ConfigureVersionLink(builder);
        ConfigureDispatch(builder);
    }

    private static void ConfigureNotification(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralTransientNotificationRecord>();
        entity.ToTable("CentralTransientNotifications", table =>
        {
            table.HasTrigger("TR_CentralTransientNotifications_Immutable");
            table.HasCheckConstraint("CK_CentralTransientNotifications_Predecessor",
                "([SupersedesNotificationId] IS NULL AND [SupersedesNotificationCreatedUtc] IS NULL) OR " +
                "([SupersedesNotificationId] IS NOT NULL AND [SupersedesNotificationCreatedUtc] IS NOT NULL AND [SupersedesNotificationCreatedUtc] < [CreatedUtc])");
        });
        entity.HasKey(item => item.NotificationId);
        entity.HasAlternateKey(item => new { item.CentralTransientEventId, item.NotificationId });
        entity.HasAlternateKey(item => new
        {
            item.CentralTransientEventId,
            item.NotificationId,
            item.CreatedUtc
        });
        entity.Property(item => item.Channel).HasMaxLength(32).UseCollation(BinaryCollation).IsRequired();
        entity.Property(item => item.State).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.ReasonCode).HasMaxLength(256).UseCollation(BinaryCollation);
        entity.HasOne(item => item.Event).WithMany()
            .HasForeignKey(item => item.CentralTransientEventId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.Assessment).WithMany()
            .HasForeignKey(item => new { item.CentralTransientEventId, item.AssessmentId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.AssessmentId })
            .OnDelete(DeleteBehavior.NoAction).IsRequired();
        entity.HasOne(item => item.SupersedesNotification).WithMany()
            .HasForeignKey(item => new
            {
                item.CentralTransientEventId,
                item.SupersedesNotificationId,
                item.SupersedesNotificationCreatedUtc
            })
            .HasPrincipalKey(item => new
            {
                item.CentralTransientEventId,
                item.NotificationId,
                item.CreatedUtc
            })
            .OnDelete(DeleteBehavior.NoAction);
        entity.HasIndex(item => new { item.CentralTransientEventId, item.SupersedesNotificationId })
            .IsUnique().HasFilter("[SupersedesNotificationId] IS NOT NULL");
    }

    private static void ConfigureVersionLink(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralTransientEventVersionNotification>();
        entity.ToTable("CentralTransientEventVersionNotifications", table =>
        {
            table.HasTrigger("TR_CentralTransientEventVersionNotifications_Immutable");
            table.HasCheckConstraint("CK_CentralTransientEventVersionNotifications_Ordinal", "[Ordinal] >= 0");
        });
        entity.HasKey(item => new { item.EventVersionId, item.Ordinal });
        entity.HasIndex(item => new { item.EventVersionId, item.NotificationId }).IsUnique();
        entity.HasOne(item => item.EventVersion).WithMany()
            .HasForeignKey(item => new { item.CentralTransientEventId, item.EventVersionId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.EventVersionId })
            .OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.Notification).WithMany()
            .HasForeignKey(item => new { item.CentralTransientEventId, item.NotificationId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.NotificationId })
            .OnDelete(DeleteBehavior.NoAction).IsRequired();
    }

    private static void ConfigureDispatch(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralTransientNotificationDispatch>();
        entity.ToTable("CentralTransientNotificationDispatches", table =>
        {
            table.HasTrigger("TR_CentralTransientNotificationDispatches_Transition");
            table.HasTrigger("TR_CentralTransientNotificationDispatches_Insert");
            table.HasCheckConstraint("CK_CentralTransientNotificationDispatches_Timestamps",
                "([FencedUtc] IS NULL OR [FencedUtc] >= [CreatedUtc]) AND " +
                "([CompletedUtc] IS NULL OR [FencedUtc] IS NULL OR [CompletedUtc] >= [FencedUtc])");
            table.HasCheckConstraint("CK_CentralTransientNotificationDispatches_State",
                "([State] = 'Pending' AND [FencedUtc] IS NULL AND [CompletedUtc] IS NULL) OR " +
                "([State] = 'Fenced' AND [FencedUtc] IS NOT NULL AND [CompletedUtc] IS NULL) OR " +
                "([State] IN ('Sent', 'Failed', 'Suppressed') AND [CompletedUtc] IS NOT NULL)");
        });
        entity.HasKey(item => item.DispatchId);
        entity.Property(item => item.Channel).HasMaxLength(32).UseCollation(BinaryCollation).IsRequired();
        entity.Property(item => item.Recipient).HasMaxLength(320);
        entity.Property(item => item.RecipientIdentitySha256).HasMaxLength(64).IsUnicode(false)
            .UseCollation(BinaryCollation);
        entity.Property(item => item.State).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.ReasonCode).HasMaxLength(256).UseCollation(BinaryCollation);
        entity.Property(item => item.RequestedByActorIdentity).HasMaxLength(256).UseCollation(BinaryCollation);
        entity.Property(item => item.IdempotencyKey).HasMaxLength(128).UseCollation(BinaryCollation);
        entity.Property(item => item.CanonicalRequestSha256).HasMaxLength(64).IsUnicode(false)
            .UseCollation(BinaryCollation);
        entity.Property(item => item.RowVersion).IsRowVersion();
        entity.HasIndex(item => new { item.State, item.CreatedUtc });
        entity.HasIndex(item => new
        {
            item.CentralTransientEventId,
            item.ReviewId,
            item.RecipientIdentitySha256
        }).IsUnique().HasFilter("[SupersedesDispatchId] IS NULL");
        entity.HasIndex(item => item.SupersedesDispatchId).IsUnique()
            .HasFilter("[SupersedesDispatchId] IS NOT NULL");
        entity.HasIndex(item => new
        {
            item.CentralTransientEventId,
            item.RequestedByActorIdentity,
            item.IdempotencyKey
        }).IsUnique().HasFilter("[RequestedByActorIdentity] IS NOT NULL AND [IdempotencyKey] IS NOT NULL");
        entity.HasOne(item => item.Event).WithMany()
            .HasForeignKey(item => item.CentralTransientEventId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.Review).WithMany()
            .HasForeignKey(item => new { item.CentralTransientEventId, item.ReviewId, item.AssessmentId })
            .HasPrincipalKey(item => new
            {
                item.CentralTransientEventId,
                item.ReviewId,
                item.AssessmentId
            })
            .OnDelete(DeleteBehavior.NoAction).IsRequired();
        entity.HasOne(item => item.InitialNotification).WithMany()
            .HasForeignKey(item => new { item.CentralTransientEventId, item.InitialNotificationId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.NotificationId })
            .OnDelete(DeleteBehavior.NoAction).IsRequired();
        entity.HasOne(item => item.LatestNotification).WithMany()
            .HasForeignKey(item => new { item.CentralTransientEventId, item.LatestNotificationId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.NotificationId })
            .OnDelete(DeleteBehavior.NoAction).IsRequired();
        entity.HasOne(item => item.SupersedesDispatch).WithMany()
            .HasForeignKey(item => item.SupersedesDispatchId).OnDelete(DeleteBehavior.NoAction);
    }
}
