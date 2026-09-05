using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class CentralElasticRunnerInstanceConfiguration : IEntityTypeConfiguration<CentralElasticRunnerInstance>
{
    public void Configure(EntityTypeBuilder<CentralElasticRunnerInstance> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("CentralElasticRunnerInstances", table => table.HasCheckConstraint(
            "CK_CentralElasticRunnerInstances_State",
            "[State] IN (N'Starting', N'Running', N'Stopping', N'Stopped', N'Orphaned', N'Abandoned')"));
        builder.HasKey(instance => instance.Id);
        builder.Property(instance => instance.Provider).HasMaxLength(64).IsRequired();
        builder.Property(instance => instance.HostName).HasMaxLength(256).IsRequired();
        builder.Property(instance => instance.InstanceId).HasMaxLength(128).IsRequired();
        // Runner ids are case-sensitive protocol identifiers: the same binary collation as CentralProcessingRunners.
        builder.Property(instance => instance.RunnerId).HasMaxLength(128).IsRequired().UseCollation("Latin1_General_100_BIN2");
        builder.Property(instance => instance.ProcessArchitecture).HasMaxLength(32).IsRequired();
        builder.Property(instance => instance.RuntimeImage).HasMaxLength(256).IsRequired();
        builder.Property(instance => instance.State).HasMaxLength(32).IsRequired();
        builder.Property(instance => instance.Reason).HasMaxLength(256);
        builder.HasIndex(instance => instance.InstanceId).IsUnique();
        builder.HasIndex(instance => new { instance.Provider, instance.State });
        builder.HasIndex(instance => instance.StartedAtUtc);
    }
}
