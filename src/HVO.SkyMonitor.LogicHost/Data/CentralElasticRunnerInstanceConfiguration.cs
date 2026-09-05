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
            "[State] IN (N'Starting', N'Running', N'Stopping', N'Stopped', N'Orphaned')"));
        builder.HasKey(instance => instance.Id);
        builder.Property(instance => instance.Provider).HasMaxLength(64).IsRequired();
        builder.Property(instance => instance.InstanceId).HasMaxLength(128).IsRequired();
        builder.Property(instance => instance.RunnerId).HasMaxLength(128).IsRequired();
        builder.Property(instance => instance.ProcessArchitecture).HasMaxLength(32).IsRequired();
        builder.Property(instance => instance.RuntimeImage).HasMaxLength(256).IsRequired();
        builder.Property(instance => instance.State).HasMaxLength(32).IsRequired();
        builder.Property(instance => instance.Reason).HasMaxLength(256);
        builder.HasIndex(instance => instance.InstanceId).IsUnique();
        builder.HasIndex(instance => new { instance.Provider, instance.State });
        builder.HasIndex(instance => instance.StartedAtUtc);
    }
}
