namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record DeviceRegistrationEnvelopePayload(
    Guid RegistrationId,
    string DeviceId,
    Guid DevicePublicId,
    Guid ObservatoryId,
    string FriendlyName,
    string EnvelopeVersion,
    string DeviceKey,
    string RegistrationToken,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);
