using System;

namespace HVO.SkyMonitor.LogicHost.Data;

/// <summary>
/// Tracks the lifecycle of a camera agent device registration.
/// </summary>
internal sealed class DeviceRegistration
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string DeviceId { get; set; } = string.Empty;

    public Guid ObservatoryId { get; set; }

    public string FriendlyName { get; set; } = string.Empty;

    public DeviceRegistrationStatus Status { get; set; } = DeviceRegistrationStatus.Pending;

    public string VerificationCodeHash { get; set; } = string.Empty;

    public Guid? DevicePublicId { get; set; }

    public string? RegistrationTokenHash { get; set; }

    public string? DeviceKeyHash { get; set; }

    public DateTimeOffset IssuedAtUtc { get; set; }

    public DateTimeOffset? ExpiresAtUtc { get; set; }

    public DateTimeOffset? LastSeenUtc { get; set; }

    public string? RevokedReason { get; set; }

    public DateTimeOffset? ActivatedAtUtc { get; set; }

    public string EnvelopeVersion { get; set; } = "v1";
}

internal enum DeviceRegistrationStatus
{
    Pending = 0,
    Active = 1,
    Revoked = 2
}
