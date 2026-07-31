using HVO.SkyMonitor.Common.Security;

namespace HVO.SkyMonitor.LogicHost.Configuration;

internal sealed class DatabaseSeedOptions
{
    public const string SectionName = "DatabaseSeed";

    public IList<SeedUserOptions> Users { get; set; } = [];

    public IList<SeedApiKeyOptions> ApiKeys { get; set; } = [];

    public IList<SeedConfidentialClientOptions> ConfidentialClients { get; set; } = [];

    public IList<SeedPublicClientOptions> PublicClients { get; set; } = [];
}

internal sealed class SeedUserOptions
{
    public string Email { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public bool? IsPlatformEditor { get; set; }
}

internal sealed class SeedApiKeyOptions
{
    public string RawKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public ApiKeyAccessLevel AccessLevel { get; set; } = ApiKeyAccessLevel.Read;
}

internal sealed class SeedConfidentialClientOptions
{
    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public IList<string> Scopes { get; set; } = [];
}

internal sealed class SeedPublicClientOptions
{
    public string ClientId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public IList<string> Scopes { get; set; } = [];

    public IList<string> RedirectUris { get; set; } = [];

    public IList<string> PostLogoutRedirectUris { get; set; } = [];
}
