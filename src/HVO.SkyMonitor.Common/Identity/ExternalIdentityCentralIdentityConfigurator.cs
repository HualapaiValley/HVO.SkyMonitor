using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.Common.Identity;

/// <summary>
/// Applies Azure Entra External ID (B2C) environment overrides to <see cref="CentralIdentityOptions"/>.
/// </summary>
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Type is materialized via dependency injection when CentralIdentityOptions are resolved.")]
internal sealed class ExternalIdentityCentralIdentityConfigurator(
    IConfiguration configuration,
    ILogger<ExternalIdentityCentralIdentityConfigurator> logger) : IConfigureOptions<CentralIdentityOptions>
{
    private static readonly char[] ScopeSeparators = [' ', ','];

    private readonly IConfiguration configuration = configuration;
    private readonly ILogger<ExternalIdentityCentralIdentityConfigurator> logger = logger;

    public void Configure(CentralIdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var overridesApplied = false;
        overridesApplied |= TryApplyAuthority(options);
        overridesApplied |= TryApplyTokenEndpoint(options);
        overridesApplied |= TryApplyClientCredentials(options);
        overridesApplied |= TryApplyInteractiveClient(options);

        if (overridesApplied)
        {
            Log.ExternalIdentityOverridesApplied(logger);
        }
    }

    private bool TryApplyAuthority(CentralIdentityOptions options)
    {
        var authority = configuration["B2C_AUTHORITY"];
        if (string.IsNullOrWhiteSpace(authority))
        {
            return false;
        }

        if (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri))
        {
            Log.InvalidAuthority(logger, authority);
            return false;
        }

        options.ServiceUrl = authorityUri;
        options.InteractiveClient ??= new InteractiveClientOptions();
        options.InteractiveClient.PublicAuthority ??= authorityUri;
        return true;
    }

    private bool TryApplyTokenEndpoint(CentralIdentityOptions options)
    {
        var tokenEndpoint = configuration["B2C_TOKEN_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(tokenEndpoint))
        {
            return false;
        }

        if (!Uri.TryCreate(tokenEndpoint, UriKind.Absolute, out var tokenEndpointUri))
        {
            Log.InvalidTokenEndpoint(logger, tokenEndpoint);
            return false;
        }

        options.TokenEndpoint = tokenEndpointUri;
        return true;
    }

    private bool TryApplyClientCredentials(CentralIdentityOptions options)
    {
        var clientId = configuration["B2C_CLIENT_ID"] ?? configuration["B2C_CLIENT_CREDENTIALS_CLIENT_ID"];
        var clientSecret = configuration["B2C_CLIENT_SECRET"] ?? configuration["B2C_CLIENT_CREDENTIALS_CLIENT_SECRET"];
        var scopeList = configuration["B2C_CLIENT_SCOPES"];

        if (string.IsNullOrWhiteSpace(clientId) &&
            string.IsNullOrWhiteSpace(clientSecret) &&
            string.IsNullOrWhiteSpace(scopeList))
        {
            return false;
        }

        options.ClientCredentials ??= new ClientCredentialsOptions();
        var applied = false;

        if (!string.IsNullOrWhiteSpace(clientId))
        {
            options.ClientCredentials.ClientId = clientId;
            applied = true;
        }

        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            options.ClientCredentials.ClientSecret = clientSecret;
            applied = true;
        }

        if (!string.IsNullOrWhiteSpace(scopeList))
        {
            var scopes = SplitScopes(scopeList);
            if (scopes.Length > 0)
            {
                options.ClientCredentials.Scopes.Clear();
                foreach (var scope in scopes)
                {
                    options.ClientCredentials.Scopes.Add(scope);
                }

                applied = true;
            }
        }

        return applied;
    }

    private bool TryApplyInteractiveClient(CentralIdentityOptions options)
    {
        var clientId = configuration["B2C_INTERACTIVE_CLIENT_ID"];
        var clientSecret = configuration["B2C_INTERACTIVE_CLIENT_SECRET"];
        var scopesValue = configuration["B2C_INTERACTIVE_SCOPES"];
        var callbackPath = configuration["B2C_CALLBACK_PATH"];
        var signedOutCallbackPath = configuration["B2C_SIGNED_OUT_CALLBACK_PATH"];
        var remoteSignOutPath = configuration["B2C_REMOTE_SIGNOUT_PATH"];

        if (string.IsNullOrWhiteSpace(clientId) &&
            string.IsNullOrWhiteSpace(clientSecret) &&
            string.IsNullOrWhiteSpace(scopesValue) &&
            string.IsNullOrWhiteSpace(callbackPath) &&
            string.IsNullOrWhiteSpace(signedOutCallbackPath) &&
            string.IsNullOrWhiteSpace(remoteSignOutPath))
        {
            return false;
        }

        options.InteractiveClient ??= new InteractiveClientOptions();
        var applied = false;

        if (!string.IsNullOrWhiteSpace(clientId))
        {
            options.InteractiveClient.ClientId = clientId;
            applied = true;
        }

        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            options.InteractiveClient.ClientSecret = clientSecret;
            applied = true;
        }

        if (!string.IsNullOrWhiteSpace(scopesValue))
        {
            var scopes = SplitScopes(scopesValue);
            if (scopes.Length > 0)
            {
                options.InteractiveClient.Scopes.Clear();
                foreach (var scope in scopes)
                {
                    options.InteractiveClient.Scopes.Add(scope);
                }

                applied = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(callbackPath))
        {
            options.InteractiveClient.CallbackPath = callbackPath;
            applied = true;
        }

        if (!string.IsNullOrWhiteSpace(signedOutCallbackPath))
        {
            options.InteractiveClient.SignedOutCallbackPath = signedOutCallbackPath;
            applied = true;
        }

        if (!string.IsNullOrWhiteSpace(remoteSignOutPath))
        {
            options.InteractiveClient.RemoteSignOutPath = remoteSignOutPath;
            applied = true;
        }

        return applied;
    }

    private static string[] SplitScopes(string? scopeList)
    {
        if (string.IsNullOrWhiteSpace(scopeList))
        {
            return Array.Empty<string>();
        }

        return scopeList
            .Split(ScopeSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static partial class Log
    {
        private static readonly Action<ILogger, Exception?> ExternalIdentityOverridesAppliedLog =
            LoggerMessage.Define(
                LogLevel.Information,
                new EventId(1500, nameof(ExternalIdentityOverridesApplied)),
                "Central Identity configured from Azure Entra External ID environment variables.");

        private static readonly Action<ILogger, string, Exception?> InvalidAuthorityLog =
            LoggerMessage.Define<string>(
                LogLevel.Warning,
                new EventId(1501, nameof(InvalidAuthority)),
                "B2C_AUTHORITY value '{Authority}' is not a valid absolute URI.");

        private static readonly Action<ILogger, string, Exception?> InvalidTokenEndpointLog =
            LoggerMessage.Define<string>(
                LogLevel.Warning,
                new EventId(1502, nameof(InvalidTokenEndpoint)),
                "B2C_TOKEN_ENDPOINT value '{TokenEndpoint}' is not a valid absolute URI.");

        public static void ExternalIdentityOverridesApplied(ILogger logger) =>
            ExternalIdentityOverridesAppliedLog(logger, null);

        public static void InvalidAuthority(ILogger logger, string authority) =>
            InvalidAuthorityLog(logger, authority, null);

        public static void InvalidTokenEndpoint(ILogger logger, string tokenEndpoint) =>
            InvalidTokenEndpointLog(logger, tokenEndpoint, null);
    }
}
