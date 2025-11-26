using System.ComponentModel.DataAnnotations;

namespace HVO.SkyMonitor.LogicHost.Data;

/// <summary>
/// Represents a physical observatory configured by a LogicHost operator.
/// </summary>
internal sealed class Observatory
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Primary owner of the observatory (Identity user id).</summary>
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>Friendly display name.</summary>
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public double LatitudeDegrees { get; set; }

    public double LongitudeDegrees { get; set; }

    public double ElevationMeters { get; set; }

    [MaxLength(128)]
    public string TimeZoneId { get; set; } = "UTC";

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? UpdatedAtUtc { get; set; }

    public bool IsActive { get; set; } = true;

    public ICollection<DeviceRegistration> DeviceRegistrations { get; } = new List<DeviceRegistration>();
}
