using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Data;

internal static class CentralReconstructionConfiguration
{
    public static void Configure(ModelBuilder builder)
    {
        var timing = builder.Entity<CentralCaptureTiming>();
        timing.ToTable("CentralCaptureTimings");
        timing.HasKey(item => item.CentralFrameId);
        timing.HasOne(item => item.Frame).WithOne(item => item.Timing)
            .HasForeignKey<CentralCaptureTiming>(item => item.CentralFrameId).OnDelete(DeleteBehavior.Cascade);

        var control = builder.Entity<CentralCaptureControl>();
        control.ToTable("CentralCaptureControls");
        control.HasKey(item => item.CentralFrameId);
        control.HasOne(item => item.Frame).WithOne(item => item.Control)
            .HasForeignKey<CentralCaptureControl>(item => item.CentralFrameId).OnDelete(DeleteBehavior.Cascade);

        var profile = builder.Entity<CentralCaptureProfile>();
        profile.ToTable("CentralCaptureProfiles");
        profile.HasKey(item => item.Id);
        profile.Property(item => item.Kind).HasConversion<string>().HasMaxLength(32);
        profile.Property(item => item.Name).HasMaxLength(128).IsRequired();
        profile.Property(item => item.Version).HasMaxLength(128).IsRequired();
        profile.Property(item => item.Sha256).HasMaxLength(64).IsRequired();
        profile.HasIndex(item => new { item.CentralFrameId, item.Kind }).IsUnique();
        profile.HasOne(item => item.Frame).WithMany(item => item.Profiles)
            .HasForeignKey(item => item.CentralFrameId).OnDelete(DeleteBehavior.Cascade);
        profile.HasOne(item => item.DeviceRigProfile).WithMany()
            .HasForeignKey(item => item.DeviceRigProfileId).OnDelete(DeleteBehavior.Restrict);

        var layout = builder.Entity<CentralArtifactLayout>();
        layout.ToTable("CentralArtifactLayouts");
        layout.HasKey(item => item.CentralArtifactId);
        layout.Property(item => item.PixelFormat).HasMaxLength(32).IsRequired();
        layout.Property(item => item.ByteOrder).HasMaxLength(32).IsRequired();
        layout.Property(item => item.Packing).HasMaxLength(32).IsRequired();
        layout.Property(item => item.CfaPattern).HasMaxLength(32).IsRequired();
        layout.Property(item => item.StoredCodeTransform).HasMaxLength(64);
        layout.Property(item => item.LevelCodeSpace).HasMaxLength(32);
        layout.Property(item => item.BinningAlgorithm).HasMaxLength(64);
        layout.HasOne(item => item.Artifact).WithOne(item => item.Layout)
            .HasForeignKey<CentralArtifactLayout>(item => item.CentralArtifactId).OnDelete(DeleteBehavior.Cascade);

        var recipe = builder.Entity<CentralArtifactRecipe>();
        recipe.ToTable("CentralArtifactRecipes");
        recipe.HasKey(item => item.CentralArtifactId);
        recipe.Property(item => item.Name).HasMaxLength(128).IsRequired();
        recipe.Property(item => item.SemanticVersion).HasMaxLength(64).IsRequired();
        recipe.Property(item => item.ImplementationVersion).HasMaxLength(128).IsRequired();
        recipe.Property(item => item.OptionsJson).IsRequired();
        recipe.Property(item => item.OptionsSha256).HasMaxLength(64).IsRequired();
        recipe.HasOne(item => item.Artifact).WithOne(item => item.Recipe)
            .HasForeignKey<CentralArtifactRecipe>(item => item.CentralArtifactId).OnDelete(DeleteBehavior.Cascade);

        var source = builder.Entity<CentralArtifactSource>();
        source.ToTable("CentralArtifactSources");
        source.HasKey(item => item.Id);
        source.HasIndex(item => new { item.CentralArtifactId, item.Ordinal }).IsUnique();
        source.HasIndex(item => new { item.CentralArtifactId, item.SourceArtifactId }).IsUnique();
        source.HasOne(item => item.Artifact).WithMany(item => item.Sources)
            .HasForeignKey(item => item.CentralArtifactId).OnDelete(DeleteBehavior.Cascade);
        source.HasOne(item => item.ResolvedArtifact).WithMany()
            .HasForeignKey(item => item.ResolvedCentralArtifactId).OnDelete(DeleteBehavior.Restrict);

        var identity = builder.Entity<CentralArtifactIngestIdentity>();
        identity.ToTable("CentralArtifactIngestIdentities");
        identity.HasKey(item => item.Id);
        identity.Property(item => item.ManifestSchemaVersion).HasMaxLength(16).IsRequired();
        identity.Property(item => item.IdempotencyKey).HasMaxLength(64).IsRequired();
        identity.HasIndex(item => item.IdempotencyKey).IsUnique();
        identity.HasIndex(item => new { item.CentralArtifactId, item.ManifestSchemaVersion }).IsUnique();
        identity.HasOne(item => item.Artifact).WithMany(item => item.IngestIdentities)
            .HasForeignKey(item => item.CentralArtifactId).OnDelete(DeleteBehavior.Cascade);
    }
}
