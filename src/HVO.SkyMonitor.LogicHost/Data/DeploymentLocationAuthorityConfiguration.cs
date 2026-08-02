using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Data;

internal static class DeploymentLocationAuthorityConfiguration
{
    internal static void Configure(ModelBuilder builder)
    {
        ConfigureObservatoryLocation(builder);
        ConfigureDeploymentLocation(builder);
        ConfigureReconciliationWork(builder);
        ConfigureReconciliationCaptures(builder);
        ConfigureResolutionAudit(builder);
        ConfigureCaptureLocation(builder);
    }

    private static void ConfigureReconciliationCaptures(ModelBuilder builder)
    {
        var entity = builder.Entity<DeploymentLocationReconciliationCapture>();
        entity.ToTable("DeploymentLocationReconciliationCaptures");
        entity.HasKey(item => new
        {
            item.DeploymentLocationReconciliationWorkId,
            item.AuthorityConcurrencyToken,
            item.CentralFrameId
        });
        entity.HasIndex(item => new
        {
            item.DeploymentLocationReconciliationWorkId,
            item.AuthorityConcurrencyToken,
            item.FirstReceivedAtUtc,
            item.CentralFrameId
        }).HasDatabaseName("IX_DeploymentLocationReconciliationCaptures_Work_Generation_Cursor");
        entity.HasOne(item => item.Work).WithMany(item => item.Captures)
            .HasForeignKey(item => item.DeploymentLocationReconciliationWorkId)
            .OnDelete(DeleteBehavior.Cascade).IsRequired();
        entity.HasOne(item => item.CentralFrame).WithMany()
            .HasForeignKey(item => item.CentralFrameId)
            .OnDelete(DeleteBehavior.Restrict).IsRequired();
    }

    private static void ConfigureReconciliationWork(ModelBuilder builder)
    {
        var entity = builder.Entity<DeploymentLocationReconciliationWork>();
        entity.ToTable("DeploymentLocationReconciliationWork", table =>
        {
            table.HasCheckConstraint("CK_DeploymentLocationReconciliationWork_Status",
                "[Status] IN (N'Pending', N'Processing', N'Retry', N'Completed')");
            table.HasCheckConstraint("CK_DeploymentLocationReconciliationWork_Counts",
                "[AttemptCount] >= 0 AND [DiscoveredCaptureCount] >= 0 AND [CompletedCaptureCount] >= 0 AND [ScheduledArtifactCount] >= 0 AND [ActiveBatchCaptureCount] >= 0 AND ([CaptureCount] IS NULL OR ([CaptureCount] = [DiscoveredCaptureCount] AND [CompletedCaptureCount] + [ActiveBatchCaptureCount] <= [CaptureCount]))");
            table.HasCheckConstraint("CK_DeploymentLocationReconciliationWork_Lease",
                "([LeaseToken] IS NULL AND [LeaseOwner] IS NULL AND [LeaseExpiresAtUtc] IS NULL) OR ([LeaseToken] IS NOT NULL AND [LeaseOwner] IS NOT NULL AND [LeaseExpiresAtUtc] IS NOT NULL)");
            table.HasCheckConstraint("CK_DeploymentLocationReconciliationWork_ActiveBatch",
                "([ActiveBatchUpperFirstReceivedAtUtc] IS NULL AND [ActiveBatchUpperCentralFrameId] IS NULL AND [ActiveBatchCaptureCount] = 0 AND [SchedulingCentralFrameId] IS NULL AND [SchedulingCentralArtifactId] IS NULL) OR ([ActiveBatchUpperFirstReceivedAtUtc] IS NOT NULL AND [ActiveBatchUpperCentralFrameId] IS NOT NULL AND [ActiveBatchCaptureCount] > 0 AND (([SchedulingCentralFrameId] IS NULL AND [SchedulingCentralArtifactId] IS NULL) OR ([SchedulingCentralFrameId] IS NOT NULL AND [SchedulingCentralArtifactId] IS NOT NULL)))");
            table.HasCheckConstraint("CK_DeploymentLocationReconciliationWork_Discovery",
                "([DiscoveryCutoffUtc] IS NULL AND [CaptureCount] IS NULL AND [DiscoveredCaptureCount] = 0 AND [DiscoveryCursorCentralFrameId] IS NULL) OR ([DiscoveryCutoffUtc] IS NOT NULL AND [CaptureCount] IS NULL AND [CompletedCaptureCount] = 0 AND [ActiveBatchCaptureCount] = 0 AND [LastCompletedCentralFrameId] IS NULL) OR ([DiscoveryCutoffUtc] IS NOT NULL AND [CaptureCount] IS NOT NULL)");
            table.HasCheckConstraint("CK_DeploymentLocationReconciliationWork_DiscoveryCursor",
                "([DiscoveryCursorFirstReceivedAtUtc] IS NULL AND [DiscoveryCursorCentralFrameId] IS NULL) OR ([DiscoveryCursorFirstReceivedAtUtc] IS NOT NULL AND [DiscoveryCursorCentralFrameId] IS NOT NULL)");
            table.HasCheckConstraint("CK_DeploymentLocationReconciliationWork_Cursor",
                "([LastCompletedFirstReceivedAtUtc] IS NULL AND [LastCompletedCentralFrameId] IS NULL) OR ([LastCompletedFirstReceivedAtUtc] IS NOT NULL AND [LastCompletedCentralFrameId] IS NOT NULL)");
            table.HasCheckConstraint("CK_DeploymentLocationReconciliationWork_Timestamps",
                "[UpdatedAtUtc] >= [CreatedAtUtc] AND ([StartedAtUtc] IS NULL OR [StartedAtUtc] >= [CreatedAtUtc]) AND ([CompletedAtUtc] IS NULL OR [CompletedAtUtc] >= [CreatedAtUtc])");
            table.HasCheckConstraint("CK_DeploymentLocationReconciliationWork_State",
                "([Status] = N'Processing' AND [LeaseToken] IS NOT NULL AND [NextAttemptAtUtc] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] IN (N'Pending', N'Retry') AND [LeaseToken] IS NULL AND [NextAttemptAtUtc] IS NOT NULL AND [CompletedAtUtc] IS NULL AND ([Status] <> N'Retry' OR [LastErrorCode] IS NOT NULL)) OR ([Status] = N'Completed' AND [LeaseToken] IS NULL AND [NextAttemptAtUtc] IS NULL AND [LastErrorCode] IS NULL AND [CompletedAtUtc] IS NOT NULL AND [ActiveBatchUpperCentralFrameId] IS NULL AND [CaptureCount] IS NOT NULL AND [CompletedCaptureCount] = [CaptureCount])");
        });
        entity.HasKey(item => item.Id);
        entity.Property(item => item.Status).HasMaxLength(32).IsRequired();
        entity.Property(item => item.LastErrorCode).HasMaxLength(128);
        entity.Property(item => item.LeaseOwner).HasMaxLength(256);
        entity.Property(item => item.TraceParent).HasMaxLength(128);
        entity.Property(item => item.TraceState).HasMaxLength(512);
        entity.Property(item => item.RowVersion).IsRowVersion();
        entity.HasIndex(item => item.DeviceDeploymentLocationVersionId).IsUnique();
        entity.HasIndex(item => new { item.Status, item.NextAttemptAtUtc, item.CreatedAtUtc, item.Id });
        entity.HasIndex(item => new { item.Status, item.LeaseExpiresAtUtc, item.CreatedAtUtc, item.Id });
        entity.HasOne(item => item.DeploymentLocation).WithOne(item => item.ReconciliationWork)
            .HasForeignKey<DeploymentLocationReconciliationWork>(item => item.DeviceDeploymentLocationVersionId)
            .OnDelete(DeleteBehavior.Restrict).IsRequired();
    }

    private static void ConfigureObservatoryLocation(ModelBuilder builder)
    {
        var entity = builder.Entity<ObservatoryLocationVersion>();
        entity.ToTable("ObservatoryLocationVersions", table =>
        {
            table.HasCheckConstraint("CK_ObservatoryLocationVersions_Version", "[Version] >= 1");
            table.HasCheckConstraint("CK_ObservatoryLocationVersions_Latitude", "[LatitudeDegrees] >= -90 AND [LatitudeDegrees] <= 90");
            table.HasCheckConstraint("CK_ObservatoryLocationVersions_Longitude", "[LongitudeDegrees] >= -180 AND [LongitudeDegrees] <= 180");
            table.HasCheckConstraint("CK_ObservatoryLocationVersions_Radius", "[AllowedDeploymentRadiusMeters] IS NULL OR [AllowedDeploymentRadiusMeters] >= 0");
            table.HasCheckConstraint("CK_ObservatoryLocationVersions_Interval", "[SupersededAtUtc] IS NULL OR [SupersededAtUtc] >= [EffectiveFromUtc]");
        });
        entity.HasKey(item => item.Id);
        entity.Property(item => item.CanonicalSha256).HasMaxLength(64).IsRequired();
        entity.Property(item => item.TimeZoneId).HasMaxLength(128).IsRequired();
        entity.Property(item => item.RecordedBy).HasMaxLength(450).IsRequired();
        entity.HasIndex(item => new { item.ObservatoryId, item.Version }).IsUnique();
        entity.HasIndex(item => item.CanonicalSha256);
        entity.HasIndex(item => item.ObservatoryId)
            .IsUnique()
            .HasFilter("[SupersededAtUtc] IS NULL");
        entity.HasOne(item => item.Observatory).WithMany(item => item.LocationVersions)
            .HasForeignKey(item => item.ObservatoryId).OnDelete(DeleteBehavior.Restrict).IsRequired();
    }

    private static void ConfigureDeploymentLocation(ModelBuilder builder)
    {
        var entity = builder.Entity<DeviceDeploymentLocationVersion>();
        entity.ToTable("DeviceDeploymentLocationVersions", table =>
        {
            table.HasCheckConstraint("CK_DeviceDeploymentLocationVersions_Version", "[Version] >= 1 AND [ObservatoryLocationVersionNumber] >= 1");
            table.HasCheckConstraint("CK_DeviceDeploymentLocationVersions_Latitude", "[LatitudeDegrees] >= -90 AND [LatitudeDegrees] <= 90");
            table.HasCheckConstraint("CK_DeviceDeploymentLocationVersions_Longitude", "[LongitudeDegrees] >= -180 AND [LongitudeDegrees] <= 180");
            table.HasCheckConstraint("CK_DeviceDeploymentLocationVersions_Accuracy", "[HorizontalAccuracyMeters] IS NULL OR [HorizontalAccuracyMeters] >= 0");
            table.HasCheckConstraint("CK_DeviceDeploymentLocationVersions_Interval", "[EffectiveUntilUtc] IS NULL OR [EffectiveUntilUtc] > [EffectiveFromUtc]");
            table.HasCheckConstraint("CK_DeviceDeploymentLocationVersions_Resolution", "([Status] = N'Pending' AND [ResolvedAtUtc] IS NULL) OR ([Status] <> N'Pending' AND [ResolvedAtUtc] IS NOT NULL)");
        });
        entity.HasKey(item => item.Id);
        entity.Property(item => item.LocationId).HasMaxLength(128).IsRequired();
        entity.Property(item => item.CanonicalSha256).HasMaxLength(64).IsRequired();
        entity.Property(item => item.ObservatoryLocationCanonicalSha256).HasMaxLength(64).IsRequired();
        entity.Property(item => item.Source).HasMaxLength(512).IsRequired();
        entity.Property(item => item.SourceKind).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.TimeZoneId).HasMaxLength(128).IsRequired();
        entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.ReasonCode).HasMaxLength(128);
        entity.Property(item => item.ResolvedByUserId).HasMaxLength(450);
        entity.Property(item => item.ConcurrencyToken).IsConcurrencyToken().IsRequired();
        entity.HasIndex(item => new
        {
            item.RegistrationId,
            item.LocationId,
            item.Version,
            item.ObservatoryLocationVersionId
        }).IsUnique();
        entity.HasIndex(item => new { item.ObservatoryId, item.Status });
        entity.HasIndex(item => new { item.RegistrationId, item.Status, item.ProposedAtUtc, item.Id });
        entity.HasIndex(item => new { item.RegistrationId, item.ProposedAtUtc, item.Id });
        entity.HasOne(item => item.Registration).WithMany(item => item.DeploymentLocations)
            .HasForeignKey(item => item.RegistrationId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.ObservatoryLocationVersion).WithMany()
            .HasForeignKey(item => item.ObservatoryLocationVersionId).OnDelete(DeleteBehavior.Restrict).IsRequired();
    }

    private static void ConfigureResolutionAudit(ModelBuilder builder)
    {
        var entity = builder.Entity<DeploymentLocationResolutionAudit>();
        entity.ToTable("DeploymentLocationResolutionAudits");
        entity.HasKey(item => item.Id);
        entity.Property(item => item.PreviousStatus).HasConversion<string>().HasMaxLength(32);
        entity.Property(item => item.NewStatus).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.ActorUserId).HasMaxLength(450).IsRequired();
        entity.Property(item => item.Reason).HasMaxLength(512).IsRequired();
        entity.HasIndex(item => new { item.RegistrationId, item.OccurredAtUtc });
        entity.HasOne(item => item.DeploymentLocation).WithMany(item => item.ResolutionAudits)
            .HasForeignKey(item => item.DeviceDeploymentLocationVersionId).OnDelete(DeleteBehavior.Restrict).IsRequired();
    }

    private static void ConfigureCaptureLocation(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralCaptureLocation>();
        entity.ToTable("CentralCaptureLocations", table =>
        {
            table.HasCheckConstraint("CK_CentralCaptureLocations_Version", "[Version] >= 1");
            table.HasCheckConstraint("CK_CentralCaptureLocations_Accuracy", "[HorizontalAccuracyMeters] IS NULL OR [HorizontalAccuracyMeters] >= 0");
            table.HasCheckConstraint("CK_CentralCaptureLocations_Interval", "[EffectiveUntilUtc] IS NULL OR [EffectiveUntilUtc] > [EffectiveFromUtc]");
        });
        entity.HasKey(item => item.CentralFrameId);
        entity.Property(item => item.LocationId).HasMaxLength(128).IsRequired();
        entity.Property(item => item.Source).HasMaxLength(512).IsRequired();
        entity.HasIndex(item => new { item.LocationId, item.Version });
        entity.HasIndex(item => item.DeviceDeploymentLocationVersionId);
        entity.HasOne(item => item.CentralFrame).WithOne(item => item.Location)
            .HasForeignKey<CentralCaptureLocation>(item => item.CentralFrameId).OnDelete(DeleteBehavior.Cascade).IsRequired();
        entity.HasOne(item => item.DeploymentLocation).WithMany()
            .HasForeignKey(item => item.DeviceDeploymentLocationVersionId).OnDelete(DeleteBehavior.Restrict);
    }
}
