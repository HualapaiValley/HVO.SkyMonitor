using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class CentralProcessingUsageRecordConfiguration : IEntityTypeConfiguration<CentralProcessingUsageRecord>
{
    public void Configure(EntityTypeBuilder<CentralProcessingUsageRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("CentralProcessingUsageRecords", table =>
        {
            table.HasCheckConstraint(
                "CK_CentralProcessingUsageRecords_Counters",
                "[AttemptNumber] >= 1 AND [InputBytes] >= 0 AND [OutputBytes] >= 0 AND [RecipeDurationTicks] >= 0 AND [EndedAtUtc] >= [LeaseAcquiredAtUtc]");
            table.HasCheckConstraint(
                "CK_CentralProcessingUsageRecords_Outcome",
                "[Outcome] IN (N'Completed', N'RetryableFailure', N'TerminalFailure', N'LeaseExpired', N'Canceled', N'Skipped', N'Quarantined', N'Superseded')");
        });
        builder.HasKey(record => record.Id);
        builder.Property(record => record.RecipeName).HasMaxLength(128).IsRequired();
        builder.Property(record => record.ResourceClass).HasMaxLength(64).IsRequired();
        builder.Property(record => record.WorkerId).HasMaxLength(256).IsRequired();
        builder.Property(record => record.Outcome).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(record => record.ReasonCode).HasMaxLength(256);
        builder.HasIndex(record => new { record.CentralDerivativeJobId, record.AttemptNumber }).IsUnique();
        builder.HasIndex(record => record.RecordedAtUtc);
        builder.HasIndex(record => record.SignaledAtUtc).HasFilter("[SignaledAtUtc] IS NULL");
        builder.HasIndex(record => new { record.ObservatoryId, record.EndedAtUtc });
        builder.HasIndex(record => new { record.DevicePublicId, record.EndedAtUtc });
    }
}
