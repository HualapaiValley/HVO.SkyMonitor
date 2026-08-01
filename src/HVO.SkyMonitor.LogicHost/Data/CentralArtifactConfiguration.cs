using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class CentralArtifactConfiguration : IEntityTypeConfiguration<CentralArtifact>
{
    public void Configure(EntityTypeBuilder<CentralArtifact> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("CentralArtifacts", table => table.HasCheckConstraint(
            "CK_CentralArtifacts_RetentionDeletion",
            "[RetentionDeletionToken] IS NULL AND [RetentionDeletionRequestedAtUtc] IS NULL AND [RetentionDeletionCompletedAtUtc] IS NULL OR [RetentionDeletionToken] IS NOT NULL AND [RetentionDeletionRequestedAtUtc] IS NOT NULL AND [ObjectState] = 'Expired' AND ([RetentionDeletionCompletedAtUtc] IS NULL OR [RetentionDeletionCompletedAtUtc] >= [RetentionDeletionRequestedAtUtc])"));
        builder.HasKey(artifact => artifact.Id);
        builder.Property(artifact => artifact.Role).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(artifact => artifact.RecipeVersion).HasMaxLength(128).IsRequired();
        builder.Property(artifact => artifact.ManifestSchemaVersion).HasMaxLength(16).IsRequired();
        builder.Property(artifact => artifact.MediaType).HasMaxLength(128).IsRequired();
        builder.Property(artifact => artifact.ChecksumSha256).HasMaxLength(64).IsRequired();
        builder.Property(artifact => artifact.StorageReference).HasMaxLength(512)
            .UseCollation("Latin1_General_100_BIN2").IsRequired();
        builder.Property(artifact => artifact.IdempotencyKey).HasMaxLength(64).IsRequired();
        builder.Property(artifact => artifact.ReceivedAtUtc).IsRequired();
        builder.Property(artifact => artifact.SourceId).HasMaxLength(256);
        builder.Property(artifact => artifact.Variant).HasMaxLength(128);
        builder.Property(artifact => artifact.ObjectState).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(artifact => artifact.ReconstructionState).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(artifact => artifact.StateReasonCode).HasMaxLength(128);
        builder.Property(artifact => artifact.RowVersion).IsRowVersion();
        builder.HasIndex(artifact => artifact.IdempotencyKey).IsUnique();
        builder.HasIndex(artifact => new { artifact.DevicePublicId, artifact.ArtifactId })
            .IsUnique()
            .HasFilter("[DevicePublicId] IS NOT NULL");
        builder.HasIndex(artifact => new { artifact.CentralFrameId, artifact.Role, artifact.RecipeVersion });
        builder.HasIndex(artifact => new { artifact.CentralFrameId, artifact.ArtifactId }).IsUnique();
        builder.HasIndex(artifact => new { artifact.CentralFrameId, artifact.ReceivedAtUtc, artifact.ArtifactId });
        builder.HasIndex(artifact => new
        {
            artifact.ObjectState,
            artifact.ReconstructionState,
            artifact.ReceivedAtUtc
        });
        builder.HasIndex(artifact => new
        {
            artifact.ObjectState,
            artifact.ObjectVerifiedAtUtc,
            artifact.ReceivedAtUtc,
            artifact.Id
        });
        builder.HasIndex(artifact => new
        {
            artifact.ReconstructionState,
            artifact.ReferenceRetryAtUtc,
            artifact.ReceivedAtUtc,
            artifact.Id
        });
        builder.HasIndex(artifact => artifact.StorageReference);
        builder.HasIndex(artifact => new
        {
            artifact.ObjectState,
            artifact.RecoveryGeneration,
            artifact.Id
        });
        builder.HasOne(artifact => artifact.Frame)
            .WithMany(frame => frame.Artifacts)
            .HasForeignKey(artifact => artifact.CentralFrameId)
            .OnDelete(DeleteBehavior.Cascade)
            .IsRequired();
    }
}
