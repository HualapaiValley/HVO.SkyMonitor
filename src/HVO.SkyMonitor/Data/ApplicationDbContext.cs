using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.Data;

/// <summary>
/// Application database context with PostgreSQL (via Aspire).
/// </summary>
public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DisplayName)
                .HasColumnName("Name")
                .IsRequired()
                .HasMaxLength(200);
            entity.Property(e => e.HashedKey)
                .HasColumnName("HashedKey")
                .IsRequired();
            entity.Property(e => e.AccessLevel).IsRequired();
            entity.Property(e => e.CreatedUtc)
                .HasColumnName("CreatedAt")
                .IsRequired();
            entity.Property(e => e.IsActive).IsRequired();
            entity.Property(e => e.CreatedBy).HasMaxLength(256);
            entity.Property(e => e.ExpiresUtc)
                .HasColumnName("ExpiresAt");

            entity.HasOne<ApplicationUser>()
                .WithMany(u => u.ApiKeys)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.IsActive, e.ExpiresUtc });
        });
    }
}
