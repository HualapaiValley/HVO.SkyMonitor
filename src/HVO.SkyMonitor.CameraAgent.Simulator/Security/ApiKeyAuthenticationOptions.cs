using Microsoft.AspNetCore.Authentication;

namespace HVO.SkyMonitor.CameraAgent.Simulator.Security;

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string AuthenticationScheme = "ApiKey";
    public const string HeaderName = "X-API-Key";
}
