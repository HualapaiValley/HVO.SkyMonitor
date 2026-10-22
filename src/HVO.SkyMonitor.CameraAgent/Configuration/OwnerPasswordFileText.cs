using System.Text;

namespace HVO.SkyMonitor.CameraAgent.Configuration;

/// <summary>
/// The one reading of an owner password file. CameraAgent seeds the owner from <c>LocalIdentity:AdminPasswordFile</c>
/// with it, and the deployment installer compiles this same file to validate an operator's <c>--password-file</c>,
/// authenticate its bootstrap check, classify the documented default, and read an owner recovery password. Every
/// reader therefore agrees on the password a file holds.
/// </summary>
/// <remarks>
/// The content is strict UTF-8. One leading UTF-8 byte-order mark is ignored and trailing carriage returns and line
/// feeds are removed; every other character, including leading, trailing, and inner spaces, is part of the password.
/// </remarks>
internal static class OwnerPasswordFileText
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Decodes owner password file content.</summary>
    /// <exception cref="DecoderFallbackException">The content is not valid UTF-8.</exception>
    internal static string Decode(ReadOnlySpan<byte> content)
    {
        ReadOnlySpan<byte> preamble = [0xEF, 0xBB, 0xBF];
        if (content.StartsWith(preamble))
        {
            content = content[preamble.Length..];
        }

        return StrictUtf8.GetString(content).TrimEnd('\r', '\n');
    }
}
