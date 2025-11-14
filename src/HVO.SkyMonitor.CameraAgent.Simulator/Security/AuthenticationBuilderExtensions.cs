using Microsoft.AspNetCore.Authentication;

namespace HVO.SkyMonitor.CameraAgent.Simulator.Security;

public static class AuthenticationBuilderExtensions
{
    public static AuthenticationBuilder AddApiKeySupport(this AuthenticationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
            ApiKeyAuthenticationOptions.AuthenticationScheme,
            options => { });
    }
}
