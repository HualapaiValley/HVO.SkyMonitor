using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Data;

internal static class CentralTransientPayloadReleaseConfiguration
{
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    public static void Configure(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var release = builder.Entity<CentralTransientPayloadRelease>();
        release.ToTable("CentralTransientPayloadReleases", table =>
        {
            table.HasTrigger("TR_CentralTransientPayloadReleases_Transition");
            table.HasTrigger("TR_CentralTransientPayloadReleases_Insert");
            table.HasCheckConstraint("CK_CentralTransientPayloadReleases_Completion",
                "([State] = 'Pending' AND [CompletedUtc] IS NULL) OR " +
                "([State] IN ('Completed', 'Failed') AND [CompletedUtc] IS NOT NULL)");
        });
        release.HasKey(item => item.ReleaseId);
        release.Property(item => item.ActorIdentity).HasMaxLength(256).UseCollation(BinaryCollation).IsRequired();
        release.Property(item => item.IdempotencyKey).HasMaxLength(128).UseCollation(BinaryCollation).IsRequired();
        release.Property(item => item.CanonicalRequestSha256).HasMaxLength(64).IsUnicode(false)
            .UseCollation(BinaryCollation).IsRequired();
        release.Property(item => item.State).HasConversion<string>().HasMaxLength(32).IsRequired();
        release.Property(item => item.ReasonCode).HasMaxLength(256).UseCollation(BinaryCollation);
        release.Property(item => item.RowVersion).IsRowVersion();
        release.HasIndex(item => new
        {
            item.CentralTransientEventId,
            item.ActorIdentity,
            item.IdempotencyKey
        }).IsUnique();
        release.HasIndex(item => new { item.State, item.CreatedUtc, item.ReleaseId });
        release.HasOne(item => item.Event).WithMany()
            .HasForeignKey(item => item.CentralTransientEventId).OnDelete(DeleteBehavior.Restrict).IsRequired();

        var item = builder.Entity<CentralTransientPayloadReleaseItem>();
        item.ToTable("CentralTransientPayloadReleaseItems", table =>
        {
            table.HasTrigger("TR_CentralTransientPayloadReleaseItems_Transition");
            table.HasTrigger("TR_CentralTransientPayloadReleaseItems_Closed");
            table.HasCheckConstraint("CK_CentralTransientPayloadReleaseItems_Ordinal", "[Ordinal] >= 0");
            table.HasCheckConstraint("CK_CentralTransientPayloadReleaseItems_Outcome",
                "([Outcome] = 'Pending' AND [ReleasedUtc] IS NULL AND [FailureReasonCode] IS NULL) OR " +
                "([Outcome] IN ('Released', 'PreservedHeld') AND [ReleasedUtc] IS NOT NULL AND [FailureReasonCode] IS NULL) OR " +
                "([Outcome] = 'Failed' AND [ReleasedUtc] IS NOT NULL AND [FailureReasonCode] IS NOT NULL)");
            table.HasCheckConstraint("CK_CentralTransientPayloadReleaseItems_RetryCount", "[RetryCount] >= 0");
            table.HasCheckConstraint("CK_CentralTransientPayloadReleaseItems_Reservation",
                "(([RequestedAtUtc] IS NULL AND [StorageReference] IS NULL AND [TargetRowVersion] IS NULL AND [TargetGeneration] IS NULL) OR " +
                "([RequestedAtUtc] IS NOT NULL AND [StorageReference] IS NOT NULL AND [TargetRowVersion] IS NOT NULL AND DATALENGTH([TargetRowVersion]) = 8 AND [TargetGeneration] IS NOT NULL)) AND " +
                "([ReservationToken] IS NULL OR ([Outcome] = 'Pending' AND [RequestedAtUtc] IS NOT NULL AND [RetryAtUtc] IS NULL)) AND " +
                "([RetryAtUtc] IS NULL OR ([Outcome] = 'Pending' AND [ReservationToken] IS NULL AND [RequestedAtUtc] IS NOT NULL)) AND " +
                "([Outcome] = 'Pending' OR ([ReservationToken] IS NULL AND [RetryAtUtc] IS NULL))");
        });
        item.HasKey(value => new { value.ReleaseId, value.Ordinal });
        item.Property(value => value.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
        item.Property(value => value.Outcome).HasConversion<string>().HasMaxLength(32).IsRequired();
        item.Property(value => value.StorageReference).HasMaxLength(1024).UseCollation(BinaryCollation);
        item.Property(value => value.TargetRowVersion).HasMaxLength(8);
        item.Property(value => value.RetryCount).HasDefaultValue(0);
        item.Property(value => value.FailureReasonCode).HasMaxLength(128).UseCollation(BinaryCollation);
        item.Property(value => value.RowVersion).IsRowVersion();
        item.HasIndex(value => new { value.ReleaseId, value.Kind, value.RecordId }).IsUnique();
        item.HasIndex(value => new { value.Kind, value.RecordId });
        item.HasIndex(value => new
        {
            value.RetryAtUtc,
            value.RequestedAtUtc,
            value.ReleaseId,
            value.Ordinal
        }).HasFilter("[Outcome] = N'Pending'").IncludeProperties(value => value.ReservationToken);
        item.HasOne(value => value.Release).WithMany(value => value.Items)
            .HasForeignKey(value => value.ReleaseId).OnDelete(DeleteBehavior.Restrict).IsRequired();
    }
}
