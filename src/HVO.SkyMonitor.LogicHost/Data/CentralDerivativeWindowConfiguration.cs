using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Data;

internal static class CentralDerivativeWindowConfiguration
{
    public static void Configure(ModelBuilder builder)
    {
        ConfigureRequirement(builder);
        ConfigureInput(builder);
    }

    private static void ConfigureRequirement(ModelBuilder builder)
    {
        var requirement = builder.Entity<CentralDerivativeJobInputRequirement>();
        requirement.ToTable("CentralDerivativeJobInputRequirements", table =>
        {
            table.HasCheckConstraint("CK_CentralDerivativeJobInputRequirements_Ordinal", "[Ordinal] >= 0");
        });
        requirement.HasKey(item => item.Id);
        requirement.Property(item => item.BindingName).HasMaxLength(128).IsRequired();
        requirement.Property(item => item.SourceKind).HasConversion<string>().HasMaxLength(32).IsRequired();
        requirement.Property(item => item.SelectorJson).HasMaxLength(2048).IsRequired();
        requirement.Property(item => item.CompatibilityMode).HasConversion<string>().HasMaxLength(32).IsRequired();
        requirement.Property(item => item.ExpectedAgentId).HasMaxLength(128).IsRequired();
        requirement.Property(item => item.ExpectedRigId).HasMaxLength(128);
        requirement.Property(item => item.ResolutionState).HasConversion<string>().HasMaxLength(32).IsRequired();
        requirement.Property(item => item.ResolutionReasonCode).HasMaxLength(256);
        requirement.HasAlternateKey(item => new { item.CentralDerivativeJobId, item.Id });
        requirement.HasIndex(item => new { item.CentralDerivativeJobId, item.Ordinal }).IsUnique();
        requirement.HasIndex(item => new
        {
            item.ExpectedAgentId,
            item.ExpectedCaptureSequence,
            item.ResolutionState,
            item.CentralDerivativeJobId
        });
        requirement.HasOne(item => item.Job).WithMany(job => job.InputRequirements)
            .HasForeignKey(item => item.CentralDerivativeJobId).OnDelete(DeleteBehavior.Cascade).IsRequired();
    }

    private static void ConfigureInput(ModelBuilder builder)
    {
        var input = builder.Entity<CentralDerivativeJobInput>();
        input.ToTable("CentralDerivativeJobInputs", table =>
        {
            table.HasCheckConstraint("CK_CentralDerivativeJobInputs_Ordinal", "[Ordinal] >= 0");
            table.HasCheckConstraint("CK_CentralDerivativeJobInputs_ByteLength", "[ByteLength] >= 0");
        });
        input.HasKey(item => item.Id);
        input.Property(item => item.CompatibilityJson).IsRequired();
        input.Property(item => item.CompatibilitySha256).HasMaxLength(64).IsUnicode(false).IsRequired();
        input.HasIndex(item => new { item.CentralDerivativeJobId, item.Ordinal }).IsUnique();
        input.HasIndex(item => new { item.CentralDerivativeJobId, item.CentralArtifactId }).IsUnique();
        input.HasIndex(item => new { item.CentralArtifactId, item.CentralDerivativeJobId });
        input.HasOne(item => item.Job).WithMany(job => job.Inputs)
            .HasForeignKey(item => item.CentralDerivativeJobId).OnDelete(DeleteBehavior.Cascade).IsRequired();
        input.HasOne(item => item.Requirement).WithOne(requirement => requirement.Input)
            .HasForeignKey<CentralDerivativeJobInput>(item => new
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
        input.HasOne(item => item.Artifact).WithMany()
            .HasForeignKey(item => item.CentralArtifactId).OnDelete(DeleteBehavior.Restrict).IsRequired();
    }
}
