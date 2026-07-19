using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Data;

internal static class CentralCloudProcessingConfiguration
{
    public static void Configure(ModelBuilder builder)
    {
        ConfigureDesignation(builder);
        ConfigureCanonicalInput(builder);
    }

    private static void ConfigureDesignation(ModelBuilder builder)
    {
        var designation = builder.Entity<CentralClearReferenceDesignation>();
        designation.ToTable("CentralClearReferenceDesignations");
        designation.HasKey(item => item.Id);
        designation.Property(item => item.RigId).HasMaxLength(128)
            .UseCollation("Latin1_General_100_BIN2").IsRequired();
        designation.Property(item => item.UpdatedBy).HasMaxLength(256).IsRequired();
        designation.Property(item => item.RowVersion).IsRowVersion();
        designation.HasIndex(item => new { item.RegistrationId, item.RigId }).IsUnique();
        designation.HasIndex(item => item.CentralArtifactId);
        designation.HasOne(item => item.Registration).WithMany()
            .HasForeignKey(item => item.RegistrationId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        designation.HasOne(item => item.Artifact).WithMany()
            .HasForeignKey(item => item.CentralArtifactId).OnDelete(DeleteBehavior.Restrict).IsRequired();
    }

    private static void ConfigureCanonicalInput(ModelBuilder builder)
    {
        var input = builder.Entity<CentralDerivativeJobCanonicalInput>();
        input.ToTable("CentralDerivativeJobCanonicalInputs", table =>
        {
            table.HasCheckConstraint("CK_CentralDerivativeJobCanonicalInputs_Ordinal", "[Ordinal] >= 0");
            table.HasCheckConstraint("CK_CentralDerivativeJobCanonicalInputs_ByteLength", "[ByteLength] > 0");
        });
        input.HasKey(item => item.Id);
        input.Property(item => item.SchemaVersion).HasMaxLength(128).IsRequired();
        input.Property(item => item.IdentitySha256).HasMaxLength(64).IsUnicode(false).IsRequired();
        input.Property(item => item.CanonicalJson).IsRequired();
        input.HasIndex(item => new { item.CentralDerivativeJobId, item.Ordinal }).IsUnique();
        input.HasIndex(item => new { item.CentralDerivativeJobId, item.CentralDerivativeJobInputRequirementId }).IsUnique();
        input.HasIndex(item => item.EnvironmentalObservationRecordId);
        input.HasOne(item => item.Job).WithMany(job => job.CanonicalInputs)
            .HasForeignKey(item => item.CentralDerivativeJobId).OnDelete(DeleteBehavior.Cascade).IsRequired();
        input.HasOne(item => item.Requirement).WithOne(requirement => requirement.CanonicalInput)
            .HasForeignKey<CentralDerivativeJobCanonicalInput>(item => new
            {
                item.CentralDerivativeJobId,
                item.CentralDerivativeJobInputRequirementId
            })
            .HasPrincipalKey<CentralDerivativeJobInputRequirement>(item => new
            {
                item.CentralDerivativeJobId,
                item.Id
            })
            .OnDelete(DeleteBehavior.NoAction).IsRequired();
        input.HasOne(item => item.EnvironmentalObservation).WithMany()
            .HasForeignKey(item => item.EnvironmentalObservationRecordId).OnDelete(DeleteBehavior.Restrict);
    }
}
