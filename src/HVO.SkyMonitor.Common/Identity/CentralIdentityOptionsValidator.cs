using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.Common.Identity;

/// <summary>
/// Validates <see cref="CentralIdentityOptions"/> to ensure External ID / fallback metadata is present.
/// </summary>
public sealed class CentralIdentityOptionsValidator : IValidateOptions<CentralIdentityOptions>
{
    public ValidateOptionsResult Validate(string? name, CentralIdentityOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail("CentralIdentity configuration is required.");
        }

        var failures = new List<string>();

        ValidateServiceUrl(options.ServiceUrl, failures);
        ValidateTokenEndpoint(options.TokenEndpoint, failures);
        ValidateTokenWindows(options, failures);

        switch (options.Mode)
        {
            case AuthenticationMode.ClientCredentials:
                ValidateClientCredentials(options.ClientCredentials, failures);
                break;
            case AuthenticationMode.ApiKey:
                ValidateApiKey(options.ApiKey, failures);
                break;
            default:
                failures.Add($"CentralIdentity:Mode value '{options.Mode}' is not supported.");
                break;
        }

        ValidateInteractiveClient(options.InteractiveClient, failures);
        ValidateLocalFallback(options.LocalFallback, failures);

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static void ValidateServiceUrl(Uri? serviceUrl, List<string> failures)
    {
        if (serviceUrl is null)
        {
            failures.Add("CentralIdentity:ServiceUrl must be specified.");
            return;
        }

        if (!serviceUrl.IsAbsoluteUri)
        {
            failures.Add("CentralIdentity:ServiceUrl must be an absolute URI.");
        }
    }

    private static void ValidateTokenEndpoint(Uri? tokenEndpoint, List<string> failures)
    {
        if (tokenEndpoint is null)
        {
            return;
        }

        if (!tokenEndpoint.IsAbsoluteUri)
        {
            failures.Add("CentralIdentity:TokenEndpoint must be an absolute URI when specified.");
        }
    }

    private static void ValidateTokenWindows(CentralIdentityOptions options, List<string> failures)
    {
        if (options.TokenCacheDurationSeconds <= 0)
        {
            failures.Add("CentralIdentity:TokenCacheDurationSeconds must be greater than zero.");
        }

        if (options.TokenRefreshWindowSeconds <= 0)
        {
            failures.Add("CentralIdentity:TokenRefreshWindowSeconds must be greater than zero.");
        }
        else if (options.TokenRefreshWindowSeconds >= options.TokenCacheDurationSeconds)
        {
            failures.Add("CentralIdentity:TokenRefreshWindowSeconds must be less than TokenCacheDurationSeconds.");
        }
    }

    private static void ValidateClientCredentials(ClientCredentialsOptions? credentials, List<string> failures)
    {
        if (credentials is null)
        {
            failures.Add("CentralIdentity:ClientCredentials configuration is required when Mode=ClientCredentials.");
            return;
        }

        if (string.IsNullOrWhiteSpace(credentials.ClientId))
        {
            failures.Add("CentralIdentity:ClientCredentials:ClientId is required.");
        }

        if (string.IsNullOrWhiteSpace(credentials.ClientSecret))
        {
            failures.Add("CentralIdentity:ClientCredentials:ClientSecret is required.");
        }

        if (credentials.Scopes is null || credentials.Scopes.Count == 0)
        {
            failures.Add("CentralIdentity:ClientCredentials:Scopes must contain at least one value.");
        }
        else if (credentials.Scopes.Any(string.IsNullOrWhiteSpace))
        {
            failures.Add("CentralIdentity:ClientCredentials:Scopes cannot contain blank values.");
        }
    }

    private static void ValidateApiKey(ApiKeyOptions? apiKey, List<string> failures)
    {
        if (apiKey is null || string.IsNullOrWhiteSpace(apiKey.Key))
        {
            failures.Add("CentralIdentity:ApiKey:Key is required when Mode=ApiKey.");
        }
    }

    private static void ValidateInteractiveClient(InteractiveClientOptions? interactiveClient, List<string> failures)
    {
        if (interactiveClient is null)
        {
            failures.Add("CentralIdentity:InteractiveClient configuration is required.");
            return;
        }

        var publicAuthority = interactiveClient.PublicAuthority;
        if (publicAuthority is not null && !publicAuthority.IsAbsoluteUri)
        {
            failures.Add("CentralIdentity:InteractiveClient:PublicAuthority must be an absolute URI when specified.");
        }

        if (string.IsNullOrWhiteSpace(interactiveClient.ClientId))
        {
            failures.Add("CentralIdentity:InteractiveClient:ClientId is required.");
        }

        if (string.IsNullOrWhiteSpace(interactiveClient.ClientSecret))
        {
            failures.Add("CentralIdentity:InteractiveClient:ClientSecret is required.");
        }

        if (!interactiveClient.Scopes.Any())
        {
            failures.Add("CentralIdentity:InteractiveClient:Scopes must contain at least one value.");
        }

        ValidatePath(interactiveClient.CallbackPath, "CentralIdentity:InteractiveClient:CallbackPath", failures);
        ValidatePath(interactiveClient.SignedOutCallbackPath, "CentralIdentity:InteractiveClient:SignedOutCallbackPath", failures);
        ValidatePath(interactiveClient.RemoteSignOutPath, "CentralIdentity:InteractiveClient:RemoteSignOutPath", failures);
    }

    private static void ValidatePath(string? path, string settingName, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            failures.Add($"{settingName} is required.");
            return;
        }

        if (!path.StartsWith('/'))
        {
            failures.Add($"{settingName} must start with '/'.");
        }
    }

    private static void ValidateLocalFallback(LocalFallbackOptions? localFallback, List<string> failures)
    {
        if (localFallback is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(localFallback.AccessCodeHash))
        {
            if (localFallback.AccessCodeHash.Length != 64)
            {
                failures.Add("CentralIdentity:LocalFallback:AccessCodeHash must be a 64-character SHA-256 hex string.");
            }
            else if (!IsHex(localFallback.AccessCodeHash))
            {
                failures.Add("CentralIdentity:LocalFallback:AccessCodeHash must contain only hexadecimal characters (0-9, A-F).");
            }
        }

        if (localFallback.RotationIntervalDays <= 0)
        {
            failures.Add("CentralIdentity:LocalFallback:RotationIntervalDays must be greater than zero.");
        }
    }

    private static bool IsHex(string value)
    {
        foreach (var ch in value)
        {
            var isDigit = ch is >= '0' and <= '9';
            var isUpper = ch is >= 'A' and <= 'F';
            var isLower = ch is >= 'a' and <= 'f';
            if (!(isDigit || isUpper || isLower))
            {
                return false;
            }
        }

        return true;
    }
}
