using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class CentralDerivativeJobConfiguration : IEntityTypeConfiguration<CentralDerivativeJob>
{
    public void Configure(EntityTypeBuilder<CentralDerivativeJob> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("CentralDerivativeJobs", table =>
        {
            table.HasCheckConstraint("CK_CentralDerivativeJobs_AttemptCount", "[AttemptCount] >= 0 AND [AttemptCount] <= [MaxAttempts]");
            table.HasCheckConstraint("CK_CentralDerivativeJobs_MaxAttempts", "[MaxAttempts] > 0");
        });
        builder.HasKey(job => job.Id);
        builder.Property(job => job.TargetRole).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(job => job.TargetRecipeVersion).HasMaxLength(128).IsRequired();
        builder.Property(job => job.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(job => job.LeaseOwner).HasMaxLength(256);
        builder.Property(job => job.LastError).HasMaxLength(2048);
        builder.Property(job => job.RowVersion).IsRowVersion();
        builder.HasIndex(job => new { job.SourceCentralArtifactId, job.TargetRole, job.TargetRecipeVersion }).IsUnique();
        builder.HasIndex(job => new { job.Status, job.AvailableAtUtc, job.CreatedAtUtc, job.Id });
        builder.HasIndex(job => new { job.Status, job.LeaseExpiresAtUtc, job.CreatedAtUtc, job.Id });
        builder.HasIndex(job => new { job.CreatedAtUtc, job.Id });
        builder.HasIndex(job => job.ResultCentralArtifactId);
        builder.HasOne(job => job.SourceArtifact)
            .WithMany()
            .HasForeignKey(job => job.SourceCentralArtifactId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired();
        builder.HasOne(job => job.ResultArtifact)
            .WithMany()
            .HasForeignKey(job => job.ResultCentralArtifactId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
