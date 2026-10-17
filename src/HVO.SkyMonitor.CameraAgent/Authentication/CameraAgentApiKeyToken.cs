using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace HVO.SkyMonitor.CameraAgent.Authentication;

/// <summary>
/// The text of a CameraAgent API key: <c>hvo_sky_</c>, a 16-character public key identifier, <c>_</c>, and 256 random
/// bits in base64url. Only a SHA-256 of the whole text is stored; the text itself is shown once, when the key is
/// created.
/// </summary>
/// <remarks>
/// The secret part is random, not chosen by a person, so a fast hash is enough: there is no dictionary to try, and
/// 256 bits cannot be searched. The identifier lets a presented key be found without comparing it against every stored
/// hash, and it is safe to show and log. The fixed prefix lets secret scanners recognise a leaked key.
/// </remarks>
internal static class CameraAgentApiKeyToken
{
    public const string Prefix = "hvo_sky_";

    /// <summary>Hexadecimal characters in the public key identifier (64 bits).</summary>
    public const int IdLength = 16;

    private const int SecretBytes = 32;
    private const int SecretLength = 43;

    /// <summary>The exact length of every key this host issues.</summary>
    public const int Length = 8 + IdLength + 1 + SecretLength;

    /// <summary>A new key and its public identifier.</summary>
    public static (string Id, string Token) Create()
    {
        var id = RandomNumberGenerator.GetHexString(IdLength, lowercase: true);
        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretBytes));
        return (id, string.Concat(Prefix, id, "_", secret));
    }

    /// <summary>Whether <paramref name="value"/> looks like a key this host issues at all.</summary>
    public static bool HasPrefix(string? value) => value?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// Reads the public identifier of a key in the issued format. Any other text, including one of the wrong length,
    /// is refused before it is hashed or looked up.
    /// </summary>
    public static bool TryReadId(string? token, out string id)
    {
        id = string.Empty;
        if (token is not { Length: Length } || !token.StartsWith(Prefix, StringComparison.Ordinal) ||
            token[Prefix.Length + IdLength] != '_')
        {
            return false;
        }

        var candidate = token.AsSpan(Prefix.Length, IdLength);
        var secret = token.AsSpan(Prefix.Length + IdLength + 1);
        foreach (var character in candidate)
        {
            if (!char.IsAsciiHexDigitLower(character))
            {
                return false;
            }
        }

        foreach (var character in secret)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                return false;
            }
        }

        id = candidate.ToString();
        return true;
    }

    /// <summary>The stored form of a key: the lowercase hexadecimal SHA-256 of its UTF-8 text.</summary>
    public static string Hash(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    /// <summary>Compares a presented key with a stored hash in time that does not depend on where they differ.</summary>
    public static bool Matches(string token, string storedHash)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (storedHash is not { Length: SHA256.HashSizeInBytes * 2 })
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[SHA256.HashSizeInBytes];
        if (Convert.FromHexString(storedHash, expected, out _, out var written) != System.Buffers.OperationStatus.Done ||
            written != SHA256.HashSizeInBytes)
        {
            return false;
        }

        Span<byte> presented = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(token), presented);
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }
}
