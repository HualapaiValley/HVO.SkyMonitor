using System;
using HVO.SkyMonitor.CameraAgent.Configuration;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Services;

public interface ICentralIdentityNavigationService
{
    Uri GetAccountManagementUrl();
}

public sealed class CentralIdentityNavigationService : ICentralIdentityNavigationService
{
    private readonly IOptionsMonitor<CentralIdentityOptions> _options;

    public CentralIdentityNavigationService(IOptionsMonitor<CentralIdentityOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Uri GetAccountManagementUrl()
    {
        return BuildUrl("/Account/Manage");
    }

    private Uri BuildUrl(string relativePath)
    {
        var options = _options.CurrentValue;
        var baseUri = options.InteractiveClient?.PublicAuthority ?? options.ServiceUrl;
        return new Uri(baseUri, EnsureLeadingSlash(relativePath));
    }

    private static string EnsureLeadingSlash(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        return path[0] == '/' ? path : "/" + path;
    }
}
