using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.Processing;
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
    internal DbSet<CentralProcessingGraphExecution> CentralProcessingGraphExecutions => Set<CentralProcessingGraphExecution>();
    internal DbSet<CentralProcessingRunner> CentralProcessingRunners => Set<CentralProcessingRunner>();
    internal DbSet<CentralProcessingUsageRecord> CentralProcessingUsageRecords => Set<CentralProcessingUsageRecord>();

    internal DbSet<CentralProcessingUsageRollup> CentralProcessingUsageRollups => Set<CentralProcessingUsageRollup>();

    internal DbSet<CentralElasticRunnerInstance> CentralElasticRunnerInstances => Set<CentralElasticRunnerInstance>();
    internal DbSet<CentralProcessingGraphExecutionSource> CentralProcessingGraphExecutionSources =>
        Set<CentralProcessingGraphExecutionSource>();
    internal DbSet<CentralDerivativeJobDependency> CentralDerivativeJobDependencies => Set<CentralDerivativeJobDependency>();
    internal DbSet<CentralDerivativeJobOutput> CentralDerivativeJobOutputs => Set<CentralDerivativeJobOutput>();
    internal DbSet<CentralClearReferenceDesignation> CentralClearReferenceDesignations => Set<CentralClearReferenceDesignation>();
    internal DbSet<CentralArtifactProcessingEvidence> CentralArtifactProcessingEvidence => Set<CentralArtifactProcessingEvidence>();
    internal DbSet<CentralCaptureTiming> CentralCaptureTimings => Set<CentralCaptureTiming>();
    internal DbSet<CentralCaptureControl> CentralCaptureControls => Set<CentralCaptureControl>();
    internal DbSet<CentralCaptureProfile> CentralCaptureProfiles => Set<CentralCaptureProfile>();
    internal DbSet<CentralArtifactLayout> CentralArtifactLayouts => Set<CentralArtifactLayout>();
    internal DbSet<CentralArtifactRecipe> CentralArtifactRecipes => Set<CentralArtifactRecipe>();
    internal DbSet<CentralStructuredProcessingProduct> CentralStructuredProcessingProducts =>
        Set<CentralStructuredProcessingProduct>();
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
    internal DbSet<CentralProcessingGraphRevision> CentralProcessingGraphRevisions => Set<CentralProcessingGraphRevision>();
    internal DbSet<CentralProcessingGraphAssignment> CentralProcessingGraphAssignments => Set<CentralProcessingGraphAssignment>();
    internal DbSet<CentralProcessingGraphDeliveryProposal> CentralProcessingGraphDeliveryProposals =>
        Set<CentralProcessingGraphDeliveryProposal>();
    internal DbSet<CentralProcessingGraphDeliveryFact> CentralProcessingGraphDeliveryFacts =>
        Set<CentralProcessingGraphDeliveryFact>();
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
    internal DbSet<DeploymentLocationReconciliationWork> DeploymentLocationReconciliationWork => Set<DeploymentLocationReconciliationWork>();
    internal DbSet<DeploymentLocationReconciliationCapture> DeploymentLocationReconciliationCaptures => Set<DeploymentLocationReconciliationCapture>();
    internal DbSet<CentralCaptureLocation> CentralCaptureLocations => Set<CentralCaptureLocation>();
    internal DbSet<DatabaseInitializationState> DatabaseInitializationState => Set<DatabaseInitializationState>();

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
        DeviceFleetConfiguration.Configure(builder);
        builder.ApplyConfiguration(new CentralFrameConfiguration());
        builder.ApplyConfiguration(new CentralArtifactConfiguration());
        builder.ApplyConfiguration(new CentralArtifactDownloadAuthorizationConfiguration());
        CentralProcessingOverrideConfiguration.Configure(builder);
        builder.ApplyConfiguration(new CentralDerivativeJobConfiguration());
        builder.ApplyConfiguration(new CentralProcessingRunnerConfiguration());
        builder.ApplyConfiguration(new CentralProcessingUsageRecordConfiguration());
        builder.ApplyConfiguration(new CentralProcessingUsageRollupConfiguration());
        builder.ApplyConfiguration(new CentralElasticRunnerInstanceConfiguration());
        CentralProcessingGraphExecutionConfiguration.Configure(builder);
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
        ProcessingGraphCatalogConfiguration.Configure(builder);
        RegisteredUserNetworkConfiguration.Configure(builder);
        DeploymentLocationAuthorityConfiguration.Configure(builder);
        builder.ApplyConfiguration(new DatabaseInitializationStateConfiguration());

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
            || ChangeTracker.Entries<CentralProcessingGraphAssignment>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted)
            || ChangeTracker.Entries<CentralProcessingGraphDeliveryProposal>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted)
            || ChangeTracker.Entries<CentralProcessingGraphDeliveryFact>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted)
            || ChangeTracker.Entries<CentralProcessingGraphExecutionSource>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted)
            || ChangeTracker.Entries<CentralDerivativeJobDependency>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted)
            || ChangeTracker.Entries<LogicalCamera>().Any(entry => entry.State == EntityState.Deleted);
        if (immutableMutation)
        {
            throw new InvalidOperationException("Audit records are immutable.");
        }

        ValidateSingleSupersession<ObservatoryPublicationProfileVersion>(nameof(ObservatoryPublicationProfileVersion.SupersededAtUtc));
        ValidateSingleSupersession<ObservatoryLocationDisclosureVersion>(nameof(ObservatoryLocationDisclosureVersion.SupersededAtUtc));
        ValidateInstallationRetirement();
        ValidateProcessingGraphRevisionTransition();
        ValidateProcessingGraphExecutionMutation();
        ValidateGraphJobMutation();
        ValidateGraphInputMutation();
        ValidateGraphOutputMutation();
        ValidateGraphEvidenceMutation();
        ValidateGraphChildInsertion();
        if (ChangeTracker.Entries<CentralFrame>().Any(entry =>
                entry.State == EntityState.Modified
                && entry.Property(frame => frame.LogicalCameraInstallationId).IsModified))
        {
            throw new InvalidOperationException("Capture installation authority is immutable.");
        }
    }

    private void ValidateProcessingGraphExecutionMutation()
    {
        string[] mutableProperties =
        [
            nameof(CentralProcessingGraphExecution.Status),
            nameof(CentralProcessingGraphExecution.UpdatedAtUtc),
            nameof(CentralProcessingGraphExecution.StartedAtUtc),
            nameof(CentralProcessingGraphExecution.CancellationRequestedAtUtc),
            nameof(CentralProcessingGraphExecution.CompletedAtUtc),
            nameof(CentralProcessingGraphExecution.ExpandedAtUtc),
            nameof(CentralProcessingGraphExecution.RowVersion)
        ];
        foreach (var entry in ChangeTracker.Entries<CentralProcessingGraphExecution>())
        {
            if (entry.State == EntityState.Deleted || entry.State == EntityState.Modified &&
                entry.Properties.Any(property => property.IsModified &&
                    !mutableProperties.Contains(property.Metadata.Name, StringComparer.Ordinal)))
            {
                throw new InvalidOperationException("Processing graph execution identity is immutable.");
            }
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.ExpandedAtUtc is not null)
                {
                    throw new InvalidOperationException("Processing graph executions must be created unsealed.");
                }
                ValidateProcessingGraphExecutionState(entry.Entity);
                continue;
            }
            if (entry.State != EntityState.Modified)
            {
                continue;
            }

            var originalStatus = (CentralProcessingGraphExecutionStatus)entry.OriginalValues[
                nameof(CentralProcessingGraphExecution.Status)]!;
            if (!IsLegalGraphExecutionTransition(originalStatus, entry.Entity.Status))
            {
                throw new InvalidOperationException("The processing graph execution status transition is invalid.");
            }
            var originalExpandedAtUtc = (DateTimeOffset?)entry.OriginalValues[
                nameof(CentralProcessingGraphExecution.ExpandedAtUtc)];
            if (entry.Property(nameof(CentralProcessingGraphExecution.ExpandedAtUtc)).IsModified &&
                (originalExpandedAtUtc is not null || entry.Entity.ExpandedAtUtc is null))
            {
                throw new InvalidOperationException("Processing graph expansion may be sealed exactly once.");
            }
            EnsureTimestampIsOneTime(entry, nameof(CentralProcessingGraphExecution.StartedAtUtc));
            EnsureTimestampIsOneTime(entry, nameof(CentralProcessingGraphExecution.CancellationRequestedAtUtc));
            EnsureTimestampIsOneTime(entry, nameof(CentralProcessingGraphExecution.CompletedAtUtc));
            if (entry.Entity.UpdatedAtUtc < (DateTimeOffset)entry.OriginalValues[
                    nameof(CentralProcessingGraphExecution.UpdatedAtUtc)]!)
            {
                throw new InvalidOperationException("Processing graph execution time cannot move backwards.");
            }
            ValidateProcessingGraphExecutionState(entry.Entity);
            if (IsTerminalGraphExecutionStatus(entry.Entity.Status))
            {
                ValidateGraphTerminalNodeOutcomes(entry);
            }
        }
    }

    private void ValidateGraphTerminalNodeOutcomes(
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<CentralProcessingGraphExecution> entry)
    {
        var trackedJobs = ChangeTracker.Entries<CentralDerivativeJob>()
            .Where(candidate => candidate.State != EntityState.Deleted &&
                candidate.Entity.GraphExecutionId == entry.Entity.Id)
            .Select(candidate => candidate.Entity)
            .ToArray();
        var jobs = entry.Collection(execution => execution.Jobs).IsLoaded
            ? entry.Entity.Jobs.ToArray()
            : trackedJobs;
        if (jobs.Length != entry.Entity.ExpectedNodeCount)
        {
            throw new InvalidOperationException(
                "All processing graph node outcomes must be loaded before recording a terminal execution.");
        }
        if (jobs.Any(job => !IsTerminalGraphJobStatus(job.Status)))
        {
            throw new InvalidOperationException(
                "Processing graph execution cannot terminate while a node is nonterminal.");
        }
        if (entry.Entity.Status == CentralProcessingGraphExecutionStatus.Completed &&
            jobs.Any(job => job.Status is not (CentralDerivativeJobStatus.Completed or
                CentralDerivativeJobStatus.Skipped)))
        {
            throw new InvalidOperationException("Completed processing graphs cannot hide node failure.");
        }
        if (entry.Entity.Status == CentralProcessingGraphExecutionStatus.CompletedWithOptionalFailures &&
            (jobs.Any(job => job.GraphFailurePolicy == ProcessingGraphNodeFailurePolicy.Required &&
                job.Status is not (CentralDerivativeJobStatus.Completed or CentralDerivativeJobStatus.Skipped)) ||
              !jobs.Any(job => job.GraphFailurePolicy == ProcessingGraphNodeFailurePolicy.Optional &&
                  job.Status is CentralDerivativeJobStatus.TerminalFailure or CentralDerivativeJobStatus.Canceled or
                      CentralDerivativeJobStatus.Quarantined or CentralDerivativeJobStatus.Superseded)))
        {
            throw new InvalidOperationException("Optional-failure completion does not match node outcomes.");
        }
    }

    /// <summary>
    /// Graph-owned <see cref="CentralDerivativeJob"/> columns whose values are frozen at expansion. This list must stay
    /// identical to the column set compared by <c>TR_CentralDerivativeJobs_GraphIdentityImmutable</c> in
    /// <c>Data/Migrations/BaselineTriggers.sql</c> ("Derivative graph executable identity is immutable.");
    /// <c>CentralProcessingGraphInvariantTests</c> diffs the two.
    /// </summary>
    internal static readonly string[] GraphJobFrozenProperties =
    [
        nameof(CentralDerivativeJob.SourceCentralArtifactId),
        nameof(CentralDerivativeJob.TargetRole),
        nameof(CentralDerivativeJob.TargetRecipeVersion),
        nameof(CentralDerivativeJob.TargetVariant),
        nameof(CentralDerivativeJob.RecipeName),
        nameof(CentralDerivativeJob.RecipeOptionsJson),
        nameof(CentralDerivativeJob.InputSelectorJson),
        nameof(CentralDerivativeJob.RequestedRecipeIdentitySha256),
        nameof(CentralDerivativeJob.ExpectedRecipeIdentitySha256),
        nameof(CentralDerivativeJob.RequestIdentitySha256),
        nameof(CentralDerivativeJob.TraceParent),
        nameof(CentralDerivativeJob.TraceState),
        nameof(CentralDerivativeJob.GraphExecutionId),
        nameof(CentralDerivativeJob.GraphNodeId),
        nameof(CentralDerivativeJob.GraphNodeOrdinal),
        nameof(CentralDerivativeJob.SharedNodePlanIdentitySha256),
        nameof(CentralDerivativeJob.FrozenNodePlanJson),
        nameof(CentralDerivativeJob.GraphFailurePolicy),
        nameof(CentralDerivativeJob.WaitKind),
        nameof(CentralDerivativeJob.ResolutionDeadlineUtc),
        nameof(CentralDerivativeJob.ResolutionStartedAtUtc),
        nameof(CentralDerivativeJob.MissingInputOutcome),
        nameof(CentralDerivativeJob.MinimumInputCount),
        nameof(CentralDerivativeJob.PredecessorJobId),
        nameof(CentralDerivativeJob.CreatedAtUtc)
    ];

    private void ValidateGraphJobMutation()
    {
        var frozenProperties = GraphJobFrozenProperties;
        foreach (var entry in ChangeTracker.Entries<CentralDerivativeJob>())
        {
            var wasGraphOwned = entry.State == EntityState.Added
                ? entry.Entity.GraphExecutionId is not null
                : (Guid?)entry.OriginalValues[nameof(CentralDerivativeJob.GraphExecutionId)] is not null;
            var isGraphOwned = wasGraphOwned || entry.Entity.GraphExecutionId is not null;
            if (entry.State == EntityState.Deleted && isGraphOwned ||
                entry.State == EntityState.Modified && isGraphOwned && entry.Properties.Any(property =>
                    property.IsModified && frozenProperties.Contains(property.Metadata.Name, StringComparer.Ordinal)))
            {
                throw new InvalidOperationException("Derivative graph executable identity is immutable.");
            }
            if (entry.State is EntityState.Added or EntityState.Modified && isGraphOwned &&
                entry.Entity.GraphExecution is { } execution && IsTerminalGraphExecutionStatus(execution.Status) &&
                !IsTerminalGraphJobStatus(entry.Entity.Status))
            {
                throw new InvalidOperationException(
                    "Terminal processing graph executions require terminal node outcomes.");
            }
            if (entry.State != EntityState.Modified || !isGraphOwned)
            {
                continue;
            }
            EnsureStringIsOneTime(entry, nameof(CentralDerivativeJob.InputSetIdentitySha256));
            EnsureTimestampIsOneTime(entry, nameof(CentralDerivativeJob.ResolutionCompletedAtUtc));
        }
    }

    private void ValidateGraphInputMutation()
    {
        string[] frozenRequirementProperties =
        [
            nameof(CentralDerivativeJobInputRequirement.CentralDerivativeJobId),
            nameof(CentralDerivativeJobInputRequirement.Ordinal),
            nameof(CentralDerivativeJobInputRequirement.BindingName),
            nameof(CentralDerivativeJobInputRequirement.GraphDependencyId),
            nameof(CentralDerivativeJobInputRequirement.GraphInputOrdinal),
            nameof(CentralDerivativeJobInputRequirement.GraphInputBindingKind),
            nameof(CentralDerivativeJobInputRequirement.SourceKind),
            nameof(CentralDerivativeJobInputRequirement.SequenceOffset),
            nameof(CentralDerivativeJobInputRequirement.IsRequired),
            nameof(CentralDerivativeJobInputRequirement.SelectorJson),
            nameof(CentralDerivativeJobInputRequirement.CompatibilityMode),
            nameof(CentralDerivativeJobInputRequirement.ExpectedAgentId),
            nameof(CentralDerivativeJobInputRequirement.ExpectedRigId),
            nameof(CentralDerivativeJobInputRequirement.ExpectedCaptureSequence)
        ];
        foreach (var entry in ChangeTracker.Entries<CentralDerivativeJobInputRequirement>())
        {
            var wasGraphOwned = entry.State == EntityState.Added
                ? entry.Entity.GraphDependencyId is not null
                : (Guid?)entry.OriginalValues[nameof(CentralDerivativeJobInputRequirement.GraphDependencyId)] is not null;
            var isGraphOwned = wasGraphOwned || entry.Entity.GraphDependencyId is not null ||
                entry.Entity.Job?.GraphExecutionId is not null;
            if (entry.State == EntityState.Deleted && isGraphOwned ||
                entry.State == EntityState.Modified && isGraphOwned && entry.Properties.Any(property =>
                    property.IsModified && frozenRequirementProperties.Contains(
                        property.Metadata.Name, StringComparer.Ordinal)))
            {
                throw new InvalidOperationException("Derivative graph input requirement identity is immutable.");
            }
            if (!isGraphOwned || entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }
            if (entry.State == EntityState.Modified)
            {
                var originalState = (CentralDerivativeInputResolutionState)entry.OriginalValues[
                    nameof(CentralDerivativeJobInputRequirement.ResolutionState)]!;
                if (originalState != CentralDerivativeInputResolutionState.Waiting &&
                    entry.Properties.Any(property => property.IsModified && property.Metadata.Name is
                        nameof(CentralDerivativeJobInputRequirement.ExpectedCentralArtifactId) or
                        nameof(CentralDerivativeJobInputRequirement.ResolutionState) or
                        nameof(CentralDerivativeJobInputRequirement.ResolutionReasonCode) or
                        nameof(CentralDerivativeJobInputRequirement.ResolvedAtUtc)))
                {
                    throw new InvalidOperationException("Derivative graph input resolution is immutable after settlement.");
                }
                if (originalState == CentralDerivativeInputResolutionState.Waiting &&
                    entry.Entity.ResolutionState is not (CentralDerivativeInputResolutionState.Waiting or
                        CentralDerivativeInputResolutionState.Resolved or
                        CentralDerivativeInputResolutionState.Missing or
                        CentralDerivativeInputResolutionState.Incompatible))
                {
                    throw new InvalidOperationException("Derivative graph input resolution transition is invalid.");
                }
            }
            ValidateGraphInputResolution(entry.Entity);
        }

        var immutableSelectedInput = ChangeTracker.Entries<CentralDerivativeJobInput>().Any(entry =>
                (entry.State is EntityState.Modified or EntityState.Deleted) && IsGraphOwned(entry.Entity))
            || ChangeTracker.Entries<CentralDerivativeJobCanonicalInput>().Any(entry =>
                (entry.State is EntityState.Modified or EntityState.Deleted) && IsGraphOwned(entry.Entity));
        if (immutableSelectedInput)
        {
            throw new InvalidOperationException("Selected derivative graph inputs are immutable.");
        }
        foreach (var entry in ChangeTracker.Entries<CentralDerivativeJobInput>().Where(entry =>
                     entry.State == EntityState.Added && IsGraphOwned(entry.Entity)))
        {
            if (!CanAppendGraphInput(entry.Entity.Job))
            {
                throw new InvalidOperationException("Selected derivative graph inputs cannot be appended after freezing.");
            }
            if (entry.Entity.Requirement is not { SourceKind: CentralDerivativeInputSourceKind.Artifact } requirement ||
                entry.Entity.Ordinal != requirement.Ordinal ||
                ChangeTracker.Entries<CentralDerivativeJobCanonicalInput>().Any(candidate =>
                    candidate.State != EntityState.Deleted &&
                    candidate.Entity.CentralDerivativeJobId == entry.Entity.CentralDerivativeJobId &&
                    candidate.Entity.CentralDerivativeJobInputRequirementId ==
                        entry.Entity.CentralDerivativeJobInputRequirementId))
            {
                throw new InvalidOperationException("Selected derivative graph input binding is inconsistent.");
            }
        }
        foreach (var entry in ChangeTracker.Entries<CentralDerivativeJobCanonicalInput>().Where(entry =>
                     entry.State == EntityState.Added && IsGraphOwned(entry.Entity)))
        {
            if (!CanAppendGraphInput(entry.Entity.Job))
            {
                throw new InvalidOperationException("Selected derivative graph inputs cannot be appended after freezing.");
            }
            if (entry.Entity.Requirement is not { SourceKind: not CentralDerivativeInputSourceKind.Artifact } requirement ||
                entry.Entity.Ordinal != requirement.Ordinal ||
                ChangeTracker.Entries<CentralDerivativeJobInput>().Any(candidate =>
                    candidate.State != EntityState.Deleted &&
                    candidate.Entity.CentralDerivativeJobId == entry.Entity.CentralDerivativeJobId &&
                    candidate.Entity.CentralDerivativeJobInputRequirementId ==
                        entry.Entity.CentralDerivativeJobInputRequirementId))
            {
                throw new InvalidOperationException("Selected derivative graph input binding is inconsistent.");
            }
        }
    }

    private void ValidateGraphChildInsertion()
    {
        var insertsIntoSealedExecution = ChangeTracker.Entries<CentralProcessingGraphExecutionSource>().Any(entry =>
                entry.State == EntityState.Added && entry.Entity.Execution?.ExpandedAtUtc is not null)
            || ChangeTracker.Entries<CentralDerivativeJobDependency>().Any(entry =>
                entry.State == EntityState.Added && entry.Entity.Execution?.ExpandedAtUtc is not null)
            || ChangeTracker.Entries<CentralDerivativeJob>().Any(entry => entry.State == EntityState.Added &&
                entry.Entity.GraphExecution?.ExpandedAtUtc is not null)
            || ChangeTracker.Entries<CentralDerivativeJobInputRequirement>().Any(entry =>
                entry.State == EntityState.Added && entry.Entity.Job?.GraphExecution?.ExpandedAtUtc is not null)
            || ChangeTracker.Entries<CentralDerivativeJobOutput>().Any(entry => entry.State == EntityState.Added &&
                entry.Entity.Job?.GraphExecution?.ExpandedAtUtc is not null);
        if (insertsIntoSealedExecution)
        {
            throw new InvalidOperationException("Processing graph topology cannot change after expansion is sealed.");
        }
    }

    private static bool IsGraphOwned(CentralDerivativeJobInput input)
        => input.Job?.GraphExecutionId is not null || input.Requirement?.GraphDependencyId is not null;

    private static bool IsGraphOwned(CentralDerivativeJobCanonicalInput input)
        => input.Job?.GraphExecutionId is not null || input.Requirement?.GraphDependencyId is not null;

    private bool CanAppendGraphInput(CentralDerivativeJob? job)
    {
        if (job?.GraphExecution?.ExpandedAtUtc is null)
        {
            return false;
        }
        var jobEntry = Entry(job);
        return jobEntry.State != EntityState.Added &&
            jobEntry.OriginalValues[nameof(CentralDerivativeJob.InputSetIdentitySha256)] is null;
    }

    private static void ValidateGraphInputResolution(CentralDerivativeJobInputRequirement requirement)
    {
        var valid = requirement.ResolutionState switch
        {
            CentralDerivativeInputResolutionState.Waiting => requirement.ExpectedCentralArtifactId is null &&
                requirement.ResolvedAtUtc is null,
            CentralDerivativeInputResolutionState.Resolved => requirement.ResolvedAtUtc is not null &&
                (requirement.SourceKind == CentralDerivativeInputSourceKind.Artifact
                    ? requirement.ExpectedCentralArtifactId is not null
                    : requirement.ExpectedCentralArtifactId is null),
            CentralDerivativeInputResolutionState.Missing or CentralDerivativeInputResolutionState.Incompatible =>
                requirement.ExpectedCentralArtifactId is null && requirement.ResolvedAtUtc is not null &&
                !string.IsNullOrWhiteSpace(requirement.ResolutionReasonCode),
            _ => false
        };
        if (!valid)
        {
            throw new InvalidOperationException("Derivative graph input resolution evidence is inconsistent.");
        }
    }

    private static void ValidateProcessingGraphExecutionState(CentralProcessingGraphExecution execution)
    {
        if (!Enum.IsDefined(execution.ExecutionClass) || !Enum.IsDefined(execution.Status) ||
            !Enum.IsDefined(execution.Trigger) || execution.RevisionId == Guid.Empty ||
            execution.ExpectedSourceCount is < 0 or > ProcessingGraphCompiler.MaximumSources ||
            execution.ExpectedNodeCount is < 0 or > ProcessingGraphCompiler.MaximumNodes ||
            execution.ExpectedDependencyCount is < 0 or >
                CentralProcessingGraphExecutionConfiguration.MaximumExpectedDependencyCount ||
            execution.ExpectedOutputCount is < 0 or >
                CentralProcessingGraphExecutionConfiguration.MaximumExpectedOutputCount ||
            execution.ExecutionClass == CentralProcessingGraphExecutionClass.Live &&
                (execution.Trigger != CentralProcessingGraphTrigger.Ingest || execution.AssignmentId is null) ||
            execution.ExecutionClass == CentralProcessingGraphExecutionClass.Replay &&
                (execution.Trigger is not (CentralProcessingGraphTrigger.Replay or CentralProcessingGraphTrigger.Reprocess) ||
                    execution.AssignmentId is not null) ||
            !IsBoundedNonWhiteSpace(execution.ActorId, 450) ||
            !IsBoundedNonWhiteSpace(execution.IdempotencyKey, 256) ||
            !IsBoundedNonWhiteSpace(execution.ReasonCode, 128) ||
            execution.ExpandedAtUtc is null && execution.Status != CentralProcessingGraphExecutionStatus.Pending ||
            execution.ExpandedAtUtc < execution.CreatedAtUtc || execution.UpdatedAtUtc < execution.CreatedAtUtc)
        {
            throw new InvalidOperationException("Processing graph execution state is inconsistent.");
        }
        var validTimestamps = execution.Status switch
        {
            CentralProcessingGraphExecutionStatus.Pending => execution.StartedAtUtc is null &&
                execution.CancellationRequestedAtUtc is null && execution.CompletedAtUtc is null,
            CentralProcessingGraphExecutionStatus.Running => execution.StartedAtUtc is not null &&
                execution.CancellationRequestedAtUtc is null && execution.CompletedAtUtc is null,
            CentralProcessingGraphExecutionStatus.CancelRequested =>
                execution.CancellationRequestedAtUtc is not null && execution.CompletedAtUtc is null,
            CentralProcessingGraphExecutionStatus.Completed or
                CentralProcessingGraphExecutionStatus.CompletedWithOptionalFailures =>
                execution.StartedAtUtc is not null && execution.CompletedAtUtc is not null,
            CentralProcessingGraphExecutionStatus.Failed => execution.CompletedAtUtc is not null,
            CentralProcessingGraphExecutionStatus.Canceled => execution.CancellationRequestedAtUtc is not null &&
                execution.CompletedAtUtc is not null,
            CentralProcessingGraphExecutionStatus.Superseded => execution.CompletedAtUtc is not null,
            _ => false
        };
        if (!validTimestamps || execution.StartedAtUtc < execution.CreatedAtUtc ||
            execution.CancellationRequestedAtUtc < execution.CreatedAtUtc ||
            execution.CompletedAtUtc < execution.CreatedAtUtc || execution.ExpandedAtUtc > execution.UpdatedAtUtc)
        {
            throw new InvalidOperationException("Processing graph execution timestamps are inconsistent.");
        }
        if (execution.Revision is not null &&
            (execution.Revision.PublishedAtUtc is null || execution.Revision.PublishedAtUtc > execution.CreatedAtUtc))
        {
            throw new InvalidOperationException("Processing graph execution requires a published revision.");
        }
    }

    private static bool IsLegalGraphExecutionTransition(
        CentralProcessingGraphExecutionStatus before,
        CentralProcessingGraphExecutionStatus after) => before switch
        {
            CentralProcessingGraphExecutionStatus.Pending => after is CentralProcessingGraphExecutionStatus.Pending or
                CentralProcessingGraphExecutionStatus.Running or CentralProcessingGraphExecutionStatus.CancelRequested or
                CentralProcessingGraphExecutionStatus.Failed or CentralProcessingGraphExecutionStatus.Superseded,
            CentralProcessingGraphExecutionStatus.Running => after is CentralProcessingGraphExecutionStatus.Running or
                CentralProcessingGraphExecutionStatus.Completed or
                CentralProcessingGraphExecutionStatus.CompletedWithOptionalFailures or
                CentralProcessingGraphExecutionStatus.Failed or CentralProcessingGraphExecutionStatus.CancelRequested or
                CentralProcessingGraphExecutionStatus.Superseded,
            CentralProcessingGraphExecutionStatus.CancelRequested =>
                after is CentralProcessingGraphExecutionStatus.CancelRequested or
                    CentralProcessingGraphExecutionStatus.Canceled or CentralProcessingGraphExecutionStatus.Failed or
                    CentralProcessingGraphExecutionStatus.Superseded,
            // Completed, CompletedWithOptionalFailures, Failed, Canceled, and Superseded are final; node recovery goes
            // through graph replay, never by reopening the terminal execution row.
            _ => before == after
        };

    private static bool IsTerminalGraphExecutionStatus(CentralProcessingGraphExecutionStatus status)
        => status is CentralProcessingGraphExecutionStatus.Completed or
            CentralProcessingGraphExecutionStatus.CompletedWithOptionalFailures or
            CentralProcessingGraphExecutionStatus.Failed or
            CentralProcessingGraphExecutionStatus.Canceled or
            CentralProcessingGraphExecutionStatus.Superseded;

    private static bool IsTerminalGraphJobStatus(CentralDerivativeJobStatus status)
        => status is CentralDerivativeJobStatus.Completed or CentralDerivativeJobStatus.TerminalFailure or
            CentralDerivativeJobStatus.Canceled or CentralDerivativeJobStatus.Skipped or
            CentralDerivativeJobStatus.Quarantined or CentralDerivativeJobStatus.Superseded;

    private static bool IsBoundedNonWhiteSpace(string? value, int maximumLength)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength;

    private static void EnsureTimestampIsOneTime(
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry,
        string propertyName)
    {
        if (!entry.Property(propertyName).IsModified)
        {
            return;
        }
        if (entry.OriginalValues[propertyName] is not null || entry.CurrentValues[propertyName] is null)
        {
            throw new InvalidOperationException($"{propertyName} may be recorded exactly once.");
        }
    }

    private static void EnsureStringIsOneTime(
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry,
        string propertyName)
    {
        if (entry.Property(propertyName).IsModified &&
            (entry.OriginalValues[propertyName] is not null || entry.CurrentValues[propertyName] is null))
        {
            throw new InvalidOperationException($"{propertyName} may be recorded exactly once.");
        }
    }

    private void ValidateGraphOutputMutation()
    {
        string[] bindingProperties =
        [
            nameof(CentralDerivativeJobOutput.ResultCentralArtifactId),
            nameof(CentralDerivativeJobOutput.ResultOutputIdentitySha256),
            nameof(CentralDerivativeJobOutput.BoundAtUtc),
            nameof(CentralDerivativeJobOutput.RowVersion)
        ];
        foreach (var entry in ChangeTracker.Entries<CentralDerivativeJobOutput>())
        {
            if (entry.State == EntityState.Added &&
                (entry.Entity.ResultCentralArtifactId is not null ||
                 entry.Entity.ResultOutputIdentitySha256 is not null ||
                 entry.Entity.BoundAtUtc is not null))
            {
                throw new InvalidOperationException("Derivative graph output slots must be created unbound.");
            }
            if (entry.State == EntityState.Deleted)
            {
                throw new InvalidOperationException("Derivative graph output contracts are immutable.");
            }
            if (entry.State != EntityState.Modified)
            {
                continue;
            }
            var changedOnlyBinding = entry.Properties.All(property => !property.IsModified ||
                bindingProperties.Contains(property.Metadata.Name, StringComparer.Ordinal));
            var wasUnbound = entry.OriginalValues[nameof(CentralDerivativeJobOutput.ResultCentralArtifactId)] is null &&
                entry.OriginalValues[nameof(CentralDerivativeJobOutput.ResultOutputIdentitySha256)] is null &&
                entry.OriginalValues[nameof(CentralDerivativeJobOutput.BoundAtUtc)] is null;
            var isBound = entry.CurrentValues[nameof(CentralDerivativeJobOutput.ResultCentralArtifactId)] is not null &&
                entry.CurrentValues[nameof(CentralDerivativeJobOutput.ResultOutputIdentitySha256)] is not null &&
                entry.CurrentValues[nameof(CentralDerivativeJobOutput.BoundAtUtc)] is not null;
            if (!changedOnlyBinding || !wasUnbound || !isBound)
            {
                throw new InvalidOperationException("Derivative graph output slots may bind exactly once.");
            }
        }
    }

    private void ValidateGraphEvidenceMutation()
    {
        foreach (var entry in ChangeTracker.Entries<CentralArtifactProcessingEvidence>().Where(entry =>
                     entry.State == EntityState.Modified && entry.Properties.Any(property => property.IsModified &&
                          property.Metadata.Name is nameof(entry.Entity.GraphProductContractIdentitySha256) or
                              nameof(entry.Entity.RecipeOperationKind) or
                              nameof(entry.Entity.ProductKind) or
                             nameof(entry.Entity.ProductSchemaVersion) or
                             nameof(entry.Entity.ProductMediaType))))
        {
            throw new InvalidOperationException("Derivative graph product-contract evidence is immutable.");
        }
    }

    private void ValidateProcessingGraphRevisionTransition()
    {
        string[] lifecycleProperties =
        [
            nameof(CentralProcessingGraphRevision.PublishedAtUtc),
            nameof(CentralProcessingGraphRevision.PublishedByUserId),
            nameof(CentralProcessingGraphRevision.RetiredAtUtc),
            nameof(CentralProcessingGraphRevision.RetiredByUserId),
            nameof(CentralProcessingGraphRevision.RetirementReasonCode)
        ];
        foreach (var entry in ChangeTracker.Entries<CentralProcessingGraphRevision>())
        {
            if (entry.State == EntityState.Deleted || entry.State == EntityState.Modified &&
                entry.Properties.Any(property => property.IsModified &&
                    !lifecycleProperties.Contains(property.Metadata.Name, StringComparer.Ordinal)))
            {
                throw new InvalidOperationException("Processing graph revision content is immutable.");
            }
            if (entry.State != EntityState.Modified)
            {
                continue;
            }
            var publishedBefore = (DateTimeOffset?)entry.OriginalValues[nameof(CentralProcessingGraphRevision.PublishedAtUtc)];
            var publishedAfter = (DateTimeOffset?)entry.CurrentValues[nameof(CentralProcessingGraphRevision.PublishedAtUtc)];
            var publishedByBefore = (string?)entry.OriginalValues[nameof(CentralProcessingGraphRevision.PublishedByUserId)];
            var publishedByAfter = (string?)entry.CurrentValues[nameof(CentralProcessingGraphRevision.PublishedByUserId)];
            var retiredBefore = (DateTimeOffset?)entry.OriginalValues[nameof(CentralProcessingGraphRevision.RetiredAtUtc)];
            var retiredAfter = (DateTimeOffset?)entry.CurrentValues[nameof(CentralProcessingGraphRevision.RetiredAtUtc)];
            var retiredByAfter = (string?)entry.CurrentValues[nameof(CentralProcessingGraphRevision.RetiredByUserId)];
            var retirementReasonAfter = (string?)entry.CurrentValues[nameof(CentralProcessingGraphRevision.RetirementReasonCode)];
            var isPublish = publishedBefore is null && publishedByBefore is null && retiredBefore is null &&
                publishedAfter is not null && !string.IsNullOrWhiteSpace(publishedByAfter) && retiredAfter is null &&
                retiredByAfter is null && retirementReasonAfter is null;
            var isRetire = publishedBefore is not null && publishedAfter == publishedBefore &&
                string.Equals(publishedByAfter, publishedByBefore, StringComparison.Ordinal) && retiredBefore is null &&
                retiredAfter >= publishedBefore && !string.IsNullOrWhiteSpace(retiredByAfter) &&
                !string.IsNullOrWhiteSpace(retirementReasonAfter);
            if (!isPublish && !isRetire)
            {
                throw new InvalidOperationException("Processing graph lifecycle transitions are append-only.");
            }
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
