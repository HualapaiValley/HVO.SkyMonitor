using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class CentralArtifactDownloadAuthorizationConfiguration
    : IEntityTypeConfiguration<CentralArtifactDownloadAuthorization>
{
    public void Configure(EntityTypeBuilder<CentralArtifactDownloadAuthorization> entity)
    {
        entity.ToTable("CentralArtifactDownloadAuthorizations", table =>
        {
            table.HasTrigger("TR_CentralArtifactDownloadAuthorizations_Immutable");
            table.HasCheckConstraint(
                "CK_CentralArtifactDownloadAuthorizations_MembershipRole",
                "[MembershipRole] IN (N'Viewer', N'Manager', N'Owner')");
            table.HasCheckConstraint(
                "CK_CentralArtifactDownloadAuthorizations_Range",
                "([RangeStart] IS NULL AND [RangeEnd] IS NULL) OR ([RangeStart] >= 0 AND [RangeEnd] >= [RangeStart])");
            table.HasCheckConstraint(
                "CK_CentralArtifactDownloadAuthorizations_Expiry",
                "[ExpiresAtUtc] > [IssuedAtUtc]");
        });
        entity.HasKey(item => item.Id);
        entity.Property(item => item.ActorUserId).HasMaxLength(450).IsRequired();
        entity.Property(item => item.TokenSha256).HasMaxLength(64).IsRequired();
        entity.Property(item => item.MembershipRole).HasConversion<string>().HasMaxLength(16).IsRequired();
        entity.Property(item => item.IssuedAtUtc).IsRequired();
        entity.Property(item => item.ExpiresAtUtc).IsRequired();
        entity.HasIndex(item => new { item.CentralArtifactId, item.IssuedAtUtc, item.Id });
        entity.HasIndex(item => new { item.ActorUserId, item.IssuedAtUtc, item.Id });
        entity.HasOne(item => item.CentralArtifact).WithMany()
            .HasForeignKey(item => item.CentralArtifactId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.Observatory).WithMany()
            .HasForeignKey(item => item.ObservatoryId).OnDelete(DeleteBehavior.Restrict).IsRequired();
    }
}
