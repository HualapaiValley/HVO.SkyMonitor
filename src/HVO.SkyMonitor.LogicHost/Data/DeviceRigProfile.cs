using System;

namespace HVO.SkyMonitor.LogicHost.Data;

/// <summary>
/// Versioned snapshot of the camera agent rig configuration used for image processing.
/// </summary>
internal sealed class DeviceRigProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid RegistrationId { get; set; }

    public DeviceRegistration? Registration { get; set; }

    public Guid DevicePublicId { get; set; }

    public Guid ObservatoryId { get; set; }

    public int Version { get; set; }

    public string ConfigHash { get; set; } = string.Empty;

    public string ConfigJson { get; set; } = string.Empty;

    public string? SoftwareVersion { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset EffectiveFromUtc { get; set; }
}
