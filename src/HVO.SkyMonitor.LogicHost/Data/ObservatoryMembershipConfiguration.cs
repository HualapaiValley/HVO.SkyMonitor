using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Data;

internal static class ObservatoryMembershipConfiguration
{
    internal static void Configure(ModelBuilder builder)
    {
        ConfigureMembership(builder);
        ConfigureAudit(builder);
    }

    private static void ConfigureMembership(ModelBuilder builder)
    {
        var entity = builder.Entity<ObservatoryMembership>();
        entity.ToTable("ObservatoryMemberships", table =>
            table.HasCheckConstraint(
                "CK_ObservatoryMemberships_Role",
                "[Role] IN (N'Viewer', N'Manager', N'Owner')"));
        entity.HasKey(item => new { item.ObservatoryId, item.UserId });
        entity.Property(item => item.UserId).HasMaxLength(450).IsRequired();
        entity.Property(item => item.Role).HasConversion<string>().HasMaxLength(16).IsRequired();
        entity.Property(item => item.AddedAtUtc).IsRequired();
        entity.HasIndex(item => new { item.UserId, item.ObservatoryId });
        entity.HasIndex(item => new { item.ObservatoryId, item.Role });
        entity.HasIndex(item => new { item.ObservatoryId, item.AddedAtUtc, item.UserId });
        entity.HasOne(item => item.Observatory).WithMany(item => item.Memberships)
            .HasForeignKey(item => item.ObservatoryId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        entity.HasOne(item => item.User).WithMany()
            .HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Restrict).IsRequired();
    }

    private static void ConfigureAudit(ModelBuilder builder)
    {
        var entity = builder.Entity<ObservatoryMembershipAudit>();
        entity.ToTable("ObservatoryMembershipAudits", table =>
        {
            table.HasTrigger("TR_ObservatoryMembershipAudits_Immutable");
            table.HasCheckConstraint(
                "CK_ObservatoryMembershipAudits_Action",
                "[Action] IN (N'Granted', N'RoleChanged', N'Removed')");
            table.HasCheckConstraint(
                "CK_ObservatoryMembershipAudits_PreviousRole",
                "[PreviousRole] IS NULL OR [PreviousRole] IN (N'Viewer', N'Manager', N'Owner')");
            table.HasCheckConstraint(
                "CK_ObservatoryMembershipAudits_NewRole",
                "[NewRole] IS NULL OR [NewRole] IN (N'Viewer', N'Manager', N'Owner')");
            table.HasCheckConstraint(
                "CK_ObservatoryMembershipAudits_Transition",
                "([Action] = N'Granted' AND [PreviousRole] IS NULL AND [NewRole] IS NOT NULL) " +
                "OR ([Action] = N'RoleChanged' AND [PreviousRole] IS NOT NULL AND [NewRole] IS NOT NULL AND [PreviousRole] <> [NewRole]) " +
                "OR ([Action] = N'Removed' AND [PreviousRole] IS NOT NULL AND [NewRole] IS NULL)");
        });
        entity.HasKey(item => item.Id);
        entity.Property(item => item.TargetUserId).HasMaxLength(450).IsRequired();
        entity.Property(item => item.ActorUserId).HasMaxLength(450);
        entity.Property(item => item.Action).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(item => item.PreviousRole).HasConversion<string>().HasMaxLength(16);
        entity.Property(item => item.NewRole).HasConversion<string>().HasMaxLength(16);
        entity.Property(item => item.ReasonCode).HasMaxLength(128).IsRequired();
        entity.Property(item => item.OccurredAtUtc).IsRequired();
        entity.HasIndex(item => new { item.ObservatoryId, item.OccurredAtUtc, item.Id });
        entity.HasOne(item => item.Observatory).WithMany(item => item.MembershipAudits)
            .HasForeignKey(item => item.ObservatoryId).OnDelete(DeleteBehavior.Restrict).IsRequired();
    }
}
