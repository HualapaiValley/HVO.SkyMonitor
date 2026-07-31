using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Data;

internal static class CentralProcessingOverrideConfiguration
{
    internal static void Configure(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralProcessingOverrideVersion>();
        entity.ToTable("CentralProcessingOverrideVersions", table =>
        {
            table.HasTrigger("TR_CentralProcessingOverrideVersions_Transitions");
            table.HasCheckConstraint("CK_CentralProcessingOverrideVersions_Version", "[Version] > 0");
            table.HasCheckConstraint(
                "CK_CentralProcessingOverrideVersions_Threshold",
                "[CloudTransmissionThresholdMillionths] IS NULL OR " +
                "[CloudTransmissionThresholdMillionths] BETWEEN 1 AND 999999");
        });
        entity.HasKey(item => item.Id);
        entity.Property(item => item.ActorUserId).HasMaxLength(450).IsRequired();
        entity.Property(item => item.ReasonCode).HasMaxLength(128).IsRequired();
        entity.HasIndex(item => new { item.ObservatoryId, item.Version }).IsUnique();
        entity.HasIndex(item => item.ObservatoryId).IsUnique()
            .HasFilter("[SupersededAtUtc] IS NULL");
        entity.HasOne(item => item.Observatory).WithMany()
            .HasForeignKey(item => item.ObservatoryId).OnDelete(DeleteBehavior.Restrict).IsRequired();
    }
}
