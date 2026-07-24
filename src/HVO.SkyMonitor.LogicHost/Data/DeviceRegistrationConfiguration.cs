using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class DeviceRegistrationConfiguration : IEntityTypeConfiguration<DeviceRegistration>
{
    public void Configure(EntityTypeBuilder<DeviceRegistration> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("DeviceRegistrations");

        builder.HasKey(registration => registration.Id);

        builder.Property(registration => registration.DeviceId)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(registration => registration.ObservatoryId)
            .IsRequired();

        builder.Property(registration => registration.FriendlyName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(registration => registration.ObservatoryName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(registration => registration.ObservatoryTimeZoneId)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(registration => registration.ObservatoryLatitudeDegrees);
        builder.Property(registration => registration.ObservatoryLongitudeDegrees);
        builder.Property(registration => registration.ObservatoryElevationMeters);
        builder.Property(registration => registration.ObservatoryLocationCanonicalSha256).HasMaxLength(64);
        builder.Property(registration => registration.LocationEvidenceState)
            .HasConversion<string>().HasMaxLength(32)
            .HasDefaultValue(RegistrationLocationEvidenceState.LegacyIncomplete).IsRequired();

        builder.Property(registration => registration.OwnerUserId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(registration => registration.OwnerDisplayName)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(registration => registration.OwnerEmail)
            .HasMaxLength(256);

        builder.Property(registration => registration.OwnerConfirmationMethod)
            .HasMaxLength(64)
            .HasDefaultValue("SelfAttested")
            .IsRequired();

        builder.Property(registration => registration.OwnerConfirmationNotes)
            .HasMaxLength(512);

        builder.Property(registration => registration.OwnerConfirmedAtUtc);

        builder.Property(registration => registration.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(registration => registration.VerificationCodeHash)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(registration => registration.DevicePublicId);

        builder.Property(registration => registration.RegistrationTokenHash)
            .HasMaxLength(128);

        builder.Property(registration => registration.DeviceKeyHash)
            .HasMaxLength(128);

        builder.Property(registration => registration.IssuedAtUtc)
            .IsRequired();

        builder.Property(registration => registration.ExpiresAtUtc);
        builder.Property(registration => registration.LastSeenUtc);
        builder.Property(registration => registration.ActivatedAtUtc);

        builder.Property(registration => registration.CurrentRigProfileVersion);

        builder.Property(registration => registration.CurrentRigProfileHash)
            .HasMaxLength(128);

        builder.Property(registration => registration.CurrentRigProfileUpdatedAtUtc);

        builder.Property(registration => registration.RevokedReason)
            .HasMaxLength(512);

        builder.Property(registration => registration.EnvelopeVersion)
            .HasMaxLength(16)
            .HasDefaultValue("v2")
            .IsRequired();

        builder.HasIndex(registration => registration.DeviceId);
        builder.HasIndex(registration => new { registration.DeviceId, registration.Status })
            .IsUnique()
            .HasFilter("[Status] = N'Active'");
        builder.HasIndex(registration => new { registration.ObservatoryId, registration.Status });
        builder.HasIndex(registration => registration.DevicePublicId)
            .IsUnique()
            .HasFilter("[DevicePublicId] IS NOT NULL");
        builder.HasIndex(registration => registration.DeviceKeyHash)
            .HasFilter("[DeviceKeyHash] IS NOT NULL");

        builder.HasOne(registration => registration.Observatory)
            .WithMany(observatory => observatory.DeviceRegistrations)
            .HasForeignKey(registration => registration.ObservatoryId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired();
    }
}
