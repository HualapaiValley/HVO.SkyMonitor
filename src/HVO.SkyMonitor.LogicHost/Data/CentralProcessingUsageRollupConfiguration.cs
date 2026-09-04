using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class CentralProcessingUsageRollupConfiguration : IEntityTypeConfiguration<CentralProcessingUsageRollup>
{
    public void Configure(EntityTypeBuilder<CentralProcessingUsageRollup> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("CentralProcessingUsageRollups");
        builder.HasKey(rollup => new { rollup.ObservatoryId, rollup.ResourceClass, rollup.Outcome });
        builder.Property(rollup => rollup.ResourceClass).HasMaxLength(64).IsRequired();
        builder.Property(rollup => rollup.Outcome).HasMaxLength(32).IsRequired();
    }
}
