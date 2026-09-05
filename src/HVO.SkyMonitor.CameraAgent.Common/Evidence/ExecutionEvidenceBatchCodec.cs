namespace HVO.SkyMonitor.CameraAgent.Common.Evidence;

/// <summary>
/// Frames a bounded batch of canonical evidence envelopes as newline-delimited JSON.
/// </summary>
/// <remarks>
/// The canonical serializer never emits a raw newline (JSON escapes every control character inside a string and the
/// canonical form carries no insignificant whitespace), so <c>0x0A</c> is an unambiguous separator and every
/// envelope's bytes survive framing unchanged. That matters because the receiver recomputes the canonical payload
/// hash from the bytes it receives: a framing that re-serialized could change them, and the recomputed hash would
/// then disagree with the one the envelope carries, turning every unit into a conflict.
/// </remarks>
public static class ExecutionEvidenceBatchCodec
{
    /// <summary>The media type a request carrying this framing declares.</summary>
    public const string MediaType = "application/x-hvo-execution-evidence-v1";

    private const byte Separator = (byte)'\n';

    public static byte[] Encode(IReadOnlyList<byte[]> envelopes)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        var length = 0;
        foreach (var envelope in envelopes)
        {
            ArgumentNullException.ThrowIfNull(envelope);
            if (Array.IndexOf(envelope, Separator) >= 0)
            {
                throw new ArgumentException(
                    "A canonical evidence envelope must not contain a raw newline.", nameof(envelopes));
            }
            length += envelope.Length + 1;
        }
        var buffer = new byte[length];
        var offset = 0;
        foreach (var envelope in envelopes)
        {
            envelope.CopyTo(buffer, offset);
            offset += envelope.Length;
            buffer[offset++] = Separator;
        }
        return buffer;
    }

    /// <summary>
    /// Splits a framed batch back into its exact envelope byte sequences. A trailing separator is required so a
    /// truncated transfer cannot be mistaken for a shorter but complete batch.
    /// </summary>
    public static IReadOnlyList<byte[]> Decode(ReadOnlySpan<byte> payload, int maximumUnits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumUnits, 1);
        if (payload.Length == 0)
        {
            return [];
        }
        if (payload[^1] != Separator)
        {
            throw new InvalidDataException("The evidence batch is truncated: it does not end with a separator.");
        }
        var results = new List<byte[]>();
        var start = 0;
        for (var index = 0; index < payload.Length; index++)
        {
            if (payload[index] != Separator)
            {
                continue;
            }
            if (index == start)
            {
                throw new InvalidDataException("The evidence batch contains an empty unit.");
            }
            if (results.Count == maximumUnits)
            {
                throw new InvalidDataException("The evidence batch carries more units than the negotiated limit.");
            }
            results.Add(payload[start..index].ToArray());
            start = index + 1;
        }
        return results;
    }
}
