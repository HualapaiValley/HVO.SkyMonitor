using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class CentralDerivativeJobConfiguration : IEntityTypeConfiguration<CentralDerivativeJob>
{
    public void Configure(EntityTypeBuilder<CentralDerivativeJob> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("CentralDerivativeJobs", table =>
        {
            table.HasCheckConstraint("CK_CentralDerivativeJobs_AttemptCount", "[AttemptCount] >= 0 AND [AttemptCount] <= [MaxAttempts]");
            table.HasCheckConstraint("CK_CentralDerivativeJobs_MaxAttempts", "[MaxAttempts] > 0");
            table.HasCheckConstraint(
                "CK_CentralDerivativeJobs_GraphOwnership",
                "([GraphExecutionId] IS NULL AND [GraphNodeId] IS NULL AND [GraphNodeOrdinal] IS NULL AND " +
                "[SharedNodePlanIdentitySha256] IS NULL AND [FrozenNodePlanJson] IS NULL AND [GraphFailurePolicy] IS NULL) OR " +
                "([GraphExecutionId] IS NOT NULL AND [GraphNodeId] IS NOT NULL AND [GraphNodeOrdinal] >= 0 AND " +
                "[SharedNodePlanIdentitySha256] IS NOT NULL AND [FrozenNodePlanJson] IS NOT NULL AND [GraphFailurePolicy] IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_CentralDerivativeJobs_GraphFailurePolicy",
                "[GraphFailurePolicy] IS NULL OR [GraphFailurePolicy] IN (N'Required', N'Optional')");
            table.HasCheckConstraint(
                "CK_CentralDerivativeJobs_WaitKind",
                "[WaitKind] IS NULL OR [WaitKind] IN (N'Dependencies', N'Window')");
            table.HasCheckConstraint(
                "CK_CentralDerivativeJobs_MinimumInputCount",
                "[MinimumInputCount] IS NULL OR [MinimumInputCount] >= 0");
            table.HasTrigger("TR_CentralDerivativeJobs_GraphIdentityImmutable");
        });
        builder.HasKey(job => job.Id);
        builder.Property(job => job.TargetRole).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(job => job.TargetRecipeVersion).HasMaxLength(128).IsRequired();
        builder.Property(job => job.TargetVariant).HasMaxLength(128).IsRequired();
        builder.Property(job => job.RecipeName).HasMaxLength(128).IsRequired();
        builder.Property(job => job.RecipeOptionsJson).IsRequired();
        builder.Property(job => job.InputSelectorJson).HasMaxLength(2048).IsRequired();
        builder.Property(job => job.RequestedRecipeIdentitySha256).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(job => job.ExpectedRecipeIdentitySha256).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(job => job.RequestIdentitySha256).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(job => job.TraceParent).HasMaxLength(128).IsUnicode(false);
        builder.Property(job => job.TraceState).HasMaxLength(512).IsUnicode(false);
        builder.Property(job => job.GraphNodeId).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2");
        builder.Property(job => job.SharedNodePlanIdentitySha256).HasMaxLength(64).IsFixedLength().IsUnicode(false);
        builder.Property(job => job.GraphFailurePolicy).HasConversion<string>().HasMaxLength(16);
        builder.Property(job => job.WaitKind).HasConversion<string>().HasMaxLength(16);
        builder.Property(job => job.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(job => job.MissingInputOutcome).HasConversion<string>().HasMaxLength(32);
        builder.Property(job => job.StateReasonCode).HasMaxLength(256);
        builder.Property(job => job.InputSetIdentitySha256).HasMaxLength(64).IsUnicode(false);
        builder.Property(job => job.LeaseOwner).HasMaxLength(256);
        builder.Property(job => job.LastError).HasMaxLength(2048);
        builder.Property(job => job.CancellationRequestedBy).HasMaxLength(256);
        builder.Property(job => job.RowVersion).IsRowVersion();
        builder.HasIndex(job => job.RequestIdentitySha256).IsUnique();
        builder.HasIndex(job => new { job.SourceCentralArtifactId, job.TargetRole, job.TargetRecipeVersion });
        builder.HasIndex(job => new { job.Status, job.AvailableAtUtc, job.CreatedAtUtc, job.Id });
        builder.HasIndex(job => new { job.Status, job.LeaseExpiresAtUtc, job.CreatedAtUtc, job.Id });
        builder.HasIndex(job => new
        {
            job.Status,
            job.UpdatedAtUtc,
            job.ResolutionDeadlineUtc,
            job.CreatedAtUtc,
            job.Id
        });
        builder.HasIndex(job => new { job.CreatedAtUtc, job.Id });
        builder.HasIndex(job => job.ResultCentralArtifactId);
        builder.HasIndex(job => job.RetainedResultCentralArtifactId);
        builder.HasIndex(job => job.PredecessorJobId).IsUnique().HasFilter("[PredecessorJobId] IS NOT NULL");
        builder.HasIndex(job => new { job.GraphExecutionId, job.GraphNodeOrdinal }).IsUnique()
            .HasFilter("[GraphExecutionId] IS NOT NULL");
        builder.HasIndex(job => new { job.GraphExecutionId, job.GraphNodeId }).IsUnique()
            .HasFilter("[GraphExecutionId] IS NOT NULL");
        builder.HasOne(job => job.SourceArtifact)
            .WithMany()
            .HasForeignKey(job => job.SourceCentralArtifactId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired();
        builder.HasOne(job => job.ResultArtifact)
            .WithMany()
            .HasForeignKey(job => job.ResultCentralArtifactId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(job => job.SupersededByJob)
            .WithMany()
            .HasForeignKey(job => job.SupersededByJobId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(job => job.PredecessorJob)
            .WithMany()
            .HasForeignKey(job => job.PredecessorJobId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<CentralArtifact>()
            .WithMany()
            .HasForeignKey(job => job.RetainedResultCentralArtifactId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(job => job.GraphExecution).WithMany(execution => execution.Jobs)
            .HasForeignKey(job => job.GraphExecutionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
