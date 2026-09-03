using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Data;

internal static class ProcessingGraphCatalogConfiguration
{
    internal static void Configure(ModelBuilder builder)
    {
        ConfigureRevisions(builder);
        ConfigureAssignments(builder);
        ConfigureProposals(builder);
        ConfigureFacts(builder);
    }

    private static void ConfigureRevisions(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralProcessingGraphRevision>();
        entity.ToTable("CentralProcessingGraphRevisions", table =>
        {
            table.HasTrigger("TR_CentralProcessingGraphRevisions_Transitions");
            table.HasCheckConstraint(
                "CK_CentralProcessingGraphRevisions_Lifecycle",
                "([PublishedAtUtc] IS NULL AND [PublishedByUserId] IS NULL AND [RetiredAtUtc] IS NULL AND " +
                "[RetiredByUserId] IS NULL AND [RetirementReasonCode] IS NULL) OR " +
                "([PublishedAtUtc] IS NOT NULL AND [PublishedByUserId] IS NOT NULL AND " +
                "(([RetiredAtUtc] IS NULL AND [RetiredByUserId] IS NULL AND [RetirementReasonCode] IS NULL) OR " +
                "([RetiredAtUtc] >= [PublishedAtUtc] AND [RetiredByUserId] IS NOT NULL AND [RetirementReasonCode] IS NOT NULL)))");
        });
        entity.HasKey(item => item.Id);
        entity.Ignore(item => item.Lifecycle);
        entity.Property(item => item.Name).HasMaxLength(128).IsRequired();
        entity.Property(item => item.Revision).HasMaxLength(128).IsRequired();
        entity.Property(item => item.DefinitionJson).HasColumnType("nvarchar(max)").IsRequired();
        ConfigureRequiredSha256(entity.Property(item => item.DefinitionIdentitySha256));
        ConfigureRequiredSha256(entity.Property(item => item.PortablePlanIdentitySha256));
        ConfigureOptionalSha256(entity.Property(item => item.EdgePlanIdentitySha256));
        ConfigureOptionalSha256(entity.Property(item => item.CentralPlanIdentitySha256));
        entity.Property(item => item.CreatedByUserId).HasMaxLength(450).IsRequired();
        entity.Property(item => item.PublishedByUserId).HasMaxLength(450);
        entity.Property(item => item.RetiredByUserId).HasMaxLength(450);
        entity.Property(item => item.RetirementReasonCode).HasMaxLength(128);
        entity.HasIndex(item => new { item.Name, item.Revision }).IsUnique();
        entity.HasIndex(item => item.DefinitionIdentitySha256).IsUnique();
    }

    private static void ConfigureAssignments(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralProcessingGraphAssignment>();
        entity.ToTable("CentralProcessingGraphAssignments", table =>
        {
            table.HasTrigger("TR_CentralProcessingGraphAssignments_Immutable");
            table.HasCheckConstraint(
                "CK_CentralProcessingGraphAssignments_Scope",
                "([Scope] = N'GlobalDefault' AND [ObservatoryId] IS NULL AND [LogicalCameraId] IS NULL) OR " +
                "([Scope] = N'Observatory' AND [ObservatoryId] IS NOT NULL AND [LogicalCameraId] IS NULL) OR " +
                "([Scope] = N'LogicalCamera' AND [ObservatoryId] IS NOT NULL AND [LogicalCameraId] IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_CentralProcessingGraphAssignments_EffectiveWindow",
                "[EffectiveUntilUtc] IS NULL OR [EffectiveUntilUtc] > [EffectiveFromUtc]");
            table.HasCheckConstraint(
                "CK_CentralProcessingGraphAssignments_TargetHost",
                "[TargetHost] IN (N'Edge', N'Central')");
        });
        entity.HasKey(item => item.Id);
        entity.Property(item => item.TargetHost).HasConversion<string>().HasMaxLength(16).IsRequired();
        entity.Property(item => item.Scope).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.ActorUserId).HasMaxLength(450).IsRequired();
        entity.Property(item => item.ReasonCode).HasMaxLength(128).IsRequired();
        entity.HasIndex(item => new { item.TargetHost, item.EffectiveFromUtc, item.Id });
        entity.HasIndex(item => new { item.TargetHost, item.ObservatoryId, item.EffectiveFromUtc, item.Id });
        entity.HasIndex(item => new { item.TargetHost, item.LogicalCameraId, item.EffectiveFromUtc, item.Id });
        entity.HasOne(item => item.Revision).WithMany(item => item.Assignments)
            .HasForeignKey(item => item.RevisionId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.Observatory).WithMany()
            .HasForeignKey(item => item.ObservatoryId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(item => item.LogicalCamera).WithMany()
            .HasForeignKey(item => item.LogicalCameraId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureProposals(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralProcessingGraphDeliveryProposal>();
        entity.ToTable("CentralProcessingGraphDeliveryProposals", table =>
        {
            table.HasTrigger("TR_CentralProcessingGraphDeliveryProposals_Immutable");
            table.HasCheckConstraint(
                "CK_CentralProcessingGraphDeliveryProposals_Expiry",
                "[ExpiresAtUtc] > [IssuedAtUtc]");
        });
        entity.HasKey(item => item.Id);
        entity.Property(item => item.ExpectedActiveLocalRevisionId).HasMaxLength(64).IsUnicode(false);
        ConfigureRequiredSha256(entity.Property(item => item.CapabilitySnapshotSha256));
        entity.HasIndex(item => new
        {
            item.RegistrationId,
            item.LogicalCameraInstallationId,
            item.IssuedAtUtc,
            item.Id
        });
        entity.HasIndex(item => new
        {
            item.AssignmentId,
            item.RegistrationId,
            item.LogicalCameraInstallationId,
            item.CapabilitySnapshotSha256,
            item.ExpectedActiveLocalRevisionId
        });
        entity.HasOne(item => item.Assignment).WithMany(item => item.Proposals)
            .HasForeignKey(item => item.AssignmentId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.Revision).WithMany()
            .HasForeignKey(item => item.RevisionId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.Registration).WithMany()
            .HasForeignKey(item => item.RegistrationId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.LogicalCameraInstallation).WithMany()
            .HasForeignKey(item => item.LogicalCameraInstallationId).OnDelete(DeleteBehavior.Restrict).IsRequired();
    }

    private static void ConfigureFacts(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralProcessingGraphDeliveryFact>();
        entity.ToTable("CentralProcessingGraphDeliveryFacts", table =>
        {
            table.HasTrigger("TR_CentralProcessingGraphDeliveryFacts_Immutable");
            table.HasCheckConstraint(
                "CK_CentralProcessingGraphDeliveryFacts_Kind",
                "[Kind] IN (N'Retrieved', N'Accepted', N'Rejected', N'Activated', N'RolledBack', N'Expired', N'Superseded')");
            table.HasCheckConstraint(
                "CK_CentralProcessingGraphDeliveryFacts_Source",
                "[Source] IN (N'LogicHost', N'CameraAgent')");
        });
        entity.HasKey(item => item.Id);
        entity.Property(item => item.Kind).HasMaxLength(16).IsRequired();
        entity.Property(item => item.Source).HasMaxLength(16).IsRequired();
        entity.Property(item => item.LocalRevisionId).HasMaxLength(64).IsUnicode(false);
        ConfigureOptionalSha256(entity.Property(item => item.DefinitionIdentitySha256));
        ConfigureOptionalSha256(entity.Property(item => item.SharedPlanIdentitySha256));
        ConfigureOptionalSha256(entity.Property(item => item.LocalPlanIdentitySha256));
        entity.Property(item => item.ReasonCode).HasMaxLength(128);
        entity.HasIndex(item => item.ProposalId)
            .IsUnique()
            .HasFilter("[Kind] IN (N'Accepted', N'Rejected', N'Expired', N'Superseded')");
        entity.HasIndex(item => new { item.ProposalId, item.RecordedAtUtc, item.Id });
        entity.HasOne(item => item.Proposal).WithMany(item => item.Facts)
            .HasForeignKey(item => item.ProposalId).OnDelete(DeleteBehavior.Restrict).IsRequired();
    }

    private static void ConfigureOptionalSha256(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<string?> property)
        => property.HasMaxLength(64).IsFixedLength().IsUnicode(false);

    private static void ConfigureRequiredSha256(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<string> property)
        => property.HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
}
