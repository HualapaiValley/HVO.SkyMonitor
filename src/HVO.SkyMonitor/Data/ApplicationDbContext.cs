using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HVO.SkyMonitor.Data;

/// <summary>
/// Application database context with SQLite (will migrate to PostgreSQL in Phase 8).
/// </summary>
public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        ConfigureApiKeys(builder.Entity<ApiKey>());
    }

    private static void ConfigureApiKeys(EntityTypeBuilder<ApiKey> entity)
    {
        entity.ToTable("ApiKeys");

        entity.HasIndex(key => key.HashedKey).IsUnique();

        entity.Property(key => key.HashedKey)
            .HasMaxLength(64)
            .IsRequired();

        entity.Property(key => key.DisplayName)
            .HasColumnName("Name")
            .HasMaxLength(200)
            .IsRequired();

        entity.Property(key => key.AccessLevel)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        entity.Property(key => key.IsActive)
            .HasDefaultValue(true)
            .IsRequired();

        entity.Property(key => key.CreatedUtc)
            .HasColumnName("CreatedAt")
            .IsRequired();

        entity.Property(key => key.CreatedBy)
            .HasMaxLength(256);

        entity.Property(key => key.ExpiresUtc)
            .HasColumnName("ExpiresAt");

        entity.Property(key => key.LastUsedUtc);

        entity.HasOne<ApplicationUser>()
            .WithMany(user => user.ApiKeys)
            .HasForeignKey(key => key.UserId)
            .OnDelete(DeleteBehavior.Cascade)
            .IsRequired();

        entity.HasIndex(key => key.UserId);
        entity.HasIndex(key => new { key.IsActive, key.ExpiresUtc });
    }
}
