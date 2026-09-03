using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HVO.SkyMonitor.LogicHost.Data;

internal static class CentralProcessingGraphExecutionConfiguration
{
    internal const int MaximumExpectedDependencyCount =
        ProcessingGraphCompiler.MaximumNodes * ProcessingGraphCompiler.MaximumContractsPerNode;
    internal const int MaximumExpectedOutputCount =
        ProcessingGraphCompiler.MaximumNodes * ProcessingGraphCompiler.MaximumContractsPerNode;

    private const string BinaryCollation = "Latin1_General_100_BIN2";

    public static void Configure(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ConfigureExecution(builder);
        ConfigureSource(builder);
        ConfigureOutput(builder);
        ConfigureDependency(builder);
    }

    private static void ConfigureExecution(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralProcessingGraphExecution>();
        entity.ToTable("CentralProcessingGraphExecutions", table =>
        {
            table.HasTrigger("TR_CentralProcessingGraphExecutions_IdentityImmutable");
            table.HasCheckConstraint(
                "CK_CentralProcessingGraphExecutions_Class",
                "[ExecutionClass] IN (N'Live', N'Replay')");
            table.HasCheckConstraint(
                "CK_CentralProcessingGraphExecutions_Status",
                "[Status] IN (N'Pending', N'Running', N'Completed', N'CompletedWithOptionalFailures', " +
                "N'Failed', N'CancelRequested', N'Canceled', N'Superseded')");
            table.HasCheckConstraint(
                "CK_CentralProcessingGraphExecutions_Trigger",
                "[Trigger] IN (N'Ingest', N'Replay', N'Reprocess')");
            table.HasCheckConstraint(
                "CK_CentralProcessingGraphExecutions_Provenance",
                "LEN(LTRIM(RTRIM(REPLACE(REPLACE(REPLACE([ActorId], CHAR(9), N''), CHAR(10), N''), CHAR(13), N'')))) > 0 AND " +
                "LEN(LTRIM(RTRIM(REPLACE(REPLACE(REPLACE([IdempotencyKey], CHAR(9), N''), CHAR(10), N''), CHAR(13), N'')))) > 0 AND " +
                "LEN(LTRIM(RTRIM(REPLACE(REPLACE(REPLACE([ReasonCode], CHAR(9), N''), CHAR(10), N''), CHAR(13), N'')))) > 0 AND " +
                "(([ExecutionClass] = N'Live' AND [Trigger] = N'Ingest' AND [AssignmentId] IS NOT NULL) OR " +
                "([ExecutionClass] = N'Replay' AND [Trigger] IN (N'Replay', N'Reprocess') AND [AssignmentId] IS NULL))");
            table.HasCheckConstraint(
                "CK_CentralProcessingGraphExecutions_ExpectedCounts",
                $"[ExpectedSourceCount] >= 0 AND [ExpectedSourceCount] <= {ProcessingGraphCompiler.MaximumSources} AND " +
                $"[ExpectedNodeCount] >= 0 AND [ExpectedNodeCount] <= {ProcessingGraphCompiler.MaximumNodes} AND " +
                $"[ExpectedDependencyCount] >= 0 AND [ExpectedDependencyCount] <= {MaximumExpectedDependencyCount} AND " +
                $"[ExpectedOutputCount] >= 0 AND [ExpectedOutputCount] <= {MaximumExpectedOutputCount}");
            table.HasCheckConstraint(
                "CK_CentralProcessingGraphExecutions_Expansion",
                "([ExpandedAtUtc] IS NULL AND [Status] = N'Pending') OR " +
                "([ExpandedAtUtc] IS NOT NULL AND [ExpandedAtUtc] >= [CreatedAtUtc] AND [UpdatedAtUtc] >= [ExpandedAtUtc])");
            table.HasCheckConstraint(
                "CK_CentralProcessingGraphExecutions_Predecessor",
                "[PredecessorExecutionId] IS NULL OR [PredecessorExecutionId] <> [Id]");
            table.HasCheckConstraint(
                "CK_CentralProcessingGraphExecutions_Timestamps",
                "[UpdatedAtUtc] >= [CreatedAtUtc] AND " +
                "([StartedAtUtc] IS NULL OR [StartedAtUtc] >= [CreatedAtUtc]) AND " +
                "([CancellationRequestedAtUtc] IS NULL OR [CancellationRequestedAtUtc] >= [CreatedAtUtc]) AND " +
                "([CompletedAtUtc] IS NULL OR [CompletedAtUtc] >= [CreatedAtUtc])");
            table.HasCheckConstraint(
                "CK_CentralProcessingGraphExecutions_StatusTimestamps",
                "([Status] = N'Pending' AND [StartedAtUtc] IS NULL AND [CancellationRequestedAtUtc] IS NULL AND [CompletedAtUtc] IS NULL) OR " +
                "([Status] = N'Running' AND [StartedAtUtc] IS NOT NULL AND [CancellationRequestedAtUtc] IS NULL AND [CompletedAtUtc] IS NULL) OR " +
                "([Status] = N'CancelRequested' AND [CancellationRequestedAtUtc] IS NOT NULL AND [CompletedAtUtc] IS NULL) OR " +
                "([Status] IN (N'Completed', N'CompletedWithOptionalFailures') AND [StartedAtUtc] IS NOT NULL AND [CompletedAtUtc] IS NOT NULL) OR " +
                "([Status] = N'Failed' AND [CompletedAtUtc] IS NOT NULL) OR " +
                "([Status] = N'Canceled' AND [CancellationRequestedAtUtc] IS NOT NULL AND [CompletedAtUtc] IS NOT NULL) OR " +
                "([Status] = N'Superseded' AND [CompletedAtUtc] IS NOT NULL)");
        });
        entity.HasKey(item => item.Id);
        entity.Property(item => item.ExecutionClass).HasConversion<string>().HasMaxLength(16).IsRequired();
        entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(48).IsRequired();
        Sha256(entity.Property(item => item.RequestIdentitySha256));
        Sha256(entity.Property(item => item.DefinitionIdentitySha256));
        entity.Property(item => item.FrozenDefinitionJson).IsRequired();
        Sha256(entity.Property(item => item.CentralPlanIdentitySha256));
        entity.Property(item => item.FrozenCentralPlanJson).IsRequired();
        Sha256(entity.Property(item => item.AnchorSourceChecksumSha256));
        entity.Property(item => item.Trigger).HasConversion<string>().HasMaxLength(16).IsRequired();
        entity.Property(item => item.ActorId).HasMaxLength(450).IsRequired();
        entity.Property(item => item.IdempotencyKey).HasMaxLength(256).UseCollation(BinaryCollation).IsRequired();
        entity.Property(item => item.ReasonCode).HasMaxLength(128).UseCollation(BinaryCollation).IsRequired();
        entity.Property(item => item.RowVersion).IsRowVersion();
        entity.HasIndex(item => item.RequestIdentitySha256).IsUnique();
        entity.HasIndex(item => new { item.ExecutionClass, item.ActorId, item.IdempotencyKey }).IsUnique();
        entity.HasIndex(item => new { item.Status, item.CreatedAtUtc, item.Id });
        entity.HasIndex(item => item.ExpandedAtUtc).HasFilter("[ExpandedAtUtc] IS NULL");
        entity.HasIndex(item => new { item.LogicalCameraInstallationId, item.CreatedAtUtc, item.Id });
        entity.HasIndex(item => item.PredecessorExecutionId).IsUnique()
            .HasFilter("[PredecessorExecutionId] IS NOT NULL");
        entity.HasOne(item => item.Revision).WithMany(item => item.Executions)
            .HasForeignKey(item => item.RevisionId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.Assignment).WithMany(item => item.Executions)
            .HasForeignKey(item => item.AssignmentId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(item => item.Observatory).WithMany()
            .HasForeignKey(item => item.ObservatoryId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.LogicalCamera).WithMany()
            .HasForeignKey(item => item.LogicalCameraId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.LogicalCameraInstallation).WithMany()
            .HasForeignKey(item => item.LogicalCameraInstallationId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.AnchorSourceArtifact).WithMany()
            .HasForeignKey(item => item.AnchorSourceCentralArtifactId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.PredecessorExecution).WithOne(item => item.SuccessorExecution)
            .HasForeignKey<CentralProcessingGraphExecution>(item => item.PredecessorExecutionId)
            .OnDelete(DeleteBehavior.NoAction);
    }

    private static void ConfigureSource(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralProcessingGraphExecutionSource>();
        entity.ToTable("CentralProcessingGraphExecutionSources", table =>
        {
            table.HasTrigger("TR_CentralProcessingGraphExecutionSources_Immutable");
            table.HasCheckConstraint("CK_CentralProcessingGraphExecutionSources_Ordinal", "[Ordinal] >= 0");
            table.HasCheckConstraint("CK_CentralProcessingGraphExecutionSources_OutputOrdinal", "[OutputOrdinal] >= 0");
            table.HasCheckConstraint("CK_CentralProcessingGraphExecutionSources_ByteLength", "[ArtifactByteLength] >= 0");
        });
        entity.HasKey(item => item.Id);
        entity.Property(item => item.SourceId).HasMaxLength(128).UseCollation(BinaryCollation).IsRequired();
        Sha256(entity.Property(item => item.ArtifactChecksumSha256));
        entity.Property(item => item.SelectionEvidenceJson).IsRequired();
        Sha256(entity.Property(item => item.SelectionEvidenceSha256));
        entity.HasIndex(item => new { item.ExecutionId, item.Ordinal }).IsUnique();
        entity.HasIndex(item => new { item.ExecutionId, item.SourceId, item.OutputOrdinal }).IsUnique();
        entity.HasIndex(item => new { item.CentralArtifactId, item.ExecutionId });
        entity.HasOne(item => item.Execution).WithMany(item => item.Sources)
            .HasForeignKey(item => item.ExecutionId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.Artifact).WithMany()
            .HasForeignKey(item => item.CentralArtifactId).OnDelete(DeleteBehavior.Restrict).IsRequired();
    }

    private static void ConfigureOutput(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralDerivativeJobOutput>();
        entity.ToTable("CentralDerivativeJobOutputs", table =>
        {
            table.HasTrigger("TR_CentralDerivativeJobOutputs_BindOnce");
            table.HasCheckConstraint("CK_CentralDerivativeJobOutputs_Ordinal", "[Ordinal] >= 0");
            table.HasCheckConstraint(
                "CK_CentralDerivativeJobOutputs_Binding",
                "([ResultCentralArtifactId] IS NULL AND [ResultOutputIdentitySha256] IS NULL AND [BoundAtUtc] IS NULL) OR " +
                "([ResultCentralArtifactId] IS NOT NULL AND [ResultOutputIdentitySha256] IS NOT NULL AND [BoundAtUtc] IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_CentralDerivativeJobOutputs_ContractIdentity",
                "ISJSON([ContractJson]) = 1 AND " +
                "[ContractJson] NOT LIKE '%[^ -~]%' COLLATE Latin1_General_100_BIN2 AND " +
                "[ContractIdentitySha256] = CONVERT(varchar(64), HASHBYTES('SHA2_256', [ContractJson]), 2)");
            table.HasCheckConstraint(
                "CK_CentralDerivativeJobOutputs_Role",
                "[Role] IN (N'Raw', N'Calibrated', N'Combined', N'Preview', N'AnnotatedPreview', N'Metadata')");
            table.HasCheckConstraint(
                "CK_CentralDerivativeJobOutputs_ProductKind",
                "[ProductKind] IN (N'PixelData', N'Metadata')");
        });
        entity.HasKey(item => item.Id);
        entity.HasAlternateKey(item => new { item.CentralDerivativeJobId, item.Ordinal });
        entity.Property(item => item.Role).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.Variant).HasMaxLength(128).UseCollation(BinaryCollation).IsRequired();
        entity.Property(item => item.ProductKind).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.ContractJson).IsUnicode(false).IsRequired();
        Sha256(entity.Property(item => item.ContractIdentitySha256));
        OptionalSha256(entity.Property(item => item.ResultOutputIdentitySha256));
        entity.Property(item => item.RowVersion).IsRowVersion();
        entity.HasIndex(item => item.ResultCentralArtifactId)
            .HasFilter("[ResultCentralArtifactId] IS NOT NULL");
        entity.HasIndex(item => item.ResultOutputIdentitySha256)
            .HasFilter("[ResultOutputIdentitySha256] IS NOT NULL");
        entity.HasOne(item => item.Job).WithMany(item => item.Outputs)
            .HasForeignKey(item => item.CentralDerivativeJobId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.ResultArtifact).WithMany()
            .HasForeignKey(item => item.ResultCentralArtifactId).OnDelete(DeleteBehavior.NoAction);
    }

    private static void ConfigureDependency(ModelBuilder builder)
    {
        var entity = builder.Entity<CentralDerivativeJobDependency>();
        entity.ToTable("CentralDerivativeJobDependencies", table =>
        {
            table.HasTrigger("TR_CentralDerivativeJobDependencies_Immutable");
            table.HasCheckConstraint("CK_CentralDerivativeJobDependencies_Ordinal", "[Ordinal] >= 0");
            table.HasCheckConstraint(
                "CK_CentralDerivativeJobDependencies_Kind",
                "[Kind] IN (N'Artifact', N'CanonicalJson', N'Annotation', N'Outcome', N'Ordering')");
            table.HasCheckConstraint(
                "CK_CentralDerivativeJobDependencies_Producer",
                "([ProducerJobId] IS NOT NULL AND [ProducerSourceId] IS NULL) OR " +
                "([ProducerJobId] IS NULL AND [ProducerSourceId] IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_CentralDerivativeJobDependencies_Binding",
                "([Kind] IN (N'Outcome', N'Ordering') AND [ProducerOutputOrdinal] IS NULL AND " +
                "[ConsumerInputOrdinal] IS NULL AND [ConsumerBindingName] IS NULL AND [ConsumerBindingKind] IS NULL) OR " +
                "([Kind] IN (N'Artifact', N'CanonicalJson', N'Annotation') AND [ProducerOutputOrdinal] >= 0 AND " +
                "[ConsumerInputOrdinal] >= 0 AND [ConsumerBindingName] IS NOT NULL AND [ConsumerBindingKind] IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_CentralDerivativeJobDependencies_BindingKind",
                "[ConsumerBindingKind] IS NULL OR [ConsumerBindingKind] IN " +
                "(N'PrimaryArtifact', N'AuxiliaryArtifact', N'CanonicalJson', N'Annotation')");
            table.HasCheckConstraint(
                "CK_CentralDerivativeJobDependencies_CompatibleBinding",
                "[Kind] IN (N'Outcome', N'Ordering') OR " +
                "([Kind] = N'Artifact' AND [ConsumerBindingKind] IN (N'PrimaryArtifact', N'AuxiliaryArtifact')) OR " +
                "([Kind] = N'CanonicalJson' AND [ConsumerBindingKind] = N'CanonicalJson') OR " +
                "([Kind] = N'Annotation' AND [ConsumerBindingKind] = N'Annotation')");
        });
        entity.HasKey(item => item.Id);
        entity.HasAlternateKey(item => new { item.ConsumerJobId, item.Id });
        entity.Property(item => item.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.ConsumerBindingName).HasMaxLength(128).UseCollation(BinaryCollation);
        entity.Property(item => item.ConsumerBindingKind).HasConversion<string>().HasMaxLength(32);
        entity.HasIndex(item => new { item.ConsumerJobId, item.Ordinal }).IsUnique();
        entity.HasIndex(item => new { item.ExecutionId, item.ConsumerJobId });
        entity.HasIndex(item => new { item.ProducerJobId, item.ProducerOutputOrdinal });
        entity.HasIndex(item => item.ProducerSourceId);
        entity.HasOne(item => item.Execution).WithMany(item => item.Dependencies)
            .HasForeignKey(item => item.ExecutionId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.ConsumerJob).WithMany(item => item.Dependencies)
            .HasForeignKey(item => item.ConsumerJobId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.ProducerJob).WithMany(item => item.Dependents)
            .HasForeignKey(item => item.ProducerJobId).OnDelete(DeleteBehavior.NoAction);
        entity.HasOne(item => item.ProducerSource).WithMany(item => item.Dependents)
            .HasForeignKey(item => item.ProducerSourceId).OnDelete(DeleteBehavior.NoAction);
        entity.HasOne(item => item.ProducerOutput).WithMany(item => item.Dependents)
            .HasForeignKey(item => new { item.ProducerJobId, item.ProducerOutputOrdinal })
            .HasPrincipalKey(item => new { item.CentralDerivativeJobId, item.Ordinal })
            .OnDelete(DeleteBehavior.NoAction);
    }

    private static void Sha256(PropertyBuilder<string> property)
        => property.HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();

    private static void OptionalSha256(PropertyBuilder<string?> property)
        => property.HasMaxLength(64).IsFixedLength().IsUnicode(false);
}
