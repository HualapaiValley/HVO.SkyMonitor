using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class DeviceImageUploadConfiguration : IEntityTypeConfiguration<DeviceImageUpload>
{
    public void Configure(EntityTypeBuilder<DeviceImageUpload> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("DeviceImageUploads");

        builder.HasKey(upload => upload.Id);

        builder.Property(upload => upload.RegistrationId)
            .IsRequired();

        builder.Property(upload => upload.DevicePublicId)
            .IsRequired();

        builder.Property(upload => upload.ObservatoryId)
            .IsRequired();

        builder.Property(upload => upload.RigProfileVersion);

        builder.Property(upload => upload.CapturedAtUtc)
            .IsRequired();

        builder.Property(upload => upload.ReceivedAtUtc)
            .IsRequired();

        builder.Property(upload => upload.ContentType)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(upload => upload.FileName)
            .HasMaxLength(256);

        builder.Property(upload => upload.PayloadBase64Length)
            .IsRequired();

        builder.Property(upload => upload.StorageReference)
            .HasMaxLength(256)
            .IsRequired();

        builder.HasIndex(upload => upload.RegistrationId);
        builder.HasIndex(upload => upload.DevicePublicId);
        builder.HasIndex(upload => upload.ObservatoryId);
        builder.HasIndex(upload => upload.CapturedAtUtc);
    }
}
