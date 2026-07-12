using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HVO.SkyMonitor.LogicHost.Data;

/// <summary>
/// Application database context backed by SQL Server.
/// Includes Identity tables, API keys, and OpenIddict entities.
/// </summary>
public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    internal DbSet<DeviceRegistration> DeviceRegistrations => Set<DeviceRegistration>();
    internal DbSet<DeviceRigProfile> DeviceRigProfiles => Set<DeviceRigProfile>();
    internal DbSet<DeviceImageUpload> DeviceImageUploads => Set<DeviceImageUpload>();
    internal DbSet<Observatory> Observatories => Set<Observatory>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        base.OnModelCreating(builder);

        ConfigureApiKeys(builder.Entity<ApiKey>());
        builder.ApplyConfiguration(new DeviceRegistrationConfiguration());
        builder.ApplyConfiguration(new DeviceRigProfileConfiguration());
        builder.ApplyConfiguration(new DeviceImageUploadConfiguration());
        builder.ApplyConfiguration(new ObservatoryConfiguration());

        // Configure OpenIddict entities to use the default Entity Framework Core conventions
        builder.UseOpenIddict();
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
