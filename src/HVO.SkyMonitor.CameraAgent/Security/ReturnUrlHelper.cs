using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.WebUtilities;

namespace HVO.SkyMonitor.CameraAgent.Security;

/// <summary>
/// Provides helpers for sanitizing and constructing return URL parameters used in authentication flows.
/// </summary>
public static class ReturnUrlHelper
{
    private const string DefaultPath = "/";
    private const string LoginPath = "/Account/Login";
    private const string ExternalLoginEndpoint = "/Account/ExternalLogin";

    /// <summary>
    /// Normalizes a return URL by ensuring it stays within the current site and always begins with a single slash.
    /// Returns "/" when the supplied value is null, empty, or attempts to navigate away from the site.
    /// </summary>
    /// <param name="returnUrl">The return URL to normalize.</param>
    /// <returns>A safe, application-local return URL.</returns>
    [SuppressMessage("Design", "CA1054:Uri parameters should not be strings", Justification = "Return URLs arrive as raw query parameters and are validated before use.")]
    [SuppressMessage("Design", "CA1055:Uri return values should not be strings", Justification = "UI components require string paths for navigation and form fields.")]
    public static string NormalizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return DefaultPath;
        }

        var trimmed = returnUrl.Trim();

        if (trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("\\\\", StringComparison.Ordinal)
            || trimmed.Contains("://", StringComparison.Ordinal)
            || trimmed.Contains("..", StringComparison.Ordinal))
        {
            return DefaultPath;
        }

        foreach (var ch in trimmed)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                return DefaultPath;
            }
        }

        trimmed = "/" + trimmed.TrimStart('/');

        if (!Uri.TryCreate(trimmed, UriKind.Relative, out _))
        {
            return DefaultPath;
        }

        return trimmed;
    }

    /// <summary>
    /// Creates the login endpoint path with the provided normalized return URL.
    /// </summary>
    /// <param name="normalizedReturnUrl">A normalized return URL produced by <see cref="NormalizeReturnUrl"/>.</param>
    /// <returns>The login path with the return URL query parameter appended.</returns>
    [SuppressMessage("Design", "CA1054:Uri parameters should not be strings", Justification = "Normalized return URLs are represented as application-relative strings for routing.")]
    public static string BuildLoginPath(string normalizedReturnUrl)
    {
        return QueryHelpers.AddQueryString(LoginPath, "returnUrl", normalizedReturnUrl);
    }

    /// <summary>
    /// Creates the external login endpoint path that initiates the OIDC challenge.
    /// </summary>
    /// <param name="normalizedReturnUrl">A normalized return URL produced by <see cref="NormalizeReturnUrl"/>.</param>
    /// <returns>The external login path with the return URL query parameter appended.</returns>
    [SuppressMessage("Design", "CA1054:Uri parameters should not be strings", Justification = "Normalized return URLs are represented as application-relative strings for routing.")]
    public static string BuildExternalLoginPath(string normalizedReturnUrl)
    {
        return QueryHelpers.AddQueryString(ExternalLoginEndpoint, "returnUrl", normalizedReturnUrl);
    }
}
