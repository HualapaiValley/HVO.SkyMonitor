using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.Common.Identity;

/// <summary>
/// Helpers for configuring <see cref="CentralIdentityOptions"/> consistently across hosts.
/// </summary>
public static class CentralIdentityOptionsBuilderExtensions
{
    /// <summary>
    /// Registers validation and default scope behaviors for <see cref="CentralIdentityOptions"/>.
    /// </summary>
    public static OptionsBuilder<CentralIdentityOptions> ConfigureCentralIdentityDefaults(
        this OptionsBuilder<CentralIdentityOptions> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<CentralIdentityOptions>, CentralIdentityOptionsValidator>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<CentralIdentityOptions>, ExternalIdentityCentralIdentityConfigurator>());

        builder.Services.PostConfigure<CentralIdentityOptions>(ApplyDefaultClientScopes);
        builder.ValidateOnStart();
        return builder;
    }

    private static void ApplyDefaultClientScopes(CentralIdentityOptions options)
    {
        if (options.ClientCredentials is not { } credentials)
        {
            return;
        }

        if (credentials.Scopes.Count > 0)
        {
            return;
        }

        foreach (var scope in ClientCredentialsOptions.DefaultScopes)
        {
            credentials.Scopes.Add(scope);
        }
    }
}
