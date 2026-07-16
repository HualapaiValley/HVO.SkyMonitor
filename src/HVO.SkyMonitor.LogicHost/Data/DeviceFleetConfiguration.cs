using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HVO.SkyMonitor.LogicHost.Data;

internal static class DeviceFleetConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ConfigureCurrent(modelBuilder.Entity<DeviceFleetState>());
        ConfigureHistory(modelBuilder.Entity<DeviceHeartbeatRecord>());
    }

    private static void ConfigureCurrent(EntityTypeBuilder<DeviceFleetState> builder)
    {
        builder.ToTable("DeviceFleetStates", table =>
            table.HasCheckConstraint("CK_DeviceFleetStates_Sequence", "[Sequence] > 0"));
        builder.HasKey(state => state.RegistrationId);
        builder.Property(state => state.Sequence).IsRequired();
        builder.Property(state => state.SoftwareVersion).HasMaxLength(64).IsRequired();
        builder.Property(state => state.ConfigurationSha256).HasMaxLength(64).IsRequired();
        builder.Property(state => state.StatusFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(state => state.CurrentPayloadSha256).HasMaxLength(64).IsRequired();
        builder.Property(state => state.SnapshotJson).HasMaxLength(65_536).IsRequired();
        builder.Property(state => state.ClockDiagnostic).HasConversion<string>().HasMaxLength(48).IsRequired();
        builder.Property(state => state.ReportedHealth).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(state => state.RowVersion).IsRowVersion();
        builder.HasIndex(state => state.AgentInstanceId).IsUnique();
        builder.HasIndex(state => state.ReceivedAtUtc);
        builder.HasIndex(state => new { state.ReportedHealth, state.ReceivedAtUtc });
        builder.HasOne(state => state.Registration)
            .WithOne()
            .HasForeignKey<DeviceFleetState>(state => state.RegistrationId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureHistory(EntityTypeBuilder<DeviceHeartbeatRecord> builder)
    {
        builder.ToTable("DeviceHeartbeatRecords", table =>
            table.HasCheckConstraint("CK_DeviceHeartbeatRecords_Sequence", "[Sequence] > 0"));
        builder.HasKey(record => record.Id);
        builder.Property(record => record.Id).ValueGeneratedOnAdd();
        builder.Property(record => record.PayloadSha256).HasMaxLength(64).IsRequired();
        builder.Property(record => record.StatusFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(record => record.ReportedHealth).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(record => record.ClockDiagnostic).HasConversion<string>().HasMaxLength(48).IsRequired();
        builder.Property(record => record.SnapshotJson).HasMaxLength(65_536);
        builder.HasIndex(record => new { record.RegistrationId, record.AgentInstanceId, record.Sequence }).IsUnique();
        builder.HasIndex(record => new { record.RegistrationId, record.ReceivedAtUtc });
        builder.HasIndex(record => new { record.IsSignificantSnapshot, record.ReceivedAtUtc, record.Id });
        builder.HasOne(record => record.Registration)
            .WithMany()
            .HasForeignKey(record => record.RegistrationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
