using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class CentralProcessingRunnerConfiguration : IEntityTypeConfiguration<CentralProcessingRunner>
{
    public void Configure(EntityTypeBuilder<CentralProcessingRunner> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("CentralProcessingRunners", table =>
        {
            table.HasCheckConstraint(
                "CK_CentralProcessingRunners_Status",
                "[Status] IN (N'Active', N'Stale', N'Retired')");
            table.HasCheckConstraint(
                "CK_CentralProcessingRunners_WarmState",
                "[WarmState] IN (N'Cold', N'Warming', N'Warm', N'Degraded')");
            table.HasCheckConstraint(
                "CK_CentralProcessingRunners_Capacity",
                "[MaxConcurrency] >= 1 AND [MaxConcurrency] <= 32 AND [AvailableSlots] >= 0 AND " +
                "[AvailableSlots] <= [MaxConcurrency] AND [MaxTransferBytes] >= 1 AND [ProcessorCount] >= 1 AND " +
                "[TotalMemoryBytes] >= 0 AND [Generation] >= 1");
            table.HasCheckConstraint(
                "CK_CentralProcessingRunners_Timestamps",
                "[UpdatedAtUtc] >= [RegisteredAtUtc] AND [LastHeartbeatAtUtc] >= [RegisteredAtUtc] AND " +
                "(([Status] = N'Retired' AND [RetiredAtUtc] IS NOT NULL) OR ([Status] <> N'Retired' AND [RetiredAtUtc] IS NULL))");
        });
        builder.HasKey(runner => runner.Id);
        builder.Property(runner => runner.RunnerId).HasMaxLength(128).IsRequired().UseCollation("Latin1_General_100_BIN2");
        builder.Property(runner => runner.ClientSubject).HasMaxLength(256).IsRequired();
        builder.Property(runner => runner.DisplayName).HasMaxLength(256).IsRequired();
        builder.Property(runner => runner.OperatingSystem).HasMaxLength(256).IsRequired();
        builder.Property(runner => runner.OsArchitecture).HasMaxLength(32).IsRequired();
        builder.Property(runner => runner.ProcessArchitecture).HasMaxLength(32).IsRequired();
        builder.Property(runner => runner.RuntimeIdentifier).HasMaxLength(64).IsRequired();
        builder.Property(runner => runner.FrameworkDescription).HasMaxLength(128).IsRequired();
        builder.Property(runner => runner.ResourceClass).HasMaxLength(64).IsRequired();
        builder.Property(runner => runner.LatencyClass).HasMaxLength(64).IsRequired();
        builder.Property(runner => runner.CapabilitiesJson).IsRequired();
        builder.Property(runner => runner.EligibleRecipesJson).HasMaxLength(4000).IsRequired();
        builder.Property(runner => runner.WarmState).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(runner => runner.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(runner => runner.RowVersion).IsRowVersion();
        builder.HasIndex(runner => runner.RunnerId).IsUnique();
        builder.HasIndex(runner => new { runner.Status, runner.LastHeartbeatAtUtc });
        builder.HasIndex(runner => runner.ClientSubject);
    }
}
