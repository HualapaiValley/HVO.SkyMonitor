using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Identity;

namespace HVO.SkyMonitor.Data;

/// <summary>
/// Application user with API key support.
/// </summary>
public class ApplicationUser : IdentityUser
{
    /// <summary>
    /// API keys associated with this user.
    /// </summary>
    public ICollection<ApiKey> ApiKeys { get; set; } = new List<ApiKey>();
}
