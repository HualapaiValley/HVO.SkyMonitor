using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class ObservatoryConfiguration : IEntityTypeConfiguration<Observatory>
{
    public void Configure(EntityTypeBuilder<Observatory> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Observatories");

        builder.HasKey(observatory => observatory.Id);

        builder.Property(observatory => observatory.OwnerUserId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(observatory => observatory.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(observatory => observatory.LatitudeDegrees)
            .HasColumnType("double precision");

        builder.Property(observatory => observatory.LongitudeDegrees)
            .HasColumnType("double precision");

        builder.Property(observatory => observatory.ElevationMeters)
            .HasColumnType("double precision");

        builder.Property(observatory => observatory.TimeZoneId)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(observatory => observatory.CreatedAtUtc)
            .IsRequired();

        builder.Property(observatory => observatory.IsActive)
            .HasDefaultValue(true)
            .IsRequired();

        builder.HasIndex(observatory => observatory.OwnerUserId);
        builder.HasIndex(observatory => observatory.Name);
    }
}
