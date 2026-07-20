using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Data;

internal static class CentralTransientValidationConfiguration
{
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    public static void Configure(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ConfigureEvent(builder);
        ConfigureEventVersion(builder);
        ConfigureObservation(builder);
        ConfigureSource(builder);
        ConfigureBackground(builder);
        ConfigureAssessment(builder);
        ConfigureEventVersionObservation(builder);
        ConfigureEventVersionAssessment(builder);
        ConfigureAssessmentObservation(builder);
        ConfigureValidationJob(builder);
        ConfigureExtractionReceipt(builder);
        ConfigureExtractionSource(builder);
        ConfigureIdentitySlot(builder);
        ConfigureContextDependency(builder);
        ConfigureOutcomeVersion(builder);
    }

    private static void ConfigureEvent(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralTransientEventRecord>();
        entity.ToTable("CentralTransientEvents", table => table.HasTrigger("TR_CentralTransientEvents_Immutable"));
        entity.HasKey(item => item.Id);
        entity.Property(item => item.AgentId).HasMaxLength(128).UseCollation(BinaryCollation).IsRequired();
        entity.HasAlternateKey(item => new { item.AgentId, item.EventId });
        entity.HasAlternateKey(item => new { item.Id, item.EventId });
        entity.HasIndex(item => new { item.AgentId, item.EventCreatedUtc, item.EventId });
    }

    private static void ConfigureEventVersion(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralTransientEventVersionRecord>();
        entity.ToTable("CentralTransientEventVersions", table =>
        {
            table.HasTrigger("TR_CentralTransientEventVersions_Immutable");
            table.HasCheckConstraint("CK_CentralTransientEventVersions_Version", "[Version] > 0");
            table.HasCheckConstraint("CK_CentralTransientEventVersions_ReceiptLength", "[CanonicalEventByteLength] > 0");
            table.HasCheckConstraint("CK_CentralTransientEventVersions_ObservedInterval", "[FirstObservedUtc] <= [LastObservedUtc] AND [LastObservedUtc] <= [VersionCreatedUtc]");
            table.HasCheckConstraint("CK_CentralTransientEventVersions_Predecessor", "([Version] = 1 AND [PreviousVersionNumber] IS NULL AND [PreviousEventVersionId] IS NULL AND [PreviousVersionCreatedUtc] IS NULL) OR ([Version] > 1 AND [PreviousVersionNumber] IS NOT NULL AND [PreviousVersionNumber] = [Version] - 1 AND [PreviousEventVersionId] IS NOT NULL AND [PreviousEventVersionId] <> [EventVersionId] AND [PreviousVersionCreatedUtc] IS NOT NULL AND [PreviousVersionCreatedUtc] < [VersionCreatedUtc])");
        });
        entity.HasKey(item => item.EventVersionId);
        entity.HasAlternateKey(item => new { item.CentralTransientEventId, item.EventVersionId });
        entity.HasAlternateKey(item => new
        {
            item.CentralTransientEventId,
            item.Version,
            item.EventVersionId,
            item.VersionCreatedUtc
        });
        entity.Property(item => item.State).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.SchemaVersion).HasMaxLength(128).IsRequired();
        entity.Property(item => item.CanonicalEventJson).IsRequired();
        Sha256(entity.Property(item => item.CanonicalEventSha256));
        entity.HasIndex(item => new { item.CentralTransientEventId, item.Version }).IsUnique();
        entity.HasIndex(item => new { item.CentralTransientEventId, item.PreviousEventVersionId })
            .IsUnique().HasFilter("[PreviousEventVersionId] IS NOT NULL");
        entity.HasOne(item => item.Event).WithMany(item => item.Versions)
            .HasForeignKey(item => item.CentralTransientEventId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.PreviousVersion).WithMany()
            .HasForeignKey(item => new
            {
                item.CentralTransientEventId,
                item.PreviousVersionNumber,
                item.PreviousEventVersionId,
                item.PreviousVersionCreatedUtc
            })
            .HasPrincipalKey(item => new
            {
                item.CentralTransientEventId,
                item.Version,
                item.EventVersionId,
                item.VersionCreatedUtc
            })
            .OnDelete(DeleteBehavior.NoAction);
    }

    private static void ConfigureObservation(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralTransientObservationRecord>();
        entity.ToTable("CentralTransientObservations", table =>
        {
            table.HasTrigger("TR_CentralTransientObservations_Immutable");
            table.HasCheckConstraint("CK_CentralTransientObservations_SourceReference", "[SourceReferenceId] = [ObservationId]");
        });
        entity.HasKey(item => item.ObservationId);
        entity.HasAlternateKey(item => new { item.CentralTransientEventId, item.ObservationId });
        Sha256(entity.Property(item => item.DetectorInputIdentitySha256));
        entity.Property(item => item.CalibrationIdentity).HasMaxLength(256).UseCollation(BinaryCollation).IsRequired();
        entity.Property(item => item.MaskIdentity).HasMaxLength(256).UseCollation(BinaryCollation).IsRequired();
        entity.Property(item => item.ProcessingProfileIdentity).HasMaxLength(256).UseCollation(BinaryCollation).IsRequired();
        entity.Property(item => item.ExtractionProducerSchemaVersion).HasMaxLength(128).IsRequired();
        entity.Property(item => item.ExtractionProducerKind).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.ExtractionProducerName).HasMaxLength(128).UseCollation(BinaryCollation).IsRequired();
        entity.Property(item => item.ExtractionProducerVersion).HasMaxLength(128).UseCollation(BinaryCollation).IsRequired();
        Sha256(entity.Property(item => item.ExtractionRecipeIdentitySha256));
        Sha256(entity.Property(item => item.ExtractionReceiptIdentitySha256));
        entity.Property(item => item.GeometryJson).IsRequired();
        entity.Property(item => item.FeaturesJson).IsRequired();
        entity.HasIndex(item => item.OriginatingCandidateId).IsUnique().HasFilter("[OriginatingCandidateId] IS NOT NULL");
        entity.HasOne(item => item.Event).WithMany(item => item.Observations)
            .HasForeignKey(item => item.CentralTransientEventId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.Source).WithOne(item => item.Observation)
            .HasForeignKey<CentralTransientObservationRecord>(item => item.SourceReferenceId)
            .OnDelete(DeleteBehavior.Restrict).IsRequired();
    }

    private static void ConfigureSource(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralTransientObservationSourceReference>();
        entity.ToTable("CentralTransientObservationSources", table =>
        {
            table.HasTrigger("TR_CentralTransientObservationSources_Immutable");
            table.HasCheckConstraint("CK_CentralTransientObservationSources_ObservedInterval", "[ObservationStartedUtc] <= [ObservationEndedUtc]");
        });
        entity.HasKey(item => item.ObservationId);
        entity.Property(item => item.EvidenceSchemaVersion).HasMaxLength(128).IsRequired();
        entity.Property(item => item.LocatorSchemaVersion).HasMaxLength(128).IsRequired();
        entity.Property(item => item.LocatorKind).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.ArtifactRole).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.ArtifactVariant).HasMaxLength(128).UseCollation(BinaryCollation).IsRequired();
        Sha256(entity.Property(item => item.ArtifactRecipeIdentitySha256));
        Sha256(entity.Property(item => item.ArtifactChecksumSha256));
        entity.Property(item => item.TimingQuality).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.TimingProvenanceSource).HasMaxLength(128).UseCollation(BinaryCollation).IsRequired();
        entity.Property(item => item.TimingProvenanceVersion).HasMaxLength(128).UseCollation(BinaryCollation).IsRequired();
        entity.HasIndex(item => item.EvidenceId);
        entity.HasIndex(item => item.CentralArtifactId);
        entity.HasOne(item => item.Artifact).WithMany()
            .HasForeignKey(item => item.CentralArtifactId).OnDelete(DeleteBehavior.Restrict).IsRequired();
    }

    private static void ConfigureBackground(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralTransientObservationBackgroundReference>();
        entity.ToTable("CentralTransientObservationBackgrounds", table =>
        {
            table.HasTrigger("TR_CentralTransientObservationBackgrounds_Immutable");
            table.HasCheckConstraint("CK_CentralTransientObservationBackgrounds_Ordinal", "[Ordinal] >= 0");
        });
        entity.HasKey(item => item.Id);
        entity.Property(item => item.ArtifactRole).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.ArtifactVariant).HasMaxLength(128).UseCollation(BinaryCollation).IsRequired();
        Sha256(entity.Property(item => item.ArtifactRecipeIdentitySha256));
        Sha256(entity.Property(item => item.ArtifactChecksumSha256));
        entity.HasIndex(item => new { item.ObservationId, item.Ordinal }).IsUnique();
        entity.HasIndex(item => new { item.ObservationId, item.CentralArtifactId }).IsUnique();
        entity.HasIndex(item => item.CentralArtifactId);
        entity.HasOne(item => item.Observation).WithMany(item => item.Backgrounds)
            .HasForeignKey(item => item.ObservationId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.Artifact).WithMany()
            .HasForeignKey(item => item.CentralArtifactId).OnDelete(DeleteBehavior.Restrict).IsRequired();
    }

    private static void ConfigureAssessment(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralTransientAssessmentRecord>();
        entity.ToTable("CentralTransientAssessments", table =>
        {
            table.HasTrigger("TR_CentralTransientAssessments_Immutable");
            table.HasCheckConstraint("CK_CentralTransientAssessments_Confidence", "[ConfidenceMillionths] >= 0 AND [ConfidenceMillionths] <= 1000000");
            table.HasCheckConstraint("CK_CentralTransientAssessments_ReceiptLength", "[CanonicalReceiptByteLength] > 0");
            table.HasCheckConstraint("CK_CentralTransientAssessments_Predecessor", "([SupersedesAssessmentId] IS NULL AND [SupersedesAssessmentCreatedUtc] IS NULL) OR ([SupersedesAssessmentId] IS NOT NULL AND [SupersedesAssessmentId] <> [AssessmentId] AND [SupersedesAssessmentCreatedUtc] IS NOT NULL AND [SupersedesAssessmentCreatedUtc] < [CreatedUtc])");
        });
        entity.HasKey(item => item.AssessmentId);
        entity.HasAlternateKey(item => new { item.CentralTransientEventId, item.AssessmentId });
        entity.HasAlternateKey(item => new
        {
            item.CentralTransientEventId,
            item.AssessmentId,
            item.CreatedUtc
        });
        entity.Property(item => item.Authority).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.Classification).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.MeteorSeverity).HasConversion<string>().HasMaxLength(32);
        entity.Property(item => item.ProducerSchemaVersion).HasMaxLength(128).IsRequired();
        entity.Property(item => item.ProducerKind).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.ProducerName).HasMaxLength(128).UseCollation(BinaryCollation).IsRequired();
        entity.Property(item => item.ProducerVersion).HasMaxLength(128).UseCollation(BinaryCollation).IsRequired();
        Sha256(entity.Property(item => item.RecipeIdentitySha256));
        entity.Property(item => item.ReceiptSchemaVersion).HasMaxLength(128).IsRequired();
        Sha256(entity.Property(item => item.ExecutionIdentitySha256));
        Sha256(entity.Property(item => item.OptionsIdentitySha256));
        entity.Property(item => item.CanonicalReceiptJson).IsRequired();
        Sha256(entity.Property(item => item.CanonicalReceiptSha256));
        entity.HasIndex(item => new
        {
            item.CentralTransientEventId,
            item.ProducerSchemaVersion,
            item.ProducerKind,
            item.ProducerName,
            item.ProducerVersion,
            item.RecipeIdentitySha256,
            item.ExecutionIdentitySha256
        }).IsUnique();
        entity.HasIndex(item => new { item.CentralTransientEventId, item.SupersedesAssessmentId })
            .IsUnique().HasFilter("[SupersedesAssessmentId] IS NOT NULL");
        entity.HasOne(item => item.Event).WithMany(item => item.Assessments)
            .HasForeignKey(item => item.CentralTransientEventId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.SupersedesAssessment).WithMany()
            .HasForeignKey(item => new
            {
                item.CentralTransientEventId,
                item.SupersedesAssessmentId,
                item.SupersedesAssessmentCreatedUtc
            })
            .HasPrincipalKey(item => new
            {
                item.CentralTransientEventId,
                item.AssessmentId,
                item.CreatedUtc
            })
            .OnDelete(DeleteBehavior.NoAction);
    }

    private static void ConfigureEventVersionObservation(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralTransientEventVersionObservation>();
        entity.ToTable("CentralTransientEventVersionObservations", table =>
        {
            table.HasTrigger("TR_CentralTransientEventVersionObservations_Immutable");
            table.HasCheckConstraint("CK_CentralTransientEventVersionObservations_Ordinal", "[Ordinal] >= 0");
        });
        entity.HasKey(item => new { item.EventVersionId, item.Ordinal });
        entity.HasIndex(item => new { item.EventVersionId, item.ObservationId }).IsUnique();
        entity.HasOne(item => item.EventVersion).WithMany(item => item.Observations)
            .HasForeignKey(item => new { item.CentralTransientEventId, item.EventVersionId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.EventVersionId })
            .OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.Observation).WithMany()
            .HasForeignKey(item => new { item.CentralTransientEventId, item.ObservationId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.ObservationId })
            .OnDelete(DeleteBehavior.NoAction).IsRequired();
    }

    private static void ConfigureEventVersionAssessment(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralTransientEventVersionAssessment>();
        entity.ToTable("CentralTransientEventVersionAssessments", table =>
        {
            table.HasTrigger("TR_CentralTransientEventVersionAssessments_Immutable");
            table.HasCheckConstraint("CK_CentralTransientEventVersionAssessments_Ordinal", "[Ordinal] >= 0");
        });
        entity.HasKey(item => new { item.EventVersionId, item.Ordinal });
        entity.HasIndex(item => new { item.EventVersionId, item.AssessmentId }).IsUnique();
        entity.HasOne(item => item.EventVersion).WithMany(item => item.Assessments)
            .HasForeignKey(item => new { item.CentralTransientEventId, item.EventVersionId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.EventVersionId })
            .OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.Assessment).WithMany()
            .HasForeignKey(item => new { item.CentralTransientEventId, item.AssessmentId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.AssessmentId })
            .OnDelete(DeleteBehavior.NoAction).IsRequired();
    }

    private static void ConfigureAssessmentObservation(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralTransientAssessmentObservation>();
        entity.ToTable("CentralTransientAssessmentObservations", table =>
        {
            table.HasTrigger("TR_CentralTransientAssessmentObservations_Immutable");
            table.HasCheckConstraint("CK_CentralTransientAssessmentObservations_Ordinal", "[Ordinal] >= 0");
        });
        entity.HasKey(item => new { item.AssessmentId, item.Ordinal });
        entity.HasIndex(item => new { item.AssessmentId, item.ObservationId }).IsUnique();
        entity.HasOne(item => item.Assessment).WithMany(item => item.EvidenceObservations)
            .HasForeignKey(item => new { item.CentralTransientEventId, item.AssessmentId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.AssessmentId })
            .OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.Observation).WithMany()
            .HasForeignKey(item => new { item.CentralTransientEventId, item.ObservationId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.ObservationId })
            .OnDelete(DeleteBehavior.NoAction).IsRequired();
    }

    private static void ConfigureValidationJob(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralTransientValidationJob>();
        entity.ToTable("CentralTransientValidationJobs", table =>
        {
            table.HasTrigger("TR_CentralTransientValidationJobs_CommittedImmutable");
            table.HasCheckConstraint("CK_CentralTransientValidationJobs_ExecutionOptions", "([ExecutionOptionsJson] IS NULL AND [ExecutionOptionsIdentitySha256] IS NULL) OR ([ExecutionOptionsJson] IS NOT NULL AND [ExecutionOptionsIdentitySha256] IS NOT NULL)");
            table.HasCheckConstraint("CK_CentralTransientValidationJobs_Outcome", "([OutcomeRecordedAtUtc] IS NULL AND [OutcomeState] IS NULL AND [OutcomeReasonCode] IS NULL AND [OutcomeEvidenceJson] IS NULL AND [OutcomeEvidenceIdentitySha256] IS NULL) OR ([OutcomeRecordedAtUtc] IS NOT NULL AND [OutcomeState] IS NOT NULL AND [OutcomeReasonCode] IS NOT NULL AND [OutcomeEvidenceJson] IS NOT NULL AND [OutcomeEvidenceIdentitySha256] IS NOT NULL)");
            table.HasCheckConstraint("CK_CentralTransientValidationJobs_CommitOutcome", "[CommittedAtUtc] IS NULL OR [OutcomeRecordedAtUtc] IS NOT NULL");
        });
        entity.HasKey(item => item.CentralDerivativeJobId);
        entity.Property(item => item.AgentId).HasMaxLength(128).UseCollation(BinaryCollation).IsRequired();
        entity.Property(item => item.SubmissionSchemaVersion).HasMaxLength(128).IsRequired();
        Sha256(entity.Property(item => item.SubmissionIdentitySha256));
        entity.Property(item => item.OutcomeState).HasConversion<string>().HasMaxLength(32);
        entity.Property(item => item.OutcomeReasonCode).HasMaxLength(256).UseCollation(BinaryCollation);
        Sha256Optional(entity.Property(item => item.ExecutionOptionsIdentitySha256));
        Sha256Optional(entity.Property(item => item.OutcomeEvidenceIdentitySha256));
        entity.HasIndex(item => item.SubmissionIdentitySha256).IsUnique();
        entity.HasIndex(item => item.ProvisionalCentralDerivativeJobId).IsUnique()
            .HasFilter("[ProvisionalCentralDerivativeJobId] IS NOT NULL");
        entity.HasOne(item => item.Job).WithOne()
            .HasForeignKey<CentralTransientValidationJob>(item => item.CentralDerivativeJobId)
            .OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.ProvisionalValidationJob).WithMany()
            .HasForeignKey(item => item.ProvisionalCentralDerivativeJobId)
            .OnDelete(DeleteBehavior.NoAction);
    }

    private static void ConfigureExtractionReceipt(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralTransientExtractionReceipt>();
        entity.ToTable("CentralTransientExtractionReceipts", table =>
        {
            table.HasTrigger("TR_CentralTransientExtractionReceipts_Immutable");
            table.HasCheckConstraint("CK_CentralTransientExtractionReceipts_ReceiptLength", "[CanonicalReceiptByteLength] > 0");
        });
        entity.HasKey(item => item.CentralDerivativeJobId);
        entity.Property(item => item.SchemaVersion).HasMaxLength(128).IsRequired();
        Sha256(entity.Property(item => item.ExtractionIdentitySha256));
        Sha256(entity.Property(item => item.OptionsIdentitySha256));
        entity.Property(item => item.CanonicalReceiptJson).IsRequired();
        Sha256(entity.Property(item => item.CanonicalReceiptSha256));
        entity.HasIndex(item => item.ExtractionIdentitySha256).IsUnique();
        entity.HasOne(item => item.ValidationJob).WithOne(item => item.ExtractionReceipt)
            .HasForeignKey<CentralTransientExtractionReceipt>(item => item.CentralDerivativeJobId)
            .OnDelete(DeleteBehavior.Restrict).IsRequired();
    }

    private static void ConfigureExtractionSource(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralTransientExtractionSourceReference>();
        entity.ToTable("CentralTransientExtractionSources", table =>
        {
            table.HasTrigger("TR_CentralTransientExtractionSources_Immutable");
            table.HasCheckConstraint("CK_CentralTransientExtractionSources_Ordinal", "[Ordinal] >= 0");
            table.HasCheckConstraint("CK_CentralTransientExtractionSources_ObservedInterval", "[ObservationStartedUtc] <= [ObservationEndedUtc]");
        });
        entity.HasKey(item => new { item.CentralDerivativeJobId, item.Ordinal });
        entity.Property(item => item.Position).HasConversion<string>().HasMaxLength(32).IsRequired();
        Sha256(entity.Property(item => item.DetectorInputIdentitySha256));
        entity.Property(item => item.EvidenceSchemaVersion).HasMaxLength(128).IsRequired();
        entity.Property(item => item.LocatorSchemaVersion).HasMaxLength(128).IsRequired();
        entity.Property(item => item.LocatorKind).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.ArtifactRole).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.ArtifactVariant).HasMaxLength(128).UseCollation(BinaryCollation).IsRequired();
        Sha256(entity.Property(item => item.ArtifactRecipeIdentitySha256));
        Sha256(entity.Property(item => item.ArtifactChecksumSha256));
        entity.Property(item => item.TimingQuality).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.TimingProvenanceSource).HasMaxLength(128).UseCollation(BinaryCollation).IsRequired();
        entity.Property(item => item.TimingProvenanceVersion).HasMaxLength(128).UseCollation(BinaryCollation).IsRequired();
        entity.HasIndex(item => new { item.CentralDerivativeJobId, item.EvidenceId }).IsUnique();
        entity.HasIndex(item => item.CentralArtifactId);
        entity.HasOne(item => item.ExtractionReceipt).WithMany(item => item.Sources)
            .HasForeignKey(item => item.CentralDerivativeJobId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.Artifact).WithMany()
            .HasForeignKey(item => item.CentralArtifactId).OnDelete(DeleteBehavior.Restrict).IsRequired();
    }

    private static void ConfigureIdentitySlot(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralTransientValidationIdentitySlot>();
        entity.ToTable("CentralTransientValidationIdentitySlots", table =>
        {
            table.HasTrigger("TR_CentralTransientValidationIdentitySlots_TerminalImmutable");
            table.HasCheckConstraint("CK_CentralTransientValidationIdentitySlots_Ordinal", "[Ordinal] >= 0");
            table.HasCheckConstraint("CK_CentralTransientValidationIdentitySlots_State", "([State] IN ('Reserved', 'Unused') AND [CentralTransientEventId] IS NULL AND [PersistedEventId] IS NULL AND [PersistedEventVersionId] IS NULL AND [PersistedObservationId] IS NULL AND [PersistedAssessmentId] IS NULL) OR ([State] = 'Committed' AND [CentralTransientEventId] IS NOT NULL AND [PersistedEventId] = COALESCE([AdoptedEventId], [SubmittedEventId]) AND [PersistedEventVersionId] IS NOT NULL AND [PersistedObservationId] = [ObservationId] AND [PersistedAssessmentId] = [AssessmentId])");
            table.HasCheckConstraint("CK_CentralTransientValidationIdentitySlots_Association", "([AdoptedEventId] IS NULL AND [AssociationIdentitySha256] IS NULL) OR ([AdoptedEventId] IS NOT NULL AND [AssociationIdentitySha256] IS NOT NULL)");
        });
        entity.HasKey(item => item.Id);
        entity.Property(item => item.State).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.HasIndex(item => new { item.CentralDerivativeJobId, item.Ordinal }).IsUnique();
        entity.HasIndex(item => item.CandidateId).IsUnique();
        entity.HasIndex(item => item.ObservationId).IsUnique();
        entity.HasIndex(item => item.AssessmentId).IsUnique();
        Sha256Optional(entity.Property(item => item.AssociationIdentitySha256));
        entity.HasIndex(item => item.AssociationIdentitySha256).IsUnique()
            .HasFilter("[AssociationIdentitySha256] IS NOT NULL");
        entity.HasOne(item => item.ValidationJob).WithMany(item => item.IdentitySlots)
            .HasForeignKey(item => item.CentralDerivativeJobId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.Event).WithMany()
            .HasForeignKey(item => new { item.CentralTransientEventId, item.PersistedEventId })
            .HasPrincipalKey(item => new { item.Id, item.EventId })
            .OnDelete(DeleteBehavior.NoAction);
        entity.HasOne(item => item.PersistedEventVersion).WithMany()
            .HasForeignKey(item => new { item.CentralTransientEventId, item.PersistedEventVersionId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.EventVersionId })
            .OnDelete(DeleteBehavior.NoAction);
        entity.HasOne(item => item.PersistedObservation).WithMany()
            .HasForeignKey(item => new { item.CentralTransientEventId, item.PersistedObservationId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.ObservationId })
            .OnDelete(DeleteBehavior.NoAction);
        entity.HasOne(item => item.PersistedAssessment).WithMany()
            .HasForeignKey(item => new { item.CentralTransientEventId, item.PersistedAssessmentId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.AssessmentId })
            .OnDelete(DeleteBehavior.NoAction);
    }

    private static void ConfigureContextDependency(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralTransientContextDependency>();
        entity.ToTable("CentralTransientContextDependencies", table =>
        {
            table.HasCheckConstraint("CK_CentralTransientContextDependencies_Ordinal", "[Ordinal] >= 0 AND [Ordinal] < 4");
        });
        entity.HasKey(item => new { item.CentralDerivativeJobId, item.Ordinal });
        Sha256(entity.Property(item => item.RequestedRecipeIdentitySha256));
        Sha256(entity.Property(item => item.ExecutionOptionsIdentitySha256));
        entity.HasIndex(item => new { item.ContextCentralArtifactId, item.ExecutionOptionsIdentitySha256 });
        entity.HasIndex(item => item.RequiredCentralDerivativeJobId);
        entity.HasOne(item => item.ValidationJob).WithMany(item => item.ContextDependencies)
            .HasForeignKey(item => item.CentralDerivativeJobId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.ContextArtifact).WithMany()
            .HasForeignKey(item => item.ContextCentralArtifactId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.RequiredValidationJob).WithMany()
            .HasForeignKey(item => item.RequiredCentralDerivativeJobId).OnDelete(DeleteBehavior.NoAction);
    }

    private static void ConfigureOutcomeVersion(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralTransientValidationOutcomeVersion>();
        entity.ToTable("CentralTransientValidationOutcomeVersions", table =>
        {
            table.HasTrigger("TR_CentralTransientValidationOutcomeVersions_Immutable");
            table.HasCheckConstraint("CK_CentralTransientValidationOutcomeVersions_Version", "[Version] > 0");
        });
        entity.HasKey(item => item.Id);
        entity.Property(item => item.State).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.ReasonCode).HasMaxLength(256).UseCollation(BinaryCollation).IsRequired();
        entity.Property(item => item.EvidenceJson).IsRequired();
        Sha256(entity.Property(item => item.EvidenceIdentitySha256));
        entity.HasIndex(item => new { item.CentralDerivativeJobId, item.Version }).IsUnique();
        entity.HasIndex(item => new { item.CentralDerivativeJobId, item.EvidenceIdentitySha256 }).IsUnique();
        entity.HasOne(item => item.ValidationJob).WithMany(item => item.OutcomeVersions)
            .HasForeignKey(item => item.CentralDerivativeJobId).OnDelete(DeleteBehavior.Restrict).IsRequired();
    }

    private static void Sha256(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<string> property)
    {
        property.HasMaxLength(64).IsUnicode(false).UseCollation(BinaryCollation).IsRequired();
    }

    private static void Sha256Optional(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<string?> property)
    {
        property.HasMaxLength(64).IsUnicode(false).UseCollation(BinaryCollation);
    }
}
