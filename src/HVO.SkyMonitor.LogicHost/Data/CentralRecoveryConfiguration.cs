using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Data;

internal static class CentralRecoveryConfiguration
{
    public static void Configure(ModelBuilder builder)
    {
        var checkpoint = builder.Entity<CentralRecoveryCheckpoint>();
        checkpoint.ToTable("CentralRecoveryCheckpoints");
        checkpoint.HasKey(item => item.Id);
        checkpoint.Property(item => item.Id).ValueGeneratedNever();
        checkpoint.Property(item => item.Phase).HasMaxLength(32).IsRequired();
        checkpoint.Property(item => item.ObjectCursor).HasMaxLength(1024);
        checkpoint.Property(item => item.StagingCursor).HasMaxLength(1024);
        checkpoint.HasData(new CentralRecoveryCheckpoint
        {
            Id = CentralRecoveryCheckpoint.SingletonId,
            Generation = 0,
            Phase = CentralRecoveryPhases.Idle,
            NextInventoryAtUtc = DateTimeOffset.UnixEpoch
        });

        var disposition = builder.Entity<CentralObjectRecoveryDisposition>();
        disposition.ToTable("CentralObjectRecoveryDispositions");
        disposition.HasKey(item => item.Id);
        disposition.Property(item => item.SourceObjectIdentitySha256).HasMaxLength(64).IsUnicode(false).IsRequired();
        disposition.Property(item => item.SourceObjectKey).HasMaxLength(1024).UseCollation("Latin1_General_100_BIN2").IsRequired();
        disposition.Property(item => item.TargetObjectKey).HasMaxLength(1024).UseCollation("Latin1_General_100_BIN2");
        disposition.Property(item => item.Kind).HasMaxLength(32).IsRequired();
        disposition.Property(item => item.State).HasMaxLength(32).IsRequired();
        disposition.Property(item => item.ContentChecksumSha256).HasMaxLength(64).IsUnicode(false);
        disposition.Property(item => item.ReasonCode).HasMaxLength(128);
        disposition.Property(item => item.RowVersion).IsRowVersion();
        disposition.HasIndex(item => item.SourceObjectIdentitySha256).IsUnique();
        disposition.HasIndex(item => new { item.State, item.UpdatedAtUtc, item.Id });
    }
}
