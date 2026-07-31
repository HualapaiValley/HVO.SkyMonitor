using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Data;

internal static class NetworkAuthorityConfiguration
{
    internal static void Configure(ModelBuilder builder)
    {
        ConfigurePublicationProfiles(builder);
        ConfigureLocationDisclosures(builder);
        ConfigurePublicationDecisions(builder);
        ConfigureLogicalCameras(builder);
        ConfigureInvitations(builder);
    }

    private static void ConfigurePublicationProfiles(ModelBuilder builder)
    {
        var entity = builder.Entity<ObservatoryPublicationProfileVersion>();
        entity.ToTable("ObservatoryPublicationProfileVersions", table =>
        {
            table.HasTrigger("TR_ObservatoryPublicationProfileVersions_Transitions");
            table.HasCheckConstraint("CK_ObservatoryPublicationProfileVersions_Version", "[Version] > 0");
            table.HasCheckConstraint(
                "CK_ObservatoryPublicationProfileVersions_Visibility",
                "[ProfileVisibility] IN (N'Private', N'Public')");
        });
        entity.HasKey(item => item.Id);
        entity.Property(item => item.PublicSlug).HasMaxLength(128).IsRequired();
        entity.Property(item => item.PublicDisplayName).HasMaxLength(200).IsRequired();
        entity.Property(item => item.PublicDescription).HasMaxLength(2000).IsRequired();
        entity.Property(item => item.ProfileVisibility).HasConversion<string>().HasMaxLength(16).IsRequired();
        entity.Property(item => item.ActorUserId).HasMaxLength(450).IsRequired();
        entity.Property(item => item.ReasonCode).HasMaxLength(128).IsRequired();
        entity.Property(item => item.CanonicalSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        entity.HasIndex(item => new { item.ObservatoryId, item.Version }).IsUnique();
        entity.HasIndex(item => item.ObservatoryId).IsUnique()
            .HasFilter("[SupersededAtUtc] IS NULL");
        entity.HasIndex(item => item.PublicSlug).IsUnique()
            .HasFilter("[SupersededAtUtc] IS NULL AND [ProfileVisibility] = N'Public'");
        entity.HasOne(item => item.Observatory).WithMany()
            .HasForeignKey(item => item.ObservatoryId).OnDelete(DeleteBehavior.Restrict).IsRequired();
    }

    private static void ConfigureLocationDisclosures(ModelBuilder builder)
    {
        var entity = builder.Entity<ObservatoryLocationDisclosureVersion>();
        entity.ToTable("ObservatoryLocationDisclosureVersions", table =>
        {
            table.HasTrigger("TR_ObservatoryLocationDisclosureVersions_Transitions");
            table.HasCheckConstraint("CK_ObservatoryLocationDisclosureVersions_Version", "[Version] > 0");
            table.HasCheckConstraint(
                "CK_ObservatoryLocationDisclosureVersions_Level",
                "[DisclosureLevel] IN (N'Hidden', N'Region', N'Approximate', N'Exact')");
            table.HasCheckConstraint(
                "CK_ObservatoryLocationDisclosureVersions_Projection",
                "([DisclosureLevel] = N'Hidden' AND [RegionCode] IS NULL AND [RegionLabel] IS NULL " +
                "AND [PublicLatitudeDegrees] IS NULL AND [PublicLongitudeDegrees] IS NULL " +
                "AND [PublicPrecisionMeters] IS NULL AND [SourceObservatoryLocationVersionId] IS NULL) OR " +
                "([DisclosureLevel] = N'Region' AND [RegionCode] IS NOT NULL AND [RegionLabel] IS NOT NULL " +
                "AND [PublicLatitudeDegrees] IS NULL AND [PublicLongitudeDegrees] IS NULL " +
                "AND [PublicPrecisionMeters] IS NULL AND [SourceObservatoryLocationVersionId] IS NULL) OR " +
                "([DisclosureLevel] = N'Approximate' AND [PublicLatitudeDegrees] BETWEEN -90 AND 90 " +
                "AND [PublicLongitudeDegrees] BETWEEN -180 AND 180 AND [PublicPrecisionMeters] > 0 " +
                "AND [SourceObservatoryLocationVersionId] IS NOT NULL) OR " +
                "([DisclosureLevel] = N'Exact' AND [PublicLatitudeDegrees] BETWEEN -90 AND 90 " +
                "AND [PublicLongitudeDegrees] BETWEEN -180 AND 180 AND [PublicPrecisionMeters] >= 0 " +
                "AND [SourceObservatoryLocationVersionId] IS NOT NULL)");
        });
        entity.HasKey(item => item.Id);
        entity.Property(item => item.DisclosureLevel).HasConversion<string>().HasMaxLength(16).IsRequired();
        entity.Property(item => item.RegionCode).HasMaxLength(64);
        entity.Property(item => item.RegionLabel).HasMaxLength(200);
        entity.Property(item => item.ActorUserId).HasMaxLength(450).IsRequired();
        entity.Property(item => item.ReasonCode).HasMaxLength(128).IsRequired();
        entity.Property(item => item.CanonicalSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        entity.HasIndex(item => new { item.ObservatoryId, item.Version }).IsUnique();
        entity.HasIndex(item => item.ObservatoryId).IsUnique()
            .HasFilter("[SupersededAtUtc] IS NULL");
        entity.HasOne(item => item.Observatory).WithMany()
            .HasForeignKey(item => item.ObservatoryId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.SourceObservatoryLocationVersion).WithMany()
            .HasForeignKey(item => item.SourceObservatoryLocationVersionId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigurePublicationDecisions(ModelBuilder builder)
    {
        var entity = builder.Entity<PublicRecordPublicationDecision>();
        entity.ToTable("PublicRecordPublicationDecisions", table =>
        {
            table.HasTrigger("TR_PublicRecordPublicationDecisions_Immutable");
            table.HasCheckConstraint(
                "CK_PublicRecordPublicationDecisions_SubjectKind",
                "[SubjectKind] IN (N'LogicalCamera', N'Artifact', N'TransientEvent', N'TransientDerivative')");
            table.HasCheckConstraint(
                "CK_PublicRecordPublicationDecisions_State",
                "[State] IN (N'Released', N'Withdrawn')");
            table.HasCheckConstraint(
                "CK_PublicRecordPublicationDecisions_Subject",
                "([SubjectKind] = N'LogicalCamera' AND [LogicalCameraId] IS NOT NULL " +
                "AND [CentralArtifactId] IS NULL AND [CentralTransientEventId] IS NULL " +
                "AND [SourceEventVersionId] IS NULL AND [CentralTransientDerivativeId] IS NULL) OR " +
                "([SubjectKind] = N'Artifact' AND [LogicalCameraId] IS NULL " +
                "AND [CentralArtifactId] IS NOT NULL AND [CentralTransientEventId] IS NULL " +
                "AND [SourceEventVersionId] IS NULL AND [CentralTransientDerivativeId] IS NULL) OR " +
                "([SubjectKind] = N'TransientEvent' AND [LogicalCameraId] IS NULL " +
                "AND [CentralArtifactId] IS NULL AND [CentralTransientEventId] IS NOT NULL " +
                "AND [SourceEventVersionId] IS NOT NULL AND [CentralTransientDerivativeId] IS NULL) OR " +
                "([SubjectKind] = N'TransientDerivative' AND [LogicalCameraId] IS NULL " +
                "AND [CentralArtifactId] IS NULL AND [CentralTransientEventId] IS NULL " +
                "AND [SourceEventVersionId] IS NULL AND [CentralTransientDerivativeId] IS NOT NULL)");
        });
        entity.HasKey(item => item.Id);
        entity.Property(item => item.SubjectKind).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.State).HasConversion<string>().HasMaxLength(16).IsRequired();
        entity.Property(item => item.ProjectionSchemaVersion).HasMaxLength(32).IsRequired();
        entity.Property(item => item.ActorUserId).HasMaxLength(450).IsRequired();
        entity.Property(item => item.ReasonCode).HasMaxLength(128).IsRequired();
        entity.HasIndex(item => item.PublicId);
        entity.HasIndex(item => new { item.AuthorityObservatoryId, item.OccurredAtUtc, item.Id });
        entity.HasIndex(item => item.SupersedesDecisionId).IsUnique()
            .HasFilter("[SupersedesDecisionId] IS NOT NULL");
        entity.HasOne(item => item.AuthorityObservatory).WithMany()
            .HasForeignKey(item => item.AuthorityObservatoryId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.LogicalCamera).WithMany()
            .HasForeignKey(item => item.LogicalCameraId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(item => item.CentralArtifact).WithMany()
            .HasForeignKey(item => item.CentralArtifactId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(item => item.CentralTransientEvent).WithMany()
            .HasForeignKey(item => item.CentralTransientEventId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(item => item.SourceEventVersion).WithMany()
            .HasForeignKey(item => item.SourceEventVersionId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(item => item.CentralTransientDerivative).WithMany()
            .HasForeignKey(item => item.CentralTransientDerivativeId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(item => item.SupersedesDecision).WithOne()
            .HasForeignKey<PublicRecordPublicationDecision>(item => item.SupersedesDecisionId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureLogicalCameras(ModelBuilder builder)
    {
        var camera = builder.Entity<LogicalCamera>();
        camera.ToTable("LogicalCameras");
        camera.HasKey(item => item.Id);
        camera.Property(item => item.Slug).HasMaxLength(128).IsRequired();
        camera.Property(item => item.Name).HasMaxLength(200).IsRequired();
        camera.Property(item => item.Description).HasMaxLength(2000).IsRequired();
        camera.Property(item => item.CreatedByUserId).HasMaxLength(450).IsRequired();
        camera.Property(item => item.RowVersion).IsRowVersion();
        camera.HasIndex(item => new { item.ObservatoryId, item.Slug }).IsUnique();
        camera.HasIndex(item => new { item.ObservatoryId, item.Name, item.Id });
        camera.HasIndex(item => new { item.ObservatoryId, item.Name, item.Slug });
        camera.HasOne(item => item.Observatory).WithMany()
            .HasForeignKey(item => item.ObservatoryId).OnDelete(DeleteBehavior.Restrict).IsRequired();

        var installation = builder.Entity<LogicalCameraInstallation>();
        installation.ToTable("LogicalCameraInstallations", table =>
            table.HasTrigger("TR_LogicalCameraInstallations_Transitions"));
        installation.HasKey(item => item.Id);
        installation.Property(item => item.AssignedByUserId).HasMaxLength(450).IsRequired();
        installation.Property(item => item.RetiredByUserId).HasMaxLength(450);
        installation.Property(item => item.AssignmentReasonCode).HasMaxLength(128).IsRequired();
        installation.Property(item => item.RetirementReasonCode).HasMaxLength(128);
        installation.Property(item => item.RowVersion).IsRowVersion();
        installation.HasIndex(item => item.RegistrationId).IsUnique();
        installation.HasIndex(item => item.InstallationPublicId).IsUnique();
        installation.HasIndex(item => item.LogicalCameraId).IsUnique()
            .HasFilter("[RetiredAtUtc] IS NULL");
        installation.HasIndex(item => new
        {
            item.LogicalCameraId,
            item.AssignedAtUtc,
            item.InstallationPublicId
        });
        installation.HasIndex(item => item.ReplacesInstallationId).IsUnique()
            .HasFilter("[ReplacesInstallationId] IS NOT NULL");
        installation.HasOne(item => item.LogicalCamera).WithMany(item => item.Installations)
            .HasForeignKey(item => item.LogicalCameraId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        installation.HasOne(item => item.Registration).WithMany()
            .HasForeignKey(item => item.RegistrationId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        installation.HasOne(item => item.ReplacesInstallation).WithOne()
            .HasForeignKey<LogicalCameraInstallation>(item => item.ReplacesInstallationId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureInvitations(ModelBuilder builder)
    {
        var invitation = builder.Entity<ObservatoryInvitation>();
        invitation.ToTable("ObservatoryInvitations", table =>
        {
            table.HasTrigger("TR_ObservatoryInvitations_Immutable");
            table.HasCheckConstraint(
                "CK_ObservatoryInvitations_OfferedRole",
                "[OfferedRole] IN (N'Viewer', N'Manager', N'Owner')");
            table.HasCheckConstraint("CK_ObservatoryInvitations_Expiry", "[ExpiresAtUtc] > [IssuedAtUtc]");
        });
        invitation.HasKey(item => item.Id);
        invitation.Property(item => item.TargetUserId).HasMaxLength(450).IsRequired();
        invitation.Property(item => item.TargetEmailSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        invitation.Property(item => item.OfferedRole).HasConversion<string>().HasMaxLength(16).IsRequired();
        invitation.Property(item => item.InvitedByUserId).HasMaxLength(450).IsRequired();
        invitation.Property(item => item.AcceptanceTokenSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        invitation.Property(item => item.CanonicalSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        invitation.HasIndex(item => new { item.ObservatoryId, item.TargetUserId, item.IssuedAtUtc });
        invitation.HasIndex(item => new { item.ObservatoryId, item.ExpiresAtUtc, item.Id });
        invitation.HasOne(item => item.Observatory).WithMany()
            .HasForeignKey(item => item.ObservatoryId).OnDelete(DeleteBehavior.Restrict).IsRequired();

        var disposition = builder.Entity<ObservatoryInvitationDisposition>();
        disposition.ToTable("ObservatoryInvitationDispositions", table =>
        {
            table.HasTrigger("TR_ObservatoryInvitationDispositions_Immutable");
            table.HasCheckConstraint(
                "CK_ObservatoryInvitationDispositions_Action",
                "[Action] IN (N'Accepted', N'Declined', N'Revoked', N'Expired')");
        });
        disposition.HasKey(item => item.Id);
        disposition.Property(item => item.Action).HasConversion<string>().HasMaxLength(16).IsRequired();
        disposition.Property(item => item.ActorUserId).HasMaxLength(450).IsRequired();
        disposition.Property(item => item.ReasonCode).HasMaxLength(128).IsRequired();
        disposition.HasIndex(item => item.InvitationId).IsUnique();
        disposition.HasOne(item => item.Invitation).WithMany(item => item.Dispositions)
            .HasForeignKey(item => item.InvitationId).OnDelete(DeleteBehavior.Restrict).IsRequired();
    }
}
