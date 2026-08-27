using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Data;

internal static class CentralTransientDerivativeConfiguration
{
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    public static void Configure(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ConfigureJob(builder);
        ConfigureIntent(builder);
        ConfigureDerivative(builder);
        ConfigureSource(builder);
        ConfigureBackground(builder);
        ConfigureVersionLink(builder);
    }

    private static void ConfigureJob(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralTransientDerivativeJob>();
        entity.ToTable("CentralTransientDerivativeJobs", table =>
        {
            table.HasTrigger("TR_CentralTransientDerivativeJobs_CommittedImmutable");
            table.HasCheckConstraint("CK_CentralTransientDerivativeJobs_RequestLength", "[CanonicalRequestByteLength] > 0");
            table.HasCheckConstraint("CK_CentralTransientDerivativeJobs_OutputCount", "[ExpectedOutputCount] = 5");
        });
        entity.HasKey(item => item.CentralDerivativeJobId);
        entity.HasAlternateKey(item => new
        {
            item.CentralTransientEventId,
            item.CentralDerivativeJobId,
            item.SourceEventVersionId
        });
        entity.HasAlternateKey(item => new
        {
            item.CentralTransientEventId,
            item.CentralDerivativeJobId
        });
        Sha256(entity.Property(item => item.RequestIdentitySha256));
        entity.Property(item => item.ProducerSchemaVersion).HasMaxLength(128).IsRequired();
        entity.Property(item => item.ProducerName).HasMaxLength(128).UseCollation(BinaryCollation).IsRequired();
        entity.Property(item => item.ProducerVersion).HasMaxLength(128).UseCollation(BinaryCollation).IsRequired();
        Sha256(entity.Property(item => item.RecipeIdentitySha256));
        Sha256(entity.Property(item => item.OptionsIdentitySha256));
        entity.Property(item => item.CanonicalRequestJson).IsRequired();
        Sha256(entity.Property(item => item.CanonicalRequestSha256));
        entity.HasIndex(item => item.RequestIdentitySha256).IsUnique();
        entity.HasIndex(item => new
        {
            item.CentralTransientEventId,
            item.SourceEventVersionId,
            item.RecipeIdentitySha256,
            item.OptionsIdentitySha256
        }).IsUnique();
        entity.HasOne(item => item.Job).WithOne()
            .HasForeignKey<CentralTransientDerivativeJob>(item => item.CentralDerivativeJobId)
            .OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.Event).WithMany()
            .HasForeignKey(item => item.CentralTransientEventId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.SourceEventVersion).WithMany()
            .HasForeignKey(item => new { item.CentralTransientEventId, item.SourceEventVersionId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.EventVersionId })
            .OnDelete(DeleteBehavior.NoAction).IsRequired();
    }

    private static void ConfigureIntent(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralTransientDerivativeOutputIntent>();
        entity.ToTable("CentralTransientDerivativeOutputIntents", table =>
        {
            table.HasTrigger("TR_CentralTransientDerivativeOutputIntents_TerminalImmutable");
            table.HasTrigger("TR_CentralTransientDerivativeOutputIntents_Closed");
            table.HasCheckConstraint("CK_CentralTransientDerivativeOutputIntents_ByteLength", "[ByteLength] > 0");
            table.HasCheckConstraint("CK_CentralTransientDerivativeOutputIntents_Commit", "[CommittedAtUtc] IS NULL OR ([ObjectState] IN ('Available', 'Expired') AND [ObjectVerifiedAtUtc] IS NOT NULL AND [StorageETag] IS NOT NULL)");
        });
        entity.HasKey(item => item.Id);
        entity.HasAlternateKey(item => new
        {
            item.CentralTransientEventId,
            item.CentralDerivativeJobId,
            item.Id
        });
        entity.Property(item => item.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.ArtifactRole).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.ArtifactVariant).HasMaxLength(128).UseCollation(BinaryCollation).IsRequired();
        entity.Property(item => item.MediaType).HasMaxLength(128).IsRequired();
        Sha256(entity.Property(item => item.ChecksumSha256));
        Sha256(entity.Property(item => item.OutputIdentitySha256));
        entity.Property(item => item.StorageReference).HasMaxLength(1024).IsRequired();
        entity.Property(item => item.StorageETag).HasMaxLength(128).UseCollation(BinaryCollation);
        entity.Property<byte[]>("StorageReferenceSha256").HasColumnType("binary(32)")
            .HasComputedColumnSql("CONVERT(binary(32), HASHBYTES('SHA2_256', [StorageReference]))", stored: true);
        entity.Property(item => item.ObjectState).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.StateReasonCode).HasMaxLength(256).UseCollation(BinaryCollation);
        entity.Property(item => item.RowVersion).IsRowVersion();
        entity.HasIndex(item => item.ArtifactId).IsUnique();
        entity.HasIndex(item => new { item.CentralTransientEventId, item.OutputIdentitySha256 }).IsUnique();
        entity.HasIndex(item => new { item.ObjectState, item.CreatedAtUtc });
        entity.HasIndex("StorageReferenceSha256");
        entity.HasOne(item => item.DerivativeJob).WithMany(item => item.OutputIntents)
            .HasForeignKey(item => new { item.CentralTransientEventId, item.CentralDerivativeJobId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.CentralDerivativeJobId })
            .OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasIndex(item => new { item.CentralDerivativeJobId, item.Kind }).IsUnique();
    }

    private static void ConfigureDerivative(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralTransientDerivativeRecord>();
        entity.ToTable("CentralTransientDerivatives", table =>
        {
            table.HasTrigger("TR_CentralTransientDerivatives_Immutable");
            table.HasTrigger("TR_CentralTransientDerivatives_Closed");
            table.HasCheckConstraint("CK_CentralTransientDerivatives_ByteLength", "[ByteLength] > 0");
            table.HasCheckConstraint("CK_CentralTransientDerivatives_ReviewAssessment", "[ReviewId] IS NULL OR [AssessmentId] IS NOT NULL");
        });
        entity.HasKey(item => item.DerivativeId);
        entity.HasAlternateKey(item => new { item.CentralTransientEventId, item.DerivativeId });
        entity.Property(item => item.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.ArtifactRole).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.ArtifactVariant).HasMaxLength(128).UseCollation(BinaryCollation).IsRequired();
        entity.Property(item => item.MediaType).HasMaxLength(128).IsRequired();
        Sha256(entity.Property(item => item.ArtifactChecksumSha256));
        Sha256(entity.Property(item => item.RecipeIdentitySha256));
        Sha256(entity.Property(item => item.OptionsIdentitySha256));
        Sha256(entity.Property(item => item.OutputIdentitySha256));
        entity.Property(item => item.LimitationsJson).HasMaxLength(1024).IsRequired();
        entity.HasIndex(item => item.ArtifactId).IsUnique();
        entity.HasIndex(item => new { item.CentralTransientEventId, item.OutputIdentitySha256 }).IsUnique();
        entity.HasOne(item => item.Event).WithMany()
            .HasForeignKey(item => item.CentralTransientEventId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.SourceEventVersion).WithMany()
            .HasForeignKey(item => new { item.CentralTransientEventId, item.SourceEventVersionId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.EventVersionId })
            .OnDelete(DeleteBehavior.NoAction).IsRequired();
        entity.HasOne(item => item.DerivativeJob).WithMany()
            .HasForeignKey(item => new
            {
                item.CentralTransientEventId,
                item.CentralDerivativeJobId,
                item.SourceEventVersionId
            })
            .HasPrincipalKey(item => new
            {
                item.CentralTransientEventId,
                item.CentralDerivativeJobId,
                item.SourceEventVersionId
            })
            .OnDelete(DeleteBehavior.NoAction).IsRequired();
        entity.HasOne(item => item.OutputIntent).WithOne()
            .HasForeignKey<CentralTransientDerivativeRecord>(item => new
            {
                item.CentralTransientEventId,
                item.CentralDerivativeJobId,
                item.OutputIntentId
            })
            .HasPrincipalKey<CentralTransientDerivativeOutputIntent>(item => new
            {
                item.CentralTransientEventId,
                item.CentralDerivativeJobId,
                item.Id
            })
            .OnDelete(DeleteBehavior.NoAction).IsRequired();
        entity.HasOne<CentralTransientAssessmentRecord>().WithMany()
            .HasForeignKey(item => new { item.CentralTransientEventId, item.AssessmentId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.AssessmentId })
            .OnDelete(DeleteBehavior.NoAction);
        entity.HasOne<CentralTransientReviewRecord>().WithMany()
            .HasForeignKey(item => new
            {
                item.CentralTransientEventId,
                item.ReviewId,
                item.AssessmentId
            })
            .HasPrincipalKey(item => new
            {
                item.CentralTransientEventId,
                item.ReviewId,
                item.AssessmentId
            })
            .OnDelete(DeleteBehavior.NoAction);
    }

    private static void ConfigureSource(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralTransientDerivativeSourceReference>();
        entity.ToTable("CentralTransientDerivativeSources", table =>
        {
            table.HasTrigger("TR_CentralTransientDerivativeSources_Immutable");
            table.HasTrigger("TR_CentralTransientDerivativeSources_Closed");
            table.HasCheckConstraint("CK_CentralTransientDerivativeSources_Ordinal", "[Ordinal] >= 0");
            table.HasCheckConstraint("CK_CentralTransientDerivativeSources_ObservedInterval", "[ObservationStartedUtc] <= [ObservationEndedUtc]");
        });
        entity.HasKey(item => new { item.DerivativeId, item.Ordinal });
        entity.HasAlternateKey(item => new { item.DerivativeId, item.Ordinal, item.ObservationId });
        Sha256(entity.Property(item => item.ArtifactChecksumSha256));
        entity.HasIndex(item => new { item.DerivativeId, item.EvidenceId }).IsUnique();
        entity.HasIndex(item => item.CentralArtifactId);
        entity.HasOne(item => item.Derivative).WithMany(item => item.Sources)
            .HasForeignKey(item => new { item.CentralTransientEventId, item.DerivativeId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.DerivativeId })
            .OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne<CentralTransientObservationRecord>().WithMany()
            .HasForeignKey(item => new { item.CentralTransientEventId, item.ObservationId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.ObservationId })
            .OnDelete(DeleteBehavior.NoAction).IsRequired();
        entity.HasOne<CentralTransientObservationSourceReference>().WithMany()
            .HasForeignKey(item => new
            {
                item.ObservationId,
                item.EvidenceId,
                item.CentralArtifactId,
                item.ArtifactId,
                item.ArtifactChecksumSha256,
                item.ObservationStartedUtc,
                item.ObservationEndedUtc
            })
            .HasPrincipalKey(item => new
            {
                item.ObservationId,
                item.EvidenceId,
                item.CentralArtifactId,
                item.ArtifactId,
                item.ArtifactChecksumSha256,
                item.ObservationStartedUtc,
                item.ObservationEndedUtc
            })
            .OnDelete(DeleteBehavior.Restrict).IsRequired();
    }

    private static void ConfigureBackground(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralTransientDerivativeBackgroundReference>();
        entity.ToTable("CentralTransientDerivativeBackgrounds", table =>
        {
            table.HasTrigger("TR_CentralTransientDerivativeBackgrounds_Immutable");
            table.HasTrigger("TR_CentralTransientDerivativeBackgrounds_Closed");
            table.HasCheckConstraint("CK_CentralTransientDerivativeBackgrounds_Ordinals", "[ObservationOrdinal] >= 0 AND [BackgroundOrdinal] >= 0");
        });
        entity.HasKey(item => new { item.DerivativeId, item.ObservationOrdinal, item.BackgroundOrdinal });
        Sha256(entity.Property(item => item.ArtifactChecksumSha256));
        entity.HasIndex(item => new
        {
            item.DerivativeId,
            item.ObservationOrdinal,
            item.CentralArtifactId
        }).IsUnique();
        entity.HasIndex(item => item.CentralArtifactId);
        entity.HasOne(item => item.Derivative).WithMany(item => item.Backgrounds)
            .HasForeignKey(item => new { item.CentralTransientEventId, item.DerivativeId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.DerivativeId })
            .OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne<CentralTransientObservationRecord>().WithMany()
            .HasForeignKey(item => new { item.CentralTransientEventId, item.ObservationId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.ObservationId })
            .OnDelete(DeleteBehavior.NoAction).IsRequired();
        entity.HasOne<CentralTransientDerivativeSourceReference>().WithMany()
            .HasForeignKey(item => new
            {
                item.DerivativeId,
                item.ObservationOrdinal,
                item.ObservationId
            })
            .HasPrincipalKey(item => new
            {
                item.DerivativeId,
                item.Ordinal,
                item.ObservationId
            })
            .OnDelete(DeleteBehavior.NoAction).IsRequired();
        entity.HasOne<CentralTransientObservationBackgroundReference>().WithMany()
            .HasForeignKey(item => new
            {
                item.ObservationId,
                item.BackgroundOrdinal,
                item.CentralArtifactId,
                item.ArtifactId,
                item.ArtifactChecksumSha256
            })
            .HasPrincipalKey(item => new
            {
                item.ObservationId,
                item.Ordinal,
                item.CentralArtifactId,
                item.ArtifactId,
                item.ArtifactChecksumSha256
            })
            .OnDelete(DeleteBehavior.Restrict).IsRequired();
    }

    private static void ConfigureVersionLink(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralTransientEventVersionDerivative>();
        entity.ToTable("CentralTransientEventVersionDerivatives", table =>
        {
            table.HasTrigger("TR_CentralTransientEventVersionDerivatives_Immutable");
            table.HasCheckConstraint("CK_CentralTransientEventVersionDerivatives_Ordinal", "[Ordinal] >= 0");
        });
        entity.HasKey(item => new { item.EventVersionId, item.Ordinal });
        entity.HasIndex(item => new { item.EventVersionId, item.DerivativeId }).IsUnique();
        entity.HasOne(item => item.EventVersion).WithMany(item => item.Derivatives)
            .HasForeignKey(item => new { item.CentralTransientEventId, item.EventVersionId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.EventVersionId })
            .OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.Derivative).WithMany()
            .HasForeignKey(item => new { item.CentralTransientEventId, item.DerivativeId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.DerivativeId })
            .OnDelete(DeleteBehavior.NoAction).IsRequired();
    }

    private static void Sha256(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<string> property)
        => property.HasMaxLength(64).IsUnicode(false).UseCollation(BinaryCollation).IsRequired();
}
