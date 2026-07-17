using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HVO.SkyMonitor.LogicHost.Data;

internal static class EnvironmentalObservationConfiguration
{
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ConfigureSource(modelBuilder.Entity<EnvironmentalObservationSourceRecord>());
        ConfigureObservation(modelBuilder.Entity<EnvironmentalObservationRecord>());
        ConfigureLineage(modelBuilder.Entity<EnvironmentalObservationLineageRecord>());
    }

    private static void ConfigureSource(EntityTypeBuilder<EnvironmentalObservationSourceRecord> builder)
    {
        builder.ToTable("EnvironmentalObservationSources");
        builder.HasKey(source => source.Id);
        builder.Property(source => source.IdentitySha256).HasMaxLength(64).IsRequired();
        builder.Property(source => source.ContentSha256).HasMaxLength(64).IsRequired();
        builder.Property(source => source.RigId).HasMaxLength(128).UseCollation(BinaryCollation);
        builder.Property(source => source.Provider).HasMaxLength(128).IsRequired();
        builder.Property(source => source.SourceId).HasMaxLength(128).IsRequired();
        builder.Property(source => source.Version).HasMaxLength(64).IsRequired();
        builder.Property(source => source.Kind).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(source => source.MethodName).HasMaxLength(128).IsRequired();
        builder.Property(source => source.MethodVersion).HasMaxLength(64).IsRequired();
        builder.Property(source => source.ParametersJson).HasMaxLength(32_768).IsRequired();
        builder.Property(source => source.ParametersSha256).HasMaxLength(64).IsRequired();
        builder.HasIndex(source => source.IdentitySha256).IsUnique();
        builder.HasIndex(source => new { source.SiteId, source.AgentId, source.RigId, source.Kind });
        builder.HasOne(source => source.Site)
            .WithMany()
            .HasForeignKey(source => source.SiteId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureObservation(EntityTypeBuilder<EnvironmentalObservationRecord> builder)
    {
        builder.ToTable("EnvironmentalObservations", table =>
        {
            table.HasCheckConstraint(
                "CK_EnvironmentalObservations_Value",
                "([NumericValue] IS NOT NULL AND [BooleanValue] IS NULL) OR ([NumericValue] IS NULL AND [BooleanValue] IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_EnvironmentalObservations_Validity",
                "[ValidFromUtc] < [ValidThroughUtc] AND [StaleAfterUtc] >= [ValidFromUtc] AND [StaleAfterUtc] <= [ValidThroughUtc]");
            table.HasCheckConstraint(
                "CK_EnvironmentalObservations_ObservedInterval",
                "([ObservedFromUtc] IS NULL AND [ObservedThroughUtc] IS NULL) OR ([ObservedFromUtc] IS NOT NULL AND [ObservedThroughUtc] IS NOT NULL AND [ObservedFromUtc] <= [ObservedThroughUtc])");
            table.HasCheckConstraint(
                "CK_EnvironmentalObservations_SubmittedValue",
                "([SubmittedNumericValue] IS NULL AND [SubmittedUnit] IS NULL) OR ([SubmittedNumericValue] IS NOT NULL AND [SubmittedUnit] IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_EnvironmentalObservations_Uncertainty",
                "[Uncertainty] IS NULL OR [Uncertainty] >= 0");
        });
        builder.HasKey(observation => observation.Id);
        builder.Property(observation => observation.RigId).HasMaxLength(128).UseCollation(BinaryCollation);
        builder.Property(observation => observation.SourceKind).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(observation => observation.SourceIdentitySha256).HasMaxLength(64).IsRequired();
        builder.Property(observation => observation.SchemaVersion).HasMaxLength(64).IsRequired();
        builder.Property(observation => observation.Kind).HasConversion<string>().HasMaxLength(48).IsRequired();
        builder.Property(observation => observation.Unit).HasConversion<string>().HasMaxLength(48).IsRequired();
        builder.Property(observation => observation.Quality).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(observation => observation.SubmittedUnit).HasMaxLength(64);
        builder.Property(observation => observation.ClockDiagnostic).HasConversion<string>().HasMaxLength(48).IsRequired();
        builder.Property(observation => observation.PayloadSha256).HasMaxLength(64).IsRequired();
        builder.HasIndex(observation => new { observation.SourceRecordId, observation.ObservationId }).IsUnique();
        builder.HasIndex(observation => new
        {
            observation.SourceRecordId,
            observation.Kind,
            observation.ValidFromUtc,
            observation.ValidThroughUtc,
            observation.ObservedAtUtc,
            observation.Id
        }).HasDatabaseName("IX_EnvironmentalObservations_SourceKindValidity");
        builder.HasIndex(observation => new
        {
            observation.SourceRecordId,
            observation.Kind,
            observation.ValidThroughUtc,
            observation.ValidFromUtc,
            observation.ObservedAtUtc,
            observation.Id
        }).HasDatabaseName("IX_EnvironmentalObservations_SourceKindValidityEnd");
        builder.HasIndex(observation => new
        {
            observation.SiteId,
            observation.AgentId,
            observation.RigId,
            observation.SourceKind,
            observation.Kind,
            observation.Quality,
            observation.ValidThroughUtc,
            observation.ValidFromUtc,
            observation.StaleAfterUtc,
            observation.ObservedAtUtc,
            observation.SourceIdentitySha256,
            observation.ObservationId,
            observation.Id
        }).HasDatabaseName("IX_EnvironmentalObservations_TargetKindValidityEnd");
        builder.HasIndex(observation => new
        {
            observation.SiteId,
            observation.AgentId,
            observation.RigId,
            observation.SourceKind,
            observation.Kind,
            observation.Quality,
            observation.ObservedAtUtc,
            observation.ValidFromUtc,
            observation.SourceIdentitySha256,
            observation.ObservationId,
            observation.Id
        }).HasDatabaseName("IX_EnvironmentalObservations_TargetScopeKindObserved");
        builder.HasIndex(observation => new
        {
            observation.SiteId,
            observation.Kind,
            observation.ObservedAtUtc,
            observation.AgentId,
            observation.RigId,
            observation.SourceIdentitySha256,
            observation.ObservationId,
            observation.Id
        }).HasDatabaseName("IX_EnvironmentalObservations_TargetKindObserved");
        builder.HasIndex(observation => new
        {
            observation.SourceRecordId,
            observation.Kind,
            observation.ObservedAtUtc,
            observation.Id
        }).HasDatabaseName("IX_EnvironmentalObservations_SourceKindObserved");
        builder.HasIndex(observation => new { observation.ReceivedAtUtc, observation.ValidThroughUtc, observation.Id })
            .HasDatabaseName("IX_EnvironmentalObservations_Retention");
        builder.HasOne(observation => observation.Source)
            .WithMany(source => source.Observations)
            .HasForeignKey(observation => observation.SourceRecordId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureLineage(EntityTypeBuilder<EnvironmentalObservationLineageRecord> builder)
    {
        builder.ToTable("EnvironmentalObservationLineage");
        builder.HasKey(item => new { item.DerivedObservationRecordId, item.Ordinal });
        builder.ToTable("EnvironmentalObservationLineage", table =>
            table.HasCheckConstraint("CK_EnvironmentalObservationLineage_Ordinal", "[Ordinal] >= 0"));
        builder.HasIndex(item => new { item.DerivedObservationRecordId, item.SourceObservationRecordId }).IsUnique();
        builder.HasIndex(item => item.SourceObservationRecordId);
        builder.HasOne(item => item.DerivedObservation)
            .WithMany(observation => observation.Lineage)
            .HasForeignKey(item => item.DerivedObservationRecordId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.SourceObservation)
            .WithMany(observation => observation.ReferencedBy)
            .HasForeignKey(item => item.SourceObservationRecordId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
