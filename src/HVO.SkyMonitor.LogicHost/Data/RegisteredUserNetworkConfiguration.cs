using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Data;

internal static class RegisteredUserNetworkConfiguration
{
    internal static void Configure(ModelBuilder builder)
    {
        var placement = builder.Entity<CuratedPublicPlacementDecision>();
        placement.ToTable("CuratedPublicPlacementDecisions", table =>
        {
            table.HasTrigger("TR_CuratedPublicPlacementDecisions_Immutable");
            table.HasCheckConstraint("CK_CuratedPublicPlacementDecisions_Surface", "[Surface] IN (N'HomeObservatory', N'HomeEvent')");
            table.HasCheckConstraint("CK_CuratedPublicPlacementDecisions_State", "[State] IN (N'Featured', N'Suppressed', N'Cleared')");
            table.HasCheckConstraint("CK_CuratedPublicPlacementDecisions_Subject", "([Surface] = N'HomeObservatory' AND [ObservatoryId] IS NOT NULL AND [PublicRecordId] IS NULL) OR ([Surface] = N'HomeEvent' AND [ObservatoryId] IS NULL AND [PublicRecordId] IS NOT NULL)");
            table.HasCheckConstraint("CK_CuratedPublicPlacementDecisions_Order", "([State] = N'Featured' AND [DisplayOrder] BETWEEN 0 AND 999) OR ([State] <> N'Featured' AND [DisplayOrder] IS NULL)");
        });
        placement.HasKey(item => item.Id);
        placement.Property(item => item.Surface).HasConversion<string>().HasMaxLength(32).IsRequired();
        placement.Property(item => item.State).HasConversion<string>().HasMaxLength(16).IsRequired();
        placement.Property(item => item.ActorUserId).HasMaxLength(450).IsRequired();
        placement.Property(item => item.ReasonCode).HasMaxLength(128).IsRequired();
        placement.HasIndex(item => new { item.Surface, item.ObservatoryId, item.OccurredAtUtc });
        placement.HasIndex(item => new { item.Surface, item.PublicRecordId, item.OccurredAtUtc });
        placement.HasIndex(item => item.SupersedesDecisionId).IsUnique().HasFilter("[SupersedesDecisionId] IS NOT NULL");
        placement.HasOne(item => item.Observatory).WithMany().HasForeignKey(item => item.ObservatoryId).OnDelete(DeleteBehavior.Restrict);
        placement.HasOne(item => item.SupersedesDecision).WithOne()
            .HasForeignKey<CuratedPublicPlacementDecision>(item => item.SupersedesDecisionId).OnDelete(DeleteBehavior.Restrict);

        var follow = builder.Entity<RegisteredUserObservatoryFollow>();
        follow.ToTable("RegisteredUserObservatoryFollows");
        follow.HasKey(item => new { item.UserId, item.ObservatoryId });
        follow.Property(item => item.UserId).HasMaxLength(450).IsRequired();
        follow.HasIndex(item => new { item.UserId, item.CreatedUtc, item.ObservatoryId });
        follow.HasOne(item => item.User).WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Cascade).IsRequired();
        follow.HasOne(item => item.Observatory).WithMany().HasForeignKey(item => item.ObservatoryId).OnDelete(DeleteBehavior.Restrict).IsRequired();

        var bookmark = builder.Entity<RegisteredUserTransientEventBookmark>();
        bookmark.ToTable("RegisteredUserTransientEventBookmarks");
        bookmark.HasKey(item => new { item.UserId, item.CentralTransientEventId });
        bookmark.Property(item => item.UserId).HasMaxLength(450).IsRequired();
        bookmark.HasIndex(item => new { item.UserId, item.CreatedUtc, item.CentralTransientEventId });
        bookmark.HasOne(item => item.User).WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Cascade).IsRequired();
        bookmark.HasOne(item => item.Event).WithMany().HasForeignKey(item => item.CentralTransientEventId).OnDelete(DeleteBehavior.Restrict).IsRequired();

        var subscription = builder.Entity<RegisteredUserSubscription>();
        subscription.ToTable("RegisteredUserSubscriptions", table =>
            table.HasCheckConstraint("CK_RegisteredUserSubscriptions_Kind", "[Kind] = N'VerifiedEvent'"));
        subscription.HasKey(item => item.Id);
        subscription.Property(item => item.UserId).HasMaxLength(450).IsRequired();
        subscription.Property(item => item.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
        subscription.HasIndex(item => new { item.UserId, item.Kind }).IsUnique();
        subscription.HasOne(item => item.User).WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Cascade).IsRequired();

        var preference = builder.Entity<RegisteredUserNotificationPreference>();
        preference.ToTable("RegisteredUserNotificationPreferences");
        preference.HasKey(item => item.UserId);
        preference.Property(item => item.UserId).HasMaxLength(450).IsRequired();
        preference.Property(item => item.RowVersion).IsRowVersion();
        preference.HasOne(item => item.User).WithOne().HasForeignKey<RegisteredUserNotificationPreference>(item => item.UserId).OnDelete(DeleteBehavior.Cascade).IsRequired();

        var notification = builder.Entity<RegisteredUserNotification>();
        notification.ToTable("RegisteredUserNotifications", table =>
            table.HasCheckConstraint("CK_RegisteredUserNotifications_Kind", "[Kind] = N'VerifiedEventReleased'"));
        notification.HasKey(item => item.Id);
        notification.Property(item => item.UserId).HasMaxLength(450).IsRequired();
        notification.Property(item => item.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
        notification.Property(item => item.Title).HasMaxLength(200).IsRequired();
        notification.Property(item => item.DeduplicationKey).HasMaxLength(128).IsRequired();
        notification.Property(item => item.RowVersion).IsRowVersion();
        notification.HasIndex(item => new { item.UserId, item.DeduplicationKey }).IsUnique();
        notification.HasIndex(item => new { item.UserId, item.ReadUtc, item.CreatedUtc, item.Id });
        notification.HasOne(item => item.User).WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Cascade).IsRequired();
        notification.HasOne(item => item.Event).WithMany().HasForeignKey(item => item.CentralTransientEventId).OnDelete(DeleteBehavior.Restrict);
    }
}
