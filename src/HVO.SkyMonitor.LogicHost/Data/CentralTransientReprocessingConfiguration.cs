using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Data;

internal static class CentralTransientReprocessingConfiguration
{
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    public static void Configure(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var entity = builder.Entity<CentralTransientReprocessingJob>();
        entity.ToTable("CentralTransientReprocessingJobs", table =>
        {
            table.HasTrigger("TR_CentralTransientReprocessingJobs_Immutable");
            table.HasCheckConstraint("CK_CentralTransientReprocessingJobs_RequestLength",
                "[CanonicalRequestByteLength] > 0");
            table.HasCheckConstraint("CK_CentralTransientReprocessingJobs_Commit",
                "([CommittedUtc] IS NULL AND [ResultAssessmentId] IS NULL AND [ResultEventVersionId] IS NULL) OR " +
                "([CommittedUtc] IS NOT NULL AND [ResultAssessmentId] IS NOT NULL AND [ResultEventVersionId] IS NOT NULL)");
        });
        entity.HasKey(item => item.CentralDerivativeJobId);
        entity.Property(item => item.ActorIdentity).HasMaxLength(256).UseCollation(BinaryCollation).IsRequired();
        entity.Property(item => item.IdempotencyKey).HasMaxLength(128).UseCollation(BinaryCollation).IsRequired();
        entity.Property(item => item.CanonicalRequestJson).IsRequired();
        Sha256(entity.Property(item => item.CanonicalRequestSha256));
        Sha256(entity.Property(item => item.RequestIdentitySha256));
        entity.Property(item => item.ProducerName).HasMaxLength(128).UseCollation(BinaryCollation).IsRequired();
        entity.Property(item => item.ProducerVersion).HasMaxLength(128).UseCollation(BinaryCollation).IsRequired();
        Sha256(entity.Property(item => item.RecipeIdentitySha256));
        Sha256(entity.Property(item => item.OptionsIdentitySha256));
        entity.Property(item => item.OptionsJson).IsRequired();
        entity.HasIndex(item => new
        {
            item.CentralTransientEventId,
            item.ActorIdentity,
            item.IdempotencyKey
        }).IsUnique();
        entity.HasIndex(item => item.RequestIdentitySha256).IsUnique();
        entity.HasOne(item => item.Job).WithOne()
            .HasForeignKey<CentralTransientReprocessingJob>(item => item.CentralDerivativeJobId)
            .OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.Event).WithMany()
            .HasForeignKey(item => item.CentralTransientEventId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.SourceEventVersion).WithMany()
            .HasForeignKey(item => new { item.CentralTransientEventId, item.SourceEventVersionId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.EventVersionId })
            .OnDelete(DeleteBehavior.NoAction).IsRequired();
        entity.HasOne<CentralTransientAssessmentRecord>().WithMany()
            .HasForeignKey(item => new { item.CentralTransientEventId, item.ResultAssessmentId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.AssessmentId })
            .OnDelete(DeleteBehavior.NoAction);
        entity.HasOne<CentralTransientEventVersionRecord>().WithMany()
            .HasForeignKey(item => new { item.CentralTransientEventId, item.ResultEventVersionId })
            .HasPrincipalKey(item => new { item.CentralTransientEventId, item.EventVersionId })
            .OnDelete(DeleteBehavior.NoAction);

        var request = builder.Entity<CentralTransientReprocessingRequestRecord>();
        request.ToTable("CentralTransientReprocessingRequests", table =>
            table.HasTrigger("TR_CentralTransientReprocessingRequests_Immutable"));
        request.HasKey(item => item.RequestId);
        request.Property(item => item.ActorIdentity).HasMaxLength(256).UseCollation(BinaryCollation).IsRequired();
        request.Property(item => item.IdempotencyKey).HasMaxLength(128).UseCollation(BinaryCollation).IsRequired();
        Sha256(request.Property(item => item.RecipeIdentitySha256));
        Sha256(request.Property(item => item.OptionsIdentitySha256));
        request.HasIndex(item => new
        {
            item.CentralTransientEventId,
            item.ActorIdentity,
            item.IdempotencyKey
        }).IsUnique();
        request.HasOne(item => item.Event).WithMany()
            .HasForeignKey(item => item.CentralTransientEventId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        request.HasOne(item => item.ReprocessingJob).WithMany()
            .HasForeignKey(item => item.CentralDerivativeJobId).OnDelete(DeleteBehavior.Restrict).IsRequired();
    }

    private static void Sha256(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<string> property)
        => property.HasMaxLength(64).IsUnicode(false).UseCollation(BinaryCollation).IsRequired();
}
