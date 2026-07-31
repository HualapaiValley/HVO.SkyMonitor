using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HVO.SkyMonitor.LogicHost.Data;

/// <summary>
/// Application database context backed by SQL Server.
/// Includes Identity tables, API keys, and OpenIddict entities.
/// </summary>
public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    internal DbSet<DeviceRegistration> DeviceRegistrations => Set<DeviceRegistration>();
    internal DbSet<DeviceRigProfile> DeviceRigProfiles => Set<DeviceRigProfile>();
    internal DbSet<DeviceImageUpload> DeviceImageUploads => Set<DeviceImageUpload>();
    internal DbSet<DeviceFleetState> DeviceFleetStates => Set<DeviceFleetState>();
    internal DbSet<DeviceHeartbeatRecord> DeviceHeartbeatRecords => Set<DeviceHeartbeatRecord>();
    internal DbSet<CentralFrame> CentralFrames => Set<CentralFrame>();
    internal DbSet<CentralArtifact> CentralArtifacts => Set<CentralArtifact>();
    internal DbSet<CentralArtifactDownloadAuthorization> CentralArtifactDownloadAuthorizations =>
        Set<CentralArtifactDownloadAuthorization>();
    internal DbSet<CentralProcessingOverrideVersion> CentralProcessingOverrideVersions =>
        Set<CentralProcessingOverrideVersion>();
    internal DbSet<CentralDerivativeJob> CentralDerivativeJobs => Set<CentralDerivativeJob>();
    internal DbSet<CentralDerivativeJobAttempt> CentralDerivativeJobAttempts => Set<CentralDerivativeJobAttempt>();
    internal DbSet<CentralDerivativeJobInputRequirement> CentralDerivativeJobInputRequirements => Set<CentralDerivativeJobInputRequirement>();
    internal DbSet<CentralDerivativeJobInput> CentralDerivativeJobInputs => Set<CentralDerivativeJobInput>();
    internal DbSet<CentralDerivativeJobCanonicalInput> CentralDerivativeJobCanonicalInputs => Set<CentralDerivativeJobCanonicalInput>();
    internal DbSet<CentralClearReferenceDesignation> CentralClearReferenceDesignations => Set<CentralClearReferenceDesignation>();
    internal DbSet<CentralArtifactProcessingEvidence> CentralArtifactProcessingEvidence => Set<CentralArtifactProcessingEvidence>();
    internal DbSet<CentralCaptureTiming> CentralCaptureTimings => Set<CentralCaptureTiming>();
    internal DbSet<CentralCaptureControl> CentralCaptureControls => Set<CentralCaptureControl>();
    internal DbSet<CentralCaptureProfile> CentralCaptureProfiles => Set<CentralCaptureProfile>();
    internal DbSet<CentralArtifactLayout> CentralArtifactLayouts => Set<CentralArtifactLayout>();
    internal DbSet<CentralArtifactRecipe> CentralArtifactRecipes => Set<CentralArtifactRecipe>();
    internal DbSet<CentralArtifactSource> CentralArtifactSources => Set<CentralArtifactSource>();
    internal DbSet<CentralArtifactIngestIdentity> CentralArtifactIngestIdentities => Set<CentralArtifactIngestIdentity>();
    internal DbSet<CentralRecoveryCheckpoint> CentralRecoveryCheckpoints => Set<CentralRecoveryCheckpoint>();
    internal DbSet<CentralObjectRecoveryDisposition> CentralObjectRecoveryDispositions => Set<CentralObjectRecoveryDisposition>();
    internal DbSet<EnvironmentalObservationSourceRecord> EnvironmentalObservationSources => Set<EnvironmentalObservationSourceRecord>();
    internal DbSet<EnvironmentalObservationRecord> EnvironmentalObservations => Set<EnvironmentalObservationRecord>();
    internal DbSet<EnvironmentalObservationLineageRecord> EnvironmentalObservationLineage => Set<EnvironmentalObservationLineageRecord>();
    internal DbSet<CentralTransientEventRecord> CentralTransientEvents => Set<CentralTransientEventRecord>();
    internal DbSet<CentralTransientEventVersionRecord> CentralTransientEventVersions => Set<CentralTransientEventVersionRecord>();
    internal DbSet<CentralTransientObservationRecord> CentralTransientObservations => Set<CentralTransientObservationRecord>();
    internal DbSet<CentralTransientObservationSourceReference> CentralTransientObservationSources => Set<CentralTransientObservationSourceReference>();
    internal DbSet<CentralTransientObservationBackgroundReference> CentralTransientObservationBackgrounds => Set<CentralTransientObservationBackgroundReference>();
    internal DbSet<CentralTransientAssessmentRecord> CentralTransientAssessments => Set<CentralTransientAssessmentRecord>();
    internal DbSet<CentralTransientReviewRecord> CentralTransientReviews => Set<CentralTransientReviewRecord>();
    internal DbSet<CentralTransientEventCurrent> CentralTransientEventCurrent => Set<CentralTransientEventCurrent>();
    internal DbSet<CentralTransientReviewMutationRecord> CentralTransientReviewMutations => Set<CentralTransientReviewMutationRecord>();
    internal DbSet<CentralTransientDerivativeJob> CentralTransientDerivativeJobs => Set<CentralTransientDerivativeJob>();
    internal DbSet<CentralTransientDerivativeOutputIntent> CentralTransientDerivativeOutputIntents => Set<CentralTransientDerivativeOutputIntent>();
    internal DbSet<CentralTransientDerivativeRecord> CentralTransientDerivatives => Set<CentralTransientDerivativeRecord>();
    internal DbSet<CentralTransientDerivativeSourceReference> CentralTransientDerivativeSources => Set<CentralTransientDerivativeSourceReference>();
    internal DbSet<CentralTransientDerivativeBackgroundReference> CentralTransientDerivativeBackgrounds => Set<CentralTransientDerivativeBackgroundReference>();
    internal DbSet<CentralTransientNotificationRecord> CentralTransientNotifications => Set<CentralTransientNotificationRecord>();
    internal DbSet<CentralTransientNotificationDispatch> CentralTransientNotificationDispatches => Set<CentralTransientNotificationDispatch>();
    internal DbSet<CentralTransientReprocessingJob> CentralTransientReprocessingJobs => Set<CentralTransientReprocessingJob>();
    internal DbSet<CentralTransientReprocessingRequestRecord> CentralTransientReprocessingRequests => Set<CentralTransientReprocessingRequestRecord>();
    internal DbSet<CentralTransientPayloadRelease> CentralTransientPayloadReleases => Set<CentralTransientPayloadRelease>();
    internal DbSet<CentralTransientPayloadReleaseItem> CentralTransientPayloadReleaseItems => Set<CentralTransientPayloadReleaseItem>();
    internal DbSet<CentralTransientValidationJob> CentralTransientValidationJobs => Set<CentralTransientValidationJob>();
    internal DbSet<CentralTransientExtractionReceipt> CentralTransientExtractionReceipts => Set<CentralTransientExtractionReceipt>();
    internal DbSet<CentralTransientExtractionSourceReference> CentralTransientExtractionSources => Set<CentralTransientExtractionSourceReference>();
    internal DbSet<CentralTransientValidationIdentitySlot> CentralTransientValidationIdentitySlots => Set<CentralTransientValidationIdentitySlot>();
    internal DbSet<CentralTransientContextDependency> CentralTransientContextDependencies => Set<CentralTransientContextDependency>();
    internal DbSet<CentralTransientValidationOutcomeVersion> CentralTransientValidationOutcomeVersions => Set<CentralTransientValidationOutcomeVersion>();
    internal DbSet<CentralTransientSubmissionAudit> CentralTransientSubmissionAudits => Set<CentralTransientSubmissionAudit>();
    internal DbSet<Observatory> Observatories => Set<Observatory>();
    internal DbSet<ObservatoryMembership> ObservatoryMemberships => Set<ObservatoryMembership>();
    internal DbSet<ObservatoryMembershipAudit> ObservatoryMembershipAudits => Set<ObservatoryMembershipAudit>();
    internal DbSet<ObservatoryPublicationProfileVersion> ObservatoryPublicationProfileVersions =>
        Set<ObservatoryPublicationProfileVersion>();
    internal DbSet<ObservatoryLocationDisclosureVersion> ObservatoryLocationDisclosureVersions =>
        Set<ObservatoryLocationDisclosureVersion>();
    internal DbSet<PublicRecordPublicationDecision> PublicRecordPublicationDecisions =>
        Set<PublicRecordPublicationDecision>();
    internal DbSet<LogicalCamera> LogicalCameras => Set<LogicalCamera>();
    internal DbSet<LogicalCameraInstallation> LogicalCameraInstallations => Set<LogicalCameraInstallation>();
    internal DbSet<ObservatoryInvitation> ObservatoryInvitations => Set<ObservatoryInvitation>();
    internal DbSet<ObservatoryInvitationDisposition> ObservatoryInvitationDispositions =>
        Set<ObservatoryInvitationDisposition>();
    internal DbSet<CuratedPublicPlacementDecision> CuratedPublicPlacementDecisions => Set<CuratedPublicPlacementDecision>();
    internal DbSet<RegisteredUserObservatoryFollow> RegisteredUserObservatoryFollows => Set<RegisteredUserObservatoryFollow>();
    internal DbSet<RegisteredUserTransientEventBookmark> RegisteredUserTransientEventBookmarks => Set<RegisteredUserTransientEventBookmark>();
    internal DbSet<RegisteredUserSubscription> RegisteredUserSubscriptions => Set<RegisteredUserSubscription>();
    internal DbSet<RegisteredUserNotificationPreference> RegisteredUserNotificationPreferences => Set<RegisteredUserNotificationPreference>();
    internal DbSet<RegisteredUserNotification> RegisteredUserNotifications => Set<RegisteredUserNotification>();
    internal DbSet<ObservatoryLocationVersion> ObservatoryLocationVersions => Set<ObservatoryLocationVersion>();
    internal DbSet<DeviceDeploymentLocationVersion> DeviceDeploymentLocationVersions => Set<DeviceDeploymentLocationVersion>();
    internal DbSet<DeploymentLocationResolutionAudit> DeploymentLocationResolutionAudits => Set<DeploymentLocationResolutionAudit>();
    internal DbSet<CentralCaptureLocation> CentralCaptureLocations => Set<CentralCaptureLocation>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RejectMembershipAuditMutation();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        RejectMembershipAuditMutation();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        base.OnModelCreating(builder);

        ConfigureApiKeys(builder.Entity<ApiKey>());
        builder.ApplyConfiguration(new DeviceRegistrationConfiguration());
        builder.ApplyConfiguration(new DeviceRigProfileConfiguration());
        builder.ApplyConfiguration(new DeviceImageUploadConfiguration());
        DeviceFleetConfiguration.Configure(builder);
        builder.ApplyConfiguration(new CentralFrameConfiguration());
        builder.ApplyConfiguration(new CentralArtifactConfiguration());
        builder.ApplyConfiguration(new CentralArtifactDownloadAuthorizationConfiguration());
        CentralProcessingOverrideConfiguration.Configure(builder);
        builder.ApplyConfiguration(new CentralDerivativeJobConfiguration());
        CentralDerivativeExecutionConfiguration.Configure(builder);
        CentralDerivativeWindowConfiguration.Configure(builder);
        CentralCloudProcessingConfiguration.Configure(builder);
        CentralReconstructionConfiguration.Configure(builder);
        CentralRecoveryConfiguration.Configure(builder);
        EnvironmentalObservationConfiguration.Configure(builder);
        CentralTransientValidationConfiguration.Configure(builder);
        CentralTransientDerivativeConfiguration.Configure(builder);
        CentralTransientNotificationConfiguration.Configure(builder);
        CentralTransientReprocessingConfiguration.Configure(builder);
        CentralTransientPayloadReleaseConfiguration.Configure(builder);
        builder.ApplyConfiguration(new ObservatoryConfiguration());
        ObservatoryMembershipConfiguration.Configure(builder);
        NetworkAuthorityConfiguration.Configure(builder);
        RegisteredUserNetworkConfiguration.Configure(builder);
        DeploymentLocationAuthorityConfiguration.Configure(builder);

        // Configure OpenIddict entities to use the default Entity Framework Core conventions
        builder.UseOpenIddict();
    }

    private static void ConfigureApiKeys(EntityTypeBuilder<ApiKey> entity)
    {
        entity.ToTable("ApiKeys");

        entity.HasIndex(key => key.HashedKey).IsUnique();

        entity.Property(key => key.HashedKey)
            .HasMaxLength(64)
            .IsRequired();

        entity.Property(key => key.DisplayName)
            .HasColumnName("Name")
            .HasMaxLength(200)
            .IsRequired();

        entity.Property(key => key.AccessLevel)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        entity.HasOne<Observatory>()
            .WithMany()
            .HasForeignKey(key => key.ObservatoryId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.Property(key => key.IsActive)
            .HasDefaultValue(true)
            .IsRequired();

        entity.Property(key => key.CreatedUtc)
            .HasColumnName("CreatedAt")
            .IsRequired();

        entity.Property(key => key.CreatedBy)
            .HasMaxLength(256);

        entity.Property(key => key.ExpiresUtc)
            .HasColumnName("ExpiresAt");

        entity.Property(key => key.LastUsedUtc);

        entity.HasOne<ApplicationUser>()
            .WithMany(user => user.ApiKeys)
            .HasForeignKey(key => key.UserId)
            .OnDelete(DeleteBehavior.Cascade)
            .IsRequired();

        entity.HasIndex(key => key.UserId);
        entity.HasIndex(key => new { key.UserId, key.ObservatoryId });
        entity.HasIndex(key => new { key.IsActive, key.ExpiresUtc });
    }

    private void RejectMembershipAuditMutation()
    {
        var immutableMutation = ChangeTracker.Entries<ObservatoryMembershipAudit>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted)
            || ChangeTracker.Entries<CentralArtifactDownloadAuthorization>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted)
            || ChangeTracker.Entries<PublicRecordPublicationDecision>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted)
            || ChangeTracker.Entries<ObservatoryInvitation>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted)
            || ChangeTracker.Entries<ObservatoryInvitationDisposition>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted)
            || ChangeTracker.Entries<CuratedPublicPlacementDecision>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted)
            || ChangeTracker.Entries<LogicalCamera>().Any(entry => entry.State == EntityState.Deleted);
        if (immutableMutation)
        {
            throw new InvalidOperationException("Audit records are immutable.");
        }

        ValidateSingleSupersession<ObservatoryPublicationProfileVersion>(nameof(ObservatoryPublicationProfileVersion.SupersededAtUtc));
        ValidateSingleSupersession<ObservatoryLocationDisclosureVersion>(nameof(ObservatoryLocationDisclosureVersion.SupersededAtUtc));
        ValidateInstallationRetirement();
        if (ChangeTracker.Entries<CentralFrame>().Any(entry =>
                entry.State == EntityState.Modified
                && entry.Property(frame => frame.LogicalCameraInstallationId).IsModified))
        {
            throw new InvalidOperationException("Capture installation authority is immutable.");
        }
    }

    private void ValidateSingleSupersession<TEntity>(string supersededPropertyName)
        where TEntity : class
    {
        foreach (var entry in ChangeTracker.Entries<TEntity>())
        {
            if (entry.State == EntityState.Deleted)
            {
                throw new InvalidOperationException("Versioned authority records cannot be deleted.");
            }
            if (entry.State != EntityState.Modified)
            {
                continue;
            }
            var property = entry.Property(supersededPropertyName);
            if (entry.Properties.Any(candidate => candidate.IsModified && candidate.Metadata.Name != supersededPropertyName)
                || property.OriginalValue is not null
                || property.CurrentValue is null)
            {
                throw new InvalidOperationException("Versioned authority records allow only one terminal supersession.");
            }
        }
    }

    private void ValidateInstallationRetirement()
    {
        string[] allowedProperties =
        [
            nameof(LogicalCameraInstallation.RetiredAtUtc),
            nameof(LogicalCameraInstallation.RetiredByUserId),
            nameof(LogicalCameraInstallation.RetirementReasonCode),
            nameof(LogicalCameraInstallation.RowVersion)
        ];
        foreach (var entry in ChangeTracker.Entries<LogicalCameraInstallation>())
        {
            if (entry.State == EntityState.Deleted)
            {
                throw new InvalidOperationException("Logical camera installation history cannot be deleted.");
            }
            if (entry.State != EntityState.Modified)
            {
                continue;
            }
            if (entry.Properties.Any(property => property.IsModified
                    && !allowedProperties.Contains(property.Metadata.Name, StringComparer.Ordinal))
                || entry.Property(nameof(LogicalCameraInstallation.RetiredAtUtc)).OriginalValue is not null
                || entry.Property(nameof(LogicalCameraInstallation.RetiredAtUtc)).CurrentValue is null)
            {
                throw new InvalidOperationException("Logical camera installations allow only one terminal retirement.");
            }
        }
    }
}
