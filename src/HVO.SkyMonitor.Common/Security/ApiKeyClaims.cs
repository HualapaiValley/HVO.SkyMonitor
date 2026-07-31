namespace HVO.SkyMonitor.Common.Security;

public static class ApiKeyClaims
{
    public const string AccessLevel = "hvo:apikey:access";
    public const string ApiKeyId = "hvo:apikey:id";
    public const string ObservatoryId = "hvo:apikey:observatory-id";
    public const string AuthenticationType = "hvo:auth:scheme";
}
