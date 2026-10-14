namespace HVO.SkyMonitor.Video.FFmpeg;

/// <summary>Checks persisted media evidence without trusting host-specific publication metadata.</summary>
public static class TimeLapseEvidenceValidation
{
    public static void Validate(TimeLapseEncodingEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var media = evidence.Media;
        if (evidence.ProfileVersion != FFmpegTimeLapseEncoder.ProfileVersion || !Enum.IsDefined(evidence.Profile) ||
            !evidence.Capability.Available || !Sha256(evidence.Capability.BinarySha256) || !Sha256(evidence.Capability.ProbeBinarySha256) ||
            !Sha256(evidence.TimelineIdentitySha256) || !Sha256(evidence.EncodingIdentitySha256) || !Sha256(evidence.PayloadSha256) ||
            evidence.PayloadBytes is < 1 or > 32L * 1024 * 1024 * 1024 ||
            media.Width is < 2 or > 4096 || media.Height is < 2 or > 4096 || media.Width % 2 != 0 || media.Height % 2 != 0 ||
            media.DurationTicks <= 0 || !Sha256(media.StreamIdentitySha256) || media.Packets.Count is < 1 or > 400000 ||
            evidence.EncodedImageChecksums.Count > 40001 || evidence.EncodedImageChecksums.Any(static image => image.Key < -1 || !Sha256(image.Value)))
            throw new InvalidDataException("Invalid retained video encoding evidence.");
        long end = 0;
        foreach (var packet in media.Packets)
        {
            if (packet.PresentationTick != end || packet.DecodeTick != end || packet.DurationTicks <= 0 ||
                packet.DurationTicks > media.DurationTicks - end || packet.Sha256 is not { Length: 71 } ||
                !packet.Sha256.StartsWith("SHA256:", StringComparison.Ordinal) || !Sha256(packet.Sha256[7..]))
                throw new InvalidDataException("Invalid retained video packet timing or checksum.");
            end += packet.DurationTicks;
        }
        if (end != media.DurationTicks) throw new InvalidDataException("Retained video packet duration is incomplete.");
    }

    private static bool Sha256(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
