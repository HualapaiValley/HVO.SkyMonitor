using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Data;

internal static class DeploymentLocationAuthorityConfiguration
{
    internal static void Configure(ModelBuilder builder)
    {
        ConfigureObservatoryLocation(builder);
        ConfigureDeploymentLocation(builder);
        ConfigureResolutionAudit(builder);
        ConfigureCaptureLocation(builder);
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
