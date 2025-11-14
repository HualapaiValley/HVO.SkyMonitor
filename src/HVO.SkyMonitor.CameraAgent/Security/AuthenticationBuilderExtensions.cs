using Microsoft.AspNetCore.Authentication;

namespace HVO.SkyMonitor.CameraAgent.Security;

public static class AuthenticationBuilderExtensions
{
    public static AuthenticationBuilder AddApiKeySupport<THandler>(this AuthenticationBuilder builder)
        where THandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddScheme<ApiKeyAuthenticationOptions, THandler>(
            ApiKeyAuthenticationOptions.AuthenticationScheme,
            options => { });
    }
}
