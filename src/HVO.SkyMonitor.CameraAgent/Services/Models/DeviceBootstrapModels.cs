using System;
using HVO.SkyMonitor.Common.Identity;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Services.Models;

internal sealed record DeviceBootstrapRequestDto(
    string DeviceId,
    string Envelope,
    string? Nonce,
    DeploymentLocationSnapshot DeploymentLocation,
    DeploymentLocationSourceKind DeploymentLocationSourceKind);

internal sealed record DeviceBootstrapResponseDto(
    Guid RegistrationId,
    Guid DevicePublicId,
    string EnvelopeVersion,
    string DeviceKey,
    DeviceBootstrapPayloadDto Payload);

internal sealed record DeviceBootstrapPayloadDto(
    string? Ciphertext,
    string? Nonce,
    string? Tag,
    string? Algorithm);

internal sealed record DeviceBootstrapSecretsPayload(
    Guid DevicePublicId,
    Guid ObservatoryId,
    string FriendlyName,
    string RegistrationToken,
    string HeartbeatEndpoint,
    int HeartbeatIntervalSeconds,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    CentralIdentityOptions CentralIdentity,
    string RigProfileEndpoint = "/api/device/profile/rig",
    DeploymentLocationAcknowledgment? DeploymentLocationAcknowledgment = null);
