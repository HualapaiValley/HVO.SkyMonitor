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
            table.HasCheckConstraint(
                "CK_CentralDerivativeJobInputRequirements_GraphBinding",
                "([GraphDependencyId] IS NULL AND [GraphInputOrdinal] IS NULL AND [GraphInputBindingKind] IS NULL) OR " +
                "([GraphDependencyId] IS NOT NULL AND [GraphInputOrdinal] >= 0 AND [GraphInputBindingKind] IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_CentralDerivativeJobInputRequirements_GraphInputBindingKind",
                "[GraphInputBindingKind] IS NULL OR [GraphInputBindingKind] IN " +
                "(N'PrimaryArtifact', N'AuxiliaryArtifact', N'CanonicalJson', N'Annotation')");
            table.HasCheckConstraint(
                "CK_CentralDerivativeJobInputRequirements_GraphResolution",
                "[GraphDependencyId] IS NULL OR " +
                "([ResolutionState] = N'Waiting' AND [ExpectedCentralArtifactId] IS NULL AND [ResolvedAtUtc] IS NULL) OR " +
                "([ResolutionState] = N'Resolved' AND [ResolvedAtUtc] IS NOT NULL AND " +
                "(([SourceKind] = N'Artifact' AND [ExpectedCentralArtifactId] IS NOT NULL) OR " +
                "([SourceKind] <> N'Artifact' AND [ExpectedCentralArtifactId] IS NULL))) OR " +
                "([ResolutionState] IN (N'Missing', N'Incompatible') AND " +
                "[ExpectedCentralArtifactId] IS NULL AND [ResolvedAtUtc] IS NOT NULL AND LEN([ResolutionReasonCode]) > 0)");
            table.HasTrigger("TR_CentralDerivativeJobInputRequirements_GraphBindingImmutable");
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
        requirement.Property(item => item.GraphInputBindingKind).HasConversion<string>().HasMaxLength(32);
        requirement.HasAlternateKey(item => new { item.CentralDerivativeJobId, item.Id });
        requirement.HasIndex(item => new { item.CentralDerivativeJobId, item.Ordinal }).IsUnique();
        requirement.HasIndex(item => new
        {
            item.ExpectedAgentId,
            item.ExpectedCaptureSequence,
            item.ResolutionState,
            item.CentralDerivativeJobId
        });
        requirement.HasIndex(item => item.ExpectedCentralArtifactId);
        requirement.HasIndex(item => new { item.CentralDerivativeJobId, item.GraphDependencyId })
            .HasFilter("[GraphDependencyId] IS NOT NULL");
        requirement.HasOne(item => item.Job).WithMany(job => job.InputRequirements)
            .HasForeignKey(item => item.CentralDerivativeJobId).OnDelete(DeleteBehavior.Cascade).IsRequired();
        requirement.HasOne(item => item.ExpectedArtifact).WithMany()
            .HasForeignKey(item => item.ExpectedCentralArtifactId).OnDelete(DeleteBehavior.Restrict);
        requirement.HasOne(item => item.GraphDependency).WithMany(item => item.InputRequirements)
            .HasForeignKey(item => new { item.CentralDerivativeJobId, item.GraphDependencyId })
            .HasPrincipalKey(item => new { item.ConsumerJobId, item.Id })
            .OnDelete(DeleteBehavior.NoAction);
    }

    private static void ConfigureInput(ModelBuilder builder)
    {
        var input = builder.Entity<CentralDerivativeJobInput>();
        input.ToTable("CentralDerivativeJobInputs", table =>
        {
            table.HasTrigger("TR_CentralDerivativeJobInputs_GraphImmutable");
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
