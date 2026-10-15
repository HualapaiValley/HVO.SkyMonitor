using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HVO.SkyMonitor.LogicHost.Data;

internal static class ImportedExecutionEvidenceConfiguration
{
    public static void Configure(ModelBuilder builder)
    {
        var binding = builder.Entity<ExecutionEvidenceBinding>();
        Immutable(binding, "ExecutionEvidenceBindings");
        binding.HasKey(x => x.Id);
        binding.HasIndex(x => new { x.OriginInstallationId, x.AgentInstanceId }).IsUnique();
        binding.HasIndex(x => new { x.RegistrationId, x.Id });
        binding.Property(x => x.CreatedByUserId).HasMaxLength(450);
        binding.HasOne<Observatory>().WithMany().HasForeignKey(x => x.ObservatoryId).OnDelete(DeleteBehavior.Restrict);
        binding.HasOne<DeviceRegistration>().WithMany().HasForeignKey(x => x.RegistrationId).OnDelete(DeleteBehavior.Restrict);
        binding.HasOne<LogicalCameraInstallation>().WithMany().HasForeignKey(x => x.LogicalCameraInstallationId).OnDelete(DeleteBehavior.Restrict);

        var usage = builder.Entity<ExecutionEvidenceBindingUsage>();
        usage.ToTable("ExecutionEvidenceBindingUsage", t => t.HasCheckConstraint("CK_ExecutionEvidenceBindingUsage_Nonnegative",
            "[StoredBytes] >= 0 AND [UnitCount] >= 0 AND [OriginCount] >= 0 AND [ConflictCount] >= 0 AND [ObservationCount] >= 0"));
        usage.HasKey(x => x.BindingId);
        usage.HasOne<ExecutionEvidenceBinding>().WithOne().HasForeignKey<ExecutionEvidenceBindingUsage>(x => x.BindingId).OnDelete(DeleteBehavior.Restrict);

        var origin = builder.Entity<ImportedExecutionEvidenceOrigin>();
        origin.ToTable("ImportedExecutionEvidenceOrigins", t =>
        {
            t.HasTrigger("TR_ImportedExecutionEvidenceOrigins_Identity");
            t.HasCheckConstraint("CK_ImportedExecutionEvidenceOrigins_Sequence", "[ContiguousThroughSequence] >= 0 AND [HighestSeenSequence] >= [ContiguousThroughSequence]");
        });
        origin.HasKey(x => x.Id);
        origin.HasAlternateKey(x => new { x.Id, x.BindingId });
        Hash(origin.Property(x => x.IdentitySha256));
        origin.Property(x => x.SoftwareVersion).HasMaxLength(64).UseCollation("Latin1_General_100_BIN2");
        origin.HasIndex(x => x.IdentitySha256).IsUnique();
        origin.HasIndex(x => new { x.BindingId, x.Id });
        origin.HasIndex(x => new { x.BindingId, x.GapDetectedAtUtc });
        origin.HasOne<ExecutionEvidenceBinding>().WithMany().HasForeignKey(x => x.BindingId).OnDelete(DeleteBehavior.Restrict);

        var unit = builder.Entity<ImportedExecutionEvidenceUnit>();
        Immutable(unit, "ImportedExecutionEvidenceUnits");
        unit.ToTable(t =>
        {
            t.HasCheckConstraint("CK_ImportedExecutionEvidenceUnits_Sequence", "[OriginSequence] > 0");
            t.HasCheckConstraint("CK_ImportedExecutionEvidenceUnits_Bytes", "DATALENGTH([CanonicalBytes]) BETWEEN 1 AND 8388608");
            t.HasCheckConstraint("CK_ImportedExecutionEvidenceUnits_Disposition", "([Accepted] = 1 AND [RejectionReasonCode] IS NULL AND [RejectionFieldPath] IS NULL) OR ([Accepted] = 0 AND [RejectionReasonCode] IS NOT NULL)");
            t.HasCheckConstraint("CK_ImportedExecutionEvidenceUnits_Kind", "[Kind] IN ('GraphRevision', 'GraphExecution', 'ArtifactAvailability')");
        });
        unit.HasKey(x => x.Id);
        unit.HasAlternateKey(x => new { x.Id, x.BindingId });
        Hash(unit.Property(x => x.PayloadSha256));
        unit.Property(x => x.Kind).HasMaxLength(32).IsUnicode(false);
        unit.Property(x => x.RevisionId).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2");
        unit.Property(x => x.RejectionReasonCode).HasMaxLength(128);
        unit.Property(x => x.RejectionFieldPath).HasMaxLength(256);
        unit.HasIndex(x => new { x.OriginId, x.OriginSequence }).IsUnique();
        unit.HasIndex(x => new { x.OriginId, x.EvidenceId }).IsUnique().HasFilter("[Accepted] = 1");
        unit.HasIndex(x => new { x.BindingId, x.Id });
        unit.HasIndex(x => new { x.BindingId, x.Kind, x.RevisionId, x.Accepted, x.Id });
        unit.HasIndex(x => new { x.BindingId, x.Kind, x.ExecutionId, x.Accepted, x.Id });
        unit.HasOne<ImportedExecutionEvidenceOrigin>().WithMany().HasForeignKey(x => new { x.OriginId, x.BindingId }).HasPrincipalKey(x => new { x.Id, x.BindingId }).OnDelete(DeleteBehavior.Restrict);
        unit.HasOne<ExecutionEvidenceBinding>().WithMany().HasForeignKey(x => x.BindingId).OnDelete(DeleteBehavior.Restrict);

        var range = builder.Entity<ImportedExecutionEvidenceRange>();
        range.ToTable("ImportedExecutionEvidenceRanges", t => t.HasCheckConstraint("CK_ImportedExecutionEvidenceRanges_Order", "[FromSequence] > 0 AND [ToSequence] >= [FromSequence]"));
        range.HasKey(x => new { x.OriginId, x.FromSequence });
        range.HasIndex(x => new { x.OriginId, x.ToSequence });
        range.HasOne<ImportedExecutionEvidenceOrigin>().WithMany().HasForeignKey(x => x.OriginId).OnDelete(DeleteBehavior.Restrict);

        var conflict = builder.Entity<ImportedExecutionEvidenceConflict>();
        Immutable(conflict, "ImportedExecutionEvidenceConflicts");
        conflict.HasKey(x => x.Id);
        Hash(conflict.Property(x => x.SubmittedPayloadSha256));
        conflict.HasIndex(x => new { x.UnitId, x.SubmittedPayloadSha256 }).IsUnique();
        conflict.HasIndex(x => new { x.BindingId, x.Id });
        conflict.HasOne<ImportedExecutionEvidenceUnit>().WithMany().HasForeignKey(x => new { x.UnitId, x.BindingId }).HasPrincipalKey(x => new { x.Id, x.BindingId }).OnDelete(DeleteBehavior.Restrict);
        conflict.HasOne<ExecutionEvidenceBinding>().WithMany().HasForeignKey(x => x.BindingId).OnDelete(DeleteBehavior.Restrict);

        var artifact = builder.Entity<ImportedExecutionEvidenceArtifact>();
        Immutable(artifact, "ImportedExecutionEvidenceArtifacts");
        artifact.HasKey(x => x.Id);
        artifact.HasAlternateKey(x => new { x.Id, x.BindingId });
        artifact.HasIndex(x => new { x.UnitId, x.Ordinal }).IsUnique();
        artifact.HasIndex(x => new { x.BindingId, x.Id });
        artifact.HasIndex(x => new { x.BindingId, x.ArtifactId, x.Id });
        Hash(artifact.Property(x => x.OutputIdentitySha256));
        Hash(artifact.Property(x => x.PayloadSha256));
        Hash(artifact.Property(x => x.DescriptorSha256));
        artifact.Property(x => x.MediaType).HasMaxLength(128);
        artifact.Property(x => x.Role).HasMaxLength(32);
        artifact.Property(x => x.Variant).HasMaxLength(128);
        artifact.HasOne<ImportedExecutionEvidenceUnit>().WithMany().HasForeignKey(x => new { x.UnitId, x.BindingId }).HasPrincipalKey(x => new { x.Id, x.BindingId }).OnDelete(DeleteBehavior.Restrict);
        artifact.HasOne<ExecutionEvidenceBinding>().WithMany().HasForeignKey(x => x.BindingId).OnDelete(DeleteBehavior.Restrict);

        var check = builder.Entity<ExecutionEvidenceArtifactCheck>();
        check.ToTable("ExecutionEvidenceArtifactChecks");
        check.HasKey(x => x.ArtifactReferenceId);
        check.HasIndex(x => new { x.NextCheckAtUtc, x.ArtifactReferenceId });
        check.HasIndex(x => new { x.BindingId, x.NextCheckAtUtc, x.ArtifactReferenceId });
        check.Property(x => x.State).HasMaxLength(32);
        check.Property(x => x.ReasonCode).HasMaxLength(128);
        check.Property(x => x.ObjectGeneration).HasMaxLength(256);
        check.HasOne<ImportedExecutionEvidenceArtifact>().WithOne().HasForeignKey<ExecutionEvidenceArtifactCheck>(x => new { x.ArtifactReferenceId, x.BindingId }).HasPrincipalKey<ImportedExecutionEvidenceArtifact>(x => new { x.Id, x.BindingId }).OnDelete(DeleteBehavior.Restrict);

        var observation = builder.Entity<ImportedExecutionEvidenceObservation>();
        Immutable(observation, "ImportedExecutionEvidenceObservations");
        observation.HasKey(x => x.Id);
        observation.HasIndex(x => new { x.BindingId, x.Id });
        observation.HasIndex(x => new { x.ArtifactReferenceId, x.Id });
        observation.Property(x => x.State).HasMaxLength(32);
        observation.Property(x => x.ReasonCode).HasMaxLength(128);
        observation.Property(x => x.ObjectGeneration).HasMaxLength(256);
        observation.HasOne<ImportedExecutionEvidenceArtifact>().WithMany().HasForeignKey(x => new { x.ArtifactReferenceId, x.BindingId }).HasPrincipalKey(x => new { x.Id, x.BindingId }).OnDelete(DeleteBehavior.Restrict);
        observation.HasOne<ExecutionEvidenceBinding>().WithMany().HasForeignKey(x => x.BindingId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void Hash(PropertyBuilder property)
        => property.HasMaxLength(64).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");

    private static void Immutable<T>(EntityTypeBuilder<T> entity, string table) where T : class
        => entity.ToTable(table, t => t.HasTrigger($"TR_{table}_Immutable"));
}
