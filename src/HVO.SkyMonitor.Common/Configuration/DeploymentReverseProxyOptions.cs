using System.ComponentModel.DataAnnotations;
using System.Collections.ObjectModel;

namespace HVO.SkyMonitor.Common.Configuration;

public sealed class DeploymentReverseProxyOptions
{
    public const string SectionName = "ReverseProxy";

    public bool Enabled { get; set; }

    [Required]
    public Collection<string> TrustedProxies { get; } = [];
}
