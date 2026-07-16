using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Data;

internal static class CentralDerivativeExecutionConfiguration
{
    public static void Configure(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var attempt = builder.Entity<CentralDerivativeJobAttempt>();
        attempt.ToTable("CentralDerivativeJobAttempts", table =>
        {
            table.HasCheckConstraint("CK_CentralDerivativeJobAttempts_AttemptNumber", "[AttemptNumber] > 0");
            table.HasCheckConstraint("CK_CentralDerivativeJobAttempts_Bytes", "[InputBytes] >= 0 AND [OutputBytes] >= 0");
            table.HasCheckConstraint("CK_CentralDerivativeJobAttempts_RecipeDurationTicks", "[RecipeDurationTicks] >= 0");
        });
        attempt.HasKey(item => item.Id);
        attempt.Property(item => item.WorkerId).HasMaxLength(256).IsRequired();
        attempt.Property(item => item.Outcome).HasConversion<string>().HasMaxLength(32).IsRequired();
        attempt.Property(item => item.ReasonCode).HasMaxLength(256);
        attempt.HasIndex(item => new { item.CentralDerivativeJobId, item.AttemptNumber }).IsUnique();
        attempt.HasIndex(item => new { item.Outcome, item.LeaseExpiresAtUtc });
        attempt.HasOne(item => item.Job)
            .WithMany(job => job.Attempts)
            .HasForeignKey(item => item.CentralDerivativeJobId)
            .OnDelete(DeleteBehavior.Cascade)
            .IsRequired();

        var evidence = builder.Entity<CentralArtifactProcessingEvidence>();
        evidence.ToTable("CentralArtifactProcessingEvidence", table =>
        {
            table.HasCheckConstraint("CK_CentralArtifactProcessingEvidence_AttemptNumber", "[AttemptNumber] > 0");
            table.HasCheckConstraint("CK_CentralArtifactProcessingEvidence_TotalIntegrationTicks", "[TotalIntegrationTicks] >= 0");
        });
        evidence.HasKey(item => item.CentralArtifactId);
        evidence.Property(item => item.OutputIdentitySha256).HasMaxLength(64).IsUnicode(false).IsRequired();
        evidence.Property(item => item.RequestedRecipeIdentitySha256).HasMaxLength(64).IsUnicode(false).IsRequired();
        evidence.Property(item => item.RecipeIdentitySha256).HasMaxLength(64).IsUnicode(false).IsRequired();
        evidence.Property(item => item.AlgorithmsJson).IsRequired();
        evidence.Property(item => item.CompatibilityJson).IsRequired();
        evidence.HasIndex(item => new { item.DevicePublicId, item.OutputIdentitySha256 }).IsUnique();
        evidence.HasIndex(item => item.CentralDerivativeJobId);
        evidence.HasOne(item => item.Artifact)
            .WithOne()
            .HasForeignKey<CentralArtifactProcessingEvidence>(item => item.CentralArtifactId)
            .OnDelete(DeleteBehavior.Cascade)
            .IsRequired();
        evidence.HasOne(item => item.Job)
            .WithMany()
            .HasForeignKey(item => item.CentralDerivativeJobId)
            .OnDelete(DeleteBehavior.NoAction)
            .IsRequired();
    }
}
