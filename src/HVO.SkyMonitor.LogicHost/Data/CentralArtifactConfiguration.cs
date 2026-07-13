using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class CentralArtifactConfiguration : IEntityTypeConfiguration<CentralArtifact>
{
    public void Configure(EntityTypeBuilder<CentralArtifact> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("CentralArtifacts");
        builder.HasKey(artifact => artifact.Id);
        builder.Property(artifact => artifact.Role).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(artifact => artifact.RecipeVersion).HasMaxLength(128).IsRequired();
        builder.Property(artifact => artifact.ManifestSchemaVersion).HasMaxLength(16).IsRequired();
        builder.Property(artifact => artifact.MediaType).HasMaxLength(128).IsRequired();
        builder.Property(artifact => artifact.ChecksumSha256).HasMaxLength(64).IsRequired();
        builder.Property(artifact => artifact.StorageReference).HasMaxLength(512).IsRequired();
        builder.Property(artifact => artifact.IdempotencyKey).HasMaxLength(64).IsRequired();
        builder.Property(artifact => artifact.ReceivedAtUtc).IsRequired();
        builder.HasIndex(artifact => artifact.IdempotencyKey).IsUnique();
        builder.HasIndex(artifact => new { artifact.CentralFrameId, artifact.Role, artifact.RecipeVersion }).IsUnique();
        builder.HasIndex(artifact => new { artifact.CentralFrameId, artifact.ArtifactId }).IsUnique();
        builder.HasOne(artifact => artifact.Frame)
            .WithMany(frame => frame.Artifacts)
            .HasForeignKey(artifact => artifact.CentralFrameId)
            .OnDelete(DeleteBehavior.Cascade)
            .IsRequired();
    }
}
