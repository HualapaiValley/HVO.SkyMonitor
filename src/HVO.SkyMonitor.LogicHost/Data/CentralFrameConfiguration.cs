using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class CentralFrameConfiguration : IEntityTypeConfiguration<CentralFrame>
{
    public void Configure(EntityTypeBuilder<CentralFrame> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("CentralFrames");
        builder.HasKey(frame => frame.Id);
        builder.Property(frame => frame.AgentId).HasMaxLength(128).IsRequired();
        builder.Property(frame => frame.CapturedAtUtc).IsRequired();
        builder.Property(frame => frame.FirstReceivedAtUtc).IsRequired();
        builder.Property(frame => frame.SceneProvenanceJson);
        builder.Property(frame => frame.RigId).HasMaxLength(128);
        builder.Property(frame => frame.CycleEvidenceJson);
        builder.HasIndex(frame => new { frame.DevicePublicId, frame.FrameId }).IsUnique();
        builder.HasIndex(frame => new { frame.DevicePublicId, frame.CaptureSequence })
            .IsUnique().HasFilter("[CaptureSequence] IS NOT NULL");
        builder.HasIndex(frame => new { frame.DevicePublicId, frame.CapturedAtUtc, frame.FrameId });
        builder.HasIndex(frame => new { frame.AgentId, frame.CapturedAtUtc });
        builder.HasOne(frame => frame.DeviceRigProfile).WithMany()
            .HasForeignKey(frame => frame.DeviceRigProfileId).OnDelete(DeleteBehavior.Restrict);
        // Registration and observatory IDs are historical snapshots. Their source rows may be retired.
    }
}
