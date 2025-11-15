using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Identity;

namespace HVO.SkyMonitor.Data;

/// <summary>
/// Application user with API key support and account type distinction.
/// </summary>
public class ApplicationUser : IdentityUser
{
    /// <summary>
    /// Type of account (User or System).
    /// Defaults to User for interactive authentication.
    /// </summary>
    public AccountType AccountType { get; set; } = AccountType.User;

    /// <summary>
    /// API keys associated with this user.
    /// </summary>
    public ICollection<ApiKey> ApiKeys { get; set; } = new List<ApiKey>();
}
