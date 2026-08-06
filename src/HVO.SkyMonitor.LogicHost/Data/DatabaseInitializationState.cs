using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HVO.SkyMonitor.LogicHost.Data;

internal enum DatabaseInitializationStatus
{
    Running,
    Completed,
    Failed
}

internal sealed class DatabaseInitializationState
{
    internal const byte SingletonId = 1;

    public byte Id { get; set; } = SingletonId;
    public int InitializationVersion { get; set; }
    public DatabaseInitializationStatus Status { get; set; }
    public Guid AttemptId { get; set; }
    public string TargetMigrationId { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string? FailureStage { get; set; }
}

internal sealed class DatabaseInitializationStateConfiguration : IEntityTypeConfiguration<DatabaseInitializationState>
{
    public void Configure(EntityTypeBuilder<DatabaseInitializationState> entity)
    {
        entity.ToTable("DatabaseInitializationState", table =>
        {
            table.HasCheckConstraint("CK_DatabaseInitializationState_Singleton", "[Id] = 1");
            table.HasCheckConstraint("CK_DatabaseInitializationState_Version", "[InitializationVersion] > 0");
            table.HasCheckConstraint(
                "CK_DatabaseInitializationState_Status",
                "[Status] IN (N'Running', N'Completed', N'Failed')");
            table.HasCheckConstraint(
                "CK_DatabaseInitializationState_Completion",
                "([Status] = N'Completed' AND [CompletedAtUtc] IS NOT NULL AND [FailureStage] IS NULL) OR " +
                "([Status] <> N'Completed' AND [CompletedAtUtc] IS NULL)");
        });
        entity.HasKey(state => state.Id);
        entity.Property(state => state.Id).ValueGeneratedNever();
        entity.Property(state => state.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        entity.Property(state => state.TargetMigrationId).HasMaxLength(150).IsRequired();
        entity.Property(state => state.FailureStage).HasMaxLength(32);
    }
}
