using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class DeviceRigProfileConfiguration : IEntityTypeConfiguration<DeviceRigProfile>
{
    public void Configure(EntityTypeBuilder<DeviceRigProfile> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("DeviceRigProfiles");

        builder.HasKey(profile => profile.Id);

        builder.Property(profile => profile.RegistrationId)
            .IsRequired();

        builder.Property(profile => profile.DevicePublicId)
            .IsRequired();

        builder.Property(profile => profile.ObservatoryId)
            .IsRequired();

        builder.Property(profile => profile.Version)
            .IsRequired();

        builder.Property(profile => profile.ConfigHash)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(profile => profile.ConfigJson)
            .HasMaxLength(262144)
            .IsRequired();

        builder.Property(profile => profile.ProfileName).HasMaxLength(128).IsRequired();
        builder.Property(profile => profile.ProfileVersion).HasMaxLength(128).IsRequired();
        builder.Property(profile => profile.ProfileSha256).HasMaxLength(64).IsRequired();

        builder.Property(profile => profile.SoftwareVersion)
            .HasMaxLength(64);

        builder.Property(profile => profile.CreatedAtUtc)
            .IsRequired();

        builder.Property(profile => profile.EffectiveFromUtc)
            .IsRequired();

        builder.HasIndex(profile => new { profile.DevicePublicId, profile.Version })
            .IsUnique();

        builder.HasIndex(profile => new { profile.RegistrationId, profile.Version });

        builder.HasIndex(profile => profile.ObservatoryId);
        builder.HasIndex(profile => new { profile.DevicePublicId, profile.ProfileName, profile.ProfileVersion, profile.ProfileSha256 });

        builder.HasOne(profile => profile.Registration)
            .WithMany()
            .HasForeignKey(profile => profile.RegistrationId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired();
    }
}
