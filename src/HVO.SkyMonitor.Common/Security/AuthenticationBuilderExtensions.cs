using Microsoft.AspNetCore.Authentication;

namespace HVO.SkyMonitor.Common.Security;

public static class AuthenticationBuilderExtensions
{
    public static AuthenticationBuilder AddApiKeySupport(this AuthenticationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddScheme<ApiKeyAuthenticationOptions, DefaultApiKeyAuthenticationHandler>(
            ApiKeyAuthenticationOptions.AuthenticationScheme,
            _ => { });
    }

    public static AuthenticationBuilder AddApiKeySupport<THandler>(this AuthenticationBuilder builder)
        where THandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddScheme<ApiKeyAuthenticationOptions, THandler>(
            ApiKeyAuthenticationOptions.AuthenticationScheme,
            _ => { });
    }
}
