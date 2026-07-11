using System;

namespace HVO.SkyMonitor.LogicHost.Data;

/// <summary>
/// Stores metadata about a device upload so the processing pipeline can reference the correct rig profile.
/// Does not store image bytes.
/// </summary>
internal sealed class DeviceImageUpload
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid RegistrationId { get; set; }

    public Guid DevicePublicId { get; set; }

    public Guid ObservatoryId { get; set; }

    public int? RigProfileVersion { get; set; }

    public DateTimeOffset CapturedAtUtc { get; set; }

    public DateTimeOffset ReceivedAtUtc { get; set; }

    public string ContentType { get; set; } = string.Empty;

    public string? FileName { get; set; }

    public int PayloadBase64Length { get; set; }

    public string StorageReference { get; set; } = string.Empty;

    public string? IdempotencyKey { get; set; }

    public Guid? ArtifactId { get; set; }

    public string? ArtifactRole { get; set; }

    public string? ChecksumSha256 { get; set; }

    public long? ByteLength { get; set; }

    public string? AgentId { get; set; }
}
